using System.Text.Json;
using TabletDeck;

// Standalone contract tests: the executor and foreground provider are fakes.
// No user data, desktop windows, keyboard injection, or installed applications are used.
var root=Path.Combine(Path.GetTempPath(),"tabletdeck-copilot-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed=0;
void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
void Reject(Action action,string label){try{action();throw new Exception("FAIL accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
async Task RejectAsync(Func<Task> action,string label){try{await action();throw new Exception("FAIL accepted "+label);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,label);}}
CopilotSettings Enable(CopilotSettings settings)=>settings with{Enabled=true};
CopilotSettings Bind(CopilotSettings settings,string shortcut,bool global=false)=>settings with{Presets=[new("custom","Custom",["Editor.exe"],[new("test","Test","Edit",shortcut,global)])],ManualPresetId="custom"};
var current=new CopilotForeground("Resolve.exe",true,101,23);
var calls=new List<DeckAction>();TaskCompletionSource? waiting=null;
Task Execute(DeckAction action){calls.Add(action);return waiting?.Task??Task.CompletedTask;}
var controller=new ComputerCopilot(root,Execute,()=>current);
CopilotActionRequest Request(CopilotSnapshot state,string id)=>new(state.ActivePresetId,id,state.ContextToken);
try {
    var defaults=controller.Settings;ComputerCopilot.Validate(defaults);
    Check(!defaults.Enabled&&defaults.Automatic&&defaults.Presets.Length==7,"default presets are useful and opt in");
    Check(defaults.Presets.Any(p=>p.Id=="resolve"&&p.Controls.Any(c=>c.Shortcut=="LEFT"))&&defaults.Presets.Any(p=>p.Id=="photoshop"&&p.Controls.Any(c=>c.Shortcut=="B")),"timeline jog and Photoshop tools use editable shortcut metadata");
    Check(defaults.Presets.Where(p=>p.Id is "coding" or "claude").SelectMany(p=>p.Controls).Any(c=>c.Shortcut=="")&&!defaults.Presets.SelectMany(p=>p.Controls).Any(c=>c.Shortcut.Contains("ENTER",StringComparison.OrdinalIgnoreCase)),"AI presets offer assignment placeholders without a send shortcut");
    var state=controller.Snapshot();Check(state.ActivePresetId=="resolve"&&state.DetectedPresetId=="resolve","unique foreground app chooses contextual preset");
    await RejectAsync(()=>controller.Execute(Request(state,"frame-forward")),"disabled controller refuses execution");Check(calls.Count==0,"disabled controller never calls executor");
    Check(!state.Capabilities.ExtendedDisplay&&!state.Capabilities.DisplayCapture&&state.Capabilities.ContextualControls,"capabilities never claim a virtual display or capture");
    controller.Save(Enable(defaults));state=controller.Snapshot();
    controller.Follow(false);state=controller.Snapshot();Check(!state.Settings.Automatic&&state.ActivePresetId=="resolve"&&state.Settings.ManualPresetId=="resolve","pin captures the currently followed layout without executing controls");
    current=new("Photoshop.exe",true,103,24);Check(controller.Snapshot().ActivePresetId=="resolve","pinned layout stays selected when foreground app changes");
    controller.Follow(true);Check(controller.Snapshot().Settings.Automatic&&controller.Snapshot().ActivePresetId=="photoshop","tablet can resume following without rewriting bindings");
    current=new("Resolve.exe",true,101,23);state=controller.Snapshot();
    await controller.Execute(Request(state,"frame-forward"));Check(calls.Count==1&&calls[^1].Kind=="hotkey"&&calls[^1].Target=="RIGHT","configured timeline frame action resolves to one allowlisted hotkey");
    await RejectAsync(()=>controller.Execute(new("resolve","arbitrary",state.ContextToken)),"remote cannot invent a control ID");
    await RejectAsync(()=>controller.Execute(new("photoshop","brush",state.ContextToken)),"remote cannot invoke a hidden preset");
    await RejectAsync(()=>controller.Execute(new("resolve","frame-forward","garbage")),"malformed context token is rejected");
    current=current with{WindowId=102};await RejectAsync(()=>controller.Execute(Request(state,"frame-forward")),"window change rejects delayed action even in same app");
    state=controller.Snapshot();Check(state.ContextToken==controller.Snapshot().ContextToken,"context token stays stable while the window and settings stay unchanged");
    controller.SelectPreset("photoshop");state=controller.Snapshot();Check(!state.Settings.Automatic&&state.ActivePresetId=="photoshop"&&calls.Count==1,"manual preset pins layout without launching or switching apps");
    await RejectAsync(()=>controller.Execute(Request(state,"brush")),"manual app-specific shortcut refuses mismatched foreground app");
    current=new("Photoshop.exe",true,103,24);state=controller.Snapshot();await controller.Execute(Request(state,"brush"));Check(calls[^1].Target=="B","manual preset works after matching app has focus");
    controller.Save(controller.Settings with{Automatic=true});current=new("chrome.exe",true,104,25);state=controller.Snapshot();Check(state.DetectedPresetId==""&&state.ActivePresetId=="photoshop","shared browser process never guesses YouTube versus Claude");
    var beforeMedia=calls.Count;controller.SelectPreset("youtube");state=controller.Snapshot();await RejectAsync(()=>controller.Execute(Request(state,"back")),"YouTube layout refuses legacy player keys and requires targeted session controls");
    await RejectAsync(()=>controller.Execute(Request(state,"media-play")),"YouTube layout cannot fall back to a system-wide media key");
    controller.SelectPreset("spotify");current=new("Spotify.exe",true,105,26);state=controller.Snapshot();await RejectAsync(()=>controller.Execute(Request(state,"media-play")),"Spotify layout cannot send a play/pause key to YouTube");
    await RejectAsync(()=>controller.Execute(Request(state,"volume-up")),"Spotify layout cannot change system volume");Check(calls.Count==beforeMedia,"refused player shortcuts never reach the keyboard sink");
    controller.SelectPreset("desktop");current=new("",false);state=controller.Snapshot();await controller.Execute(Request(state,"media-play"));Check(calls[^1].Target=="PLAYPAUSE","explicit Desktop global media key works without foreground access");
    controller.SelectPreset("youtube");state=controller.Snapshot();
    await RejectAsync(()=>controller.Execute(Request(state,"back")),"local browser shortcut refuses unavailable foreground");
    var leaked=controller.Settings;leaked.Presets[0]=leaked.Presets[0] with{Label="Changed externally"};Check(controller.Settings.Presets[0].Label=="Desktop","settings snapshots cannot mutate saved configuration");
    Reject(()=>controller.Save(Bind(defaults,"CTRL+ENTER")),"chat sending shortcut is rejected");
    Reject(()=>controller.Save(Bind(defaults,"CTRL+P",true)),"app shortcut cannot bypass foreground guard by becoming global");
    Reject(()=>controller.Save(Bind(defaults,"CTRL+CTRL+Z")),"repeated key chord is rejected");
    Reject(()=>controller.Save(Bind(defaults,"SHIFT+CTRL")),"modifier-only chord is rejected");
    Reject(()=>controller.Save(Bind(defaults,"CTRL+OEM")),"unsupported shortcut is rejected by shared parser");
    Reject(()=>controller.Save(Bind(defaults,"CTRL+S") with{Presets=[new("custom","Custom",["..\\Editor.exe"],[new("test","Test","Edit","CTRL+S")])]}),"executable path cannot be substituted for allowed app identity");
    Reject(()=>controller.Save(defaults with{ManualPresetId="missing"}),"missing selected preset is rejected");
    Reject(()=>controller.Save(defaults with{Presets=[defaults.Presets[0],defaults.Presets[0]]}),"duplicate preset identity is rejected");
    var custom=Enable(Bind(defaults," ctrl + shift + z "));controller.Save(custom);current=new("Editor.exe",true,105,26);state=controller.Snapshot();
    await controller.Execute(Request(state,"test"));Check(calls[^1].Target=="CTRL+SHIFT+Z","custom saved shortcut is normalized and executed by ID");
    waiting=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=controller.Execute(Request(state,"test"));
    await RejectAsync(()=>controller.Execute(Request(state,"test")),"concurrent taps are refused without building a queue");waiting.SetResult();await pending;waiting=null;
    var reloaded=new ComputerCopilot(root,Execute,()=>current);Check(reloaded.Settings.Enabled&&reloaded.Settings.ManualPresetId=="custom","saved custom settings survive isolated reload");
    state=controller.Snapshot();controller.Save(controller.Settings);await RejectAsync(()=>controller.Execute(Request(state,"test")),"saving bindings invalidates previously rendered action token");
    controller.Save(Bind(defaults,"") with{Enabled=true});state=controller.Snapshot();await RejectAsync(()=>controller.Execute(Request(state,"test")),"unassigned controls cannot inject a key");
    File.WriteAllText(Path.Combine(root,"computer-copilot.json"),"{broken");var broken=new ComputerCopilot(root,Execute,()=>current);Check(!broken.Settings.Enabled&&broken.Snapshot().Status.Contains("could not be read"),"damaged settings fail closed with visible recovery status");
    var blocked=new ComputerCopilot(root,Execute,()=>throw new System.ComponentModel.Win32Exception());Check(!blocked.Snapshot().Foreground.Available,"foreground read failure produces safe unavailable state");
    Console.WriteLine($"{passed} isolated Copilot backend checks passed; no real input or GUI used.");
} finally {
    // Delete only individually known files created inside this unique test directory.
    foreach(var filename in new[]{"computer-copilot.json","computer-copilot.json.tmp"}){var file=Path.Combine(root,filename);if(File.Exists(file))File.Delete(file);}
    Directory.Delete(root);
}

namespace TabletDeck {
    // Minimal host contracts let tests compile the production controller without starting its web host.
    public sealed class DeckAction {public string Id{get;set;}="";public string Kind{get;set;}="";public string Label{get;set;}="";public string Target{get;set;}="";public string Icon{get;set;}="";public string Color{get;set;}="";}
    public static class DataStore {public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true,MaxDepth=24};}
}
