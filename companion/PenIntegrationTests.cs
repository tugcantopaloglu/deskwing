using System.Text.Json;

namespace TabletDeck;

internal static class PenIntegrationTests
{
    internal static async Task Run()
    {
        var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
        void Reject(Action action,string label){try{action();throw new Exception("Accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
        async Task RejectAsync(Func<Task> action,string label){try{await action();throw new Exception("Accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
        var root=Path.Combine(Path.GetTempPath(),"tabletdeck-pen-integration-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new DataStore(root);var soundCount=0;long time=1000;
            var key=new DeckAction{Id="key",Kind="hotkey",Target="CTRL+S"};
            var hold=new DeckAction{Id="hold",Kind="holdHotkey",Target="CTRL+M"};
            var sound=new DeckAction{Id="sound",Kind="soundpadPlay",Target="1",Value="mic"};
            var macro=new DeckAction{Id="macro",Kind="macro",Steps=[new(){Kind="delay",Target="10000"}]};
            store.Modify(config=>config.Pages=[new(){Buttons=[key,hold,sound,macro]}]);
            using var holds=new HoldKeys((_,_)=>{},()=>DateTimeOffset.UtcNow);
            await using var obs=new ObsClient(()=>store.Config);
            var engine=new DeckEngine(store,obs,(_,_,_,_)=>{soundCount++;return Task.CompletedTask;},_=>Task.FromResult(new SoundpadLibraryResult([],null)));
            using var mixer=new AppAudioMixer();var commands=new FastCommands(store,engine,holds,mixer);
            var platform=new FixturePlatform();using var pen=new WindowsPen(platform,()=>time);commands.ConfigurePen(pen);
            PenRequest Begin()=>new(Operation:"begin",Session:Guid.NewGuid().ToString("N"));
            Reject(()=>commands.Pen(Begin(),"one"),"tablet cannot begin native input before PC opt-in");
            commands.ConfigurePenSettings(new(true,"fixture"));
            var first=(WindowsPen.PenLease)commands.Pen(Begin(),"one");
            Check(pen.Active&&platform.Begins==1&&!commands.CanUpdateTablet,"active pen prevents automatic tablet replacement");
            Reject(()=>commands.BeginMaintenance(),"native pen lease prevents update maintenance");
            await RejectAsync(()=>commands.Action(new(key.Id)),"active pen blocks configured keyboard action");
            await RejectAsync(()=>commands.Action(new(macro.Id)),"active pen blocks macro execution");
            await RejectAsync(()=>commands.ExecuteConfigured(new(){Kind="app",Target="unused.exe"}),"active pen blocks app launching before OS dispatch");
            await RejectAsync(()=>commands.ExecuteConfigured(new(){Kind="url",Target="https://example.invalid"}),"active pen blocks browser launching before OS dispatch");
            Reject(()=>commands.Hold(new("begin",hold.Id),"one"),"active pen blocks beginning keyboard hold");
            await commands.Action(new(sound.Id));Check(soundCount==1&&pen.Active,"Soundpad remains usable during pen session without injecting keyboard input");
            commands.ReleasePen("other",first.Lease);commands.RevokePen("other");Check(pen.Active,"another principal cannot release the active pen session");
            commands.ReleasePen("one",first.Lease);Check(!pen.Active,"disconnect cleanup releases its exact owned pen lease");
            var second=(WindowsPen.PenLease)commands.Pen(Begin(),"one");commands.ReleasePen("one",first.Lease);
            Check(pen.Active&&first.Lease!=second.Lease,"old connection cleanup cannot stop a newer pen session for the same tablet");
            time+=10;
            await commands.Execute("/api/pen",JsonSerializer.SerializeToElement(new PenRequest(Operation:"samples",Lease:second.Lease,Sequence:1,ElapsedMs:10,Samples:[new("down",.5,.5,.6)]),DataStore.Json),"one");
            Check(platform.Frames.Count==1&&platform.Frames[0].Phase=="down","fast pen dispatch reaches only the injected fake platform");
            commands.RevokePen("one");Check(!pen.Active&&platform.Frames.Any(frame=>frame.Phase=="up"&&frame.Cancelled),"device revocation releases a fake pen contact and ends ownership");
            commands.Hold(new("begin",hold.Id),"one");Reject(()=>commands.Pen(Begin(),"one"),"keyboard hold prevents pen session start");holds.ReleaseAll();
            var running=commands.Action(new(macro.Id));Reject(()=>commands.Pen(Begin(),"one"),"running macro prevents pen session start");engine.Execution.Cancel(engine.Execution.Status.Active!.Id);await RejectAsync(()=>running,"fixture macro cancels without running external controls");
            var third=(WindowsPen.PenLease)commands.Pen(Begin(),"one");time+=1300;pen.Expire();Check(!pen.Active&&commands.CanUpdateTablet,"expired pen lease releases update exclusion");
            commands.BeginMaintenance();
            await RejectAsync(()=>commands.Execute("/api/pen",JsonSerializer.SerializeToElement(new{operation="cancel",padding=new string('x',17000)},DataStore.Json),"one"),"oversized direct fast pen body is refused before deserialization");
            await RejectAsync(()=>commands.Execute("/api/pen",JsonSerializer.SerializeToElement(Begin(),DataStore.Json),"one"),"fast pen begin is refused during maintenance");
            commands.Pen(new(Operation:"cancel",Lease:third.Lease),"one");Check(!pen.Active,"late pen cancellation stays available and harmless during maintenance");
            commands.EndMaintenance();commands.Pen(Begin(),"one");commands.ConfigurePenSettings(new(false));Check(!pen.Active&&!pen.Status.Enabled,"PC disable ends the active session");
            Check(FastCommands.Supports("/api/pen"),"native pen route is allowlisted on persistent transport");
            Console.WriteLine(passed+" pen integration checks passed with fake native input only.");
        }finally{
            var full=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath());
            if(full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("tabletdeck-pen-integration-",StringComparison.Ordinal)&&Directory.Exists(full))Directory.Delete(full,true);
        }
    }
    private sealed class FixturePlatform:IWindowsPenPlatform
    {
        public bool Supported=>true;
        public int Begins;
        public List<PenFrame> Frames {get;}=[];
        private readonly PenDestination target=new(new("fixture","Fixture canvas","display",640,480),new(0,0,640,480));
        public PenDestination[] Targets()=>[target];
        public PenDestination Resolve(PenDestination candidate,bool requireForeground)=>target;
        public void Begin()=>Begins++;
        public void Inject(PenFrame frame)=>Frames.Add(frame);
        public void Reset(){}
        public void Dispose(){}
    }
}
