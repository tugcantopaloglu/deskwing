using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TabletDeck;

/// <summary>GPU desktop duplication for one server-selected display, never a desktop fallback.
/// Construction enumerates/allocates only. Capture is the sole method that obtains pixels.</summary>
internal sealed class WindowsDxgiCapture : IDisposable
{
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private readonly string sourceName;
    private readonly int x,y,width,height,stride;
    private readonly byte[] pixels;
    private IntPtr factory,adapter,output,output1,device,context,duplication,staging;
    private IntPtr monitor;
    private bool disposed,frameOwned,systemMemory,pointerStateKnown;
    private GetOutputDesc? getOutputDesc;
    private AcquireFrame? acquire;
    private ReleaseFrame? releaseFrame;
    private CopyResource? copyResource;
    private MapResource? mapResource;
    private UnmapResource? unmapResource;
    private MapDesktop? mapDesktop;
    private UnmapDesktop? unmapDesktop;

    internal int Width=>width;
    internal int Height=>height;
    internal int Stride=>stride;
    internal bool WasUpdated {get;private set;}
    internal bool SeparateCursorVisible {get;private set;}
    internal long LastPresentTime {get;private set;}

    internal WindowsDxgiCapture(string sourceName,int x,int y,int width,int height)
    {
        ValidateInteropLayouts();
        WindowsDisplayCapture.ValidateDimensions(width,height);
        if(string.IsNullOrEmpty(sourceName)||sourceName.Length>32)throw new ArgumentException("Choose the owned display's exact source name.");
        if((long)x+width>int.MaxValue||(long)y+height>int.MaxValue)throw new ArgumentOutOfRangeException(nameof(x));
        this.sourceName=sourceName;this.x=x;this.y=y;this.width=width;this.height=height;
        stride=checked(width*4);pixels=new byte[checked(stride*height)];
        try
        {
            var factoryId=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            Check(Native.CreateDXGIFactory1(ref factoryId,out factory),"Windows GPU capture is unavailable.");
            SelectExactOutput();
            var outputId=new Guid("00cddea8-939b-4b83-a340-a685226666cc");
            Check(Marshal.QueryInterface(output,in outputId,out output1),"The selected display does not support GPU duplication.");
            var levels=new uint[]{0xb100,0xb000,0xa100,0xa000};
            Check(Native.D3D11CreateDevice(adapter,0,IntPtr.Zero,0x20,levels,(uint)levels.Length,7,out device,out _,out context),"A GPU capture device could not be created for the selected display.");
            Check(Method<DuplicateOutput>(output1,22)(output1,device,out duplication),"GPU duplication is unavailable for this selected display. The compatible capture path may still work.");
            Method<GetDuplicationDesc>(duplication,7)(duplication,out var description);
            if(description.Mode.Width!=width||description.Mode.Height!=height||description.Mode.Format!=87||description.Rotation!=1)
                throw new NotSupportedException("The selected display's GPU format, dimensions or rotation are unsupported.");
            systemMemory=description.DesktopImageInSystemMemory!=0;
            acquire=Method<AcquireFrame>(duplication,8);releaseFrame=Method<ReleaseFrame>(duplication,14);
            if(systemMemory)
            {
                mapDesktop=Method<MapDesktop>(duplication,12);unmapDesktop=Method<UnmapDesktop>(duplication,13);
            }
            else
            {
                var texture=new TextureDescription{Width=(uint)width,Height=(uint)height,MipLevels=1,ArraySize=1,Format=87,SampleCount=1,Usage=3,CpuAccessFlags=0x20000};
                Check(Method<CreateTexture>(device,5)(device,ref texture,IntPtr.Zero,out staging),"GPU readback storage could not be created.");
                copyResource=Method<CopyResource>(context,47);mapResource=Method<MapResource>(context,14);unmapResource=Method<UnmapResource>(context,15);
            }
            ValidateOutput();
        }
        catch{Dispose();throw;}
    }

    // Null means DXGI has no fresh frame during this bounded wait. The caller may separately send
    // a static-image heartbeat, but must not count it as newly captured desktop motion.
    internal byte[]? Capture(CancellationToken cancellation)
    {
        RequireOwner();cancellation.ThrowIfCancellationRequested();WasUpdated=false;
        ValidateOutput();
        IntPtr resource=IntPtr.Zero,texture=IntPtr.Zero;
        var result=acquire!(duplication,16,out var info,out resource);
        if(result<0)Release(ref resource);
        if(result==unchecked((int)0x887a0027))return null; // DXGI_ERROR_WAIT_TIMEOUT
        Check(result,"The selected display's GPU capture session ended. Open the display again.");
        frameOwned=true;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Desktop-only updates do not carry a new pointer state. Preserve the last cursor
            // visibility until DXGI explicitly reports a pointer update.
            if(!pointerStateKnown||info.LastMouseUpdateTime!=0){SeparateCursorVisible=info.Pointer.Visible!=0;pointerStateKnown=true;}
            WasUpdated=info.LastPresentTime>0&&info.LastPresentTime!=LastPresentTime;
            if(info.LastPresentTime>0)LastPresentTime=info.LastPresentTime;
            if(resource==IntPtr.Zero)throw new InvalidOperationException("GPU capture returned no desktop image.");
            var textureId=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Check(Marshal.QueryInterface(resource,in textureId,out texture),"GPU capture returned an unsupported image resource.");
            Method<GetTextureDesc>(texture,10)(texture,out var textureDescription);
            if(textureDescription.Width!=width||textureDescription.Height!=height||textureDescription.Format!=87||textureDescription.SampleCount!=1||textureDescription.ArraySize!=1)
                throw new InvalidOperationException("The selected display's GPU image changed dimensions or format.");
            if(systemMemory)
            {
                Check(mapDesktop!(duplication,out var mapped),"GPU desktop memory is unavailable.");
                try{CopyRows(mapped.Bits,checked((uint)mapped.Pitch));}
                finally{Check(unmapDesktop!(duplication),"GPU desktop memory could not be released.");}
            }
            else
            {
                copyResource!(context,staging,texture);
                Check(mapResource!(context,staging,0,1,0,out var mapped),"The GPU image could not be read.");
                try{CopyRows(mapped.Data,mapped.RowPitch);}
                finally{unmapResource!(context,staging,0);}
            }
            cancellation.ThrowIfCancellationRequested();ValidateOutput();
            return pixels;
        }
        finally
        {
            Release(ref texture);Release(ref resource);
            if(frameOwned){frameOwned=false;Check(releaseFrame!(duplication),"The selected display's GPU frame could not be released.");}
        }
    }

    private void SelectExactOutput()
    {
        var enumerate=Method<EnumerateAdapters>(factory,12);
        for(uint index=0;index<64;index++)
        {
            var result=enumerate(factory,index,out var candidate);
            if(result==unchecked((int)0x887a0002))break;
            Check(result,"Windows GPU adapters could not be inspected.");
            try
            {
                var enumerateOutputs=Method<EnumerateOutputs>(candidate,7);
                for(uint outputIndex=0;outputIndex<64;outputIndex++)
                {
                    result=enumerateOutputs(candidate,outputIndex,out var screen);
                    if(result==unchecked((int)0x887a0002))break;
                    Check(result,"Windows GPU displays could not be inspected.");
                    try
                    {
                        var describe=Method<GetOutputDesc>(screen,7);Check(describe(screen,out var description),"Windows GPU display identity is unavailable.");
                        if(!string.Equals(description.DeviceName,sourceName,StringComparison.OrdinalIgnoreCase))continue;
                        if(!Matches(description))throw new InvalidOperationException("The selected GPU display's bounds or rotation changed.");
                        adapter=candidate;candidate=IntPtr.Zero;output=screen;screen=IntPtr.Zero;monitor=description.Monitor;getOutputDesc=describe;return;
                    }
                    finally{Release(ref screen);}
                }
            }
            finally{Release(ref candidate);}
        }
        throw new NotSupportedException("The selected virtual display is not exposed through Windows GPU duplication.");
    }

    private bool Matches(OutputDescription description)=>description.AttachedToDesktop!=0&&description.Rotation==1&&description.Bounds.Left==x&&description.Bounds.Top==y&&(long)description.Bounds.Right-x==width&&(long)description.Bounds.Bottom-y==height;
    private void ValidateOutput()
    {
        Check(getOutputDesc!(output,out var description),"The selected GPU display is no longer available.");
        if(!Matches(description)||description.Monitor!=monitor||!string.Equals(description.DeviceName,sourceName,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected GPU display changed. This frame was discarded.");
    }
    private void CopyRows(IntPtr data,uint pitch)
        =>CopyRows(data,pitch,width,height,pixels);
    internal static void CopyRows(IntPtr data,uint pitch,int width,int height,byte[] pixels)
    {
        WindowsDisplayCapture.ValidateDimensions(width,height);
        var stride=checked(width*4);
        if(pixels.Length!=checked(stride*height))throw new InvalidOperationException("GPU capture output storage does not match the desktop dimensions.");
        if(data==IntPtr.Zero||pitch<stride||pitch>65536||(long)pitch*height>256*1024*1024)
            throw new InvalidOperationException("GPU capture returned an invalid pixel stride.");
        if(pitch==stride)Marshal.Copy(data,pixels,0,pixels.Length);
        else for(var row=0;row<height;row++)Marshal.Copy(IntPtr.Add(data,checked((int)(row*pitch))),pixels,row*stride,stride);
    }
    internal static void ValidateInteropLayouts()
    {
        if(IntPtr.Size!=8||Marshal.SizeOf<OutputDescription>()!=96||Marshal.SizeOf<FrameInformation>()!=48||Marshal.SizeOf<DuplicationDescription>()!=36||Marshal.SizeOf<TextureDescription>()!=44||Marshal.SizeOf<MappedResource>()!=16||Marshal.SizeOf<MappedDesktop>()!=16)
            throw new PlatformNotSupportedException("GPU capture requires the supported 64-bit Windows interop layout.");
    }
    private void RequireOwner()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("GPU capture must remain on its owning stream worker.");
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(frameOwned&&duplication!=IntPtr.Zero){frameOwned=false;releaseFrame?.Invoke(duplication);}
        Release(ref staging);Release(ref duplication);Release(ref context);Release(ref device);
        Release(ref output1);Release(ref output);Release(ref adapter);Release(ref factory);
        Array.Clear(pixels);
    }
    private static void Release(ref IntPtr pointer){if(pointer!=IntPtr.Zero){Marshal.Release(pointer);pointer=IntPtr.Zero;}}
    private static T Method<T>(IntPtr instance,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),checked(slot*IntPtr.Size)));
    private static void Check(int result,string message){if(result<0)throw new NotSupportedException(message,new COMException(message,result));}

    [StructLayout(LayoutKind.Sequential)]private struct Rectangle{internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct OutputDescription{[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string DeviceName;internal Rectangle Bounds;internal int AttachedToDesktop;internal uint Rotation;internal IntPtr Monitor;}
    [StructLayout(LayoutKind.Sequential)]private struct PointerPosition{internal int X,Y,Visible;}
    [StructLayout(LayoutKind.Sequential)]private struct FrameInformation{internal long LastPresentTime,LastMouseUpdateTime;internal uint AccumulatedFrames;internal int RectanglesCoalesced,ProtectedContentMasked;internal PointerPosition Pointer;internal uint TotalMetadataSize,PointerShapeSize;}
    [StructLayout(LayoutKind.Sequential)]private struct ModeDescription{internal uint Width,Height,RefreshNumerator,RefreshDenominator,Format,ScanlineOrdering,Scaling;}
    [StructLayout(LayoutKind.Sequential)]private struct DuplicationDescription{internal ModeDescription Mode;internal uint Rotation;internal int DesktopImageInSystemMemory;}
    [StructLayout(LayoutKind.Sequential)]private struct TextureDescription{internal uint Width,Height,MipLevels,ArraySize,Format,SampleCount,SampleQuality,Usage,BindFlags,CpuAccessFlags,MiscFlags;}
    [StructLayout(LayoutKind.Sequential)]private struct MappedResource{internal IntPtr Data;internal uint RowPitch,DepthPitch;}
    [StructLayout(LayoutKind.Sequential)]private struct MappedDesktop{internal int Pitch;internal IntPtr Bits;}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int EnumerateAdapters(IntPtr instance,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int EnumerateOutputs(IntPtr instance,uint index,out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetOutputDesc(IntPtr instance,out OutputDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int DuplicateOutput(IntPtr instance,IntPtr device,out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate void GetDuplicationDesc(IntPtr instance,out DuplicationDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int AcquireFrame(IntPtr instance,uint milliseconds,out FrameInformation information,out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int ReleaseFrame(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int CreateTexture(IntPtr instance,ref TextureDescription description,IntPtr data,out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate void GetTextureDesc(IntPtr instance,out TextureDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate void CopyResource(IntPtr instance,IntPtr destination,IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int MapResource(IntPtr instance,IntPtr resource,uint subresource,uint mode,uint flags,out MappedResource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate void UnmapResource(IntPtr instance,IntPtr resource,uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int MapDesktop(IntPtr instance,out MappedDesktop mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int UnmapDesktop(IntPtr instance);
    private static class Native
    {
        [DllImport("dxgi.dll",ExactSpelling=true)]internal static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
        [DllImport("d3d11.dll",ExactSpelling=true)]internal static extern int D3D11CreateDevice(IntPtr adapter,uint driverType,IntPtr software,uint flags,[In]uint[] featureLevels,uint featureLevelCount,uint sdkVersion,out IntPtr device,out uint featureLevel,out IntPtr context);
    }
}
