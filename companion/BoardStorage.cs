using System.Text.Json;
using System.Security.Cryptography;

namespace TabletDeck;

public sealed class BoardProfile
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Name {get;set;}="New profile";
    public List<DeckPage> Pages {get;set;}=[];
}
public sealed record FavouriteSound(string Key,string Name);
public sealed record ProfileSummary(string Id,string Name,int Pages,int Buttons,bool Active);
public sealed record BackupSummary(string Id,DateTimeOffset Created,string Reason,string ActiveProfile,int Profiles,int Pages,int Buttons,int Favourites);
public sealed record ProfileBoardSnapshot(string ProfileId,string ProfileName,List<DeckPage> Pages,string BoardRevision);
public sealed record BoardTransferRequest(string SourceProfileId,string SourceRevision,string TargetProfileId,string TargetRevision,string SourcePageId,string TargetPageId,string[] ButtonIds,string Operation);
internal sealed record BoardBackup(string Id,DateTimeOffset Created,string Reason,DeckConfig Config);

public sealed partial class DataStore
{
    private ConfigHistory history=null!;
    public string RecoveryWarning {get;private set;}="";
    private static string Revision(string id,List<DeckPage> pages)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{id,pages},Json)));
    public string BoardRevision {get{lock(gate)return Revision(Config.ProfileId,Config.Pages);}}
    public (DeckConfig Config,string Revision) ActiveSnapshot(){lock(gate)return (Copy(Config),Revision(Config.ProfileId,Config.Pages));}
    public ProfileBoardSnapshot ProfileSnapshot(string id){lock(gate){var pages=ProfilePages(Config,id);return new(id,id==Config.ProfileId?Config.ProfileName:Config.Profiles.Single(p=>p.Id==id).Name,Copy(pages),Revision(id,pages));}}
    private static List<DeckPage> ProfilePages(DeckConfig config,string id)=>id==config.ProfileId?config.Pages:config.Profiles.FirstOrDefault(p=>p.Id==id)?.Pages??throw new ArgumentException("This profile no longer exists. Refresh the profiles.");
    private static void ExpectRevision(string? expected,string actual){if(expected is not null&&expected!=actual)throw new InvalidOperationException("This board changed since you opened it. Reload the latest board, then review your draft before saving.");}
    public static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,Json),Json)!;
    internal static bool ValidSoundKey(string? key)=>key is {Length:64}&&key.All(Uri.IsHexDigit)&&key==key.ToUpperInvariant();
    private void InitializeConfig()
    {
        history=new ConfigHistory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,"config.json");var existed=File.Exists(path);
        try{Config=existed?ReadConfig(path):DeckConfig.Defaults();Validate(Config);}
        catch(Exception ex) when(ex is JsonException or ArgumentException or IOException){
            // Retain the original bytes privately; never replace a damaged file without a copy.
            if(existed)File.Copy(path,Path.Combine(DirectoryPath,"config.corrupt-"+Guid.NewGuid().ToString("N")+".json"));
            Config=history.Latest()??DeckConfig.Defaults();
            RecoveryWarning="The saved board was damaged. "+(history.HasValidBackup?"The latest valid backup was recovered.":"A default board was loaded.")+" The original file was kept privately. Re-enter your OBS password; pairing is unchanged.";
        }
        var migrated=Config.ProfileId.Length==0;
        if(migrated)Config.ProfileId=Guid.NewGuid().ToString("N");
        Validate(Config);
        if(existed&&migrated&&RecoveryWarning.Length==0)history.Capture(Config,"Before profile migration");
        if(!existed||migrated||RecoveryWarning.Length>0)Write("config.json",Config);
    }
    internal static DeckConfig ReadConfig(string path)
    {
        if(new FileInfo(path).Length>1024*1024)throw new ArgumentException("The saved board exceeds 1 MB.");
        return JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(path),Json)??throw new ArgumentException("The saved board is empty.");
    }
    private void SaveBoard(DeckConfig config,string reason,bool backup=true)
    {
        lock(gate){
            Validate(config);
            if(config.ProfileId.Length==0)config.ProfileId=Config.ProfileId;
            if(JsonSerializer.SerializeToUtf8Bytes(config,Json).Length>1024*1024)throw new ArgumentException("All profiles together must fit within 1 MB.");
            if(JsonSerializer.Serialize(Config,Json)==JsonSerializer.Serialize(config,Json))return;
            if(backup)history.Capture(Config,reason);
            Write("config.json",config);Config=config;
        }
    }
    public void SaveActive(DeckConfig submitted,bool replacePassword,string? expectedRevision=null)
    {
        lock(gate){
            if(submitted is null||submitted.ProfileId is null)throw new ArgumentException("Invalid board settings.");
            if(submitted.ProfileId.Length>0&&submitted.ProfileId!=Config.ProfileId)throw new InvalidOperationException("The profile changed. Reload Edit before saving.");
            ExpectRevision(expectedRevision,Revision(Config.ProfileId,Config.Pages));
            var next=Copy(submitted);next.ProfileId=Config.ProfileId;next.ProfileName=Config.ProfileName;
            next.Profiles=Copy(Config.Profiles);next.Favourites=Copy(Config.Favourites);
            if(!replacePassword)next.ObsPassword=Config.ObsPassword;
            // Revision-aware editors own board contents only. The legacy contract remains
            // available for older local clients; current setup tools have dedicated routes.
            if(expectedRevision is not null){next.ObsPort=Config.ObsPort;next.ObsPassword=Config.ObsPassword;next.MicInput=Config.MicInput;next.DesktopInput=Config.DesktopInput;next.MixerInputs=Copy(Config.MixerInputs);}
            SaveBoard(next,"Before board edit");
        }
    }
    public object TransferButtons(BoardTransferRequest request)
    {
        lock(gate){
            if(request is null||request.SourceRevision is null||request.TargetRevision is null||request.ButtonIds is null||request.ButtonIds.Length is <1 or >120||request.ButtonIds.Distinct(StringComparer.Ordinal).Count()!=request.ButtonIds.Length||request.Operation is not ("copy" or "move"))throw new ArgumentException("Choose buttons, two current pages, and Copy or Move.");
            var next=Copy(Config);var sourcePages=ProfilePages(next,request.SourceProfileId);var targetPages=ProfilePages(next,request.TargetProfileId);
            ExpectRevision(request.SourceRevision,Revision(request.SourceProfileId,sourcePages));ExpectRevision(request.TargetRevision,Revision(request.TargetProfileId,targetPages));
            var source=sourcePages.FirstOrDefault(p=>p.Id==request.SourcePageId)??throw new ArgumentException("The source page no longer exists.");
            var target=targetPages.FirstOrDefault(p=>p.Id==request.TargetPageId)??throw new ArgumentException("The destination page no longer exists.");
            if(source.Id==target.Id)throw new ArgumentException("Choose a different destination page.");
            if(source.LiveScenes||source.LiveSoundpad||target.LiveScenes||target.LiveSoundpad)throw new ArgumentException("Choose saved-button pages for copying or moving buttons.");
            var selected=request.ButtonIds.Select(id=>source.Buttons.FirstOrDefault(a=>a.Id==id)??throw new ArgumentException("A selected button changed. Reload the board.")).ToArray();
            if(target.Buttons.Count+selected.Length>120)throw new ArgumentException("The destination page can contain at most 120 buttons.");
            void NewIds(DeckAction action){action.Id=Guid.NewGuid().ToString("N");foreach(var step in action.Steps)NewIds(step);}
            foreach(var action in selected){var duplicate=Copy(action);NewIds(duplicate);target.Buttons.Add(duplicate);}
            if(request.Operation=="move"){var ids=request.ButtonIds.ToHashSet(StringComparer.Ordinal);source.Buttons.RemoveAll(a=>ids.Contains(a.Id));}
            SaveBoard(next,request.Operation=="copy"?"Before button copy":"Before button move");
            return new{ok=true,sourceRevision=Revision(request.SourceProfileId,ProfilePages(Config,request.SourceProfileId)),targetRevision=Revision(request.TargetProfileId,ProfilePages(Config,request.TargetProfileId))};
        }
    }
    public ProfileSummary[] ProfileList(){lock(gate)return new[]{new ProfileSummary(Config.ProfileId,Config.ProfileName,Config.Pages.Count,Config.Pages.Sum(p=>p.Buttons.Count),true)}.Concat(Config.Profiles.Select(p=>new ProfileSummary(p.Id,p.Name,p.Pages.Count,p.Pages.Sum(page=>page.Buttons.Count),false))).ToArray();}
    public string CreateProfile(string name,bool duplicate)
    {
        lock(gate){
            if(Config.Profiles.Count>=9)throw new ArgumentException("Keep at most 10 profiles.");
            var next=Copy(Config);var pages=duplicate?Copy(next.Pages):new List<DeckPage>{new(){Name="Main",Icon="home"}};
            void NewIds(DeckAction a){a.Id=Guid.NewGuid().ToString("N");foreach(var step in a.Steps)NewIds(step);}
            foreach(var page in pages){page.Id=Guid.NewGuid().ToString("N");foreach(var action in page.Buttons)NewIds(action);}
            var profile=new BoardProfile{Name=ProfileName(name),Pages=pages};next.Profiles.Add(profile);SaveBoard(next,"Before profile creation");return profile.Id;
        }
    }
    public void RenameProfile(string id,string name)
    {
        lock(gate){var next=Copy(Config);if(next.ProfileId==id)next.ProfileName=ProfileName(name);else (next.Profiles.FirstOrDefault(p=>p.Id==id)??throw new ArgumentException("Unknown profile.")).Name=ProfileName(name);SaveBoard(next,"Before profile rename");}
    }
    public string ImportProfile(string name,List<DeckPage> pages){lock(gate){
        if(Config.Profiles.Count>=9)throw new ArgumentException("Keep at most 10 profiles. Delete one before importing.");
        var copy=Copy(pages);void NewIds(DeckAction action){action.Id=Guid.NewGuid().ToString("N");foreach(var step in action.Steps)NewIds(step);}
        foreach(var page in copy){page.Id=Guid.NewGuid().ToString("N");foreach(var action in page.Buttons)NewIds(action);}
        var profile=new BoardProfile{Name=ProfileName(name),Pages=copy};var next=Copy(Config);next.Profiles.Add(profile);SaveBoard(next,"Before portable profile import");return profile.Id;
    }}
    private static string ProfileName(string? name)=>!string.IsNullOrWhiteSpace(name)&&name.Trim().Length<=30?name.Trim():throw new ArgumentException("Profile names need 1–30 characters.");
    public void DeleteProfile(string id)
    {
        lock(gate){if(id==Config.ProfileId)throw new ArgumentException("Switch to another profile before deleting this one.");var next=Copy(Config);if(next.Profiles.RemoveAll(p=>p.Id==id)!=1)throw new ArgumentException("Unknown profile.");SaveBoard(next,"Before profile deletion");}
    }
    public void SwitchProfile(string id)
    {
        lock(gate){
            if(id==Config.ProfileId)return;
            var next=Copy(Config);var profile=next.Profiles.FirstOrDefault(p=>p.Id==id)??throw new ArgumentException("Unknown profile.");next.Profiles.Remove(profile);
            next.Profiles.Add(new(){Id=next.ProfileId,Name=next.ProfileName,Pages=next.Pages});next.ProfileId=profile.Id;next.ProfileName=profile.Name;next.Pages=profile.Pages;
            SaveBoard(next,"Profile switch",false);
        }
    }
    public FavouriteSound[] Favourites(){lock(gate)return Config.Favourites.ToArray();}
    public void Favourite(string key,bool selected,SoundpadLibraryResult? library=null)
    {
        if(!ValidSoundKey(key))throw new ArgumentException("Choose a sound with a stable library identity.");
        lock(gate){
            var next=Copy(Config);next.Favourites.RemoveAll(s=>s.Key==key);
            if(selected){var matches=library?.Sounds.Where(s=>s.Key==key).ToArray();if(matches?.Length!=1)throw new ArgumentException("This sound is missing or duplicated. Refresh the library.");next.Favourites.Add(new(key,matches[0].Name));}
            SaveBoard(next,"Before favourite change");
        }
    }
    public BackupSummary[] Backups(){lock(gate)return history.List();}
    public BackupSummary BackupNow(){lock(gate)return history.Capture(Config,"Manual backup");}
    public object PreviewBackup(string id){lock(gate){var backup=history.Load(id);return new{backup=ConfigHistory.Summary(backup),profiles=ProfileSummaries(backup.Config),current=ProfileList(),credentialsPreserved=true};}}
    private static ProfileSummary[] ProfileSummaries(DeckConfig c)=>new[]{new ProfileSummary(c.ProfileId,c.ProfileName,c.Pages.Count,c.Pages.Sum(p=>p.Buttons.Count),true)}.Concat(c.Profiles.Select(p=>new ProfileSummary(p.Id,p.Name,p.Pages.Count,p.Pages.Sum(page=>page.Buttons.Count),false))).ToArray();
    public void RestoreBackup(string id)
    {
        lock(gate){var next=Copy(history.Load(id).Config);next.ObsPassword=Config.ObsPassword;SaveBoard(next,"Before backup restore");}
    }
    public string RestoreBackupProfile(string backupId,string profileId,string name)
    {
        lock(gate){var backup=history.Load(backupId);var pages=ProfilePages(backup.Config,profileId);return ImportProfile(name,Copy(pages));}
    }
}

internal sealed class ConfigHistory
{
    private readonly string directory;
    public bool HasValidBackup {get;private set;}
    public ConfigHistory(string root){directory=Path.Combine(root,"backups");Directory.CreateDirectory(directory);}
    private string FilePath(string id){if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid backup identity.");return Path.Combine(directory,id+".json");}
    public BoardBackup Load(string id)
    {
        var path=FilePath(id);if(!File.Exists(path))throw new ArgumentException("That backup is no longer available. Refresh backups.");
        if(new FileInfo(path).Length>2*1024*1024)throw new ArgumentException("Backup exceeds its size limit.");
        var backup=JsonSerializer.Deserialize<BoardBackup>(File.ReadAllText(path),DataStore.Json)??throw new ArgumentException("Invalid backup.");
        if(backup.Id!=id||backup.Config is null)throw new ArgumentException("Invalid backup contents.");
        DataStore.Validate(backup.Config);
        if(backup.Config.ObsPassword.Length>0)throw new ArgumentException("Backups must not contain credentials.");
        return backup;
    }
    private BoardBackup[] Valid()
    {
        var result=new List<BoardBackup>();
        foreach(var path in Directory.EnumerateFiles(directory,"*.json")){
            try{result.Add(Load(Path.GetFileNameWithoutExtension(path)));}catch(Exception ex) when(ex is JsonException or ArgumentException or IOException){/* A damaged snapshot cannot replace a valid board. */}
        }
        var sorted=result.OrderByDescending(b=>b.Created).ThenByDescending(b=>b.Id,StringComparer.Ordinal).ToArray();HasValidBackup=sorted.Length>0;return sorted;
    }
    public DeckConfig? Latest()=>Valid().FirstOrDefault()?.Config;
    public BackupSummary[] List()=>Valid().Select(Summary).ToArray();
    public static BackupSummary Summary(BoardBackup b)=>new(b.Id,b.Created,b.Reason,b.Config.ProfileName,b.Config.Profiles.Count+1,b.Config.Pages.Count+b.Config.Profiles.Sum(p=>p.Pages.Count),b.Config.Pages.Sum(p=>p.Buttons.Count)+b.Config.Profiles.Sum(p=>p.Pages.Sum(page=>page.Buttons.Count)),b.Config.Favourites.Count);
    public BackupSummary Capture(DeckConfig config,string reason)
    {
        var safe=DataStore.Copy(config);safe.ObsPassword="";DataStore.Validate(safe);
        var backup=new BoardBackup(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,reason,safe);var path=FilePath(backup.Id);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(backup,DataStore.Json));File.Move(path+".tmp",path);
        foreach(var old in Valid().Skip(20))File.Delete(FilePath(old.Id));
        return Summary(backup);
    }
}
