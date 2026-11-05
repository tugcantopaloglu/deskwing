using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TabletDeck;

public sealed record CopilotControl(string Id,string Label,string Group,string Shortcut,bool Global=false);
public sealed record CopilotPreset(string Id,string Label,string[] Applications,CopilotControl[] Controls);
public sealed record CopilotSettings(bool Enabled,bool Automatic,string ManualPresetId,CopilotPreset[] Presets);
public sealed record CopilotActionRequest(string PresetId,string ControlId,string ContextToken);
public sealed record CopilotPresetRequest(string PresetId);
public sealed record CopilotFollowRequest(bool Automatic);
public sealed record CopilotForeground(string Application,bool Available,long WindowId=0,int ProcessId=0);
public sealed record CopilotForegroundInfo(string Application,bool Available);
public sealed record CopilotCapabilities(bool ContextualControls,bool DisplayCapture,bool ExtendedDisplay,string DisplayNote);
public sealed record CopilotSnapshot(CopilotSettings Settings,CopilotForegroundInfo Foreground,string DetectedPresetId,string ActivePresetId,string ContextToken,string Status,CopilotCapabilities Capabilities);

// A contextual shortcut controller. Bindings are configured on the PC; remote callers send IDs only.
// The execution delegate must use the host's shared execution/maintenance/hold guards.
public sealed class ComputerCopilot
{
    private readonly string path;
    private readonly Func<DeckAction,Task> execute;
    private readonly Func<CopilotForeground> foreground;
    private readonly object gate=new();
    private readonly SemaphoreSlim actionGate=new(1,1);
    private CopilotSettings settings=Defaults();
    private string token=Guid.NewGuid().ToString("N"),contextKey="",loadWarning="";
    private long revision;
    private static readonly HashSet<string> globalShortcuts=new(StringComparer.Ordinal){"PLAYPAUSE","NEXTTRACK","PREVTRACK","VOLUMEUP","VOLUMEDOWN","VOLUMEMUTE","ALT+TAB","WIN+TAB"};
    public ComputerCopilot(string root,Func<DeckAction,Task> execute,Func<CopilotForeground>? foreground=null)
    {
        path=Path.Combine(root,"computer-copilot.json");this.execute=execute;this.foreground=foreground??ReadForeground;
        try {
            if(File.Exists(path)) {
                if(new FileInfo(path).Length>128*1024)throw new ArgumentException("Copilot settings are too large.");
                var saved=JsonSerializer.Deserialize<CopilotSettings>(File.ReadAllText(path),DataStore.Json)??throw new ArgumentException("Missing Copilot settings.");
                Validate(saved);settings=Clone(saved);
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){loadWarning="Computer controls settings could not be read. Controls are off; save settings on the PC to restore them.";}
    }
    private static CopilotSettings Clone(CopilotSettings value)=>value with{Presets=value.Presets.Select(p=>p with{Applications=p.Applications.ToArray(),Controls=p.Controls.ToArray()}).ToArray()};
    public CopilotSettings Settings {get{lock(gate)return Clone(settings);}}
    private static bool Id(string? value)=>value is {Length:>0 and <=64}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    private static bool Label(string? value)=>value is {Length:>0 and <=80}&&!string.IsNullOrWhiteSpace(value)&&!value.Any(char.IsControl);
    private static bool Application(string? value)=>value is {Length:>4 and <=100}&&value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is ' ' or '.' or '-' or '_');
    private static string Normalize(string value)=>string.Join('+',value.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    public static void Validate(CopilotSettings value)
    {
        if(value is null||value.Presets is null||value.Presets.Length is <1 or >16||!Id(value.ManualPresetId)||value.Presets.Any(p=>p is null||!Id(p.Id)||!Label(p.Label)||p.Applications is null||p.Applications.Length>20||p.Applications.Any(a=>!Application(a))||p.Applications.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Applications.Length||p.Controls is null||p.Controls.Length is <1 or >40)||value.Presets.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=value.Presets.Length||!value.Presets.Any(p=>p.Id==value.ManualPresetId))throw new ArgumentException("Choose 1–16 named presets, an existing manual preset, and executable names such as Resolve.exe.");
        foreach(var preset in value.Presets) {
            if(preset.Controls.Select(c=>c?.Id).Distinct(StringComparer.Ordinal).Count()!=preset.Controls.Length)throw new ArgumentException("Control IDs must be unique inside each preset.");
            foreach(var control in preset.Controls) {
                if(control is null||!Id(control.Id)||!Label(control.Label)||!Label(control.Group)||control.Shortcut is null||control.Shortcut.Length>80)throw new ArgumentException("Each control needs an ID, label, group, and supported shortcut.");
                // Empty bindings are intentional placeholders for application-specific commands.
                if(control.Shortcut.Length==0) {if(control.Global)throw new ArgumentException("A global control needs a media or app-switching shortcut.");continue;}
                var normalized=Normalize(control.Shortcut);var keys=WindowsActions.ParseHotkey(normalized);
                if(keys.Contains((ushort)0x0D))throw new ArgumentException("Enter shortcuts are unavailable in Computer controls; send chat messages from the PC.");
                if(keys.Distinct().Count()!=keys.Length||keys.All(k=>k is 0x10 or 0x11 or 0x12 or 0x5B))throw new ArgumentException("Use a shortcut with distinct keys and a non-modifier key.");
                if(control.Global&&!globalShortcuts.Contains(normalized))throw new ArgumentException("Global controls support media keys and ALT+TAB or WIN+TAB only.");
                if(!control.Global&&preset.Applications.Length==0)throw new ArgumentException("App-specific controls need at least one executable name.");
            }
        }
    }
    private void Persist(CopilotSettings value)
    {
        var text=JsonSerializer.Serialize(value,DataStore.Json);
        if(System.Text.Encoding.UTF8.GetByteCount(text)>128*1024)throw new ArgumentException("Keep Copilot settings below 128 KB.");
        File.WriteAllText(path+".tmp",text);File.Move(path+".tmp",path,true);
        settings=Clone(value);revision++;contextKey="";token=Guid.NewGuid().ToString("N");loadWarning="";
    }
    public void Save(CopilotSettings value){Validate(value);lock(gate)Persist(value);}
    public void SelectPreset(string id)
    {
        lock(gate) {
            if(!settings.Presets.Any(p=>p.Id==id))throw new ArgumentException("Choose an existing Computer controls preset.");
            Persist(settings with{ManualPresetId=id,Automatic=false});
        }
    }
    public void Follow(bool automatic)
    {
        lock(gate){
            if(!settings.Enabled)throw new InvalidOperationException("Enable Computer controls in the PC settings first.");
            var pinned=automatic?settings.ManualPresetId:SnapshotLocked().ActivePresetId;
            Persist(settings with{Automatic=automatic,ManualPresetId=pinned});
        }
    }
    private CopilotForeground Foreground()
    {
        try {var value=foreground();return value.Available&&Application(value.Application)?value:new("",false);}
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException){return new("",false);}
    }
    private CopilotSnapshot SnapshotLocked()
    {
        var current=Foreground();
        // Shared browser processes cannot identify websites without inspecting page contents.
        // Presets with overlapping executable names stay manual rather than guessing a website.
        var matches=settings.Presets.Where(p=>p.Applications.Contains(current.Application,StringComparer.OrdinalIgnoreCase)).ToArray();
        var detected=matches.Length==1?matches[0].Id:"";
        var active=settings.Automatic&&detected.Length>0?detected:settings.ManualPresetId;
        var nextKey=$"{revision}:{active}:{current.Application}:{current.Available}:{current.WindowId}:{current.ProcessId}";
        if(contextKey!=nextKey){contextKey=nextKey;token=Guid.NewGuid().ToString("N");}
        var status=loadWarning.Length>0?loadWarning:!settings.Enabled?"Computer controls are off. Enable them in the PC settings.":active=="spotify"?"Live controls target Spotify's own player session.":active=="youtube"?"Choose the browser player by its current title. Controls affect only that selected session.":!current.Available?"Cannot read the foreground app. Global media and app-switching controls remain available.":settings.Automatic&&detected.Length>0?"Controls follow the foreground app.":"Manual preset. Focus the matching PC app before using its controls.";
        return new(Clone(settings),new(current.Application,current.Available),detected,active,token,status,new(true,false,false,"A real extra Windows desktop requires a separately installed virtual or indirect display driver and tablet transport. This controller does not install a driver or create a display."));
    }
    public CopilotSnapshot Snapshot(){lock(gate)return SnapshotLocked();}
    public async Task<object> Execute(CopilotActionRequest request)
    {
        if(request is null||!Id(request.PresetId)||!Id(request.ControlId)||!Guid.TryParseExact(request.ContextToken,"N",out _))throw new ArgumentException("Choose a current Computer controls button.");
        if(!await actionGate.WaitAsync(0))throw new InvalidOperationException("A Computer control is still running. Try again after it finishes.");
        try {
            DeckAction action;
            lock(gate) {
                var state=SnapshotLocked();
                if(!settings.Enabled)throw new InvalidOperationException("Enable Computer controls in the PC settings first.");
                if(request.ContextToken!=state.ContextToken||request.PresetId!=state.ActivePresetId)throw new InvalidOperationException("The foreground app or controls changed. Refresh before tapping again.");
                var preset=settings.Presets.Single(p=>p.Id==request.PresetId);
                var control=preset.Controls.FirstOrDefault(c=>c.Id==request.ControlId)??throw new ArgumentException("This control no longer exists. Refresh the controls.");
                if(preset.Id is "spotify" or "youtube"&&(control.Global||control.Group is "Media" or "Player" or "Volume"))throw new InvalidOperationException("Use the selected player's live controls. Global media shortcuts are disabled in Spotify and YouTube layouts.");
                if(control.Shortcut.Length==0)throw new InvalidOperationException("Assign this shortcut in the PC settings first.");
                if(!control.Global&&(!state.Foreground.Available||!preset.Applications.Contains(state.Foreground.Application,StringComparer.OrdinalIgnoreCase)))throw new InvalidOperationException("Focus a matching PC app before using this control.");
                action=new DeckAction{Id="copilot-"+preset.Id+"-"+control.Id,Kind="hotkey",Label=control.Label,Target=Normalize(control.Shortcut),Icon="keyboard",Color="blue"};
            }
            // Never hold this component's settings lock while entering the host's execution lock.
            await execute(action);return new{ok=true};
        }finally{actionGate.Release();}
    }
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    public static CopilotForeground ReadForeground()
    {
        var window=GetForegroundWindow();if(window==IntPtr.Zero)return new("",false);
        GetWindowThreadProcessId(window,out var id);if(id==0)return new("",false);
        using var process=Process.GetProcessById(checked((int)id));
        return new(process.ProcessName+".exe",true,window.ToInt64(),checked((int)id));
    }
    public static CopilotSettings Defaults()
    {
        CopilotControl C(string id,string label,string group,string shortcut,bool global=false)=>new(id,label,group,shortcut,global);
        CopilotControl[] Media()=>[C("media-previous","Previous track","Media","PREVTRACK",true),C("media-play","Play / pause","Media","PLAYPAUSE",true),C("media-next","Next track","Media","NEXTTRACK",true),C("volume-down","Volume down","Volume","VOLUMEDOWN",true),C("media-mute","Mute system","Volume","VOLUMEMUTE",true),C("volume-up","Volume up","Volume","VOLUMEUP",true)];
        var browsers=new[]{"chrome.exe","msedge.exe","firefox.exe","brave.exe"};
        return new(false,true,"desktop",[
            new("desktop","Desktop",[],[..Media(),C("switch","Switch app","Navigation","ALT+TAB",true),C("task-view","Task view","Navigation","WIN+TAB",true)]),
            new("youtube","YouTube",browsers,[C("play","Play / pause video","Player","K"),C("back","Back 10 seconds","Player","J"),C("forward","Forward 10 seconds","Player","L"),C("mute","Mute video","Player","M"),C("fullscreen","Full screen","Player","F"),C("escape","Exit full screen","Player","ESC"),..Media()]),
            new("spotify","Spotify",["Spotify.exe"],Media()),
            new("coding","Codex / GitHub Copilot",["Code.exe","Codex.exe","GitHubCopilot.exe"],[C("commands","Command palette (VS Code)","Navigate","CTRL+SHIFT+P"),C("files","Quick open (VS Code)","Navigate","CTRL+P"),C("find","Find","Navigate","CTRL+F"),C("undo","Undo","Edit","CTRL+Z"),C("redo","Redo","Edit","CTRL+Y"),C("save","Save","Edit","CTRL+S"),C("assistant","Assistant panel (assign shortcut)","Assistant",""),C("escape","Dismiss","Assistant","ESC")]),
            new("claude","Claude",["Claude.exe",..browsers],[C("copy","Copy selection","Edit","CTRL+C"),C("find","Find","Navigate","CTRL+F"),C("undo","Undo typing","Edit","CTRL+Z"),C("escape","Dismiss","Navigate","ESC"),C("assistant","Assistant panel (assign shortcut)","Assistant","")]),
            new("resolve","DaVinci Resolve",["Resolve.exe"],[C("reverse","Play reverse","Transport","J"),C("stop","Stop","Transport","K"),C("forward","Play forward","Transport","L"),C("frame-back","Previous frame","Timeline","LEFT"),C("frame-forward","Next frame","Timeline","RIGHT"),C("previous-edit","Previous edit","Timeline","UP"),C("next-edit","Next edit","Timeline","DOWN"),C("mark-in","Mark in","Timeline","I"),C("mark-out","Mark out","Timeline","O"),C("undo","Undo","Edit","CTRL+Z"),C("save","Save project","Edit","CTRL+S")]),
            new("photoshop","Photoshop",["Photoshop.exe"],[C("move","Move tool","Tools","V"),C("brush","Brush tool","Tools","B"),C("zoom","Zoom tool","Tools","Z"),C("swap-colors","Swap colors","Tools","X"),C("fit","Fit on screen","View","CTRL+0"),C("actual","Actual pixels","View","CTRL+1"),C("undo","Undo","Edit","CTRL+Z"),C("redo","Redo","Edit","CTRL+SHIFT+Z"),C("save","Save document","Edit","CTRL+S")])
        ]);
    }
}
