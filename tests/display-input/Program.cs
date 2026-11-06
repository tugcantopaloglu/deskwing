namespace TabletDeck;

// Only the video/monitor providers are stubbed. Production DisplayInput and WindowsPen run against
// a fake native platform, so these checks never capture a screen or inject operating-system input.
internal sealed record DisplayVideoTarget(Guid Monitor,long Generation,ulong Adapter,uint TargetId,string SourceKey,string SourceName,int X,int Y,int Width,int Height,double RefreshRate,CancellationToken Invalidated);
internal sealed class DisplayStream {internal DisplayVideoTarget CaptureInputTarget(string principal,string lease)=>throw new NotSupportedException();}
internal sealed class ExtendedDisplay {internal DisplayVideoTarget CaptureVideoTarget()=>throw new NotSupportedException();}
internal static class Program
{
    private static int checks;
    private static void Check(bool value,string label){if(!value)throw new Exception("Display input: "+label);checks++;}
    private static void Reject(Action action,string label){try{action();throw new Exception("Accepted: "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){checks++;}}
    private static void Main()
    {
        using(var scene=new Scene()){
            Check(!scene.Input.Status.Enabled&&!scene.Input.Active&&scene.Platform.Begins==0,"starts disabled with no synthetic device");
            Reject(()=>scene.Begin(),"a paired video viewer cannot grant input permission");
            Reject(()=>scene.Input.Configure(new(true,"wrong-target")),"PC approval must name the exact displayed target identity");
            Check(scene.Platform.Begins==0,"incorrect approval does not create input");
            scene.Allow();scene.Attached=false;Reject(()=>scene.Begin(),"an unclaimed video lease cannot receive input");
            scene.Attached=true;Reject(()=>scene.Begin("other"),"another paired principal cannot borrow the video lease");
            Reject(()=>scene.Begin(videoLease:new string('B',48)),"a different video lease is rejected");
            Check(scene.Input.Status.Enabled&&!scene.Input.Active,"failed starts do not change PC permission or create input");
            var lease=scene.Begin();
            Check(lease.Lease.Length==48&&lease.ExpiresInMs==1200&&lease.MaxBatch==32&&lease.Target?.Width==1280,"begin returns bounded lease and exact stream geometry");
            Reject(()=>scene.Begin(),"one input owner cannot queue another session");
            scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("hover",0,0),new PenSample("down",1,1,.75,-15,35,true,true)),"owner");
            Check(scene.Platform.Frames.Count==2&&scene.Platform.Frames[0].X==4480&&scene.Platform.Frames[0].Y==-80&&scene.Platform.Frames[1].X==5759&&scene.Platform.Frames[1].Y==719,"normalized corners map only to owned monitor including negative origins");
            Check(scene.Platform.Frames[1].Pressure==768&&scene.Platform.Frames[1].TiltX==-15&&scene.Platform.Frames[1].TiltY==35&&scene.Platform.Frames[1].Barrel&&scene.Platform.Frames[1].Eraser,"pen pressure, tilt, barrel and eraser reach the fake platform");
            Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("move",.5,.5)),"owner"),"replayed input sequence rejected");
            Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,2,new PenSample("move",.5,.5)),"other"),"other owner cannot inject");
            scene.Input.Execute(new("cancel",scene.VideoLease,Lease:"wrong"),"owner");Check(scene.Input.Active,"wrong cancellation lease cannot terminate active input");
            scene.Input.Execute(new("cancel",scene.VideoLease,Lease:lease.Lease),"other");Check(scene.Input.Active,"other owner cannot terminate active input");
            scene.Input.Execute(new("cancel",scene.VideoLease,Lease:lease.Lease),"owner");
            Check(!scene.Input.Active&&scene.Platform.Frames.TakeLast(2).All(frame=>frame.Cancelled)&&scene.Platform.Frames[^2].Phase=="up"&&scene.Platform.Frames[^1].Phase=="leave","explicit cancel releases current contact and removes the device");
            Check(scene.Input.Status.Enabled,"ending input does not reset the exact PC approval");
        }
        using(var scene=new Scene()){
            scene.Allow();var lease=scene.Begin(mode:"touch");
            scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",.3,.2,1,80,-80,true,true),new PenSample("up",.3,.2,1,80,-80,true,true)),"owner");
            Check(scene.Platform.Frames[0].Pressure==512&&scene.Platform.Frames[1].Pressure==0&&scene.Platform.Frames.All(frame=>frame.TiltX==0&&frame.TiltY==0&&!frame.Barrel&&!frame.Eraser),"touch is explicitly normalized to single-finger pen-style taps and drags");
            Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,2,new PenSample("down",.1,.2,double.NaN)),"owner"),"touch cannot hide nonfinite sample fields through normalization");
            Check(scene.Platform.Frames.Count==2,"invalid touch batch emits no partial input");
        }
        using(var scene=new Scene()){
            scene.Allow();Reject(()=>scene.Begin(mode:"mouse"),"unsupported input mode rejected");
            var lease=scene.Begin();scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",.1,.1,.5)),"owner");
            scene.Attached=false;scene.Input.Expire();
            Check(!scene.Input.Active&&!scene.Platform.Device&&scene.Platform.Frames[^2].Cancelled,"video close cancels a held pointer without waiting for lease renewal");
            scene.Attached=true;Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,2,new PenSample("move",.2,.2)),"owner"),"late video-session packets cannot resurrect the pointer");
        }
        using(var scene=new Scene()){
            scene.Allow();var lease=scene.Begin();scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",.1,.1,.5)),"owner");
            scene.Clock=1200;scene.Input.Expire();
            Check(!scene.Input.Active&&!scene.Platform.Device,"short input lease expires and releases contact");
            Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,2,new PenSample("move",.2,.2)),"owner"),"expired packets cannot replay after a later renewal");
        }
        using(var scene=new Scene()){
            scene.Allow();var lease=scene.Begin();scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",0,0,.5)),"owner");
            scene.Input.Revoke("other");Check(scene.Input.Active,"unrelated pairing revocation cannot stop the owner");
            scene.Input.Revoke("owner");Check(!scene.Input.Active&&!scene.Platform.Device,"owner revocation releases native contact immediately");
            scene.Begin();scene.Input.Configure(new(false));Check(!scene.Input.Status.Enabled&&!scene.Input.Active,"PC permission removal cancels active input");
        }
        using(var scene=new Scene()){
            scene.Allow();scene.Target=scene.Target with{Generation=scene.Target.Generation+1};
            scene.Input.Expire();Check(!scene.Input.Status.Enabled,"permission cannot survive monitor generation replacement while idle");
            Reject(()=>scene.Begin(),"a recreated monitor cannot inherit previous input consent");
            scene.Allow();var lease=scene.Begin();scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",1,1,.5)),"owner");
            scene.Invalidation.Cancel();scene.Input.Expire();
            Check(!scene.Input.Status.Enabled&&!scene.Input.Active&&!scene.Platform.Device,"sharing revocation invalidates permission and contact");
        }
        using(var scene=new Scene()){
            scene.Allow();var lease=scene.Begin();scene.Platform.Bounds=scene.Platform.Bounds with{X=0};
            Reject(()=>scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",0,0,.5)),"owner"),"native display geometry cannot silently map to another monitor");
            Check(!scene.Input.Active&&scene.Platform.Frames.Count==0,"geometry change injects no frame");
        }
        using(var scene=new Scene()){
            scene.Allow();var lease=scene.Begin();scene.Input.Execute(scene.Samples(lease.Lease,1,new PenSample("down",.1,.1,.5)),"owner");scene.Input.Shutdown();
            Check(!scene.Input.Active&&!scene.Input.Status.Enabled&&!scene.Platform.Device,"shutdown revokes permission and releases contact");
            Reject(()=>scene.Input.Configure(new(true,scene.Identity)),"shutdown cannot be followed by re-enabling input");
            Reject(()=>scene.Begin(),"shutdown refuses new sessions");
            scene.Input.Execute(new("cancel",scene.VideoLease,Lease:lease.Lease),"owner");Check(!scene.Input.Active,"late safety cleanup is idempotent during shutdown");
        }
        Console.WriteLine($"Display input: {checks} checks passed; fake native input only.");
    }
    private sealed class Scene : IDisposable
    {
        internal readonly FakePlatform Platform=new();
        internal readonly CancellationTokenSource Invalidation=new();
        internal readonly string VideoLease=new('A',48);
        internal DisplayVideoTarget Target;
        internal bool Attached=true;
        internal long Clock;
        internal DisplayInput Input;
        internal string Identity=>Target.Monitor.ToString("N")+":"+Target.Generation;
        internal Scene(){Target=new(Guid.NewGuid(),1,123,9,"owned","TEST",4480,-80,1280,800,120,Invalidation.Token);Input=new((owner,lease)=>{if(!Attached||owner!="owner"||lease!=VideoLease)throw new InvalidOperationException("Video no longer attached.");return Target;},()=>{if(Invalidation.IsCancellationRequested)throw new InvalidOperationException("Sharing off.");return Target;},Platform,()=>Clock);}
        internal void Allow()=>Input.Configure(new(true,Identity));
        internal WindowsPen.PenLease Begin(string owner="owner",string mode="pen",string? videoLease=null)=>(WindowsPen.PenLease)Input.Execute(new("begin",videoLease??VideoLease,Guid.NewGuid().ToString("N"),Mode:mode),owner);
        internal DisplayInputRequest Samples(string lease,long sequence,params PenSample[] samples)=>new("samples",VideoLease,Lease:lease,Sequence:sequence,ElapsedMs:Clock,Samples:samples);
        public void Dispose(){Input.Dispose();Invalidation.Dispose();}
    }
    private sealed class FakePlatform : IWindowsPenPlatform
    {
        public bool Supported=>true;
        internal PenRectangle Bounds=new(4480,-80,1280,800);
        internal bool Device;
        internal int Begins;
        internal List<PenFrame> Frames=new();
        public PenDestination[] Targets()=>[new(new("display:PHYSICAL","Physical monitor","display",1920,1080),new(0,0,1920,1080)),new(new("display:TEST","Owned monitor","display",1280,800),Bounds)];
        public PenDestination Resolve(PenDestination target,bool foreground)=>Targets().First(item=>item.Info.Id==target.Info.Id);
        public void Begin(){Device=true;Begins++;}
        public void Inject(PenFrame frame){if(!Device)throw new InvalidOperationException("No device.");Frames.Add(frame);}
        public void Reset(){Device=false;}
        public void Dispose()=>Reset();
    }
}
