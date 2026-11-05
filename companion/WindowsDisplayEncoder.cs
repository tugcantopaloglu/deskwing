using System.Runtime.InteropServices;

namespace TabletDeck;

internal sealed record EncodedVideoFrame(byte[] Data,bool KeyFrame,long Timestamp100ns);

/// <summary>Bounded, low-latency H.264 access units using the Windows inbox encoder.
/// One streaming worker owns an instance; no desktop capture or network access occurs here.</summary>
internal sealed class WindowsDisplayEncoder : IDisposable
{
    internal const int MaximumFrameBytes=2*1024*1024;
    private readonly MfNative.IMFTransform transform;
    private readonly byte[] nv12;
    private readonly int width,height,rate;
    private byte[] sequenceHeader=[];
    private bool disposed;
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private long lastTimestamp=-1;
    internal string Name=>"Windows H.264 (low latency)";
    internal bool HardwareAccelerated=>false;
    internal byte[] SequenceHeader=>(byte[])sequenceHeader.Clone();

    internal WindowsDisplayEncoder(int width,int height,int frameRate,int bitrate=0)
    {
        Validate(width,height,frameRate);
        this.width=width;this.height=height;rate=frameRate;
        nv12=new byte[checked(width*height*3/2)];
        var selectedBitrate=bitrate==0?Math.Clamp((long)width*height*frameRate/6,4_000_000,40_000_000):bitrate;
        if(selectedBitrate is <1_000_000 or >50_000_000)throw new ArgumentOutOfRangeException(nameof(bitrate));
        MfNative.Check(MfNative.MFStartup(0x00020070,0));
        MfNative.IMFTransform? created=null;
        try{
            var type=Type.GetTypeFromCLSID(MfNative.H264Encoder,throwOnError:true)!;
            created=(MfNative.IMFTransform)Activator.CreateInstance(type)!;
            // These settings prevent B-frame reordering and prefer per-picture latency over complexity.
            SetCodec(created,MfNative.LowLatency,true);
            SetCodec(created,MfNative.QualityVsSpeed,(uint)0);
            SetCodec(created,MfNative.RateControlMode,(uint)0);
            SetCodec(created,MfNative.MeanBitrate,(uint)selectedBitrate);
            SetCodec(created,MfNative.BPictureCount,(uint)0);
            SetCodec(created,MfNative.GopSize,(uint)frameRate);
            MfNative.IMFMediaType? output=null,input=null;
            try{
                MfNative.Check(MfNative.MFCreateMediaType(out output));
                ConfigureType(output,MfNative.H264);
                Set(output,MfNative.AverageBitrate,(int)selectedBitrate);
                Set(output,MfNative.Profile,66); // Baseline: no reordering, broad tablet decoder support.
                MfNative.Check(created.SetOutputType(0,output,0));
                MfNative.Check(MfNative.MFCreateMediaType(out input));
                ConfigureType(input,MfNative.NV12);
                MfNative.Check(created.SetInputType(0,input,0));
            }finally{MfNative.Release(input);MfNative.Release(output);}
            transform=created;
            RefreshSequenceHeader();
            MfNative.Check(transform.ProcessMessage(0x10000000,IntPtr.Zero));
            MfNative.Check(transform.ProcessMessage(0x10000003,IntPtr.Zero));
            created=null;
        }catch{
            MfNative.Release(created);MfNative.MFShutdown();throw;
        }
    }

    internal static void Validate(int width,int height,int frameRate)
    {
        if(width is <16 or >4096||height is <16 or >2160||(width&1)!=0||(height&1)!=0||(long)width*height>8_847_360)
            throw new ArgumentException("Choose even video dimensions no larger than 4096 × 2160.");
        if(frameRate is <60 or >240)throw new ArgumentException("Video refresh must be between 60 and 240 Hz.");
    }

    internal EncodedVideoFrame? Encode(byte[] bgra,int stride,long timestamp100ns)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        if(timestamp100ns<0||timestamp100ns<=lastTimestamp)throw new ArgumentException("Video timestamps must increase.");
        BgraToNv12(bgra,stride,width,height,nv12);
        MfNative.IMFSample? input=null;MfNative.IMFMediaBuffer? buffer=null;
        try{
            MfNative.Check(MfNative.MFCreateMemoryBuffer(nv12.Length,out buffer));
            MfNative.Check(buffer.Lock(out var pointer,out _,out _));
            try{Marshal.Copy(nv12,0,pointer,nv12.Length);}finally{MfNative.Check(buffer.Unlock());}
            MfNative.Check(buffer.SetCurrentLength(nv12.Length));
            MfNative.Check(MfNative.MFCreateSample(out input));
            MfNative.Check(input.AddBuffer(buffer));
            MfNative.Check(input.SetSampleTime(timestamp100ns));
            MfNative.Check(input.SetSampleDuration(10_000_000L/rate));
            MfNative.Check(transform.ProcessInput(0,input,0));
            lastTimestamp=timestamp100ns;
        }finally{MfNative.Release(buffer);MfNative.Release(input);}
        return ReadOutput(timestamp100ns);
    }

    // The Windows encoder applies this request to the next ProcessInput and then resets it.
    // Recovery callers use it after idle gaps so a decoder awaiting an IDR need not wait a full GOP.
    internal void RequestKeyFrame()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        if(transform is not MfNative.ICodecAPI codec)throw new InvalidOperationException("The Windows encoder cannot request a recovery frame.");
        object value=(uint)1;
        MfNative.Check(codec.SetValue(ref MfNative.ForceKeyFrame,ref value));
    }

    private EncodedVideoFrame? ReadOutput(long fallbackTimestamp)
    {
        MfNative.Check(transform.GetOutputStreamInfo(0,out var info));
        if(info.Size is <0 or >16*1024*1024)throw new InvalidOperationException("The video encoder requested an oversized frame buffer.");
        MfNative.IMFSample? supplied=null,returned=null;MfNative.IMFMediaBuffer? buffer=null,contiguous=null;
        IntPtr suppliedPointer=IntPtr.Zero;
        var output=new MfNative.OutputDataBuffer[1];
        try{
            if((info.Flags&0x100)==0){
                MfNative.Check(MfNative.MFCreateSample(out supplied));
                MfNative.Check(MfNative.MFCreateMemoryBuffer(Math.Max(info.Size,4096),out buffer));
                MfNative.Check(supplied.AddBuffer(buffer));
                suppliedPointer=Marshal.GetComInterfaceForObject(supplied,typeof(MfNative.IMFSample));output[0].Sample=suppliedPointer;
            }
            var result=transform.ProcessOutput(0,1,output,out _);
            if(result==unchecked((int)0xc00d6d72))return null; // MF_E_TRANSFORM_NEED_MORE_INPUT
            MfNative.Check(result);
            if(output[0].Sample==IntPtr.Zero)throw new InvalidOperationException("The video encoder returned no sample.");
            // Re-importing our own sample pointer would increment its RCW count on every frame.
            returned=output[0].Sample==suppliedPointer?(supplied??throw new InvalidOperationException("The encoder sample is unavailable.")):(MfNative.IMFSample)Marshal.GetObjectForIUnknown(output[0].Sample);
            MfNative.Check(returned.ConvertToContiguousBuffer(out contiguous));
            MfNative.Check(contiguous.Lock(out var data,out _,out var length));
            byte[] encoded;
            try{
                if(length is <4 or >MaximumFrameBytes)throw new InvalidOperationException("The video encoder returned an invalid frame size.");
                encoded=new byte[length];Marshal.Copy(data,encoded,0,length);
            }finally{MfNative.Check(contiguous.Unlock());}
            var key=ContainsNal(encoded,5);
            if(returned.GetUINT32(ref MfNative.CleanPoint,out var clean)>=0)key|=clean!=0;
            if(!ContainsStartCode(encoded))throw new InvalidOperationException("The video encoder returned an unsupported H.264 format.");
            if(sequenceHeader.Length==0)RefreshSequenceHeader();
            if(key&&!ContainsNal(encoded,7)){
                if(sequenceHeader.Length==0)throw new InvalidOperationException("The video encoder did not provide H.264 configuration.");
                if(encoded.Length>MaximumFrameBytes-sequenceHeader.Length)throw new InvalidOperationException("The encoded video frame exceeds its transfer limit.");
                var combined=new byte[sequenceHeader.Length+encoded.Length];sequenceHeader.CopyTo(combined,0);encoded.CopyTo(combined,sequenceHeader.Length);encoded=combined;
            }
            var timestamp=returned.GetSampleTime(out var pts)>=0?pts:fallbackTimestamp;
            return new(encoded,key,timestamp);
        }finally{
            if(output[0].Events!=IntPtr.Zero)Marshal.Release(output[0].Events);
            if(output[0].Sample!=IntPtr.Zero&&output[0].Sample!=suppliedPointer)Marshal.Release(output[0].Sample);
            if(suppliedPointer!=IntPtr.Zero)Marshal.Release(suppliedPointer);
            MfNative.Release(contiguous);
            // supplied and returned may be the same RCW; release it exactly once.
            if(returned is not null&&!ReferenceEquals(returned,supplied))MfNative.Release(returned);
            MfNative.Release(buffer);MfNative.Release(supplied);
        }
    }

    private void ConfigureType(MfNative.IMFMediaType type,Guid subtype)
    {
        Set(type,MfNative.MajorType,MfNative.Video);Set(type,MfNative.Subtype,subtype);
        Set(type,MfNative.FrameSize,((long)width<<32)|(uint)height);Set(type,MfNative.FrameRate,((long)rate<<32)|1);
        Set(type,MfNative.PixelAspectRatio,1L<<32|1);Set(type,MfNative.InterlaceMode,2);
        Set(type,new Guid("3e23d450-2c75-4d25-a00e-b91670d12327"),1); // BT.709 matrix.
        Set(type,new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11"),2); // Limited-range NV12.
    }
    private void RefreshSequenceHeader()
    {
        MfNative.IMFMediaType? type=null;
        try{
            MfNative.Check(transform.GetOutputCurrentType(0,out type));
            if(type.GetBlobSize(ref MfNative.SequenceHeader,out var size)<0)return;
            if(size is <4 or >65536)throw new InvalidOperationException("The encoder configuration is outside its size limit.");
            var bytes=new byte[size];MfNative.Check(type.GetBlob(ref MfNative.SequenceHeader,bytes,bytes.Length,out var actual));
            if(actual!=size||!ContainsNal(bytes,7)||!ContainsNal(bytes,8))throw new InvalidOperationException("The encoder configuration is invalid.");
            sequenceHeader=bytes;
        }finally{MfNative.Release(type);}
    }
    private static void Set(MfNative.IMFAttributes value,Guid key,Guid data)=>MfNative.Check(value.SetGUID(ref key,ref data));
    private static void Set(MfNative.IMFAttributes value,Guid key,int data)=>MfNative.Check(value.SetUINT32(ref key,data));
    private static void Set(MfNative.IMFAttributes value,Guid key,long data)=>MfNative.Check(value.SetUINT64(ref key,data));
    private static void SetCodec(MfNative.IMFTransform value,Guid key,object data)
    {
        if(value is not MfNative.ICodecAPI codec)return;
        var result=codec.SetValue(ref key,ref data);
        // Optional codec controls differ across installed Windows encoders; negotiated media types remain authoritative.
        if(result<0&&result!=unchecked((int)0x80004001)&&result!=unchecked((int)0x80070057)&&result!=unchecked((int)0x80070490))MfNative.Check(result);
    }
    internal static bool ContainsStartCode(ReadOnlySpan<byte> bytes)=>bytes.Length>=4&&bytes[0]==0&&bytes[1]==0&&(bytes[2]==1||(bytes[2]==0&&bytes[3]==1));
    internal static bool ContainsNal(ReadOnlySpan<byte> bytes,int kind)
    {
        for(var i=0;i+3<bytes.Length;i++){
            if(bytes[i]!=0||bytes[i+1]!=0)continue;
            var offset=bytes[i+2]==1?3:bytes[i+2]==0&&bytes[i+3]==1?4:0;
            if(offset>0&&i+offset<bytes.Length&&(bytes[i+offset]&31)==kind)return true;
        }
        return false;
    }

    internal static void BgraToNv12(byte[] bgra,int stride,int width,int height,byte[] destination)
    {
        if(width<2||height<2||(width&1)!=0||(height&1)!=0||stride<width*4||(long)stride*height>bgra.Length||destination.Length!=(long)width*height*3/2)
            throw new ArgumentException("The captured video frame has invalid pixel dimensions.");
        var uvStart=width*height;
        // Each iteration owns disjoint two-row luma and chroma spans. No per-frame pixel allocations.
        Parallel.For(0,height/2,new ParallelOptions{MaxDegreeOfParallelism=Math.Min(Environment.ProcessorCount,4)},row=>{
            var source=row*2*stride;var y=row*2*width;var uv=uvStart+row*width;
            for(var column=0;column<width;column+=2){
                var top=source+column*4;var bottom=top+stride;
                var b0=bgra[top];var g0=bgra[top+1];var r0=bgra[top+2];var b1=bgra[top+4];var g1=bgra[top+5];var r1=bgra[top+6];
                var b2=bgra[bottom];var g2=bgra[bottom+1];var r2=bgra[bottom+2];var b3=bgra[bottom+4];var g3=bgra[bottom+5];var r3=bgra[bottom+6];
                destination[y+column]=Luma(r0,g0,b0);destination[y+column+1]=Luma(r1,g1,b1);destination[y+width+column]=Luma(r2,g2,b2);destination[y+width+column+1]=Luma(r3,g3,b3);
                var r=(r0+r1+r2+r3+2)/4;var g=(g0+g1+g2+g3+2)/4;var b=(b0+b1+b2+b3+2)/4;
                destination[uv+column]=(byte)Math.Clamp(((-26*r-86*g+112*b+128)>>8)+128,16,240);
                destination[uv+column+1]=(byte)Math.Clamp(((112*r-102*g-10*b+128)>>8)+128,16,240);
            }
        });
    }
    private static byte Luma(int r,int g,int b)=>(byte)Math.Clamp(((47*r+157*g+16*b+128)>>8)+16,16,235);
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{transform.ProcessMessage(0,IntPtr.Zero);}
        finally{
            try{MfNative.Release(transform);}
            finally{MfNative.MFShutdown();Array.Clear(nv12);sequenceHeader=[];}
        }
    }
}
