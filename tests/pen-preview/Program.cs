using TabletDeck;

// Deliberately no NativePenPreviewPlatform or NativeWindowsPenPlatform instances: these fixtures
// never read the desktop, enumerate real applications or synthesize input on the test machine.
await PreviewTests.Run();

internal static class PreviewTests
{
    private static int checks;
    private static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static async Task Reject(Func<Task> work,string label)
    {
        try{await work();}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or OperationCanceledException){Check(true,label);return;}
        throw new Exception("Expected rejection: "+label);
    }
    private static PenRequest Start()=>new("begin",Session:Guid.NewGuid().ToString("N"));
    private static async Task<PenPreviewFrame> RecoveredFrame(Scene scene)
    {
        // A timed-out request deliberately does not own the native worker's completion. Wait for
        // the observable service recovery instead of assuming a hosted thread runs within 30 ms.
        for(var attempt=0;;attempt++){
            try{return await scene.Frame();}
            catch(InvalidOperationException ex) when(attempt<200&&ex.Message.StartsWith("PC preview capture is busy",StringComparison.Ordinal)){await Task.Delay(10);}
        }
    }
    internal static async Task Run()
    {
        Check(PenPreview.Dimensions(new(0,0,1920,1080),1600)==(1600,900),"landscape preview preserves source aspect");
        Check(PenPreview.Dimensions(new(0,0,1080,1920),1600)==(900,1600),"portrait preview caps height while preserving aspect");
        Check(PenPreview.Dimensions(new(0,0,1,800),160)==(1,800),"narrow client area remains valid without an artificial minimum width");
        Check(PenPreview.Dimensions(new(0,0,640,480),1600)==(640,480),"small clients are not enlarged");
        await Reject(()=>Task.FromResult(PenPreview.Dimensions(new(0,0,8192,8192),1600)),"source allocation bounded to16MP");
        await Reject(()=>Task.FromResult(PenPreview.Dimensions(new(0,0,0,100),1600)),"empty target rejected");

        using(var scene=new Scene()){
            scene.Pen.Configure(new(false));
            await Reject(()=>scene.Preview.Frame(new(new string('A',48)),"a"),"disabled permission never captures");
            Check(scene.Capture.Calls==0,"disabled request made no capture call");
        }
        using(var scene=new Scene()){
            await Reject(()=>scene.Preview.Frame(new("bad"),"a"),"malformed lease rejected");
            await Reject(()=>scene.Preview.Frame(new(scene.Lease,159),"a"),"preview width below supported range rejected");
            await Reject(()=>scene.Preview.Frame(new(scene.Lease,1601),"a"),"preview width above supported range rejected");
            await Reject(()=>scene.Preview.Frame(new(scene.Lease),"b"),"foreign paired owner rejected before capture");
            Check(scene.Capture.Calls==0,"validation and foreign owner cannot touch pixel source");
            var frame=await scene.Frame();
            Check(frame.TargetId=="window:fixture"&&frame.TargetWidth==1000&&frame.TargetHeight==500&&frame.Width==1000&&frame.Height==500,"frame binds exact selected client target dimensions");
            Check(frame.Image.StartsWith(PenPreview.ImagePrefix,StringComparison.Ordinal)&&frame.Image.Length<=PenPreview.MaximumImageLength&&frame.Sequence==1,"frame is bounded JPEG dataURL with increasing sequence");
            await Reject(()=>scene.Frame(),"global capture rate ceiling refuses immediate additional frame");
            Check(scene.Capture.Calls==1,"rate limit creates no queued capture");
            scene.Clock=84;frame=await scene.Frame();
            Check(frame.Sequence==2&&scene.Capture.Calls==2,"next interval captures fresh pixels without cache");
        }
        using(var scene=new Scene()){
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            scene.Capture.DuringCapture=()=>{entered.Set();release.Wait();};
            var frame=scene.Frame();Check(entered.Wait(2000),"fake slow capture begins");
            await Reject(()=>scene.Frame(),"a second frame is refused while capture is in flight");
            scene.Pen.Execute(new("samples",Lease:scene.Lease,Sequence:1,Samples:[new("down",.5,.5,.7)]),"a");
            Check(scene.Platform.Injections==1,"pen injection continues while frame capture is blocked");
            scene.Pen.Release("a",scene.Lease);release.Set();
            await Reject(()=>frame,"stopping during capture discards returned pixels");
        }
        foreach(var change in new[]{"resize","focus","owner","replace","expire","disable","dispose"}){
            using var scene=new Scene();
            scene.Capture.DuringCapture=()=>{
                switch(change){
                    case "resize":scene.Platform.Bounds=scene.Platform.Bounds with{Width=999};break;
                    case "focus":scene.Platform.Focused=false;break;
                    case "owner":scene.Pen.ReleaseOwner("a");break;
                    case "replace":scene.Pen.Release("a",scene.Lease);scene.Pen.Execute(Start(),"a");break;
                    case "expire":scene.Clock=1200;break;
                    case "disable":scene.Pen.Configure(new(false));break;
                    case "dispose":scene.Pen.Dispose();break;
                }
            };
            await Reject(()=>scene.Frame(),change+" during capture cannot publish stale pixels");
        }
        using(var scene=new Scene()){
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var cancel=new CancellationTokenSource();
            scene.Capture.DuringCapture=()=>{entered.Set();release.Wait();};
            var frame=scene.Preview.Frame(new(scene.Lease),"a",cancel.Token);Check(entered.Wait(2000),"cancellable fake capture begins");cancel.Cancel();
            await Reject(()=>frame,"request cancellation ends response without delivering pixels");
            await Reject(()=>scene.Frame(),"cancellation retains native capture slot until worker exits");
            scene.Capture.DuringCapture=null;scene.Clock=84;release.Set();
            Check((await RecoveredFrame(scene)).Sequence==1,"cancelled worker drains before a new frame is captured");
        }
        using(var scene=new Scene(50)){
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            scene.Capture.DuringCapture=()=>{entered.Set();release.Wait();};
            var frame=scene.Frame();Check(entered.Wait(2000),"hung target fixture begins");
            await Reject(()=>frame,"unresponsive capture has bounded response timeout");scene.Clock=84;
            await Reject(()=>scene.Frame(),"timeout never launches another native capture behind hung worker");
            Check(scene.Capture.Calls==1&&scene.Pen.Active,"timeout preserves input lease and bounds worker count");
            scene.Pen.Execute(new("samples",Lease:scene.Lease,Sequence:1,ElapsedMs:84,Samples:[new("down",.5,.5,.7)]),"a");
            Check(scene.Platform.Injections==1,"input remains independent after preview timeout");
            scene.Capture.DuringCapture=null;release.Set();
            Check((await RecoveredFrame(scene)).Sequence==1,"late timed-out frame is discarded and fresh capture recovers");
        }
        foreach(var bad in new[]{"size","jpeg","limit"}){
            using var scene=new Scene();scene.Capture.Invalid=bad;
            await Reject(()=>scene.Frame(),"invalid "+bad+" capture cannot reach client");
        }
        Console.WriteLine($"Pen preview passed: {checks} checks; fake captures and input only.");
    }

    private sealed class Scene : IDisposable
    {
        internal long Clock;
        internal readonly FakePen Platform=new();
        internal readonly FakeCapture Capture=new();
        internal readonly WindowsPen Pen;
        internal readonly PenPreview Preview;
        internal readonly string Lease;
        internal Scene(int timeout=2000){Pen=new(Platform,()=>Clock);Pen.Configure(new(true,"window:fixture"));Lease=((WindowsPen.PenLease)Pen.Execute(Start(),"a")).Lease;Preview=new(Pen,Capture,()=>Clock,timeout);}
        internal Task<PenPreviewFrame> Frame()=>Preview.Frame(new(Lease),"a");
        public void Dispose()=>Pen.Dispose();
    }
    private sealed class FakePen : IWindowsPenPlatform
    {
        public bool Supported=>true;
        internal bool Focused=true;
        internal PenRectangle Bounds=new(20,30,1000,500);
        internal int Injections;
        public PenDestination[] Targets()=>[new(new("window:fixture","Isolated test target","window",Bounds.Width,Bounds.Height),Bounds,41,3)];
        public PenDestination Resolve(PenDestination target,bool requireForeground){if(requireForeground&&!Focused)throw new InvalidOperationException("Fake target lost focus.");return Targets()[0];}
        public void Begin(){}
        public void Inject(PenFrame frame){Injections++;}
        public void Reset(){}
        public void Dispose(){}
    }
    private sealed class FakeCapture : IPenPreviewPlatform
    {
        internal Action? DuringCapture;
        internal int Calls;
        internal string Invalid="";
        public PenCapturedImage Capture(PenDestination destination,int requestedWidth,CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);DuringCapture?.Invoke();
            var size=PenPreview.Dimensions(destination.Bounds,requestedWidth);
            return new(Invalid=="size"?size.Width+1:size.Width,size.Height,Invalid switch{"jpeg"=>[0,0,0,0],"limit"=>new byte[PenPreview.MaximumJpegBytes+1],_=>[255,216,255,217]});
        }
    }
}
