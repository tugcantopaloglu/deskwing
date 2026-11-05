using System.Text.Json;

namespace TabletDeck;

public static class ReliabilityTests
{
    public static async Task Run()
    {
        var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
        void Reject(Action action,string label){try{action();throw new Exception("Accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
        async Task RejectAsync(Func<Task> action,string label){try{await action();throw new Exception("Accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
        var root=Path.Combine(Path.GetTempPath(),"tabletdeck-reliability-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new DataStore(root);var original=store.ActiveSnapshot();
            foreach(var delayTarget in new[]{"-1","10001","1.5","invalid",""})Reject(()=>DataStore.CheckAction(new(){Kind="delay",Target=delayTarget}),"invalid delay cannot be saved: "+delayTarget);
            store.Modify(c=>{c.ObsPort=4499;c.ObsPassword="kept-private";c.MicInput="Current mic";c.DesktopInput="Current desktop";c.MixerInputs=[new(Guid.NewGuid().ToString("N"),"Extra","Current extra")];});
            var edit=DataStore.Copy(original.Config);edit.Pages[0].Name="Reviewed board";edit.ObsPassword="must-not-save";
            store.SaveActive(edit,true,original.Revision);
            Check(store.Config.ObsPort==4499&&store.Config.ObsPassword=="kept-private"&&store.Config.MicInput=="Current mic"&&store.Config.DesktopInput=="Current desktop"&&store.Config.MixerInputs.Single().Input=="Current extra","revision-aware board save preserves independently changed OBS and mixer settings");
            Check(store.BoardRevision!=original.Revision&&store.Config.Pages[0].Name=="Reviewed board","board edit advances opaque revision");
            Reject(()=>store.SaveActive(original.Config,false,original.Revision),"stale same-profile save is rejected");
            var snapshot=store.ActiveSnapshot();snapshot.Config.Pages[0].Name="Leaked mutation";Check(store.Config.Pages[0].Name=="Reviewed board","editor snapshot cannot mutate stored configuration");
            var reloaded=new DataStore(root);Check(reloaded.BoardRevision==store.BoardRevision,"revision is stable across process restart");
            var source=store.ProfileSnapshot(store.Config.ProfileId);var targetId=store.CreateProfile("Destination",false);var target=store.ProfileSnapshot(targetId);var sourceButton=source.Pages[0].Buttons[0];
            var request=new BoardTransferRequest(source.ProfileId,source.BoardRevision,target.ProfileId,target.BoardRevision,source.Pages[0].Id,target.Pages[0].Id,[sourceButton.Id],"copy");
            store.TransferButtons(request);var copied=store.ProfileSnapshot(targetId);
            Check(copied.Pages[0].Buttons.Single().Id!=sourceButton.Id&&copied.Pages[0].Buttons.Single().Target==sourceButton.Target&&store.Config.ProfileId==source.ProfileId,"cross-profile copy preserves content with fresh IDs and does not switch active profile");
            Reject(()=>store.TransferButtons(request),"stale destination cannot be overwritten by transfer");
            var beforeMove=store.ProfileSnapshot(source.ProfileId);var beforeTarget=store.ProfileSnapshot(targetId);
            store.TransferButtons(request with{SourceRevision=beforeMove.BoardRevision,TargetRevision=beforeTarget.BoardRevision,Operation="move"});
            Check(store.ProfileSnapshot(source.ProfileId).Pages[0].Buttons.All(a=>a.Id!=sourceButton.Id)&&store.ProfileSnapshot(targetId).Pages[0].Buttons.Count==2,"move commits source removal and destination addition together");
            var beforeInvalid=store.BoardRevision;
            Reject(()=>store.TransferButtons(request with{SourceRevision=store.BoardRevision,TargetRevision=store.ProfileSnapshot(targetId).BoardRevision,ButtonIds=["missing"]}),"missing selected button refuses transfer");
            Check(store.BoardRevision==beforeInvalid,"failed transfer leaves current board unchanged");
            var backup=store.BackupNow();var activeId=store.Config.ProfileId;var restored=store.RestoreBackupProfile(backup.Id,targetId,"Recovered destination");
            Check(store.Config.ProfileId==activeId&&store.ProfileSnapshot(restored).Pages[0].Buttons.Count==2&&store.Config.ObsPassword=="kept-private","selected backup profile restores inactive without reverting current board or credentials");
            var hold=new DeckAction{Id="hold-fixture",Kind="holdHotkey",Target="CTRL+M",Label="Talk"};
            var key=new DeckAction{Id="key-fixture",Kind="hotkey",Target="CTRL+S",Label="Save"};
            var sound=new DeckAction{Id="sound-fixture",Kind="soundpadPlay",Target="1",Value="mic",Label="Sound"};
            store.Modify(c=>c.Pages=[new(){Buttons=[hold,key,sound]}]);
            var keyEvents=new List<bool>();using var holds=new HoldKeys((_,down)=>keyEvents.Add(down),()=>DateTimeOffset.UtcNow);
            await using var obs=new ObsClient(()=>store.Config);var sounds=0;var engine=new DeckEngine(store,obs,(_,_,_,_)=>{sounds++;return Task.CompletedTask;},_=>Task.FromResult(new SoundpadLibraryResult([],null)));
            using var mixer=new AppAudioMixer();var commands=new FastCommands(store,engine,holds,mixer);
            commands.Hold(new("begin",hold.Id,""),"fixture");
            await RejectAsync(()=>commands.Action(new(key.Id)),"held keyboard lease blocks ordinary hotkey before injection");
            await commands.Action(new(sound.Id));Check(sounds==1&&holds.Active&&keyEvents.SequenceEqual([true]),"sound playback remains available while push-to-talk is held");
            holds.ReleaseAll();
            var macro=new DeckAction{Kind="macro",Label="Keyboard macro",Steps=[new(){Kind="delay",Target="10000"},key]};
            var running=commands.ExecuteConfigured(macro);
            Reject(()=>commands.Hold(new("begin",hold.Id,""),"fixture"),"keyboard macro reserves ownership before its first keyboard step");
            engine.Execution.Cancel(engine.Execution.Status.Active!.Id);await RejectAsync(()=>running,"cancelling reserved macro stops before real keyboard input");
            commands.Hold(new("begin",hold.Id,""),"fixture");Check(holds.Active,"cancelled macro releases keyboard reservation");holds.ReleaseAll();
            commands.BeginMaintenance();
            await RejectAsync(()=>commands.ExecuteConfigured(sound),"Soundpad execution refuses maintenance");
            await RejectAsync(()=>commands.SetVolume(new("mic",-10)),"HTTPS volume entry refuses maintenance before OBS call");
            await RejectAsync(()=>commands.Execute("/api/audio",JsonSerializer.SerializeToElement(new{channel="mic",db=-10}),"fixture"),"fast volume entry uses same maintenance policy");
            Reject(()=>commands.Hold(new("begin",hold.Id,""),"fixture"),"hold begin refuses maintenance");
            commands.EndMaintenance();await commands.Action(new(sound.Id));Check(sounds==2,"controls resume after maintenance ends");
            Check(keyEvents.SequenceEqual([true,false,true,false]),"guard tests never inject keyboard input; only fake hold events occurred");
            Console.WriteLine(passed+" reliability checks passed; isolated files, fake input and fake Soundpad only.");
        }finally{
            var absolute=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath());
            if(absolute.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("tabletdeck-reliability-",StringComparison.Ordinal)&&Directory.Exists(absolute))Directory.Delete(absolute,true);
        }
    }
}
