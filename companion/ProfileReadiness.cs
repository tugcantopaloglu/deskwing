using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabletDeck;

public sealed record ReadinessCheck(string Severity,string Code,string Message,string PageId="",string ButtonId="",string StepPath="",string ActionLabel="");
public sealed record ProfileReadinessReport(string ProfileId,string ProfileName,DateTimeOffset CheckedAt,ReadinessCheck[] Checks,bool Complete,string Notice);
public sealed record ObsTargetChoices(string[] Scenes,string[] Inputs,string[] Sources,string[] Filters);
public sealed record ActionReadinessReport(bool Ok,string[] Problems,string Note,ReadinessCheck[] Checks);

/// <summary>Read-only, bounded inspection of configured targets. Never executes an action.</summary>
public static class ProfileReadiness
{
    private const int MaxQueries=48,MaxActions=2400;
    private static readonly HashSet<string> ObsKinds=new(StringComparer.Ordinal){"scene","preview","transition","mute","muteOn","muteOff","source","filter","media","record","recordStart","recordStop","stream","streamStart","streamStop","replay","virtualCamera","studioMode"};
    private static readonly HashSet<string> ReadRequests=new(StringComparer.Ordinal){"GetSceneList","GetInputList","GetSceneItemList","GetSourceFilterList"};
    private sealed record Leaf(DeckPage Page,DeckAction Root,DeckAction Action,string Path);

    private sealed class Queries(Func<string,object?,CancellationToken,Task<JsonObject>> call,CancellationToken token)
    {
        private readonly Dictionary<string,Task<JsonObject>> cache=new(StringComparer.Ordinal);
        public Task<JsonObject> Read(string type,object? data=null)
        {
            if(!ReadRequests.Contains(type))throw new InvalidOperationException("Readiness only supports read-only OBS requests.");
            var key=type+JsonSerializer.Serialize(data);
            if(cache.TryGetValue(key,out var result))return result;
            token.ThrowIfCancellationRequested();
            if(cache.Count>=MaxQueries)throw new InvalidOperationException("This board needs more target checks than one readiness pass allows. Check the remaining buttons individually.");
            return cache[key]=ReadBounded(type,data);
        }
        private async Task<JsonObject> ReadBounded(string type,object? data)
        {
            // WaitAsync also bounds a misbehaving provider that ignores the supplied token.
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await call(type,data,timeout.Token).WaitAsync(timeout.Token);
        }
    }

    public static async Task<ObsTargetChoices> Targets(Func<string,object?,CancellationToken,Task<JsonObject>> obsCall,string? scene=null,string? source=null,CancellationToken ct=default)
    {
        if(scene?.Length>200||source?.Length>200)throw new ArgumentException("Choose an OBS scene or source name up to 200 characters.");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(8));
        var queries=new Queries(obsCall,budget.Token);
        var scenes=queries.Read("GetSceneList");var inputs=queries.Read("GetInputList");
        var items=string.IsNullOrWhiteSpace(scene)?Task.FromResult(new JsonObject()):queries.Read("GetSceneItemList",new{sceneName=scene});
        var filters=string.IsNullOrWhiteSpace(source)?Task.FromResult(new JsonObject()):queries.Read("GetSourceFilterList",new{sourceName=source});
        await Task.WhenAll(scenes,inputs,items,filters);
        return new(Names(await scenes,"scenes","sceneName"),Names(await inputs,"inputs","inputName"),Names(await items,"sceneItems","sourceName"),Names(await filters,"filters","filterName"));
    }

    public static async Task<ProfileReadinessReport> Check(DeckConfig snapshot,Func<string,object?,CancellationToken,Task<JsonObject>> obsCall,Func<CancellationToken,Task<SoundpadLibraryResult>> soundpadRead,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(8));
        var queries=new Queries(obsCall,budget.Token);var checks=new List<ReadinessCheck>();var complete=true;
        var files=new Dictionary<string,Task<bool>>(StringComparer.OrdinalIgnoreCase);
        async Task<bool> FilePresent(string path){
            if(files.TryGetValue(path,out var found))return await found;
            if(files.Count>=MaxQueries)throw new InvalidOperationException("File inspection limit reached.");
            async Task<bool> ReadFile(){using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(TimeSpan.FromSeconds(1));return await Task.Run(()=>File.Exists(path)).WaitAsync(timeout.Token);}
            files[path]=ReadFile();return await files[path];
        }
        var leaves=snapshot.Pages.Where(p=>!p.LiveScenes&&!p.LiveSoundpad).SelectMany(page=>page.Buttons.SelectMany(root=>Leaves(page,root,root,""))).Take(MaxActions+1).ToArray();
        if(leaves.Length>MaxActions){leaves=leaves.Take(MaxActions).ToArray();complete=false;checks.Add(new("warning","limit","This profile is too large for one readiness pass. Only the first 2,400 action steps were checked."));}
        var needsObs=leaves.Any(item=>ObsKinds.Contains(item.Action.Kind))||snapshot.Pages.Any(p=>p.LiveScenes);
        var needsSoundpad=leaves.Any(item=>item.Action.Kind.StartsWith("soundpad",StringComparison.Ordinal))||snapshot.Pages.Any(p=>p.LiveSoundpad);
        JsonObject? sceneData=null,inputData=null;SoundpadLibraryResult? library=null;
        async Task LoadScenes(){try{sceneData=await queries.Read("GetSceneList");}catch(Exception ex) when(IsAvailability(ex,ct)){complete=false;}}
        async Task LoadInputs(){try{inputData=await queries.Read("GetInputList");}catch(Exception ex) when(IsAvailability(ex,ct)){complete=false;}}
        async Task LoadSounds(){try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);timeout.CancelAfter(TimeSpan.FromSeconds(3));library=await soundpadRead(timeout.Token).WaitAsync(timeout.Token);}catch(Exception ex) when(IsAvailability(ex,ct)){complete=false;}}
        var preload=new List<Task>();if(needsObs){preload.Add(LoadScenes());preload.Add(LoadInputs());}if(needsSoundpad)preload.Add(LoadSounds());await Task.WhenAll(preload);
        var scenes=sceneData is null?null:Names(sceneData,"scenes","sceneName").ToHashSet(StringComparer.Ordinal);
        var inputs=inputData is null?null:Names(inputData,"inputs","inputName").ToHashSet(StringComparer.Ordinal);
        foreach(var page in snapshot.Pages.Where(p=>p.LiveScenes||p.LiveSoundpad)){
            if(page.LiveScenes)checks.Add(scenes is null?new("warning","obs-unavailable","Live OBS scenes could not be checked. Open OBS setup and retry.",page.Id):scenes.Count==0?new("warning","live-scenes-empty","OBS is connected but has no scenes for this live page.",page.Id):new("ok","live-scenes","Live OBS scene catalog is available.",page.Id));
            if(page.LiveSoundpad)checks.Add(library is null?new("warning","soundpad-unavailable","Live Soundpad library could not be checked. Open Soundpad on this PC and retry.",page.Id):library.Sounds.Count==0?new("warning","live-sounds-empty","Soundpad has no sounds for this live page.",page.Id):new("ok","live-sounds","Live Soundpad library is available. Rehearse its output routing.",page.Id));
        }
        foreach(var item in leaves){
            ct.ThrowIfCancellationRequested();var action=item.Action;
            void Add(string severity,string code,string message)=>checks.Add(new(severity,code,message,item.Page.Id,item.Root.Id,item.Path,item.Root.Label));
            try {
                DataStore.CheckAction(action);
                switch(action.Kind){
                    case "macro":Add("warning","empty-macro","This macro has no steps. Add an action before using it.");break;
                    case "app":case "sound":
                        try{
                            if(!Path.IsPathFullyQualified(action.Target)||!await FilePresent(action.Target))Add("error","missing-file","Choose an existing "+(action.Kind=="app"?"app or shortcut":"sound file")+" on this PC.");
                            else Add("ok","file-present",action.Kind=="app"?"The file exists. Rehearse what the app or shortcut opens.":"The sound file exists. Rehearse its playback output and stream routing.");
                        }catch(Exception ex) when(IsAvailability(ex,ct)){complete=false;Add("warning","file-unverified","This file could not be checked within the time limit. Check it on this PC and retry.");}
                        break;
                    case "hotkey":case "holdHotkey":
                        WindowsActions.ParseHotkey(action.Target);Add("warning","shortcut-rehearsal","Shortcut syntax is valid. Confirm the matching binding in the target app and rehearse it.");break;
                    case "url":
                        if(!Uri.TryCreate(action.Target,UriKind.Absolute,out var url)||url.Scheme is not ("http" or "https"))Add("error","missing-url","Enter a complete HTTP or HTTPS address.");
                        else Add("warning","website-rehearsal","Address syntax is valid. Website access was not tested.");break;
                    case "scene":case "preview":
                        CheckName(scenes,action.Target,"scene",Add);break;
                    case "mute":case "muteOn":case "muteOff":case "media":
                        CheckName(inputs,action.Target,"input",Add);break;
                    case "source":
                        if(CheckName(scenes,action.Target,"scene",null)){
                            var sources=Names(await queries.Read("GetSceneItemList",new{sceneName=action.Target}),"sceneItems","sourceName");
                            Add(sources.Contains(action.Value,StringComparer.Ordinal)?"ok":"error","source-target",sources.Contains(action.Value,StringComparer.Ordinal)?"The source exists in the configured scene.":"The source is missing from this scene. Choose its current exact name.");
                        }else CheckName(scenes,action.Target,"scene",Add);
                        break;
                    case "filter":
                        if(sceneData is null){Add("warning","obs-unavailable","OBS could not be checked. Open OBS setup and retry.");break;}
                        if(string.IsNullOrWhiteSpace(action.Target)||string.IsNullOrWhiteSpace(action.Value)){Add("error","missing-filter","Choose a source and filter name.");break;}
                        var filters=Names(await queries.Read("GetSourceFilterList",new{sourceName=action.Target}),"filters","filterName");
                        Add(filters.Contains(action.Value,StringComparer.Ordinal)?"ok":"error","filter-target",filters.Contains(action.Value,StringComparer.Ordinal)?"The filter exists on the configured source.":"The filter is missing from this source. Choose its current exact name.");break;
                    case "soundpadPlay":
                        if(library is null){Add("warning","soundpad-unavailable","Soundpad could not be checked. Keep it running on this PC, then retry.");break;}
                        if(action.SoundpadKey.Length>0){var count=library.Sounds.Count(s=>s.Key==action.SoundpadKey);Add(count==1?"ok":"error","soundpad-identity",count==1?"The linked sound has one matching library identity. Rehearse its output routing.":"The linked sound is missing or duplicated. Reimport it from the Soundpad library.");}
                        else if(!int.TryParse(action.Target,NumberStyles.None,CultureInfo.InvariantCulture,out var index)||!library.Sounds.Any(s=>s.Index==index))Add("error","soundpad-index","This Soundpad index is missing. Reimport the sound from the library.");
                        else Add("warning","soundpad-unlinked","This sound uses a changeable library index. Reimport it to link its stable identity.");
                        break;
                    case "delay":
                        if(!int.TryParse(action.Target,NumberStyles.None,CultureInfo.InvariantCulture,out var delay)||delay is <0 or >10000)Add("error","delay-range","Choose a delay from 0 to 10,000 milliseconds.");else Add("ok","delay","Delay is configured.");break;
                    case "voicemeeter":Add("warning","voicemeeter-rehearsal","Rehearse this control with Voicemeeter running and verify the intended strip or bus.");break;
                    default:
                        if(ObsKinds.Contains(action.Kind))Add(sceneData is null?"warning":"ok",sceneData is null?"obs-unavailable":"obs-connected",sceneData is null?"OBS could not be checked. Open OBS setup and retry.":"OBS is connected. Rehearse this action's OBS settings before going live.");
                        else if(action.Kind.StartsWith("soundpad",StringComparison.Ordinal))Add(library is null?"warning":"ok",library is null?"soundpad-unavailable":"soundpad-connected",library is null?"Soundpad could not be checked. Keep it running and retry.":"Soundpad library access is available. Rehearse the transport action.");
                        else Add("ok","configured","Action is configured. No action ran during this check.");
                        break;
                }
            }catch(ArgumentException ex){Add("error","invalid-action",SafeMessage(ex.Message));}
            catch(Exception ex) when(IsAvailability(ex,ct)){
                complete=false;Add("warning","target-unverified",budget.IsCancellationRequested?"The readiness time limit was reached. This target was not verified; retry or check it individually.":"This target could not be verified. Check its current OBS source/filter names and connection, then retry.");
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(snapshot.ProfileId,snapshot.ProfileName,DateTimeOffset.UtcNow,checks.ToArray(),complete,"Read-only checks; no action, sound, stream, or recording was started. Rehearse shortcuts, media behavior, and audio routing on the actual devices.");
    }

    public static async Task<ActionReadinessReport> CheckAction(DeckAction action,DeckConfig snapshot,Func<string,object?,CancellationToken,Task<JsonObject>> obsCall,Func<CancellationToken,Task<SoundpadLibraryResult>> soundpadRead,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(action);
        // Requests can contain an unsaved action. Validate its complete macro tree
        // before traversing it, including depth, step count and icon limits.
        DataStore.CheckAction(action);
        var board=new DeckConfig{ProfileId=snapshot.ProfileId,ProfileName=snapshot.ProfileName,Pages=[new(){Name="Target check",Buttons=[action]}]};
        var report=await Check(board,obsCall,soundpadRead,ct);
        var problems=report.Checks.Where(check=>check.Severity!="ok").Select(check=>(check.StepPath.Length>0?"Step "+check.StepPath+": ":"")+check.Message).ToArray();
        var warnings=report.Checks.Where(check=>check.Severity=="warning").Select(check=>(check.StepPath.Length>0?"Step "+check.StepPath+": ":"")+check.Message).ToArray();
        var note="Validation only; no action ran. "+report.Notice+(warnings.Length>0?" "+string.Join(" ",warnings.Take(6))+(warnings.Length>6?" Review the remaining warnings in Readiness.":""):"");
        return new(report.Complete&&!report.Checks.Any(check=>check.Severity=="error"),problems,note,report.Checks);
    }

    private static IEnumerable<Leaf> Leaves(DeckPage page,DeckAction root,DeckAction action,string path)
    {
        if(action.Kind=="macro"&&action.Steps.Count>0){for(var i=0;i<action.Steps.Count;i++)foreach(var child in Leaves(page,root,action.Steps[i],path.Length==0?(i+1).ToString(CultureInfo.InvariantCulture):path+"."+(i+1).ToString(CultureInfo.InvariantCulture)))yield return child;}
        else yield return new(page,root,action,path);
    }
    private static bool CheckName(HashSet<string>? catalog,string name,string kind,Action<string,string,string>? add)
    {
        if(catalog is null){add?.Invoke("warning","obs-unavailable","OBS "+kind+" names could not be checked. Open OBS setup and retry.");return false;}
        var exists=!string.IsNullOrWhiteSpace(name)&&catalog.Contains(name);add?.Invoke(exists?"ok":"error",kind+"-target",exists?"The OBS "+kind+" exists.":"The OBS "+kind+" is missing. Choose its current exact name.");return exists;
    }
    private static string[] Names(JsonObject data,string list,string name)=>(data[list] as JsonArray??[]).Select(item=>item?[name]?.GetValue<string>()??"").Where(value=>value.Length>0&&value.Length<=200).Distinct(StringComparer.Ordinal).Take(5000).ToArray();
    private static bool IsAvailability(Exception exception,CancellationToken caller)=>exception is IOException or InvalidOperationException or System.Net.WebSockets.WebSocketException or System.Net.Http.HttpRequestException or TimeoutException || exception is OperationCanceledException&&!caller.IsCancellationRequested;
    private static string SafeMessage(string message)=>message.Length<=300?message:message[..300];
}
