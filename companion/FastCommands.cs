using System.Text.Json;

namespace TabletDeck;

// HTTP and persistent TLS commands share the same validation and execution.
internal sealed class FastCommands(DataStore store,DeckEngine engine,HoldKeys holds,AppAudioMixer appMixer)
{
    internal const int PenRequestBytes=16*1024;
    private readonly object configurationGate=new();
    private readonly Dictionary<string,DateTimeOffset> profileLocks=new();
    private DateTimeOffset manualProfileUntil;
    private bool maintenance,shuttingDown;
    private int keyboardActions;
    private int activeControls;
    private WindowsPen? pen;
    private bool PenActive=>pen?.Active==true;
    public bool Maintenance{get{lock(configurationGate)return maintenance;}}
    public bool CanUpdateTablet{get{lock(configurationGate)return !maintenance&&activeControls==0&&engine.Execution.Status.Active is null&&!holds.Active&&!PenActive&&!profileLocks.Any(p=>p.Key!="admin"&&p.Value>DateTimeOffset.UtcNow);}}
    public void BeginMaintenance(){lock(configurationGate){if(activeControls>0||engine.Execution.Status.Active is not null||holds.Active||PenActive)throw new InvalidOperationException("Finish the running action, release held keys, and stop Pen tablet before installing.");pen?.CancelAll("The companion is restarting for an update.");maintenance=true;}}
    public void EndMaintenance(){lock(configurationGate){if(!shuttingDown)maintenance=false;}}
    public void BeginShutdown()
    {
        lock(configurationGate){
            // Admission closes before cleanup so a live fast link cannot renew input
            // while the HTTP host waits for its graceful shutdown period.
            shuttingDown=true;maintenance=true;
            try{var active=engine.Execution.Status.Active;if(active is not null)engine.Execution.Cancel(active.Id);}
            finally{try{pen?.Configure(new(false));}finally{holds.ReleaseAll();}}
        }
    }
    public void ProfileLock(string principal,bool active){lock(configurationGate){foreach(var key in profileLocks.Where(p=>p.Value<DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())profileLocks.Remove(key);if(active)profileLocks[principal]=DateTimeOffset.UtcNow.AddSeconds(12);else profileLocks.Remove(principal);}}
    public void ManualProfile(string id){lock(configurationGate){ChangeBoard(()=>store.SwitchProfile(id));manualProfileUntil=DateTimeOffset.UtcNow.AddSeconds(30);}}
    public bool AutoProfile(string id){lock(configurationGate){if(maintenance||activeControls>0||engine.Execution.Status.Active is not null||holds.Active||DateTimeOffset.UtcNow<manualProfileUntil||profileLocks.Values.Any(until=>until>DateTimeOffset.UtcNow))return false;if(store.Config.ProfileId!=id){store.SwitchProfile(id);engine.Invalidate();}return true;}}
    public void ChangeBoard(System.Action change){lock(configurationGate){RequireAvailable();if(activeControls>0||engine.Execution.Status.Active is not null)throw new InvalidOperationException("Finish or cancel the running action before changing boards.");holds.ReleaseAll();change();engine.Invalidate();}}
    internal sealed record HoldLease(string Lease,int ExpiresIn=3);
    public void Release(string principal,string lease)=>holds.Release(principal,lease);
    public void ConfigurePen(WindowsPen service){lock(configurationGate){if(pen is not null)throw new InvalidOperationException("The pen service is already configured.");pen=service;}}
    public void ReleasePen(string principal,string lease)=>pen?.Release(principal,lease);
    public void RevokePen(string principal)=>pen?.ReleaseOwner(principal);
    public PenStatus ConfigurePenSettings(PenSettings settings)
    {
        lock(configurationGate){if(settings.Enabled)RequireAvailable();return (pen??throw new InvalidOperationException("PC pen input is unavailable.")).Configure(settings);}
    }
    public object Pen(PenRequest request,string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock(configurationGate){
            var service=pen??throw new InvalidOperationException("PC pen input is unavailable.");
            // Releasing a pointer must remain possible during shutdown/update recovery.
            if(request.Operation is "end" or "cancel")return service.Execute(request,principal);
            RequireAvailable();
            if(request.Operation=="begin"&&(engine.Execution.Status.Active is not null||keyboardActions>0||holds.Active))throw new InvalidOperationException("Finish the current action and release held keys before starting Pen tablet.");
            return service.Execute(request,principal);
        }
    }
    private void RequireAvailable(){if(shuttingDown)throw new InvalidOperationException("The companion is closing. Reopen it before using controls.");if(maintenance)throw new InvalidOperationException("The companion is restarting for an update. Try again after it reconnects.");}
    private static bool UsesKeyboard(DeckAction action)=>action.Kind is "hotkey" or "holdHotkey"||action.Kind=="macro"&&action.Steps.Any(UsesKeyboard);
    private Task StartAction(DeckAction action)
    {
        RequireAvailable();
        var keyboard=UsesKeyboard(action);
        if(PenActive&&action.Kind is "hotkey" or "holdHotkey" or "macro" or "app" or "url")throw new InvalidOperationException("Stop Pen tablet before running keyboard shortcuts, macros, or opening Windows apps.");
        if(keyboard&&holds.Active)throw new InvalidOperationException("Release held keys before using a keyboard shortcut or a macro that contains one.");
        if(engine.Execution.Status.Active is not null)throw new InvalidOperationException("An action is still running. Wait for it to finish or cancel it.");
        if(keyboard)keyboardActions++;
        return Run();
        async Task Run(){try{await engine.Execute(action);}finally{if(keyboard)lock(configurationGate)keyboardActions--;}}
    }
    private async Task Control(Func<Task> operation)
    {
        Task running;lock(configurationGate){RequireAvailable();activeControls++;try{running=operation();}catch{activeControls--;throw;}}
        try{await running;}finally{lock(configurationGate)activeControls--;}
    }
    public Task SetAppAudio(AppAudioRequest request)=>Control(()=>appMixer.Set(request.Id,request.Percent,request.Muted));
    public Task SetVolume(AudioRequest request)=>Control(()=>engine.Volume(request.Channel,request.Db));
    public Task ToggleMute(MuteRequest request)=>Control(()=>engine.Mute(request.Channel));
    public async Task ExecuteConfigured(DeckAction action)
    {
        Task running;
        lock(configurationGate){
            running=StartAction(action);
        }
        await running;
    }
    public async Task ExecuteExternal(string label,Func<CancellationToken,Task> operation)
    {
        Task running;
        lock(configurationGate){
            RequireAvailable();
            if(holds.Active)throw new InvalidOperationException("Release held keys before using player controls.");
            var action=new DeckAction{Kind="mediaSession",Label=label};
            running=engine.Execution.Run(action,ct=>{engine.Execution.Step(action);return operation(ct);});
        }
        await running;
    }
    public async Task<object> Action(ActionRequest request)
    {
        Task running;
        lock(configurationGate){
            RequireAvailable();
            var action=request.Id.StartsWith(LiveSoundpad.Prefix,StringComparison.Ordinal)?LiveSoundpad.Action(request.Id,store.Config):request.Id.StartsWith(LiveScenes.Prefix,StringComparison.Ordinal)&&store.Config.Pages.Any(p=>p.LiveScenes)?LiveScenes.Action(request.Id):store.Config.Pages.SelectMany(page=>page.Buttons).FirstOrDefault(action=>action.Id==request.Id)??throw new ArgumentException("This button no longer exists. Reload the board.");
            if(DeckEngine.NeedsConfirm(action)&&!request.Confirmed)throw new InvalidOperationException("Confirm this action on the tablet.");
            running=StartAction(action);
        }
        await running;return new{ok=true};
    }
    public object Hold(HoldRequest request,string principal)
    {
        if(request.Operation=="release"){holds.Release(principal,request.Lease);return new{ok=true};}
        if(request.Operation=="renew"){lock(configurationGate){RequireAvailable();holds.Renew(principal,request.Lease);return new{ok=true};}}
        if(request.Operation!="begin")throw new ArgumentException("Unknown hold operation.");
        lock(configurationGate){RequireAvailable();if(keyboardActions>0||PenActive)throw new InvalidOperationException("Finish the keyboard action or macro and stop Pen tablet before holding a shortcut.");var action=store.Config.Pages.SelectMany(page=>page.Buttons).FirstOrDefault(action=>action.Id==request.Id);if(action?.Kind!="holdHotkey")throw new ArgumentException("Choose a configured hold shortcut button.");return new HoldLease(holds.Begin(principal,action.Target));}
    }
    public static bool Supports(string path)=>path is "/api/action" or "/api/audio" or "/api/mute" or "/api/hold" or "/api/apps" or "/api/ping" or "/api/cancel" or "/api/pen";
    public async Task<object> Execute(string path,JsonElement body,string principal)
    {
        if(Maintenance&&path is not ("/api/ping" or "/api/cancel" or "/api/hold" or "/api/pen"))throw new InvalidOperationException("The companion is restarting for an update.");
        switch(path) {
            case "/api/ping":return new{pong=true};
            case "/api/cancel":return new{cancelled=engine.Execution.Cancel((body.Deserialize<CancelRequest>(DataStore.Json)??throw new ArgumentException("Missing cancellation request.")).Id)};
            case "/api/action":return await Action(body.Deserialize<ActionRequest>(DataStore.Json)??throw new ArgumentException("Missing action."));
            case "/api/hold":return Hold(body.Deserialize<HoldRequest>(DataStore.Json)??throw new ArgumentException("Missing hold request."),principal);
            case "/api/pen":if(System.Text.Encoding.UTF8.GetByteCount(body.GetRawText())>PenRequestBytes)throw new ArgumentException("Keep pen packets below 16 KB.");return Pen(body.Deserialize<PenRequest>(DataStore.Json)??throw new ArgumentException("Missing pen request."),principal);
            case "/api/apps":{await SetAppAudio(body.Deserialize<AppAudioRequest>(DataStore.Json)??throw new ArgumentException("Missing app volume request."));return new{ok=true};}
            case "/api/audio":{await SetVolume(body.Deserialize<AudioRequest>(DataStore.Json)??throw new ArgumentException("Missing audio request."));return new{ok=true};}
            case "/api/mute":{await ToggleMute(body.Deserialize<MuteRequest>(DataStore.Json)??throw new ArgumentException("Missing mute request."));return new{ok=true};}
            default:throw new ArgumentException("This request is not allowed on the fast control link.");
        }
    }
}
