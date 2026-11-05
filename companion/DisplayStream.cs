using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TabletDeck;

internal sealed record DisplayVideoStart(int RefreshRate=60);
internal sealed record DisplayVideoLease(string Lease,int Width,int Height,int RefreshRate,double SourceRefreshRate,string Codec="h264");

internal interface IDisplayVideoProducer : IDisposable
{
    EncodedVideoFrame? Next(long timestamp100ns,CancellationToken cancellation);
    string EncoderName {get;}
    bool HardwareAccelerated {get;}
}

// A single native video session is separate from pen and control transports. The encoder and GDI
// objects remain on one dedicated worker thread; a slow consumer cannot create an encoder queue.
internal sealed class DisplayStream : IDisposable
{
    internal const int MaximumAccessUnitBytes=2*1024*1024,UnclaimedLeaseMs=5000,WriteTimeoutMs=2000;
    private readonly object gate=new();
    private readonly Func<DisplayVideoTarget> captureTarget;
    private readonly Action<DisplayVideoTarget> validateTarget;
    private readonly Func<DisplayVideoTarget,int,IDisplayVideoProducer> createProducer;
    private readonly Func<long> now;
    private VideoSession? session;
    private bool shutdown,workerRunning;
    private long generation;

    private sealed class VideoSession(string lease,string owner,long generation,long expires,DisplayVideoTarget target,int rate)
    {
        internal readonly string Lease=lease,Owner=owner;
        internal readonly long Generation=generation,Expires=expires;
        internal readonly DisplayVideoTarget Target=target;
        internal readonly int Rate=rate;
        internal readonly CancellationTokenSource Cancellation=new();
        internal bool Attached;
    }

    internal DisplayStream(ExtendedDisplay display):this(display.CaptureVideoTarget,display.ValidateVideoTarget,(target,rate)=>new NativeDisplayVideoProducer(target,rate)){}
    internal DisplayStream(Func<DisplayVideoTarget> captureTarget,Action<DisplayVideoTarget> validateTarget,Func<DisplayVideoTarget,int,IDisplayVideoProducer> producerFactory,Func<long>? nowMilliseconds=null)
    {this.captureTarget=captureTarget;this.validateTarget=validateTarget;createProducer=producerFactory;now=nowMilliseconds??(()=>Environment.TickCount64);}

    internal DisplayVideoLease Start(DisplayVideoStart request,string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.RefreshRate is <60 or >240)throw new ArgumentException("Choose a video refresh rate from 60 to 240 Hz.");
        if(string.IsNullOrEmpty(principal))throw new ArgumentException("A paired tablet is required for video.");
        lock(gate)
        {
            if(shutdown)throw new InvalidOperationException("DeskWing is closing. Open the display again after reconnecting.");
            ExpireUnclaimed();
            if(session is not null||workerRunning)throw new InvalidOperationException("Another display stream is active or still closing. Close it before opening a new one.");
            var target=captureTarget();
            if(target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("Second display sharing changed. Refresh the PC setup.");
            if((target.Width,target.Height) is not ((1280,800) or (1600,900) or (1920,1080)))throw new InvalidOperationException("The Second display size is unsupported for video.");
            // Windows commonly reports 59.94 for a nominal 60 Hz mode; do not round a genuine
            // lower-rate source (for example 60 Hz requested as 120 Hz) into a false capability.
            if(!double.IsFinite(target.RefreshRate)||target.RefreshRate+0.1<request.RefreshRate)
                throw new InvalidOperationException($"The Windows display is running at {target.RefreshRate:0.##} Hz. Recreate Second display on the PC at {request.RefreshRate} Hz or higher before using this tablet setting.");
            var lease=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            session=new(lease,principal,++generation,now()+UnclaimedLeaseMs,target,request.RefreshRate);
            return new(lease,target.Width,target.Height,request.RefreshRate,target.RefreshRate);
        }
    }

    internal void Stop(string lease,string principal)
    {
        lock(gate){if(session is not null&&session.Lease==lease&&session.Owner==principal)CancelSession();}
    }
    internal void Revoke(string principal){lock(gate){if(session?.Owner==principal)CancelSession();}}
    internal void Shutdown(){lock(gate){shutdown=true;CancelSession();}}
    public void Dispose()=>Shutdown();

    internal Task StreamAsync(string lease,string principal,Stream output,Func<bool> stillAuthorized,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);ArgumentNullException.ThrowIfNull(stillAuthorized);
        VideoSession current;
        lock(gate)
        {
            ExpireUnclaimed();
            if(shutdown||session is null||session.Owner!=principal||session.Lease!=lease||session.Attached||workerRunning)
                throw new InvalidOperationException("Open a new display stream from this paired tablet.");
            if(!stillAuthorized())throw new InvalidOperationException("Display access is no longer available.");
            ct.ThrowIfCancellationRequested();validateTarget(session.Target);
            current=session;current.Attached=true;workerRunning=true;
        }
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker=new Thread(()=>Run(current,output,stillAuthorized,ct,completion)){IsBackground=true,Name="DeskWing display video"};
        try{worker.Start();}
        catch{lock(gate){workerRunning=false;if(ReferenceEquals(session,current))CancelSession();}current.Cancellation.Dispose();throw;}
        return completion.Task;
    }

    private void Run(VideoSession current,Stream output,Func<bool> stillAuthorized,CancellationToken requestCancellation,TaskCompletionSource completion)
    {
        Exception? failure=null;var cancelled=false;
        try
        {
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(requestCancellation,current.Cancellation.Token,current.Target.Invalidated);
            var ct=cancellation.Token;
            Validate(current,stillAuthorized,ct);
            using var producer=createProducer(current.Target,current.Rate);
            Validate(current,stillAuthorized,ct);
            Write(output,Header(current.Target.Width,current.Target.Height,current.Rate),ct);
            var clock=Stopwatch.StartNew();
            using var pacer=new VideoPacer();
            var framePeriod=Stopwatch.Frequency/(double)current.Rate;
            double next=0;
            long heartbeatAt=0;
            var firstFrame=true;
            while(true)
            {
                pacer.Wait(clock,next,ct);
                Validate(current,stillAuthorized,ct);
                var timestamp=(long)(clock.ElapsedTicks*(10000000.0/Stopwatch.Frequency));
                var frame=producer.Next(timestamp,ct);
                Validate(current,stillAuthorized,ct);
                if(frame is not null)
                {
                    if(frame.Data.Length is <1 or >MaximumAccessUnitBytes||frame.Timestamp100ns<0)
                        throw new InvalidOperationException("The video encoder returned an invalid frame.");
                    if(firstFrame&&!frame.KeyFrame)throw new InvalidOperationException("The video encoder did not begin with a keyframe.");
                    var packet=PacketHeader(frame.Data.Length,frame.KeyFrame,(ulong)(frame.Timestamp100ns/10));
                    // Each write is bounded and awaits the consumer. There is never a pending
                    // queue of captured or compressed frames behind this worker.
                    Write(output,packet,ct);
                    Validate(current,stillAuthorized,ct);
                    Write(output,frame.Data,ct);
                    firstFrame=false;
                    heartbeatAt=clock.ElapsedMilliseconds;
                }
                else if(clock.ElapsedMilliseconds-heartbeatAt>=500)
                {
                    // An unchanged DXGI desktop need not be recaptured or encoded. A bounded
                    // heartbeat keeps the pinned connection alive without inventing video FPS.
                    Validate(current,stillAuthorized,ct);
                    Write(output,HeartbeatHeader((ulong)(clock.ElapsedTicks*(1000000.0/Stopwatch.Frequency))),ct);
                    heartbeatAt=clock.ElapsedMilliseconds;
                }
                next+=framePeriod;
                // Keep the original cadence through a small overrun; the next capture is always
                // fresh. Only abandon a missed interval once more than one whole period is late.
                if(next<clock.ElapsedTicks-framePeriod)next=clock.ElapsedTicks;
            }
        }
        catch(PendingWriteException ex)
        {
            // Abort the HTTP observer promptly, but retain this sole worker slot until the raw
            // writer actually settles. A non-cooperative stream cannot accumulate late writes.
            failure=ex.InnerException??ex;cancelled=failure is OperationCanceledException;
            if(cancelled)completion.TrySetCanceled();else completion.TrySetException(failure);
            try{ex.Pending.GetAwaiter().GetResult();}catch(Exception late) when(late is not OutOfMemoryException){}
        }
        catch(OperationCanceledException){cancelled=true;}
        catch(Exception ex) when(ex is not OutOfMemoryException){failure=ex;}
        finally
        {
            lock(gate)
            {
                if(ReferenceEquals(session,current))CancelSession();
                workerRunning=false;
            }
            current.Cancellation.Dispose();
        }
        if(cancelled)completion.TrySetCanceled();else if(failure is not null)completion.TrySetException(failure);else completion.TrySetResult();
    }

    private void Validate(VideoSession current,Func<bool> stillAuthorized,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(gate)
        {
            if(shutdown||!ReferenceEquals(session,current)||session.Generation!=current.Generation)
                throw new OperationCanceledException("The display session ended.",ct);
        }
        if(!stillAuthorized())throw new InvalidOperationException("Display access was revoked or the companion is restarting.");
        validateTarget(current.Target);
        ct.ThrowIfCancellationRequested();
    }

    private static void Write(Stream output,ReadOnlyMemory<byte> bytes,CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(WriteTimeoutMs);
        Bounded(output.WriteAsync(bytes,timeout.Token).AsTask(),cancellation);
        Bounded(output.FlushAsync(timeout.Token),cancellation);
    }

    private static void Bounded(Task pending,CancellationToken cancellation)
    {
        try{pending.WaitAsync(TimeSpan.FromMilliseconds(WriteTimeoutMs),cancellation).GetAwaiter().GetResult();}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {if(!pending.IsCompleted)throw new PendingWriteException(pending,ex);throw;}
    }
    private sealed class PendingWriteException(Task pending,Exception failure):Exception("The display connection stopped accepting frames.",failure)
    {internal Task Pending {get;}=pending;}

    private void ExpireUnclaimed(){if(session is {Attached:false}&&now()>=session.Expires)CancelSession();}
    private void CancelSession()
    {
        var old=session;session=null;generation++;
        if(old is null)return;
        old.Cancellation.Cancel();
        if(!old.Attached)old.Cancellation.Dispose();
    }

    internal static byte[] Header(int width,int height,int refreshRate)
    {
        var header=new byte[32];"DWV1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4),1);BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6),32);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8),(uint)width);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12),(uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16),(uint)refreshRate);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20),1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24),MaximumAccessUnitBytes);
        return header;
    }
    internal static byte[] PacketHeader(int size,bool keyFrame,ulong timestampMicroseconds)
    {
        var header=new byte[16];BinaryPrimitives.WriteUInt32BigEndian(header,(uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4),keyFrame?1U:0U);BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8),timestampMicroseconds);
        return header;
    }
    internal static byte[] HeartbeatHeader(ulong timestampMicroseconds)
    {
        var header=new byte[16];BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4),8);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8),timestampMicroseconds);return header;
    }
}

internal sealed class NativeDisplayVideoProducer : IDisplayVideoProducer
{
    private readonly DisplayVideoTarget target;
    private readonly WindowsDisplayEncoder encoder;
    private readonly WindowsDisplayCapture? capture;
    private readonly WindowsDxgiCapture? gpuCapture;
    private readonly WindowsDisplayCursorOverlay? cursorOverlay;
    private readonly Action<double,double>? observeTimings;
    private long lastEncodedTimestamp=-1;
    private readonly IntPtr previousDpi;
    internal long FreshDesktopFrames {get;private set;}
    internal long CapturedFrames {get;private set;}
    internal NativeDisplayVideoProducer(DisplayVideoTarget target,int refreshRate,Action<double,double>? observeTimings=null,bool useDxgi=true)
    {
        this.target=target;this.observeTimings=observeTimings;
        previousDpi=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if(previousDpi==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot establish physical-pixel video coordinates.");
        try
        {
            encoder=new(target.Width,target.Height,refreshRate);
            if(useDxgi)
            {
                try{gpuCapture=new(target.SourceName,target.X,target.Y,target.Width,target.Height);}
                catch(NotSupportedException) when(refreshRate==60){capture=new(target.Width,target.Height);}
                catch(NotSupportedException ex){throw new InvalidOperationException("This Windows display cannot provide the selected high refresh rate. Choose 60 Hz or use a compatible display driver.",ex);}
                if(gpuCapture is not null)cursorOverlay=new(target.Width,target.Height);
            }
            else capture=new(target.Width,target.Height);
        }
        catch{cursorOverlay?.Dispose();gpuCapture?.Dispose();capture?.Dispose();encoder?.Dispose();SetThreadDpiAwarenessContext(previousDpi);throw;}
    }
    public string EncoderName=>encoder.Name;
    public bool HardwareAccelerated=>encoder.HardwareAccelerated;
    public EncodedVideoFrame? Next(long timestamp100ns,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var captureStarted=observeTimings is null?0:Stopwatch.GetTimestamp();
        var bgra=gpuCapture is not null?gpuCapture.Capture(cancellation):capture!.Capture(target.X,target.Y);
        if(bgra is null)return null;
        CapturedFrames++;if(gpuCapture?.WasUpdated==true)FreshDesktopFrames++;
        if(gpuCapture?.SeparateCursorVisible==true)cursorOverlay!.Overlay(bgra,target.Width*4,target.X,target.Y);
        var encodeStarted=observeTimings is null?0:Stopwatch.GetTimestamp();
        cancellation.ThrowIfCancellationRequested();
        if(lastEncodedTimestamp>=0&&timestamp100ns-lastEncodedTimestamp>=5_000_000)encoder.RequestKeyFrame();
        var result=encoder.Encode(bgra,target.Width*4,timestamp100ns);
        lastEncodedTimestamp=timestamp100ns;
        observeTimings?.Invoke(Stopwatch.GetElapsedTime(captureStarted,encodeStarted).TotalMilliseconds,Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds);
        return result;
    }
    public void Dispose(){try{cursorOverlay?.Dispose();gpuCapture?.Dispose();capture?.Dispose();encoder.Dispose();}finally{if(previousDpi!=IntPtr.Zero)SetThreadDpiAwarenessContext(previousDpi);}}
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}

internal sealed class VideoPacer : IDisposable
{
    private readonly EventWaitHandle timer=new(false,EventResetMode.AutoReset);
    internal VideoPacer()
    {
        var handle=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1f0003);
        if(handle.IsInvalid){var error=Marshal.GetLastWin32Error();handle.Dispose();timer.Dispose();throw new Win32Exception(error,"High-resolution video pacing is unavailable on this Windows version.");}
        var previous=timer.SafeWaitHandle;timer.SafeWaitHandle=handle;previous.Dispose();
    }
    internal void Wait(Stopwatch clock,double deadlineTicks,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var remaining=deadlineTicks-clock.ElapsedTicks;if(remaining<=0)return;
        var due=-(long)Math.Max(1,Math.Ceiling(remaining*10000000.0/Stopwatch.Frequency));
        if(!SetWaitableTimer(timer.SafeWaitHandle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(WaitHandle.WaitAny([cancellation.WaitHandle,timer])==0)cancellation.ThrowIfCancellationRequested();
    }
    public void Dispose()=>timer.Dispose();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long dueTime,int period,IntPtr completion,IntPtr argument,[MarshalAs(UnmanagedType.Bool)]bool resume);
}
