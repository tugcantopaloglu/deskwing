using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

const string marker=".tabletdeck-install.json";
// Installation is confined to an application-owned, per-user location.
// Test mode uses one separate fixed temporary folder and never creates shortcuts.
var programs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs");
var install=Path.Combine(programs,"DeskWing");
// Existing installations retain their location and ownership record; user data is separate.
var legacyInstall=Path.Combine(programs,"TabletDeckStudio");
if(!Directory.Exists(install)&&Directory.Exists(legacyInstall))install=legacyInstall;
var testMode=args.Contains("--test-mode");
if(testMode)install=Path.Combine(Path.GetTempPath(),"TabletDeckStudioSetupTests","TabletDeckStudio");
try {
    var flags=new HashSet<string>(StringComparer.Ordinal){"--uninstall","--test-mode","--no-shortcuts","--verify-payload","--quiet","--update-running","--restart"};var values=new HashSet<string>(StringComparer.Ordinal){"--wait-pid","--expected-exe","--expected-version"};
    for(var index=0;index<args.Length;index++){if(flags.Contains(args[index]))continue;if(values.Contains(args[index])&&index+1<args.Length&&!args[index+1].StartsWith("--",StringComparison.Ordinal)){index++;continue;}throw new IOException("Unknown or incomplete installer option. No files were changed.");}
    if(args.Contains("--uninstall")){Uninstall(install,testMode||args.Contains("--no-shortcuts"));return;}
    using var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskWing.Payload.zip")??throw new IOException("Installer payload is missing. Build with -p:PayloadArchive=FULL_ZIP_PATH.");
    using var archive=new ZipArchive(payload,ZipArchiveMode.Read);
    var entries=Verify(archive);
    if(args.Contains("--verify-payload")){Console.WriteLine("PASS embedded payload paths and SHA-256 checksums ("+entries.Count+" files).");return;}
    if(args.Contains("--update-running")){RunningUpdate.Apply(entries,args,testMode,Inside);return;}
    if(!args.Contains("--quiet")) {
        Console.WriteLine("DeskWing installer\nInstall to: "+install+"\nExisting DeskWing pairing and settings are retained. Exit the companion from its tray menu before updating. No apps are launched by this installer.\nType INSTALL to continue, or close this window:");
        if(Console.ReadLine()!="INSTALL")return;
    }
    var parent=Directory.GetParent(install)?.FullName??throw new IOException("Choose an application folder, not a drive root.");
    Directory.CreateDirectory(parent);
    if(Directory.Exists(install)) {
        var installed=ReadManifest(install);
        foreach(var relative in installed.Files) {
            var file=Inside(install,relative);if(!File.Exists(file))continue;
            using var check=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);
        }
    }
    var stage=Path.Combine(parent,".deskwing-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
    // The staging directory is retained on failure for inspection, never recursively deleted.
    foreach(var entry in entries){var dest=Inside(stage,entry.FullName);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);using var input=entry.Open();using var output=File.Create(dest);input.CopyTo(output);}
    var files=entries.Select(entry=>entry.FullName).ToArray();
    File.WriteAllText(Path.Combine(stage,marker),JsonSerializer.Serialize(new InstallManifest("TabletDeckStudio",install,files)));
    string? backup=null;
    if(Directory.Exists(install)){backup=Path.Combine(parent,"DeskWing-previous-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.Move(install,backup);}
    try{Directory.Move(stage,install);}catch{if(backup is not null&&!Directory.Exists(install))Directory.Move(backup,install);throw;}
    if(!testMode&&!args.Contains("--no-shortcuts")){Shortcut(install,true);InstalledStartup.Update(Path.Combine(install,"Windows","TabletDeck-Companion.exe"),Path.Combine(install,"Windows","DeskWing-Companion.exe"));}
    Console.WriteLine("Installed: "+install+"\nOpen Windows/DeskWing-Companion.exe when ready. Startup and USB reconnection are opt-in in Desktop settings.");
    if(backup is not null)Console.WriteLine("Previous installation retained at: "+backup);
}catch(Exception ex){Console.Error.WriteLine("Setup failed: "+ex.Message);Environment.ExitCode=1;}

static string Inside(string root,string relative)
{
    if(relative.Length==0||relative.Contains('\\')||relative.Contains(':')||relative.Split('/').Any(part=>part is ".." or "." or ""))throw new IOException("Unsafe archive or manifest path.");
    var full=Path.GetFullPath(Path.Combine(root,relative));
    if(!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Path escapes the installation directory.");
    for(var ancestor=Path.GetDirectoryName(full);ancestor is not null&&ancestor.Length>=Path.GetFullPath(root).Length;ancestor=Path.GetDirectoryName(ancestor))if(Directory.Exists(ancestor)&&(File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked installation folders are not supported.");
    if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked installation files are not supported.");
    return full;
}
static List<ZipArchiveEntry> Verify(ZipArchive archive)
{
    if(archive.Entries.Count>2000||archive.Entries.Sum(entry=>entry.Length)>500L*1024*1024)throw new IOException("Installer payload exceeds limits.");
    var entries=archive.Entries.Where(entry=>!entry.FullName.EndsWith('/')).ToList();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var entry in entries){Inside(Path.GetTempPath(),entry.FullName);if(!names.Add(entry.FullName)||(entry.ExternalAttributes>>16&0xF000)==0xA000)throw new IOException("Duplicate or linked payload entry.");}
    var manifest=entries.SingleOrDefault(entry=>entry.FullName=="SHA256SUMS.txt")??throw new IOException("Checksums are missing.");
    using var reader=new StreamReader(manifest.Open());var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    while(reader.ReadLine() is {} line){if(line.Length<67||line[64..66]!="  ")throw new IOException("Invalid checksum manifest.");var name=line[66..];Inside(Path.GetTempPath(),name);if(!hashes.TryAdd(name,line[..64]))throw new IOException("Duplicate checksum entry.");}
    if(hashes.Count!=entries.Count-1)throw new IOException("Payload does not match its manifest.");
    foreach(var entry in entries.Where(entry=>entry!=manifest)){using var stream=entry.Open();if(!hashes.TryGetValue(entry.FullName,out var hash)||!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new IOException("Payload checksum mismatch: "+entry.FullName);}
    if(!names.Contains("Windows/DeskWing-Companion.exe"))throw new IOException("Companion executable is missing.");return entries;
}
static InstallManifest ReadManifest(string root)
{
    var path=Path.Combine(root,".tabletdeck-install.json");
    if(!File.Exists(path))throw new IOException("This folder is not a managed DeskWing installation. Choose an empty, new destination.");
    var saved=JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path))??throw new IOException("Invalid install record.");
    if(saved.Id!="TabletDeckStudio"||!Path.GetFullPath(saved.Root).Equals(root,StringComparison.OrdinalIgnoreCase)||saved.Files.Length>2000)throw new IOException("Install record does not match this folder.");
    foreach(var file in saved.Files)Inside(root,file);return saved;
}
static void Shortcut(string root,bool create)
{
    InstalledShortcuts.Update(root,create,new[]{Environment.GetFolderPath(Environment.SpecialFolder.Programs),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)});
}
static void Uninstall(string root,bool noShortcuts)
{
    var saved=ReadManifest(root);
    foreach(var relative in saved.Files){var file=Inside(root,relative);if(File.Exists(file)){using var check=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);}}
    if(!noShortcuts){Shortcut(root,false);InstalledStartup.Update(Path.Combine(root,"Windows","TabletDeck-Companion.exe"),Path.Combine(root,"Windows","DeskWing-Companion.exe"),remove:true);}
    foreach(var relative in saved.Files){var file=Inside(root,relative);if(File.Exists(file))File.Delete(file);}
    File.Delete(Path.Combine(root,".tabletdeck-install.json"));
    var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var relative in saved.Files)for(var dir=Path.GetDirectoryName(Inside(root,relative));dir is not null&&dir.Length>root.Length;dir=Path.GetDirectoryName(dir))directories.Add(dir);
    foreach(var dir in directories.OrderByDescending(dir=>dir.Length))if(Directory.Exists(dir)&&(File.GetAttributes(dir)&FileAttributes.ReparsePoint)==0&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);
    if(!Directory.EnumerateFileSystemEntries(root).Any())Directory.Delete(root);
    Console.WriteLine("Uninstalled program files. Your board, credentials, and OBS settings remain in local application data.");
}
record InstallManifest(string Id,string Root,string[] Files);
