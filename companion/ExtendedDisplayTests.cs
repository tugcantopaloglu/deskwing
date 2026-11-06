using Microsoft.Win32.SafeHandles;

namespace TabletDeck;

// Deterministic protocol/ownership checks. This suite never opens a real driver,
// creates a Windows monitor, captures pixels, or changes desktop configuration.
public static class ExtendedDisplayTests
{
    public static async Task Run()
    {
        var checks=0;
        void Check(bool condition,string detail){if(!condition)throw new InvalidOperationException("Extended display test: "+detail);checks++;}
        async Task Reject(Func<Task> operation,string detail)
        {
            var rejected=false;try{await operation();}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or OperationCanceledException){rejected=true;}
            Check(rejected,detail);
        }
        var fake=new FakePlatform();
        using(var display=new ExtendedDisplay(fake))
        {
            var idle=display.Status();
            Check(idle.Available&&!idle.Active&&!idle.SharedWithTablet&&!idle.IndependentDesktop,"read-only capability status");
            Check(fake.Calls.Count==0,"Status must not send any driver IOCTL");
            await Reject(()=>display.FrameAsync(new()),"unshared capture rejected");
            await Reject(()=>Task.FromResult(display.CaptureVideoTarget()),"unshared video snapshot rejected");
            await Reject(()=>display.StartAsync(new(1920,1200,false,true)),"unsupported resolution rejected");
            await Reject(()=>display.StartAsync(new()),"unaccepted installed driver rejected");
            foreach(var refreshRate in new[]{0,30,59,241,int.MaxValue})
                await Reject(()=>display.StartAsync(new(1280,800,false,true,refreshRate)),"unsupported refresh rate "+refreshRate+" rejected before driver access");
            await Reject(()=>display.StartAsync(new(1280,800,false,true),new CancellationToken(true)),"cancellation before any driver operation");
            Check(fake.Calls.Count==0,"rejected requests have no driver side effects");
            var created=await display.StartAsync(new(1280,800,true,true));
            Check(created.Active&&created.IndependentDesktop&&created.SharedWithTablet&&created.Width==1280,"own independent monitor created");
            Check(fake.AddInput is {Length:56}&&BitConverter.ToInt32(fake.AddInput,8)==60,"fixed protocol structure and 60Hz refresh");
            Check(System.Text.Encoding.ASCII.GetString(fake.AddInput!,28,14).TrimEnd('\0')=="DeskWing","new virtual monitor uses the DeskWing display name");
            Check(created.RefreshRate==60,"status reports the active source refresh rate");
            var driverChecks=fake.DriverChecks;
            var videoTarget=display.CaptureVideoTarget();display.ValidateVideoTarget(videoTarget);
            Check(fake.DriverChecks==driverChecks,"per-frame video validation avoids driver trust/catalog discovery");
            Check(videoTarget.Monitor==fake.CreatedGuid&&videoTarget.Adapter==9&&videoTarget.TargetId==9&&videoTarget.SourceName==@"\\.\DISPLAY9","video authorization binds only the display created by this process");
            Check(created.Displays.Count(d=>d.IsOwned)==1&&created.Displays.Count(d=>!d.IsOwned)==2,"existing physical monitors preserved");
            var guid=fake.CreatedGuid;
            await Reject(()=>display.StartAsync(new(1280,800,false,true)),"second session rejected");
            display.SetSharing(false);
            await Reject(()=>display.FrameAsync(new()),"revoked sharing stops capture");
            Check(videoTarget.Invalidated.IsCancellationRequested,"sharing revocation cancels the active video authorization");
            await Reject(()=>Task.FromResult(display.CaptureVideoTarget()),"sharing revocation refuses new video snapshots");
            display.SetSharing(true);
            var nextVideoTarget=display.CaptureVideoTarget();
            Check(nextVideoTarget.Generation>videoTarget.Generation,"resharing advances video generation");
            await Reject(()=>{display.ValidateVideoTarget(videoTarget);return Task.CompletedTask;},"resharing never revives the prior video authorization");
            display.SetSharing(false);
            Check(!display.Status().SharedWithTablet,"sharing state explicitly revoked");
            var stopped=display.Stop();
            Check(!stopped.Active&&!stopped.SharedWithTablet&&stopped.Displays.Length==2,"stop retains physical desktop");
            Check(fake.RemovedGuids.SequenceEqual(new[]{guid}),"only own generated monitor GUID removed");
        }
        foreach(var refreshRate in new[]{60,90,120,144,165,240})
        {
            fake=new();
            using var display=new ExtendedDisplay(fake);
            var created=await display.StartAsync(new(1280,800,true,true,refreshRate));
            Check(fake.AddInput is {Length:56}&&BitConverter.ToInt32(fake.AddInput,8)==refreshRate,"requested "+refreshRate+"Hz reaches the virtual display protocol");
            Check(created.RefreshRate==refreshRate,"active "+refreshRate+"Hz source is reflected in status");
        }
        fake=new(){ActualRefreshRate=59.94};
        using(var display=new ExtendedDisplay(fake))
        {
            var created=await display.StartAsync(new(1280,800,true,true,60));
            Check(created.RefreshRate==59.94,"reported source refresh comes from Windows topology, not the requested label");
        }
        fake=new();
        using(var display=new ExtendedDisplay(fake))
        {
            await display.StartAsync(new(1280,800,true,true,120));
            var target=display.CaptureVideoTarget();fake.XOffset=40;
            await Reject(()=>{display.ValidateVideoTarget(target);return Task.CompletedTask;},"moving the owned display invalidates captured coordinates");
            fake.XOffset=0;fake.ActualRefreshRate=60;
            await Reject(()=>{display.ValidateVideoTarget(target);return Task.CompletedTask;},"changing the source refresh rate invalidates negotiated video geometry");
        }
        fake=new(){Trusted=false};
        using(var display=new ExtendedDisplay(fake))
        {
            await Reject(()=>display.StartAsync(new(1280,800,false,true)),"catalog trust failure rejected");
            Check(fake.Calls.Count==0,"untrusted driver never receives commands");
        }
        fake=new(){ForeignMonitor=true};
        using(var display=new ExtendedDisplay(fake))
        {
            await Reject(()=>display.StartAsync(new(1280,800,false,true)),"foreign driver session rejected");
            Check(fake.Calls.Count==0,"foreign monitor never adopted or removed");
        }
        fake=new(){ProtocolMajor=1};
        using(var display=new ExtendedDisplay(fake))
        {
            await Reject(()=>display.StartAsync(new(1280,800,false,true)),"unsupported protocol rejected");
            Check(fake.AddInput is null,"unsupported protocol never creates a monitor");
        }
        fake=new(){MalformedAdd=true};
        using(var display=new ExtendedDisplay(fake))
        {
            await Reject(()=>display.StartAsync(new(1280,800,false,true)),"malformed creation response rejected");
            Check(fake.RemovedGuids.SequenceEqual(new[]{fake.CreatedGuid}),"malformed response cleans up only own monitor");
        }
        fake=new();
        using(var display=new ExtendedDisplay(fake))
        {
            await display.StartAsync(new(1280,800,true,true));fake.FailRemove=true;
            var target=display.CaptureVideoTarget();
            var failed=display.Stop();
            Check(failed.Active&&!failed.SharedWithTablet,"failed removal preserves ownership while revoking sharing");
            Check(target.Invalidated.IsCancellationRequested,"failed monitor removal still cancels video immediately");
            fake.FailRemove=false;var retried=display.Stop();
            Check(!retried.Active&&fake.RemovedGuids.All(guid=>guid==fake.CreatedGuid),"failed removal retries only the owned GUID");
        }
        foreach(var kind in new[]{"clone","overlap"})
        {
            fake=new(){Clone=kind=="clone",Overlap=kind=="overlap"};
            using var display=new ExtendedDisplay(fake);
            var state=await display.StartAsync(new(1280,800,true,true));
            Check(state.Active&&!state.IndependentDesktop,"unsafe "+kind+" topology not labeled extended");
            await Reject(()=>display.FrameAsync(new()),"unsafe "+kind+" topology never captured");
            await Reject(()=>Task.FromResult(display.CaptureVideoTarget()),"unsafe "+kind+" topology never enters video encoding");
            display.Stop();
            Check(fake.RemovedGuids.SequenceEqual(new[]{fake.CreatedGuid}),"unsafe topology removes only owned monitor");
        }
        using(var diagnosticsEntered=new ManualResetEventSlim())
        using(var releaseDiagnostics=new ManualResetEventSlim())
        {
            fake=new(){BlockDriverCheck=2,DriverCheckEntered=diagnosticsEntered,ReleaseDriverCheck=releaseDiagnostics};
            using var display=new ExtendedDisplay(fake);
            // The first driver check authorizes creation. Block the subsequent read-only status
            // diagnostic after the owned display exists, as slow Windows catalog checks can do.
            var creation=Task.Run(()=>display.StartAsync(new(1280,800,true,true,120)));
            try
            {
                Check(diagnosticsEntered.Wait(TimeSpan.FromSeconds(5)),"read-only status reached the blocked driver diagnostic");
                var validation=Task.Run(()=>{var target=display.CaptureVideoTarget();display.ValidateVideoTarget(target);return target;});
                var target=await validation.WaitAsync(TimeSpan.FromSeconds(1));
                Check(target.Monitor==fake.CreatedGuid&&target.RefreshRate==120,"video snapshot and validation proceed while driver diagnostics remain blocked");
                Check(!creation.IsCompleted&&!releaseDiagnostics.IsSet,"video validation does not depend on the slow status response completing");
            }
            finally{releaseDiagnostics.Set();await creation.WaitAsync(TimeSpan.FromSeconds(5));}
        }
        Console.WriteLine($"Extended display: {checks} deterministic protocol, ownership and sharing checks passed. No real driver writes or pixel capture.");
    }

    private sealed class FakePlatform : ExtendedDisplay.IPlatform
    {
        private const string Device=@"\\?\ROOT#DISPLAY#0000#{device}";
        private const string Adapter=@"\\?\ROOT#DISPLAY#0000#{adapter}";
        public bool Trusted=true,ForeignMonitor,MalformedAdd,Clone,Overlap,FailRemove;
        public double? ActualRefreshRate;
        public int DriverChecks,XOffset;
        public int BlockDriverCheck;
        public ManualResetEventSlim? DriverCheckEntered,ReleaseDriverCheck;
        public byte ProtocolMajor;
        public byte[]? AddInput;
        public Guid CreatedGuid;
        public List<Guid> RemovedGuids=[];
        public List<uint> Calls=[];
        private bool created;
        public ExtendedDisplay.Native.Installed? FindDriver()
        {
            if(Interlocked.Increment(ref DriverChecks)==BlockDriverCheck)
            {
                DriverCheckEntered?.Set();
                if(ReleaseDriverCheck?.Wait(TimeSpan.FromSeconds(5))!=true)throw new InvalidOperationException("Fixture driver diagnostic was not released.");
            }
            return new(Device,new("Fake SudoVDA","0.2.1",Trusted,"Test fixture",null,"Fixture catalog state"),Trusted);
        }
        public SafeFileHandle Open(string path)=>new(new IntPtr(123),false);
        public ExtendedDisplay.Native.DesktopPath[] ReadPaths()
        {
            var result=new List<ExtendedDisplay.Native.DesktopPath>{new(1,1,"1:0",@"\\.\DISPLAY1","Physical 1",@"\\?\PCI#GPU#0000#{adapter}",0,0,1920,1080,false),new(1,2,"1:1",@"\\.\DISPLAY2","Physical 2",@"\\?\PCI#GPU#0000#{adapter}",1920,0,1920,1080,false)};
            if(ForeignMonitor)result.Add(new(7,7,"7:0",@"\\.\DISPLAY7","Foreign virtual",Adapter,3840,0,1280,800,false));
            if(created)result.Add(new(9,9,Clone?"1:0":"9:0",@"\\.\DISPLAY9","DeskWing",Adapter,Overlap?0:3840+XOffset,0,1280,800,Clone,ActualRefreshRate??BitConverter.ToInt32(AddInput!,8)));
            return result.ToArray();
        }
        public byte[] Ioctl(SafeFileHandle handle,uint code,byte[]? input,int outputSize)
        {
            lock(Calls)Calls.Add(code);
            if(code==0x2223fc)return [ProtocolMajor,2,1,1];
            if(code==0x222000)
            {
                AddInput=input!;CreatedGuid=new Guid(input!.AsSpan(12,16));created=true;
                if(MalformedAdd)return [];
                var output=new byte[12];BitConverter.GetBytes(9UL).CopyTo(output,0);BitConverter.GetBytes(9U).CopyTo(output,8);return output;
            }
            if(code==0x222004)
            {
                var guid=new Guid(input!);RemovedGuids.Add(guid);if(FailRemove)throw new InvalidOperationException("Fixture removal failure.");if(guid==CreatedGuid)created=false;return [];
            }
            if(code==0x222220)return [];
            throw new InvalidOperationException("Unexpected driver operation in fixture.");
        }
    }
}
