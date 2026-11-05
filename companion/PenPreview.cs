using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TabletDeck;

internal sealed record PenPreviewRequest(string Lease="",int Width=1280);
internal sealed record PenPreviewFrame(string Image,int Width,int Height,string TargetId,int TargetWidth,int TargetHeight,long Sequence,DateTimeOffset CapturedAt);
internal sealed record PenCapturedImage(int Width,int Height,byte[] Jpeg);

internal interface IPenPreviewPlatform
{
    PenCapturedImage Capture(PenDestination destination,int requestedWidth,CancellationToken cancellation);
}

// Frames have exactly the same authority and geometry as the active pen lease. There is no cache:
// ending, replacing or resizing a session can never expose a prior frame to another session.
internal sealed class PenPreview
{
    internal const int MinimumIntervalMs=84,TimeoutMs=2000,MaximumImageLength=2*1024*1024;
    internal const string ImagePrefix="data:image/jpeg;base64,";
    internal static readonly int MaximumJpegBytes=(MaximumImageLength-ImagePrefix.Length)/4*3;
    private readonly WindowsPen pen;
    private readonly IPenPreviewPlatform platform;
    private readonly Func<long> now;
    private readonly int timeoutMs;
    private readonly SemaphoreSlim captureGate=new(1,1);
    private bool attempted;
    private long attemptAt,sequence;

    internal PenPreview(WindowsPen pen):this(pen,new NativePenPreviewPlatform(),()=>Environment.TickCount64){}
    internal PenPreview(WindowsPen pen,IPenPreviewPlatform platform,Func<long> now,int timeoutMs=TimeoutMs)
    {this.pen=pen;this.platform=platform;this.now=now;this.timeoutMs=timeoutMs;}

    internal async Task<PenPreviewFrame> Frame(PenPreviewRequest request,string principal,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Lease is not {Length:48}||!request.Lease.All(char.IsAsciiHexDigit))throw new ArgumentException("Start a pen session to view its PC target.");
        if(request.Width is <160 or >1600)throw new ArgumentException("Choose a pen preview width between 160 and 1600 pixels.");
        ct.ThrowIfCancellationRequested();
        var authorization=pen.CaptureTarget(principal,request.Lease);
        Dimensions(authorization.Destination.Bounds,request.Width);
        if(!await captureGate.WaitAsync(0,ct))throw new InvalidOperationException("PC preview capture is busy. It will retry without queuing frames.");
        if(attempted&&now()-attemptAt<MinimumIntervalMs){captureGate.Release();throw new InvalidOperationException("PC preview is rate limited. It will retry shortly.");}
        attempted=true;attemptAt=now();
        var captureStarted=Stopwatch.GetTimestamp();
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(timeoutMs);
        void CheckCaptureLifetime()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            // Timer callbacks can be delayed independently of WaitAsync's timeout. A late native
            // return must be discarded even when CancelAfter has not run on a busy thread pool yet.
            if(Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds>=timeoutMs)throw new TimeoutException();
        }
        // PrintWindow is synchronous and an unresponsive app can block it. Only this one worker may
        // capture; timing out a response does NOT free its slot or create additional native workers.
        // No GDI work runs while WindowsPen's gate or the fast-control gate is held.
        var capture=Task.Run(()=>{
            try{
                CheckCaptureLifetime();
                pen.ValidateCapture(authorization);
                var bitmap=platform.Capture(authorization.Destination,request.Width,cancellation.Token);
                CheckCaptureLifetime();
                var expected=Dimensions(authorization.Destination.Bounds,request.Width);
                if(bitmap.Width!=expected.Width||bitmap.Height!=expected.Height||bitmap.Jpeg is not {Length:>=4}||bitmap.Jpeg.Length>MaximumJpegBytes||bitmap.Jpeg[0]!=255||bitmap.Jpeg[1]!=216||bitmap.Jpeg[^2]!=255||bitmap.Jpeg[^1]!=217)
                    throw new InvalidOperationException("The PC target returned an invalid or oversized preview.");
                var image=ImagePrefix+Convert.ToBase64String(bitmap.Jpeg);
                CheckCaptureLifetime();
                pen.ValidateCapture(authorization);
                var target=authorization.Destination;
                return new PenPreviewFrame(image,bitmap.Width,bitmap.Height,target.Info.Id,target.Bounds.Width,target.Bounds.Height,Interlocked.Increment(ref sequence),DateTimeOffset.UtcNow);
            }finally{cancellation.Dispose();captureGate.Release();}
        });
        // Observe late native faults too; a timed-out HTTP request must not retain a frame or an
        // unobserved exception. The continuation captures no lease, frame, bitmap or credential.
        _=capture.ContinueWith(completed=>{_ = completed.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try{
            var frame=await capture.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs),ct);
            ct.ThrowIfCancellationRequested();
            pen.ValidateCapture(authorization);
            return frame;
        }catch(TimeoutException){throw new InvalidOperationException("The PC application did not provide a preview in time. Pen input remains available.");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new InvalidOperationException("The PC application did not provide a preview in time. Pen input remains available.");}
    }

    internal static (int Width,int Height) Dimensions(PenRectangle bounds,int requestedWidth)
    {
        if(bounds.Width is <1 or >8192||bounds.Height is <1 or >8192||(long)bounds.Width*bounds.Height>16*1024*1024)
            throw new InvalidOperationException("The selected PC target exceeds the bounded preview size.");
        if(requestedWidth is <160 or >1600)throw new ArgumentException("Choose a pen preview width between 160 and 1600 pixels.");
        var width=Math.Min(requestedWidth,bounds.Width);
        var height=Math.Max(1,(int)Math.Round((double)width*bounds.Height/bounds.Width));
        if(height>1600){height=1600;width=Math.Max(1,(int)Math.Round((double)height*bounds.Width/bounds.Height));}
        return(width,height);
    }
}

internal sealed class NativePenPreviewPlatform : IPenPreviewPlatform
{
    private static readonly ImageCodecInfo JpegCodec=ImageCodecInfo.GetImageEncoders().First(codec=>codec.FormatID==ImageFormat.Jpeg.Guid);
    public PenCapturedImage Capture(PenDestination destination,int requestedWidth,CancellationToken cancellation)
    {
        var size=PenPreview.Dimensions(destination.Bounds,requestedWidth);
        cancellation.ThrowIfCancellationRequested();
        using var dpi=new CaptureDpiScope();
        using var full=new Bitmap(destination.Bounds.Width,destination.Bounds.Height,PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(full)){
            graphics.Clear(Color.Black);
            if(destination.Info.Kind=="window"){
                var window=new IntPtr(destination.Window);
                ValidateWindow(destination);
                var dc=graphics.GetHdc();
                try{
                    // PW_CLIENTONLY + PW_RENDERFULLCONTENT: ask this window to render into our own
                    // memory DC. Never fall back to a desktop crop: unrelated overlays must not leak.
                    if(!Native.PrintWindow(window,dc,3))throw new InvalidOperationException("This PC application cannot provide a window preview. Select its display on the PC to share that display instead.");
                }finally{graphics.ReleaseHdc(dc);}
                ValidateWindow(destination);
            }else if(destination.Info.Kind=="display"){
                // An explicitly selected display shares that display, including its visible windows.
                graphics.CopyFromScreen(destination.Bounds.X,destination.Bounds.Y,0,0,full.Size,CopyPixelOperation.SourceCopy);
            }else throw new InvalidOperationException("The selected pen target does not support a preview.");
        }
        cancellation.ThrowIfCancellationRequested();
        using var resized=new Bitmap(size.Width,size.Height,PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(resized)){
            graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(full,0,0,size.Width,size.Height);
        }
        // JPEG quality is bounded and progressively reduced only for unusually complex targets.
        foreach(var quality in new long[]{72,52,32}){
            cancellation.ThrowIfCancellationRequested();
            using var bytes=new MemoryStream();
            using var options=new EncoderParameters(1);
            options.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality);
            resized.Save(bytes,JpegCodec,options);
            if(bytes.Length<=PenPreview.MaximumJpegBytes)return new(size.Width,size.Height,bytes.ToArray());
        }
        throw new InvalidOperationException("The PC preview exceeds its transfer limit. Choose a smaller preview size.");
    }
    private static void ValidateWindow(PenDestination destination)
    {
        var window=new IntPtr(destination.Window);
        Native.GetWindowThreadProcessId(window,out var process);
        var origin=new Native.Point();
        if(window==IntPtr.Zero||!Native.IsWindow(window)||!Native.IsWindowVisible(window)||Native.IsIconic(window)||process!=destination.Process||Native.GetAncestor(Native.GetForegroundWindow(),2)!=window||!Native.GetClientRect(window,out var bounds)||!Native.ClientToScreen(window,ref origin)||new PenRectangle(origin.X,origin.Y,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top)!=destination.Bounds)
            throw new InvalidOperationException("The selected PC window changed or lost focus. Its preview was discarded.");
    }
    private sealed class CaptureDpiScope : IDisposable
    {
        private readonly IntPtr previous;
        public CaptureDpiScope(){previous=Native.SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot establish physical-pixel preview coordinates.");}
        public void Dispose(){if(previous!=IntPtr.Zero)Native.SetThreadDpiAwarenessContext(previous);}
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point{public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] internal struct Rect{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr window,out Rect rectangle);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
