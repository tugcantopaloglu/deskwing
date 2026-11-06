using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace TabletDeck;

public static class StudioTools
{
    public static void ValidateMixer(IReadOnlyList<MixerChannel> channels)
    {
        if(channels.Count>8||channels.Any(c=>c is null||!Guid.TryParseExact(c.Id,"N",out _)||string.IsNullOrWhiteSpace(c.Label)||c.Label.Length>40||string.IsNullOrWhiteSpace(c.Input)||c.Input.Length>200)||channels.Select(c=>c.Id).Distinct(StringComparer.Ordinal).Count()!=channels.Count)throw new ArgumentException("Choose up to 8 extra OBS inputs with unique identities and labels.");
    }
    public static void ValidateIcon(string image)
    {
        if(image.Length==0)return;
        if(!image.StartsWith("data:image/png;base64,",StringComparison.Ordinal)||image.Length>100000)throw new ArgumentException("Choose a PNG icon under 72 KB and at most 512 × 512 pixels.");
        try{
            var bytes=Convert.FromBase64String(image[22..]);
            if(bytes.Length<33||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)) is <1 or >512||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4)) is <1 or >512)throw new ArgumentException("Invalid PNG dimensions.");
            using var stream=new MemoryStream(bytes);using var bitmap=Image.FromStream(stream,false,true);
            if(bitmap.RawFormat.Guid!=ImageFormat.Png.Guid||bitmap.Width>512||bitmap.Height>512)throw new ArgumentException("Invalid PNG image.");
        }catch(Exception ex) when(ex is FormatException or ArgumentException or ExternalException){throw new ArgumentException("Choose a valid PNG icon under 72 KB and at most 512 × 512 pixels.");}
    }
    public sealed record AppChoice(string Name,string Path);
    public static AppChoice[] InstalledApps()
    {
        var apps=new Dictionary<string,AppChoice>(StringComparer.OrdinalIgnoreCase);
        void Add(string path,string name){if(apps.Count<600&&Path.IsPathFullyQualified(path)&&File.Exists(path)&&new[]{".exe",".lnk"}.Contains(Path.GetExtension(path).ToLowerInvariant()))apps.TryAdd(path,new(name.Length>120?name[..120]:name,path));}
        foreach(var folder in new[]{Environment.SpecialFolder.StartMenu,Environment.SpecialFolder.CommonStartMenu}){
            var root=Environment.GetFolderPath(folder);if(!Directory.Exists(root))continue;
            foreach(var path in Directory.EnumerateFiles(root,"*.lnk",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}).Take(600))Add(path,Path.GetFileNameWithoutExtension(path));
        }
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}){
            using var root=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");if(root is null)continue;
            foreach(var name in root.GetSubKeyNames().Take(600)){using var key=root.OpenSubKey(name);if(key?.GetValue(null) is string path)Add(path.Trim('"'),Path.GetFileNameWithoutExtension(name));}
        }
        return apps.Values.OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static readonly SemaphoreSlim pickerGate=new(1,1);
    public static async Task<string?> PickFile(string kind)
    {
        if(kind is not ("sound" or "app"))throw new ArgumentException("Choose an app or sound file.");
        if(!await pickerGate.WaitAsync(0))throw new InvalidOperationException("A file chooser is already open. Finish it before choosing another file.");
        try{
            var result=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{try{using var dialog=new Forms.OpenFileDialog{Title=kind=="app"?"Choose a Windows app":"Choose a sound",Filter=kind=="app"?"Windows apps (*.exe;*.lnk)|*.exe;*.lnk":"Audio files (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma",CheckFileExists=true,Multiselect=false,RestoreDirectory=true};result.TrySetResult(dialog.ShowDialog()==Forms.DialogResult.OK?dialog.FileName:null);}catch(Exception ex){result.TrySetException(ex);}}){IsBackground=true,Name="DeskWing file chooser"};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();return await result.Task;
        }finally{pickerGate.Release();}
    }
    public static object CheckTarget(DeckAction action)
    {
        DataStore.CheckAction(action);var problems=new List<string>();
        void Check(DeckAction a){
            if(a.Kind is "app" or "sound"&&(!Path.IsPathFullyQualified(a.Target)||!File.Exists(a.Target)))problems.Add(a.Label+": choose an existing file on this PC.");
            if(a.Kind is "scene" or "preview" or "mute" or "muteOn" or "muteOff"&&string.IsNullOrWhiteSpace(a.Target))problems.Add(a.Label+": enter an exact OBS name.");
            if(a.Kind is "hotkey" or "holdHotkey")try{WindowsActions.ParseHotkey(a.Target);}catch(ArgumentException ex){problems.Add(a.Label+": "+ex.Message);}
            foreach(var step in a.Steps)Check(step);
        }
        Check(action);return new{ok=problems.Count==0,problems,note="Validation only; no action ran. File checks do not verify what an app or shortcut will do. OBS and Soundpad names still need a rehearsal."};
    }
}
