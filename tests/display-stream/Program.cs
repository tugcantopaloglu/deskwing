using System.Buffers.Binary;
using System.Diagnostics;

namespace TabletDeck;

// No native encoder, monitor creation, screen capture, or network connection is used here.
internal static class Program
{
    private static int checks;
    private static readonly byte[] AccessUnit=[0,0,0,1,0x67,0x42,0,0x1f,0,0,0,1,0x68,0xce,0x3c,0x80,0,0,0,1,0x65,0x88,0x84];
    private static void Check(bool condition,string detail)
    {
        if(!condition)throw new InvalidOperationException("Display stream test: "+detail);
        checks++;
    }
    private static async Task Reject(Func<Task> action,string detail)
    {
        var refused=false;
        try{await action();}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or OperationCanceledException or ObjectDisposedException){refused=true;}
        Check(refused,detail);
    }
    private static void Reject(Action action,string detail)=>Reject(()=>{action();return Task.CompletedTask;},detail).GetAwaiter().GetResult();
    private static async Task Ended(Task stream)
    {
        try{await stream.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception ex) when(ex is OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException){}
    }
    private static async Task Until(Func<bool> condition,string detail)
    {
        var elapsed=Stopwatch.StartNew();
        while(!condition()&&elapsed.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(5);
        Check(condition(),detail);
    }
    private static void TryStop(DisplayStream stream,string lease,string owner)
    {
        try{stream.Stop(lease,owner);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){}
    }

    private static async Task Main(string[] args)
    {
        if(args.SequenceEqual(new[]{"--native-capture"})){await NativeCaptureProbe.Run();return;}
        if(args.Length>0)throw new ArgumentException("The only optional mode is --native-capture; executable paths are not accepted.");
        checks+=QualityChecks.Run();
        await ExtendedDisplayTests.Run();
        using(var scene=new Scene())
        {
            foreach(var rate in new[]{0,30,59,241,int.MaxValue})Reject(()=>scene.Stream.Start(new(rate),"owner"),"unsupported target rate rejected: "+rate);
            Reject(()=>scene.Stream.Start(new(60,"unknown"),"owner"),"unsupported quality preset rejected before opening capture");
            Check(scene.FactoryCalls==0&&scene.Producer.Calls==0,"invalid start cannot construct an encoder or capture pixels");
            var lease=scene.Start();
            Check(lease.Lease.Length==48&&lease.Lease.All(char.IsAsciiHexDigit),"server creates a bounded unpredictable lease");
            Check(lease.Width==1280&&lease.Height==800&&lease.RefreshRate==60&&lease.SourceRefreshRate==120&&lease.Codec=="h264","begin distinguishes stream target from actual source refresh");
            Check(lease.QualityPreset=="balanced"&&lease.AdaptiveQuality&&lease.InitialBitrate>=1_000_000,"legacy starts receive a bounded default quality profile");
            Check(scene.Stream.Status("other").Active==false&&scene.Stream.Status("owner").Active&&scene.Stream.Status("admin",true).Active,"quality status is limited to the owner or PC admin");
            Reject(()=>scene.Stream.CaptureInputTarget("owner",lease.Lease),"input cannot use an unclaimed video lease");
            Reject(()=>scene.Stream.Start(new(60),"other"),"second owner cannot replace a live stream lease");
            Reject(()=>scene.Stream.Start(new(60),"owner"),"same owner cannot queue a second stream lease");
            using var output=new MemoryStream();
            await Reject(()=>scene.Stream.StreamAsync(lease.Lease,"other",output,()=>true,CancellationToken.None),"another paired owner cannot attach to a lease");
            Check(output.Length==0&&scene.FactoryCalls==0,"unauthorized attach writes no header and starts no producer");
            TryStop(scene.Stream,lease.Lease,"other");
            await Reject(()=>scene.Stream.StreamAsync("invalid","owner",output,()=>true,CancellationToken.None),"malformed lease cannot attach");
            scene.Stream.Stop(lease.Lease,"owner");
            await Reject(()=>scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None),"explicit stop invalidates an unclaimed lease");
        }
        using(var scene=new Scene())
        {
            Reject(()=>scene.Stream.Start(new(144),"owner"),"source at120Hz cannot be labeled as144Hz video");
            scene.Target=scene.Target with{RefreshRate=59.94};
            var lease=scene.Start();
            Check(lease.RefreshRate==60&&lease.SourceRefreshRate==59.94,"59.94Hz source remains accurately reported for nominal60Hz selection");
        }
        using(var scene=new Scene())
        {
            var old=scene.Start();scene.Clock+=6000;
            using var output=new MemoryStream();
            await Reject(()=>scene.Stream.StreamAsync(old.Lease,"owner",output,()=>true,CancellationToken.None),"unclaimed lease expires before a later stream attach");
            var current=scene.Start();
            Check(current.Lease!=old.Lease,"expired lease cannot be reused by a new stream");
            TryStop(scene.Stream,old.Lease,"owner");
            await CaptureOne(scene,current);
            Check(scene.Producer.Calls>0,"late stop for old lease cannot stop the replacement stream");
        }
        foreach(var reason in new[]{"revoke","sharing","topology","dispose","shutdown"})
        {
            using var scene=new Scene();var lease=scene.Start();
            if(reason=="revoke")scene.Stream.Revoke("owner");
            if(reason=="sharing")scene.Shared=false;
            if(reason=="topology")scene.Target=scene.Target with{X=scene.Target.X+1,Generation=scene.Target.Generation+1};
            if(reason=="dispose")scene.Stream.Dispose();
            if(reason=="shutdown")scene.Stream.Shutdown();
            using var output=new MemoryStream();
            await Reject(()=>scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None),reason+" invalidates pending capture authority");
            Check(output.Length==0&&scene.FactoryCalls==0,reason+" prevents initial video output");
        }
        using(var scene=new Scene())
        {
            scene.Stream.Shutdown();Reject(()=>scene.Start(),"shutdown permanently closes new stream admission");
        }
        using(var scene=new Scene())
        {
            var lease=scene.Start();var bytes=await CaptureOne(scene,lease);
            Check(bytes.AsSpan(0,4).SequenceEqual("DWV1"u8),"wire preamble has the expected magic");
            Check(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4,2))==1&&BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6,2))==32,"wire header version and length are explicit");
            Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8,4))==1280&&BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12,4))==800,"wire dimensions use big-endian encoding");
            Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4))==60&&BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4))==1,"wire carries requested rate without claiming measured delivery");
            Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24,4))==2*1024*1024,"wire declares the decoder access-unit bound");
            Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32,4))==AccessUnit.Length,"frame header carries the bounded payload length");
            Check((BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(36,4))&1)==1,"frame header marks the actual keyframe");
            Check(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(40,8))==(ulong)(scene.Producer.LastTimestamp/10),"wire timestamp converts encoder100ns units to microseconds");
            Check(bytes.AsSpan(48,AccessUnit.Length).SequenceEqual(AccessUnit),"SPS/PPS and complete AnnexB access unit remain intact");
            Check(scene.Producer.DisposeCalls==1,"stream completion disposes the producer exactly once");
        }
        using(var scene=new Scene())
        {
            var lease=scene.Start();using var output=new MemoryStream();
            await Reject(()=>scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>false,CancellationToken.None),"revoked pairing is checked before headers");
            Check(output.Length==0&&scene.FactoryCalls==0,"failed ongoing authorization cannot initialize capture");
        }
        await StopDuringCapture();
        await BackpressureIsBounded();
        await PendingRawWriteRetainsSlot();
        await PendingWriteTimeoutRevokesInputAuthority();
        await NativeCleanupDoesNotRetainAuthority();
        await PairingRevokedDuringCapture();
        await IdleHeartbeats();
        await RevokeIdleHeartbeats();
        await CancelProducerInitialization();
        await QualityTelemetry();
        await FirstFrameFallbackTelemetry();
        Console.WriteLine($"Display stream: {checks} fake authority, framing, refresh and backpressure checks passed. No real capture or driver writes.");
    }

    private static async Task<byte[]> CaptureOne(Scene scene,DisplayVideoLease lease)
    {
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output=new AutoStopStream(cancellation,32+16+AccessUnit.Length);
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,cancellation.Token);
        await Ended(stream);
        Check(output.Length>=32+16+AccessUnit.Length,"an authorized stream produces a complete access unit");
        return output.ToArray();
    }
    private static async Task QualityTelemetry()
    {
        using var scene=new Scene();
        scene.Producer.DuringNext=()=>Thread.Sleep(15);
        var lease=scene.Stream.Start(new(120,"battery-saver",true),"owner");
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output=new MemoryStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,cancellation.Token);
        try
        {
            await Until(()=>scene.Stream.Status("owner").EncodedFrames>2,"quality telemetry follows actual produced pictures");
            Check(scene.LastQuality?.Preset=="battery-saver"&&scene.LastQuality.InitialBitrate==lease.InitialBitrate,"selected profile reaches the producer factory");
            Check(scene.Stream.CaptureInputTarget("owner",lease.Lease)==scene.Target,"input uses the current attached video target snapshot");
            Reject(()=>scene.Stream.CaptureInputTarget("other",lease.Lease),"another paired device cannot obtain the current input target");
            Reject(()=>scene.Stream.CaptureInputTarget("owner","wrong"),"input cannot attach using a different video lease");
            await Until(()=>scene.Stream.Status("owner").CaptureRate<120,"measured processing pressure adapts the live capture cadence");
            var status=scene.Stream.Status("owner");
            Check(status.TargetRefreshRate==120&&status.CaptureRate>=60&&status.Bitrate==scene.Producer.AppliedBitrate&&status.AdaptiveBitrateSupported==true,"telemetry distinguishes the requested rate, adaptive cadence and actually applied bitrate");
            Check(!scene.Stream.Status("other").Active&&scene.Stream.Status("admin",true).EncodedFrames>0,"live counters stay private to the owner and local admin");
            Check(output.ToArray().AsSpan(16,4).SequenceEqual(new byte[]{0,0,0,120}),"adaptive pacing retains the explicitly requested header rate");
        }
        finally {scene.Stream.Stop(lease.Lease,"owner");await Ended(stream);}
        Check(!scene.Stream.Status("owner").Active,"completed sessions no longer expose active quality state");
        Reject(()=>scene.Stream.CaptureInputTarget("owner",lease.Lease),"stopped session loses its input authority");
    }
    private static async Task CancelProducerInitialization()
    {
        using var scene=new Scene();using var entered=new ManualResetEventSlim();
        scene.DuringCreate=quality=>{
            Check(quality.InitializationCancellation.CanBeCanceled,"producer initialization receives the owning session cancellation");
            entered.Set();
            if(!quality.InitializationCancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))throw new Exception("Fixture initialization was not cancelled.");
            quality.InitializationCancellation.ThrowIfCancellationRequested();
        };
        var lease=scene.Start();using var output=new MemoryStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None);
        Check(entered.Wait(TimeSpan.FromSeconds(2)),"producer initialization entered");
        scene.Stream.Revoke("owner");await Ended(stream);
        Check(output.Length==0&&scene.Producer.Calls==0,"revocation interrupts initialization before any header or pixels are produced");
    }
    private static async Task FirstFrameFallbackTelemetry()
    {
        using var scene=new Scene();
        scene.Producer.ActiveEncoderName="Fixture hardware encoder";scene.Producer.ActiveHardwareAcceleration=true;
        scene.Producer.DuringNext=()=>{
            scene.Producer.ActiveEncoderName="Fixture software encoder";
            scene.Producer.ActiveHardwareAcceleration=false;
            scene.Producer.ActiveFallback="Fixture hardware rejected the first frame; software is active.";
        };
        var lease=scene.Start();using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output=new MemoryStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,cancellation.Token);
        try
        {
            await Until(()=>scene.Stream.Status("owner").EncodedFrames>0,"fallback fixture publishes an actual first frame");
            var status=scene.Stream.Status("owner");
            Check(status.Encoder=="Fixture software encoder"&&!status.HardwareAccelerated,"first-frame fallback replaces the provisional hardware encoder status");
            Check(status.EncoderFallback==scene.Producer.ActiveFallback,"status exposes the current software fallback reason after encoding begins");
        }
        finally {scene.Stream.Stop(lease.Lease,"owner");await Ended(stream);}
    }
    private static async Task StopDuringCapture()
    {
        using var scene=new Scene();var lease=scene.Start();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        scene.Producer.DuringNext=()=>{entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new InvalidOperationException("Fixture release timed out.");};
        using var output=new MemoryStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None);
        try
        {
            await Until(()=>entered.IsSet,"capture worker entered the isolated fake producer");
            var stop=Task.Run(()=>scene.Stream.Stop(lease.Lease,"owner"));
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Check(stop.IsCompletedSuccessfully,"Stop remains responsive while capture is blocked");
        }
        finally{release.Set();}
        await Ended(stream);
        Check(output.Length<=32,"a frame completing after Stop is discarded before transmission");
        Check(scene.Producer.DisposeCalls==1,"stopped in-flight capture disposes once");
    }
    private static async Task PairingRevokedDuringCapture()
    {
        using var scene=new Scene();var lease=scene.Start();var approved=true;
        scene.Producer.DuringNext=()=>approved=false;
        using var output=new MemoryStream();
        await Ended(scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>approved,CancellationToken.None));
        Check(output.Length<=32,"pairing revocation during encode prevents the resulting frame from being written");
    }
    private static async Task BackpressureIsBounded()
    {
        using var scene=new Scene();var lease=scene.Start();using var output=new BlockedOutputStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None);
        try
        {
            await Until(()=>output.Blocked,"network fixture applies backpressure after the stream header");
            var produced=scene.Producer.Calls;
            await Task.Delay(90);
            Check(scene.Producer.Calls==produced,"a blocked network write cannot build an encoded frame queue");
            using var duplicate=new MemoryStream();
            await Reject(()=>scene.Stream.StreamAsync(lease.Lease,"owner",duplicate,()=>true,CancellationToken.None),"one lease cannot attach a second transport");
            Check(duplicate.Length==0,"duplicate transport receives no stream header");
        }
        finally{scene.Stream.Stop(lease.Lease,"owner");output.Release();}
        await Ended(stream);
        Check(scene.Producer.DisposeCalls==1,"backpressure cancellation closes the sole producer");
    }
    private static async Task PendingRawWriteRetainsSlot()
    {
        using var scene=new Scene();var lease=scene.Start();using var output=new UncooperativeOutputStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None);
        try
        {
            await Until(()=>output.Blocked,"noncooperative network fixture has one pending raw write");
            scene.Stream.Stop(lease.Lease,"owner");
            await Ended(stream);
            Reject(()=>scene.Start(),"a canceled stream cannot create another worker while its raw network write remains pending");
        }
        finally{output.Release();}
        await Until(()=>scene.Producer.DisposeCalls==1,"pending network cancellation eventually disposes its sole producer");
    }
    private static async Task PendingWriteTimeoutRevokesInputAuthority()
    {
        using var scene=new Scene();var lease=scene.Start();using var output=new UncooperativeOutputStream();
        var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,CancellationToken.None);
        try
        {
            await Until(()=>output.Blocked,"timeout fixture retains one noncooperative raw write");
            try{await stream.WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("The blocked observer did not time out.");}
            catch(TimeoutException) when(stream.IsCompleted){}
            Check(stream.IsFaulted,"write deadline ends the HTTP observer independently of raw-write completion");
            Check(!scene.Stream.Status("owner").Active,"timed-out video immediately retires its capture and input authority");
            Reject(()=>scene.Stream.CaptureInputTarget("owner",lease.Lease),"old video lease cannot authorize input while its failed raw write is retained");
            Reject(()=>scene.Start(),"timeout still reserves the sole worker until the raw write settles");
        }
        finally{scene.Stream.Stop(lease.Lease,"owner");output.Release();}
        await Until(()=>scene.Producer.DisposeCalls==1,"timed-out raw write eventually releases its producer");
    }
    private static async Task NativeCleanupDoesNotRetainAuthority()
    {
        foreach(var reason in new[]{"capture failure","request cancellation","write timeout","write timeout with cleanup failure"})
        {
            using var scene=new Scene();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            using var requestCancellation=new CancellationTokenSource();
            scene.Producer.DuringDispose=()=>{entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new Exception("Fixture native cleanup was not released.");if(reason.EndsWith("cleanup failure",StringComparison.Ordinal))throw new InvalidOperationException("Fixture native cleanup failed.");};
            if(reason=="capture failure")scene.Producer.DuringNext=()=>throw new InvalidOperationException("Fixture producer failed.");
            var raw=reason.StartsWith("write timeout",StringComparison.Ordinal)?new UncooperativeOutputStream():null;
            using Stream output=raw is not null?raw:reason=="request cancellation"?new CallbackStream(length=>{if(length>=48)requestCancellation.Cancel();}):new MemoryStream();
            var lease=scene.Start();
            var stream=scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,requestCancellation.Token);
            try
            {
                await Until(()=>entered.IsSet,reason+" reaches blocked native cleanup");
                Check(stream.IsCompleted,reason+" ends the HTTP observer before native cleanup settles");
                Check(!scene.Stream.Status("owner").Active,reason+" retires video authority before native cleanup settles");
                Reject(()=>scene.Stream.CaptureInputTarget("owner",lease.Lease),reason+" cannot authorize input while native cleanup is blocked");
                Reject(()=>scene.Start(),reason+" retains the worker slot during native cleanup");
                release.Set();
                if(raw is not null)Reject(()=>scene.Start(),"pending raw write retains the slot after native cleanup is released");
            }
            finally{release.Set();raw?.Release();}
            try{await stream;}catch(Exception ex) when(ex is InvalidOperationException or OperationCanceledException or TimeoutException){}
            DisplayVideoLease? next=null;
            await Until(()=>{if(next is not null)return true;try{next=scene.Start();return true;}catch(InvalidOperationException){return false;}},reason+" eventually releases its worker after cleanup and pending writes settle");
            scene.Stream.Stop(next!.Lease,"owner");
        }
    }
    private static async Task IdleHeartbeats()
    {
        const ulong timestamp=987654321;
        var header=DisplayStream.HeartbeatHeader(timestamp);
        Check(header.Length==16&&BinaryPrimitives.ReadUInt32BigEndian(header)==0,"heartbeat is a header-only unit with no encoded payload");
        Check(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4))==8&&BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8))==timestamp,"heartbeat flag and monotonic-clock timestamp use the agreed wire format");
        using var scene=new Scene();scene.Producer.ReturnNull=true;var lease=scene.Start();
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output=new AutoStopStream(cancellation,32+2*16);
        await Ended(scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>true,cancellation.Token));
        var bytes=output.ToArray();
        Check(bytes.Length==64,"a static source sends only the stream header and two heartbeat headers");
        Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(32))==0&&BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(48))==0,"idle capture never invents encoded frames or image payloads");
        Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(36))==8&&BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(52))==8,"idle packets cannot be counted as keyframes or decoder data");
        Check(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(56))>BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(40)),"successive idle heartbeats have increasing capture-clock timestamps");
        Check(scene.Producer.Calls>2&&scene.Producer.DisposeCalls==1,"idle polling retains one producer and disposes it on cancellation");
    }
    private static async Task RevokeIdleHeartbeats()
    {
        foreach(var revoke in new[]{false,true})
        {
            using var scene=new Scene();scene.Producer.ReturnNull=true;var lease=scene.Start();var approved=true;
            using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var output=new CallbackStream(length=>{if(length<48)return;if(revoke)scene.Stream.Revoke("owner");else approved=false;});
            await Ended(scene.Stream.StreamAsync(lease.Lease,"owner",output,()=>approved,cancellation.Token));
            Check(output.Length==48,(revoke?"explicit owner revocation":"pairing authorization loss")+" stops idle heartbeat output immediately after the current unit");
            Check(scene.Producer.DisposeCalls==1,(revoke?"revoked":"unauthorized")+" idle stream closes its sole producer");
        }
    }

    private sealed class Scene : IDisposable
    {
        internal long Clock=1000;
        internal bool Shared=true;
        internal int FactoryCalls;
        internal DisplayVideoQuality? LastQuality;
        internal Action<DisplayVideoQuality>? DuringCreate;
        internal DisplayVideoTarget Target=new(Guid.NewGuid(),1,9,9,"9:0",@"\\.\DISPLAY9",3840,0,1280,800,120,CancellationToken.None);
        internal readonly FakeProducer Producer=new();
        internal readonly DisplayStream Stream;
        internal Scene()
        {
            Stream=new(()=>{if(!Shared)throw new InvalidOperationException("Fixture sharing is off.");return Target;},target=>{
                if(!Shared||target!=Target||target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("Fixture display authority changed.");
            },(target,rate,quality)=>{LastQuality=quality;Interlocked.Increment(ref FactoryCalls);DuringCreate?.Invoke(quality);return Producer;},()=>Clock);
        }
        internal DisplayVideoLease Start()=>Stream.Start(new(60),"owner");
        public void Dispose()=>Stream.Dispose();
    }
    private sealed class FakeProducer : IDisplayVideoProducer
    {
        internal int Calls,DisposeCalls,AppliedBitrate;
        internal long LastTimestamp;
        internal bool ReturnNull;
        internal Action? DuringNext;
        internal Action? DuringDispose;
        internal string ActiveEncoderName="Isolated fake encoder";
        internal bool ActiveHardwareAcceleration;
        internal string? ActiveFallback;
        public string EncoderName=>ActiveEncoderName;
        public bool HardwareAccelerated=>ActiveHardwareAcceleration;
        public string? EncoderFallback=>ActiveFallback;
        public bool TrySetBitrate(int bitrate){AppliedBitrate=bitrate;return true;}
        public EncodedVideoFrame? Next(long timestamp100ns,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);LastTimestamp=timestamp100ns;DuringNext?.Invoke();
            return ReturnNull?null:new(AccessUnit,true,timestamp100ns);
        }
        public void Dispose(){Interlocked.Increment(ref DisposeCalls);DuringDispose?.Invoke();}
    }
    private sealed class AutoStopStream(CancellationTokenSource stop,int limit) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            await base.WriteAsync(buffer,cancellation);if(Length>=limit)stop.Cancel();
        }
    }
    private sealed class CallbackStream(Action<long> afterWrite) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            await base.WriteAsync(buffer,cancellation);afterWrite(Length);
        }
    }
    private sealed class BlockedOutputStream : MemoryStream
    {
        private readonly TaskCompletionSource release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Blocked;
        public void Release()=>release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            if(Length>=32){Blocked=true;await release.Task.WaitAsync(cancellation);}
            await base.WriteAsync(buffer,cancellation);
        }
    }
    private sealed class UncooperativeOutputStream : MemoryStream
    {
        private readonly TaskCompletionSource release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Blocked;
        public void Release()=>release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default)
        {
            if(Length>=32){Blocked=true;await release.Task;}
            // Intentionally emulate a transport that does not honor cancellation. It can finish
            // late, but must not permit additional capture workers to accumulate behind it.
            await base.WriteAsync(buffer,CancellationToken.None);
        }
    }
}
