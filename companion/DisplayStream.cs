using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TabletDeck;

internal sealed record DisplayVideoStart(int RefreshRate=60,string QualityPreset="balanced",bool AdaptiveQuality=true);
internal sealed record DisplayVideoLease(string Lease,int Width,int Height,int RefreshRate,double SourceRefreshRate,string Codec="h264",string QualityPreset="balanced",bool AdaptiveQuality=true,int InitialBitrate=0);
internal sealed record DisplayVideoStatus(bool Active,string QualityPreset="",bool AdaptiveQuality=false,int TargetRefreshRate=0,int CaptureRate=0,int Bitrate=0,string Encoder="",bool HardwareAccelerated=false,string? EncoderFallback=null,bool? AdaptiveBitrateSupported=null,long EncodedFrames=0,long EncodedBytes=0,double ProcessingMs=0,double WritingMs=0,string Detail="");

internal interface IDisplayVideoProducer : IDisposable
{
    EncodedVideoFrame? Next(long timestamp100ns,CancellationToken cancellation);
    string EncoderName {get;}
    bool HardwareAccelerated {get;}
    string? EncoderFallback=>null;
    bool TrySetBitrate(int bitrate)=>false;
}

// A single native video session is separate from pen and control transports. The encoder and GDI
// objects remain on one dedicated worker thread; a slow consumer cannot create an encoder queue.
internal sealed class DisplayStream : IDisposable
{
    internal const int MaximumAccessUnitBytes=2*1024*1024,UnclaimedLeaseMs=5000,WriteTimeoutMs=2000;
    private readonly object gate=new();
    private readonly Func<DisplayVideoTarget> captureTarget;
    private readonly Action<DisplayVideoTarget> validateTarget;
    private readonly Func<DisplayVideoTarget,int,DisplayVideoQuality,IDisplayVideoProducer> createProducer;
    private readonly Func<long> now;
    private VideoSession? session;
    private bool shutdown,workerRunning;
    private long generation;

    private sealed class VideoSession(string lease,string owner,long generation,long expires,DisplayVideoTarget target,int rate,DisplayVideoQuality quality)
    {
        internal readonly string Lease=lease,Owner=owner;
        internal readonly long Generation=generation,Expires=expires;
        internal readonly DisplayVideoTarget Target=target;
        internal readonly int Rate=rate;
        internal readonly DisplayVideoQuality Quality=quality;
        internal readonly CancellationTokenSource Cancellation=new();
        internal bool Attached;
        internal DisplayVideoStatus Status=new(true,quality.Preset,quality.Adaptive,rate,rate,quality.InitialBitrate,Detail:"Opening video");
    }

    internal DisplayStream(ExtendedDisplay display):this(display.CaptureVideoTarget,display.ValidateVideoTarget,(target,rate,quality)=>new NativeDisplayVideoProducer(target,rate,quality:quality)){}
    internal DisplayStream(Func<DisplayVideoTarget> captureTarget,Action<DisplayVideoTarget> validateTarget,Func<DisplayVideoTarget,int,IDisplayVideoProducer> producerFactory,Func<long>? nowMilliseconds=null)
        :this(captureTarget,validateTarget,(target,rate,_)=>producerFactory(target,rate),nowMilliseconds){}
    internal DisplayStream(Func<DisplayVideoTarget> captureTarget,Action<DisplayVideoTarget> validateTarget,Func<DisplayVideoTarget,int,DisplayVideoQuality,IDisplayVideoProducer> producerFactory,Func<long>? nowMilliseconds=null)
    {this.captureTarget=captureTarget;this.validateTarget=validateTarget;createProducer=producerFactory;now=nowMilliseconds??(()=>Environment.TickCount64);}

    internal DisplayVideoLease Start(DisplayVideoStart request,string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.RefreshRate is <60 or >240)throw new ArgumentException("Choose a video refresh rate from 60 to 240 Hz.");
        if(request.QualityPreset is not ("balanced" or "quality" or "battery-saver"))throw new ArgumentException("Choose Balanced, Quality, or Battery saver video.");
        if(string.IsNullOrEmpty(principal))throw new ArgumentException("A paired tablet is required for video.");
        lock(gate)
        {
            if(shutdown)throw new InvalidOperationException("DeskWing is closing. Open the display again after reconnecting.");
            ExpireUnclaimed();
            if(session is not null||workerRunning)throw new InvalidOperationException("Another display stream is active or still closing. Close it before opening a new one.");
            var target=captureTarget();
            if(target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("Second display sharing changed. Refresh the PC setup.");
            if((target.Width,target.Height) is not ((1280,800) or (1600,900) or (1920,1080)))throw new InvalidOperationException("The Second display size is unsupported for video.");
            var quality=DisplayVideoQuality.Create(request.QualityPreset,request.AdaptiveQuality,target.Width,target.Height,request.RefreshRate);
            // Windows commonly reports 59.94 for a nominal 60 Hz mode; do not round a genuine
            // lower-rate source (for example 60 Hz requested as 120 Hz) into a false capability.
            if(!double.IsFinite(target.RefreshRate)||target.RefreshRate+0.1<request.RefreshRate)
                throw new InvalidOperationException($"The Windows display is running at {target.RefreshRate:0.##} Hz. Recreate Second display on the PC at {request.RefreshRate} Hz or higher before using this tablet setting.");
            var lease=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            session=new(lease,principal,++generation,now()+UnclaimedLeaseMs,target,request.RefreshRate,quality);
            return new(lease,target.Width,target.Height,request.RefreshRate,target.RefreshRate,QualityPreset:quality.Preset,AdaptiveQuality:quality.Adaptive,InitialBitrate:quality.InitialBitrate);
        }
    }

    internal void Stop(string lease,string principal)
    {
        lock(gate){if(session is not null&&session.Lease==lease&&session.Owner==principal)CancelSession();}
    }
    internal void Revoke(string principal){lock(gate){if(session?.Owner==principal)CancelSession();}}
    internal DisplayVideoStatus Status(string principal,bool administrator=false)
    {
        lock(gate){ExpireUnclaimed();return session is not null&&(administrator||session.Owner==principal)?session.Status:new(false);}
    }
    internal DisplayVideoTarget CaptureInputTarget(string principal,string lease)
    {
        VideoSession current;
        lock(gate)
        {
            if(shutdown||session is null||!session.Attached||!workerRunning||session.Owner!=principal||session.Lease!=lease||session.Cancellation.IsCancellationRequested)
                throw new InvalidOperationException("Start this tablet's current Second display before enabling input.");
            current=session;
        }
        validateTarget(current.Target);current.Target.Invalidated.ThrowIfCancellationRequested();
        lock(gate)
        {
            if(shutdown||!ReferenceEquals(session,current)||session.Generation!=current.Generation||current.Cancellation.IsCancellationRequested)
                throw new InvalidOperationException("The display session changed. Start input again.");
            return current.Target;
        }
    }
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
        CancellationTokenSource? cancellation=null;
        IDisplayVideoProducer? producer=null;
        Task? pendingWrite=null;
        try
        {
            cancellation=CancellationTokenSource.CreateLinkedTokenSource(requestCancellation,current.Cancellation.Token,current.Target.Invalidated);
            var ct=cancellation.Token;
            Validate(current,stillAuthorized,ct);
            producer=createProducer(current.Target,current.Rate,current.Quality with{InitializationCancellation=ct});
            Validate(current,stillAuthorized,ct);
            var quality=new DisplayQualityController(current.Quality);
            lock(gate){current.Status=current.Status with{Encoder=producer.EncoderName,HardwareAccelerated=producer.HardwareAccelerated,EncoderFallback=producer.EncoderFallback,Detail=quality.Reason};}
            Write(output,Header(current.Target.Width,current.Target.Height,current.Rate),ct);
            var clock=Stopwatch.StartNew();
            using var pacer=new VideoPacer();
            double next=0;
            long heartbeatAt=0;
            var firstFrame=true;
            while(true)
            {
                pacer.Wait(clock,next,ct);
                Validate(current,stillAuthorized,ct);
                var timestamp=(long)(clock.ElapsedTicks*(10000000.0/Stopwatch.Frequency));
                var processingStarted=Stopwatch.GetTimestamp();
                var frame=producer.Next(timestamp,ct);
                var processingMs=Stopwatch.GetElapsedTime(processingStarted).TotalMilliseconds;
                Validate(current,stillAuthorized,ct);
                // A hardware transform may accept setup but reject its first picture. The
                // producer can then select software before emitting anything; report that
                // actual selection rather than retaining the constructor-time snapshot.
                lock(gate){current.Status=current.Status with{Encoder=producer.EncoderName,HardwareAccelerated=producer.HardwareAccelerated,EncoderFallback=producer.EncoderFallback};}
                if(frame is not null)
                {
                    if(frame.Data.Length is <1 or >MaximumAccessUnitBytes||frame.Timestamp100ns<0)
                        throw new InvalidOperationException("The video encoder returned an invalid frame.");
                    if(firstFrame&&!frame.KeyFrame)throw new InvalidOperationException("The video encoder did not begin with a keyframe.");
                    var packet=PacketHeader(frame.Data.Length,frame.KeyFrame,(ulong)(frame.Timestamp100ns/10));
                    // Each write is bounded and awaits the consumer. There is never a pending
                    // queue of captured or compressed frames behind this worker.
                    var writingStarted=Stopwatch.GetTimestamp();
                    Write(output,packet,ct);
                    Validate(current,stillAuthorized,ct);
                    Write(output,frame.Data,ct);
                    var writingMs=Stopwatch.GetElapsedTime(writingStarted).TotalMilliseconds;
                    var decision=quality.Observe(processingMs,writingMs,clock.ElapsedMilliseconds);
                    bool? applied=null;
                    if(decision.Changed)applied=producer.TrySetBitrate(decision.Bitrate);
                    lock(gate)
                    {
                        current.Status=current.Status with{CaptureRate=decision.CaptureRate,Bitrate=applied==true?decision.Bitrate:current.Status.Bitrate,
                            AdaptiveBitrateSupported=applied??current.Status.AdaptiveBitrateSupported,EncodedFrames=current.Status.EncodedFrames+1,EncodedBytes=current.Status.EncodedBytes+frame.Data.Length,
                            ProcessingMs=Math.Round(processingMs,3),WritingMs=Math.Round(writingMs,3),Detail=decision.Reason};
                    }
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
                var framePeriod=Stopwatch.Frequency/(double)quality.CaptureRate;
                next+=framePeriod;
                // Keep the original cadence through a small overrun; the next capture is always
                // fresh. Only abandon a missed interval once more than one whole period is late.
                if(next<clock.ElapsedTicks-framePeriod)next=clock.ElapsedTicks;
            }
        }
        catch(PendingWriteException ex)
        {
            failure=ex.InnerException??ex;cancelled=failure is OperationCanceledException;
            pendingWrite=ex.Pending;
        }
        catch(OperationCanceledException){cancelled=true;}
        catch(Exception ex) when(ex is not OutOfMemoryException){failure=ex;}
        finally
        {
            // End capture/input authority and the HTTP observer before any native provider
            // cleanup. A slow Dispose must not keep a failed or cancelled video usable for pen
            // input, and must not postpone aborting its binary HTTP response.
            lock(gate){if(ReferenceEquals(session,current))CancelSession();}
            if(cancelled)completion.TrySetCanceled();else if(failure is not null)completion.TrySetException(failure);
            try{producer?.Dispose();}
            catch(Exception cleanup) when(cleanup is not OutOfMemoryException){failure??=cleanup;}
            finally
            {
                // Keep the sole worker occupied until both native cleanup and the one issued
                // raw write settle. Cleanup exceptions cannot lose the pending-write reference.
                try
                {
                    if(pendingWrite is not null)try{pendingWrite.GetAwaiter().GetResult();}catch(Exception late) when(late is not OutOfMemoryException){}
                }
                finally
                {
                    try{cancellation?.Dispose();}
                    finally
                    {
                        lock(gate){workerRunning=false;}
                        current.Cancellation.Dispose();
                    }
                }
            }
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
    internal NativeDisplayVideoProducer(DisplayVideoTarget target,int refreshRate,Action<double,double>? observeTimings=null,bool useDxgi=true,DisplayVideoQuality? quality=null)
    {
        this.target=target;this.observeTimings=observeTimings;
        previousDpi=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if(previousDpi==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot establish physical-pixel video coordinates.");
        try
        {
            encoder=new(target.Width,target.Height,refreshRate,quality?.InitialBitrate??0,initializationCancellation:quality?.InitializationCancellation??default);
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
    public string? EncoderFallback=>encoder.FallbackReason;
    public bool TrySetBitrate(int bitrate)=>encoder.TrySetBitrate(bitrate);
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
