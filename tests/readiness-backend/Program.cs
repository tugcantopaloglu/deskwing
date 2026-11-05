using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabletDeck;

// Production readiness logic with fake read-only OBS/Soundpad providers.
// No keyboard input, live OBS, sound playback, or installed app is invoked.
var passed=0;var calls=new List<string>();
void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
DeckAction Action(string kind,string target="",string value="",string? key=null)=>new(){Kind=kind,Label=kind,Target=target,Value=value,SoundpadKey=key??""};
DeckConfig Board(params DeckAction[] actions)=>new(){ProfileId="fixture-profile",ProfileName="Fixture",Pages=[new(){Id="fixture-page",Name="Main",Buttons=actions.ToList()}]};
JsonObject Json(string value)=>JsonNode.Parse(value)!.AsObject();
Task<JsonObject> Obs(string type,object? data,CancellationToken ct){
    calls.Add(type+JsonSerializer.Serialize(data));
    return Task.FromResult(type switch{
        "GetSceneList"=>Json("{\"scenes\":[{\"sceneName\":\"Studio\"},{\"sceneName\":\"Break\"}]}"),
        "GetInputList"=>Json("{\"inputs\":[{\"inputName\":\"Mic\"},{\"inputName\":\"Music\"}]}"),
        "GetSceneItemList"=>Json("{\"sceneItems\":[{\"sourceName\":\"Camera\"}]}"),
        "GetSourceFilterList"=>Json("{\"filters\":[{\"filterName\":\"Color correction\"}]}"),
        _=>throw new Exception("A non-read-only OBS request escaped the allowlist: "+type)
    });
}
var goodKey=new string('A',64);var duplicateKey=new string('B',64);
Task<SoundpadLibraryResult> Sounds(CancellationToken ct)=>Task.FromResult(new SoundpadLibraryResult([
    new(1,"Good","","",[],goodKey),new(2,"Duplicate A","","",[],duplicateKey),new(3,"Duplicate B","","",[],duplicateKey)
],null));
var folder=Path.Combine(Path.GetTempPath(),"deskwing-readiness-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var sound=Path.Combine(folder,"fixture.wav");File.WriteAllBytes(sound,[0]);
try{
    var macro=Action("macro");macro.Steps=[Action("scene","Studio"),Action("macro")];macro.Steps[1].Steps=[Action("source","Studio","Camera"),Action("filter","Camera","Missing")];
    var board=Board(Action("scene","Gone"),Action("mute","Mic"),Action("media","Gone"),Action("source","Studio","Camera"),macro,
        Action("sound",sound),Action("sound",Path.Combine(folder,"missing.wav")),Action("soundpadPlay","1","mic",goodKey),Action("soundpadPlay","2","mic",duplicateKey),Action("soundpadPlay","9","mic",new string('C',64)),Action("soundpadPlay","1","mic"),Action("hotkey","CTRL+SHIFT+M"));
    var report=await ProfileReadiness.Check(board,Obs,Sounds);
    Check(report.ProfileId==board.ProfileId&&report.ProfileName==board.ProfileName,"report belongs to the captured profile");
    Check(report.Checks.Any(c=>c.Code=="scene-target"&&c.Severity=="error")&&report.Checks.Any(c=>c.Code=="input-target"&&c.Severity=="error"),"missing scenes and inputs are actionable errors");
    Check(report.Checks.Any(c=>c.Code=="filter-target"&&c.Severity=="error"&&c.ButtonId==macro.Id&&c.StepPath=="2.2"),"nested missing filter points to the root button and exact macro step");
    Check(report.Checks.Count(c=>c.Code=="source-target"&&c.Severity=="ok")==2&&calls.Count(c=>c.StartsWith("GetSceneItemList",StringComparison.Ordinal))==1,"repeated scene queries are cached across steps");
    Check(report.Checks.Count(c=>c.Code=="soundpad-identity"&&c.Severity=="error")==2&&report.Checks.Any(c=>c.Code=="soundpad-unlinked"),"missing and duplicate stable sounds fail while positional sound is flagged");
    Check(report.Checks.Any(c=>c.Code=="file-present")&&report.Checks.Any(c=>c.Code=="missing-file"),"configured local files are checked without opening them");
    Check(report.Checks.Any(c=>c.Code=="shortcut-rehearsal")&&report.Notice.Contains("no action",StringComparison.OrdinalIgnoreCase),"shortcut validity does not claim live application behavior");
    Check(calls.All(c=>c.StartsWith("Get",StringComparison.Ordinal)),"no OBS mutation request was made");
    calls.Clear();var choices=await ProfileReadiness.Targets(Obs,"Studio","Camera");
    Check(choices.Scenes.SequenceEqual(["Studio","Break"])&&choices.Inputs.Contains("Mic")&&choices.Sources.Contains("Camera")&&choices.Filters.Contains("Color correction"),"picker targets map real OBS response shapes");
    var count=calls.Count;try{await ProfileReadiness.Targets(Obs,new string('x',201));throw new Exception("long target was accepted");}catch(ArgumentException){Check(calls.Count==count,"oversized target fails before provider access");}
    var live=Board(Action("sound",Path.Combine(folder,"hidden-missing.wav")));live.Pages[0].LiveScenes=true;
    var liveReport=await ProfileReadiness.Check(live,Obs,Sounds);
    Check(liveReport.Checks.Length==1&&liveReport.Checks[0].Code=="live-scenes","retained manual buttons are not checked behind a live page");
    var local=await ProfileReadiness.Check(Board(Action("delay","250")),(_,_,_)=>throw new Exception("unexpected OBS read"),_=>throw new Exception("unexpected Soundpad read"));
    Check(local.Complete&&local.Checks.Single().Code=="delay","local-only actions do not contact unused dependencies");
    var invalid=await ProfileReadiness.CheckAction(Action("hotkey","NOT-A-KEY"),board,Obs,Sounds);
    Check(!invalid.Ok&&invalid.Problems.Length==1,"invalid shortcut is rejected without execution");
    var direct=await ProfileReadiness.CheckAction(Action("scene","Gone"),board,Obs,Sounds);
    Check(!direct.Ok&&direct.Problems.Any(p=>p.Contains("missing",StringComparison.OrdinalIgnoreCase)),"single target check includes live OBS references");
    var shortcut=await ProfileReadiness.CheckAction(Action("hotkey","CTRL+M"),board,Obs,Sounds);
    Check(shortcut.Ok&&shortcut.Note.Contains("no action ran",StringComparison.Ordinal)&&shortcut.Note.Contains("matching binding",StringComparison.Ordinal),"legacy target-check note retains no-action assurance and concrete rehearsal guidance");
    var tooDeep=Action("macro");var nested=tooDeep;for(var i=0;i<5;i++){var child=Action("macro");nested.Steps=[child];nested=child;}nested.Steps=[Action("scene","Studio")];
    var beforeInvalid=calls.Count;try{await ProfileReadiness.CheckAction(tooDeep,board,Obs,Sounds);throw new Exception("deep macro accepted");}catch(ArgumentException){Check(calls.Count==beforeInvalid,"unsaved macro nesting is rejected before traversal or provider access");}
    var sourceActions=Enumerable.Range(0,60).Select(i=>Action("source","Scene "+i,"Camera")).ToArray();var boundedCalls=0;
    Task<JsonObject> ManyTargets(string type,object? data,CancellationToken ct){boundedCalls++;return Task.FromResult(type=="GetSceneList"?new JsonObject{["scenes"]=new JsonArray(Enumerable.Range(0,60).Select(i=>(JsonNode)new JsonObject{["sceneName"]="Scene "+i}).ToArray())}:type=="GetInputList"?Json("{\"inputs\":[]}"):Json("{\"sceneItems\":[{\"sourceName\":\"Camera\"}]}"));}
    var limited=await ProfileReadiness.Check(Board(sourceActions),ManyTargets,Sounds);
    Check(!limited.Complete&&boundedCalls<=48&&limited.Checks.Any(c=>c.Code=="target-unverified"),"large profiles cannot exceed the OBS request budget");
    using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await ProfileReadiness.Check(Board(Action("scene","Studio")),Obs,Sounds,cancelled.Token);throw new Exception("caller cancellation was swallowed");}catch(OperationCanceledException){Check(true,"caller cancellation is propagated");}}
    var watch=Stopwatch.StartNew();var unresolved=new TaskCompletionSource<JsonObject>();
    var unavailable=await ProfileReadiness.Check(Board(Action("scene","Studio")),(_,_,_)=>unresolved.Task,Sounds);
    Check(watch.Elapsed<TimeSpan.FromSeconds(5)&&!unavailable.Complete&&unavailable.Checks.All(c=>c.Severity=="warning"),"unresponsive provider is bounded and never marked ready");
    unresolved.TrySetResult(new());
    var recoveryDir=Path.Combine(folder,"recovery");var store=new DataStore(recoveryDir);
    var before=Board(Action("delay","100"));before.ProfileId=store.Config.ProfileId;before.Pages[0].Id="page-stable";store.SaveActive(before,false);var backup=store.BackupNow();
    var after=DataStore.Copy(store.Config);after.Pages[0].Buttons[0].Target="200";after.Pages[0].Buttons.Add(Action("delay","300"));store.SaveActive(after,false);
    var changes=store.CompareBackup(backup.Id);
    Check(changes.Profiles.Single(p=>p.Id==before.ProfileId).ButtonsRemoved==1&&changes.Profiles.Single(p=>p.Id==before.ProfileId).ButtonsChanged==1,"backup comparison describes restoration direction");
    var active=store.Config.ProfileId;var retained=store.Config.Pages[0].Buttons.Count;var restoredId=store.RestoreBackupProfile(backup.Id,before.ProfileId,"Recovered");
    Check(store.Config.ProfileId==active&&store.Config.Pages[0].Buttons.Count==retained&&store.Config.Profiles.Single(p=>p.Id==restoredId).Pages[0].Buttons.Single().Target=="100","selective restore creates an inactive profile without replacing current edits");
    Check(store.Config.Profiles.Single(p=>p.Id==restoredId).Pages[0].Buttons.Single().Id!=before.Pages[0].Buttons[0].Id,"selective restore regenerates action identities");
    Console.WriteLine($"{passed} readiness and selective-recovery checks passed; no real OBS, Soundpad, keyboard, or app actions executed.");
}finally{
    // The sole root was generated above, with no input-derived child path.
    if(Directory.Exists(folder))Directory.Delete(folder,true);
}
