using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TabletDeck;

public sealed class DeckAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "New button";
    public string Kind { get; set; } = "scene";
    public string Target { get; set; } = "";
    public string Value { get; set; } = "";
    public string SoundpadKey { get; set; } = "";
    public string Icon { get; set; } = "monitor";
    public string IconImage { get; set; } = "";
    public string Color { get; set; } = "violet";
    public bool Confirm { get; set; }
    public List<DeckAction> Steps { get; set; } = [];
}
public sealed class DeckPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New page";
    public string Icon { get; set; } = "grid";
    public bool LiveScenes { get; set; }
    public bool LiveSoundpad { get; set; }
    public string LiveSoundpadOutput { get; set; } = "mic";
    public List<DeckAction> Buttons { get; set; } = [];
}
public sealed class DeckConfig
{
    public string ProfileId {get;set;}="";
    public string ProfileName {get;set;}="Default";
    public List<BoardProfile> Profiles {get;set;}=[];
    public List<FavouriteSound> Favourites {get;set;}=[];
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";
    public string MicInput { get; set; } = "Mic/Aux";
    public string DesktopInput { get; set; } = "Desktop Audio";
    public List<MixerChannel> MixerInputs {get;set;}=[];
    public List<DeckPage> Pages { get; set; } = [];
    public static DeckConfig Defaults()
    {
        DeckAction A(string label, string kind, string target, string icon, string color, bool confirm = false) =>
            new() { Label = label, Kind = kind, Target = target, Icon = icon, Color = color, Confirm = confirm };
        var main = new DeckPage { Name = "Main", Icon = "home", Buttons = [
            A("Gameplay", "scene", "Gameplay", "gamepad", "violet"),
            A("Just chatting", "scene", "Just chatting", "chat", "violet"),
            A("Be right back", "scene", "Be right back", "bed", "violet"),
            A("Starting", "scene", "Starting", "clapper", "violet"),
            A("Applause", "sound", Path.Combine(AppContext.BaseDirectory,"Sounds","applause.wav"), "clap", "amber"),
            A("Air horn", "sound", Path.Combine(AppContext.BaseDirectory,"Sounds","air-horn.wav"), "horn", "amber"),
            A("Stop sounds", "stopSounds", "", "stop", "amber"),
            A("Save replay", "replay", "", "replay", "amber"),
            A("Open OBS", "app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"), "obs", "blue"),
            A("Open Discord", "app", FindDiscord(), "discord", "blue"),
            A("Record", "record", "", "record", "red", true),
            A("Start stream", "stream", "", "broadcast", "red", true)
        ] };
        return new DeckConfig { Pages = [main,
            new() { Name="Scenes", Icon="image", Buttons=main.Buttons.Take(4).Select(a=>A(a.Label,a.Kind,a.Target,a.Icon,a.Color)).ToList() },
            new() { Name="Sounds", Icon="speaker", Buttons=main.Buttons.Skip(4).Take(3).Select(a=>A(a.Label,a.Kind,a.Target,a.Icon,a.Color)).ToList() },
            new() { Name="Apps", Icon="grid", Buttons=main.Buttons.Skip(8).Take(2).Select(a=>A(a.Label,a.Kind,a.Target,a.Icon,a.Color)).ToList() },
            new() { Name="Macros", Icon="list", Buttons=[] }
        ] };
    }
    private static string FindDiscord()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Discord");
        if(!Directory.Exists(root))return "";
        return Directory.EnumerateDirectories(root,"app-*").OrderByDescending(p=>p,StringComparer.Ordinal).Select(p=>Path.Combine(p,"Discord.exe")).FirstOrDefault(File.Exists)??"";
    }
}
public sealed record PairedDevice(string Id, string Name, string TokenHash, DateTimeOffset Added);
public sealed partial class DataStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 24 };
    public string DirectoryPath { get; }
    public DeckConfig Config { get; private set; }=null!;
    public List<PairedDevice> Devices { get; private set; }
    public string DeviceRecoveryWarning {get;private set;}="";
    internal const int MaxPairedDevices=100;
    private readonly object gate = new();
    public DataStore(string path)
    {
        DirectoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(DirectoryPath);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user unavailable");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
        InitializeConfig();
        Devices = LoadDevices();
    }
    // Keep auxiliary reads bounded and deny concurrent writers while validating their bytes.
    internal static T ReadAuxiliaryJson<T>(string path,int maxBytes) where T:class
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>maxBytes)throw new ArgumentException("The saved settings exceed their size limit.");
        return JsonSerializer.Deserialize<T>(input,Json)??throw new ArgumentException("The saved settings are empty.");
    }
    internal static void QuarantineAuxiliary(string path)
    {
        var preserved=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+".corrupt-"+Guid.NewGuid().ToString("N")+".json");
        // If preservation fails, do not replace the user's original file.
        File.Copy(path,preserved);
    }
    private List<PairedDevice> LoadDevices()
    {
        var path=Path.Combine(DirectoryPath,"devices.json");
        if(!File.Exists(path))return [];
        try {
            var devices=ReadAuxiliaryJson<List<PairedDevice>>(path,1024*1024);
            ValidateDevices(devices);return devices;
        }catch(Exception ex) when(ex is JsonException or ArgumentException) {
            QuarantineAuxiliary(path);
            Write("devices.json",new List<PairedDevice>());
            DeviceRecoveryWarning="Saved tablet pairings were damaged and cleared. The original file was kept privately. Pair each trusted tablet again; previous tablet credentials are no longer accepted.";
            return [];
        }
    }
    private static void ValidateDevices(IReadOnlyList<PairedDevice> devices)
    {
        if(devices.Count>MaxPairedDevices)throw new ArgumentException("Keep at most 100 paired tablets. Revoke an unused tablet before pairing another.");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var hashes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var device in devices) {
            if(device is null||string.IsNullOrWhiteSpace(device.Id)||device.Id.Length>128||device.Id.Any(char.IsControl)||device.Name is null||device.Name.Length>80||device.Name.Any(char.IsControl)||device.TokenHash is not {Length:64}||!device.TokenHash.All(Uri.IsHexDigit)||device.Added==default||!ids.Add(device.Id)||!hashes.Add(device.TokenHash))
                throw new ArgumentException("Invalid or duplicate tablet pairing record.");
        }
    }
    private T? Read<T>(string file) => File.Exists(Path.Combine(DirectoryPath,file)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(DirectoryPath,file)), Json) : default;
    private void Write<T>(string file,T value)
    {
        var dest=Path.Combine(DirectoryPath,file); var temp=dest+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(value,Json)); File.Move(temp,dest,true);
    }
    public void Save(DeckConfig config)=>SaveBoard(config,"Before board edit");
    public void Modify(Action<DeckConfig> change)
    {
        lock(gate) {
            var copy=JsonSerializer.Deserialize<DeckConfig>(JsonSerializer.Serialize(Config,Json),Json)!;
            change(copy);Save(copy);
        }
    }
    public void Reorder(IReadOnlyList<PageOrder> order)
    {
        Modify(c=>{
            if(order.Count!=c.Pages.Count||order.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=c.Pages.Count)throw new ArgumentException("The pages changed. Reload the board before arranging.");
            foreach(var entry in order) {
                var page=c.Pages.FirstOrDefault(p=>p.Id==entry.Id)??throw new ArgumentException("Unknown page.");
                var byId=page.Buttons.ToDictionary(a=>a.Id,StringComparer.Ordinal);
                if(entry.ButtonIds.Length!=byId.Count||entry.ButtonIds.Distinct(StringComparer.Ordinal).Count()!=byId.Count||entry.ButtonIds.Any(id=>!byId.ContainsKey(id)))throw new ArgumentException("Arranging may only reorder existing buttons within their page.");
                page.Buttons=entry.ButtonIds.Select(id=>byId[id]).ToList();
            }
        });
    }
    public void ImportSounds(string pageId,IReadOnlyList<LibrarySound> sounds,string output)
    {
        if(sounds.Count is <1 or >120)throw new ArgumentException("Select between 1 and 120 sounds.");
        foreach(var sound in sounds)SoundpadClient.BuildCommand("soundpadPlay",sound.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),output);
        Modify(c=>{
            var page=c.Pages.FirstOrDefault(p=>p.Id==pageId)??throw new ArgumentException("Select an existing page.");
            foreach(var sound in sounds) {
                var target=sound.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(page.Buttons.Any(a=>a.Kind=="soundpadPlay"&&a.Value==output&&(sound.Key.Length>0?a.SoundpadKey==sound.Key:a.Target==target)))continue;
                page.Buttons.Add(new DeckAction{Label=sound.Name.Length>60?sound.Name[..60]:sound.Name,Kind="soundpadPlay",Target=target,Value=output,SoundpadKey=sound.Key,Icon="speaker",Color="amber"});
            }
        });
    }
    public void AddDevice(PairedDevice device) { lock(gate) { List<PairedDevice> next=[..Devices,device];ValidateDevices(next);Write("devices.json",next);Devices=next; } }
    private readonly Dictionary<string,DateTimeOffset> lastSeen=new(StringComparer.Ordinal);
    public DateTimeOffset? LastSeen(PairedDevice device){lock(gate)return lastSeen.TryGetValue(device.TokenHash,out var seen)?seen:null;}
    public void Revoke(string id) { lock(gate) {foreach(var device in Devices.Where(d=>d.Id==id))lastSeen.Remove(device.TokenHash); Devices=Devices.Where(x=>x.Id!=id).ToList();Write("devices.json",Devices); } }
    public bool Authenticate(string token)
    {
        if(token.Length < 32 || token.Length > 128) return false;
        var hash=SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        lock(gate){var device=Devices.FirstOrDefault(x=>CryptographicOperations.FixedTimeEquals(hash,Convert.FromHexString(x.TokenHash)));if(device is null)return false;lastSeen[device.TokenHash]=DateTimeOffset.UtcNow;return true;}
    }
    public static void Validate(DeckConfig c)
    {
        if(c is null||c.Pages is null||c.Profiles is null||c.Favourites is null||c.ProfileId is null||c.ObsPassword is null||c.MicInput is null||c.DesktopInput is null||c.MixerInputs is null)throw new ArgumentException("Invalid board settings.");
        StudioTools.ValidateMixer(c.MixerInputs);
        if(c.ObsPort is <1 or >65535 || c.Pages.Count is <1 or >30 || c.ObsPassword.Length>1024) throw new ArgumentException("Invalid settings or page count (1–30).");
        if(c.Profiles.Count>9||c.Favourites.Count>200||string.IsNullOrWhiteSpace(c.ProfileName)||c.ProfileName.Length>30)throw new ArgumentException("Keep at most 10 profiles and 200 favourites; profile names need 1–30 characters.");
        var profileIds=new HashSet<string>(StringComparer.Ordinal){c.ProfileId};
        if(c.ProfileId.Length>0&&!Guid.TryParseExact(c.ProfileId,"N",out _))throw new ArgumentException("Invalid profile identity.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pageIds=new HashSet<string>(StringComparer.Ordinal);
        ValidatePages(c.Pages,ids,pageIds);
        foreach(var profile in c.Profiles){
            if(profile is null||!Guid.TryParseExact(profile.Id,"N",out _)||!profileIds.Add(profile.Id)||string.IsNullOrWhiteSpace(profile.Name)||profile.Name.Length>30)throw new ArgumentException("Invalid or duplicate profile.");
            ValidatePages(profile.Pages,ids,pageIds);
        }
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var favourite in c.Favourites)if(favourite is null||!ValidSoundKey(favourite.Key)||!keys.Add(favourite.Key)||string.IsNullOrWhiteSpace(favourite.Name)||favourite.Name.Length>120)throw new ArgumentException("Invalid or duplicate favourite.");
    }
    private static void ValidatePages(List<DeckPage> pages,HashSet<string> ids,HashSet<string> pageIds)
    {
        if(pages is not null&&pages.Count(p=>p?.LiveSoundpad==true)>1)throw new ArgumentException("Choose one live Soundpad page per profile.");
        if(pages is null||pages.Count is <1 or >30)throw new ArgumentException("Each profile needs 1–30 pages.");
        foreach(var page in pages)
        {
            if(page is not null&&(page.LiveScenes&&page.LiveSoundpad||page.LiveSoundpadOutput is not ("mic" or "speakers" or "both")))throw new ArgumentException("Choose one live source per page and a valid Soundpad output.");
            if(page is null||string.IsNullOrWhiteSpace(page.Id)||!pageIds.Add(page.Id)||string.IsNullOrWhiteSpace(page.Name)||page.Name.Length>30||page.Buttons is null||page.Buttons.Count>120) throw new ArgumentException("Each page needs a unique identity, name, and at most 120 buttons.");
            foreach(var action in page.Buttons)
            {
                if(action is null||string.IsNullOrWhiteSpace(action.Id)||!ids.Add(action.Id)) throw new ArgumentException("Button IDs must be unique across profiles.");
                ValidateAction(action,0);
            }
        }
    }
    private static void ValidateAction(DeckAction a,int depth)
    {
        if(a is null||a.Kind is null||a.Label is null||a.Target is null||a.Value is null||a.SoundpadKey is null||a.Steps is null||a.IconImage is null)throw new ArgumentException("Invalid button fields.");
        StudioTools.ValidateIcon(a.IconImage);
        string[] kinds=["scene","preview","transition","sound","stopSounds","app","url","hotkey","holdHotkey","mute","muteOn","muteOff","source","filter","media","record","recordStart","recordStop","stream","streamStart","streamStop","replay","virtualCamera","studioMode","macro","delay","voicemeeter"];
        if(depth>3||(!kinds.Contains(a.Kind)&&!SoundpadClient.Supports(a.Kind))||a.Label.Length is <1 or >60||a.Target.Length>2048||a.Value.Length>1024||a.Steps.Count>30) throw new ArgumentException("Invalid button or macro (maximum 30 steps, 3 nesting levels).");
        if(SoundpadClient.Supports(a.Kind)&&!(a.Kind=="soundpadPlay"&&a.Target.Length==0)) SoundpadClient.BuildCommand(a.Kind,a.Target,a.Value);
        if(a.SoundpadKey.Length>0&&(a.SoundpadKey.Length!=64||!a.SoundpadKey.All(Uri.IsHexDigit)))throw new ArgumentException("Invalid Soundpad binding. Reimport the sound from the library.");
        if(a.Kind=="holdHotkey") {if(depth>0||a.Confirm)throw new ArgumentException("Hold shortcuts must be standalone buttons without confirmation.");WindowsActions.ParseHotkey(a.Target);}
        if(a.Kind=="delay"&&(!int.TryParse(a.Target,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var delay)||delay is <0 or >10000))throw new ArgumentException("Delay must be a whole number from 0 to 10000 milliseconds.");
        if(a.Kind=="voicemeeter")VoicemeeterClient.Validate(a.Target,a.Value);
        if(a.Kind=="app" && a.Target.Length>0 && (!Path.IsPathFullyQualified(a.Target)||!new[]{".exe",".lnk"}.Contains(Path.GetExtension(a.Target).ToLowerInvariant()))) throw new ArgumentException("App actions require an absolute .exe or .lnk path.");
        if(a.Kind=="url"&&a.Target.Length>0&&(!Uri.TryCreate(a.Target,UriKind.Absolute,out var u)||u.Scheme is not ("https" or "http"))) throw new ArgumentException("Website actions require http:// or https://.");
        if(a.Kind=="sound"&&a.Target.Length>0&&(!Path.IsPathFullyQualified(a.Target)||!new[]{".wav",".mp3",".wma"}.Contains(Path.GetExtension(a.Target).ToLowerInvariant()))) throw new ArgumentException("Sound actions require an absolute WAV, MP3, or WMA file path.");
        foreach(var s in a.Steps) ValidateAction(s,depth+1);
    }
    public static void CheckAction(DeckAction action)=>ValidateAction(action,0);
}
public sealed record MixerChannel(string Id,string Label,string Input);
public sealed record PageOrder(string Id,string[] ButtonIds);
