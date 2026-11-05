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
        if(args.Length>0){await NativeCaptureProbe.Run(args);return;}
        await ExtendedDisplayTests.Run();
        using(var scene=new Scene())
        {
            foreach(var rate in new[]{0,30,59,241,int.MaxValue})Reject(()=>scene.Stream.Start(new(rate),"owner"),"unsupported target rate rejected: "+rate);
            Check(scene.FactoryCalls==0&&scene.Producer.Calls==0,"invalid start cannot construct an encoder or capture pixels");
            var lease=scene.Start();
            Check(lease.Lease.Length==48&&lease.Lease.All(char.IsAsciiHexDigit),"server creates a bounded unpredictable lease");
            Check(lease.Width==1280&&lease.Height==800&&lease.RefreshRate==60&&lease.SourceRefreshRate==120&&lease.Codec=="h264","begin distinguishes stream target from actual source refresh");
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
        await PairingRevokedDuringCapture();
        await IdleHeartbeats();
        await RevokeIdleHeartbeats();
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
        internal DisplayVideoTarget Target=new(Guid.NewGuid(),1,9,9,"9:0",@"\\.\DISPLAY9",3840,0,1280,800,120,CancellationToken.None);
        internal readonly FakeProducer Producer=new();
        internal readonly DisplayStream Stream;
        internal Scene()
        {
            Stream=new(()=>{if(!Shared)throw new InvalidOperationException("Fixture sharing is off.");return Target;},target=>{
                if(!Shared||target!=Target||target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("Fixture display authority changed.");
            },(target,rate)=>{Interlocked.Increment(ref FactoryCalls);return Producer;},()=>Clock);
        }
        internal DisplayVideoLease Start()=>Stream.Start(new(60),"owner");
        public void Dispose()=>Stream.Dispose();
    }
    private sealed class FakeProducer : IDisplayVideoProducer
    {
        internal int Calls,DisposeCalls;
        internal long LastTimestamp;
        internal bool ReturnNull;
        internal Action? DuringNext;
        public string EncoderName=>"Isolated fake encoder";
        public bool HardwareAccelerated=>false;
        public EncodedVideoFrame? Next(long timestamp100ns,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);LastTimestamp=timestamp100ns;DuringNext?.Invoke();
            return ReturnNull?null:new(AccessUnit,true,timestamp100ns);
        }
        public void Dispose()=>Interlocked.Increment(ref DisposeCalls);
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
