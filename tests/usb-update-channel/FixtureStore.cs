using System.Text.Json;

namespace TabletDeck;

// This focused updater harness does not load a board or instantiate the production store.
// DesktopPreferences needs only its JSON helpers; all files live under the test's new temp root.
internal static class DataStore
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static T ReadAuxiliaryJson<T>(string path,int maximum)
    {
        if(new FileInfo(path).Length>maximum)throw new ArgumentException("Fixture JSON exceeds its bound.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path),Json)??throw new ArgumentException("Empty fixture JSON.");
    }
    internal static void QuarantineAuxiliary(string path)=>throw new InvalidOperationException("This updater fixture must not quarantine or recover board data.");
}
