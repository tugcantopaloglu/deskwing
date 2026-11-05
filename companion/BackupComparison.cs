using System.Text.Json;

namespace TabletDeck;

public sealed record BackupProfileChange(string Id,string Name,string Status,int PagesAdded,int PagesRemoved,int PagesChanged,int ButtonsAdded,int ButtonsRemoved,int ButtonsChanged);
public sealed record BackupChanges(BackupProfileChange[] Profiles,int FavouritesAdded,int FavouritesRemoved,string[] SettingsChanged,bool ActiveProfileChanged);

public sealed partial class DataStore
{
    // A comparison describes the effect of restoring the snapshot. It deliberately
    // returns counts and names, never action targets, credentials, or file contents.
    public BackupChanges CompareBackup(string id)
    {
        lock(gate)return CompareBoards(Config,history.Load(id).Config);
    }

    internal static BackupChanges CompareBoards(DeckConfig current,DeckConfig restored)
    {
        static BoardProfile[] Profiles(DeckConfig c)=>[new(){Id=c.ProfileId,Name=c.ProfileName,Pages=c.Pages},..c.Profiles];
        static Dictionary<string,DeckAction> Buttons(List<DeckPage> pages)=>pages.SelectMany(p=>p.Buttons).ToDictionary(a=>a.Id,StringComparer.Ordinal);
        static string Serialize<T>(T item)=>JsonSerializer.Serialize(item,Json);
        var before=Profiles(current).ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var after=Profiles(restored).ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var changes=new List<BackupProfileChange>();
        foreach(var profileId in after.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal)){
            before.TryGetValue(profileId,out var oldProfile);after.TryGetValue(profileId,out var newProfile);
            var oldPages=oldProfile?.Pages??[];var newPages=newProfile?.Pages??[];
            var oldById=oldPages.ToDictionary(p=>p.Id,StringComparer.Ordinal);var newById=newPages.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            var oldButtons=Buttons(oldPages);var newButtons=Buttons(newPages);
            var pagesAdded=newById.Keys.Count(id=>!oldById.ContainsKey(id));var pagesRemoved=oldById.Keys.Count(id=>!newById.ContainsKey(id));
            var pagesChanged=newById.Count(pair=>oldById.TryGetValue(pair.Key,out var old)&&Serialize(old)!=Serialize(pair.Value));
            var buttonsAdded=newButtons.Keys.Count(id=>!oldButtons.ContainsKey(id));var buttonsRemoved=oldButtons.Keys.Count(id=>!newButtons.ContainsKey(id));
            var buttonsChanged=newButtons.Count(pair=>oldButtons.TryGetValue(pair.Key,out var old)&&Serialize(old)!=Serialize(pair.Value));
            var status=oldProfile is null?"added":newProfile is null?"removed":Serialize(oldProfile)==Serialize(newProfile)?"unchanged":"changed";
            changes.Add(new(profileId,(newProfile??oldProfile)!.Name,status,pagesAdded,pagesRemoved,pagesChanged,buttonsAdded,buttonsRemoved,buttonsChanged));
        }
        var oldFavourites=current.Favourites.Select(f=>f.Key).ToHashSet(StringComparer.Ordinal);var newFavourites=restored.Favourites.Select(f=>f.Key).ToHashSet(StringComparer.Ordinal);
        var settings=new List<string>();
        if(current.ObsPort!=restored.ObsPort)settings.Add("OBS connection port");
        if(current.MicInput!=restored.MicInput)settings.Add("Microphone input");
        if(current.DesktopInput!=restored.DesktopInput)settings.Add("Desktop audio input");
        if(Serialize(current.MixerInputs)!=Serialize(restored.MixerInputs))settings.Add("Additional mixer inputs");
        return new(changes.ToArray(),newFavourites.Except(oldFavourites).Count(),oldFavourites.Except(newFavourites).Count(),settings.ToArray(),current.ProfileId!=restored.ProfileId);
    }
}
