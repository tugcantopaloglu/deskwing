using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

internal static class RunningUpdate
{
    public static void Apply(List<ZipArchiveEntry> entries,string[] args,bool testMode,Func<string,string,string> inside)
    {
        string Option(string name){var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:throw new IOException("Missing updater option.");}
        if(!int.TryParse(Option("--wait-pid"),out var pid)||pid<=0||pid==Environment.ProcessId)throw new IOException("Invalid companion process.");
        var expected=Path.GetFullPath(Option("--expected-exe"));var version=Assembly.GetExecutingAssembly().GetName().Version!;
        if(!Version.TryParse(Option("--expected-version"),out var requested)||new Version(version.Major,version.Minor,version.Build)!=requested)throw new IOException("The updater version differs from the requested release.");
        using var process=Process.GetProcessById(pid);var actual=Path.GetFullPath(process.MainModule?.FileName??throw new IOException("Cannot identify the running companion."));
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(actual)!="TabletDeck-Companion.exe"||FileVersionInfo.GetVersionInfo(actual).ProductName!="TabletDeck-Companion")throw new IOException("The selected process is not the expected DeskWing companion.");
        var installed=FileVersionInfo.GetVersionInfo(actual);if(!testMode&&requested<=new Version(installed.FileMajorPart,installed.FileMinorPart,installed.FileBuildPart))throw new IOException("This updater is not newer than the installed companion.");
        var target=Path.GetDirectoryName(actual)!;
        var testRoot=Path.Combine(Path.GetTempPath(),"TabletDeckStudioSetupTests");
        if(testMode&&!target.StartsWith(testRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Test updater target is outside its isolated folder.");
        if(Directory.GetParent(target) is null||target.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows),StringComparison.OrdinalIgnoreCase))throw new IOException("Unsupported update location.");
        var files=entries.Where(e=>e.FullName.StartsWith("Windows/",StringComparison.Ordinal)).Select(e=>(Entry:e,Relative:e.FullName[8..])).ToArray();
        foreach(var file in files)inside(target,file.Relative);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!,"update-ready-"+pid),"ready");
        // The existing service exits itself after handing off; this helper never kills a process.
        if(!process.WaitForExit(60000))throw new IOException("The companion did not exit. No files were changed.");
        var cache=testMode?Path.Combine(testRoot,"update-results"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabletDeck","updates");
        var replaced=new List<(string Destination,string? Backup)>();var managedInstallation=false;
        try{
            Directory.CreateDirectory(cache);var backup=Path.Combine(cache,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
            foreach(var file in files){var path=inside(target,file.Relative);if(File.Exists(path)){using var check=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);}}
            foreach(var file in files){var destination=inside(target,file.Relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);string? saved=null;
                if(File.Exists(destination)){saved=inside(backup,file.Relative);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Copy(destination,saved,false);}
                replaced.Add((destination,saved));using var input=file.Entry.Open();using var output=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None);input.CopyTo(output);
            }
            var managedRoot=Directory.GetParent(target)!.FullName;var manifest=Path.Combine(managedRoot,".tabletdeck-install.json");
            if(Path.GetFileName(target)=="Windows"&&File.Exists(manifest)){
                var record=JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifest))??throw new IOException("Invalid installation record.");
                if(record.Id!="TabletDeckStudio"||!Path.GetFullPath(record.Root).Equals(managedRoot,StringComparison.OrdinalIgnoreCase)||record.Files.Length>2000)throw new IOException("Invalid installation record.");foreach(var name in record.Files)inside(managedRoot,name);
                managedInstallation=true;
                var saved=Path.Combine(backup,"installation-record.json");File.Copy(manifest,saved,false);replaced.Add((manifest,saved));
                File.WriteAllText(manifest,JsonSerializer.Serialize(record with{Files=record.Files.Concat(files.Select(f=>"Windows/"+f.Relative)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()}));
            }
            File.WriteAllText(Path.Combine(cache,"last-result.json"),JsonSerializer.Serialize(new{success=true,version=requested.ToString(),message="Windows update installed. Previous program files were retained for recovery."}));
        }catch{
            foreach(var file in replaced.AsEnumerable().Reverse()){if(file.Backup is not null)File.Copy(file.Backup,file.Destination,true);else if(File.Exists(file.Destination))File.Delete(file.Destination);}
            try{File.WriteAllText(Path.Combine(cache,"last-result.json"),JsonSerializer.Serialize(new{success=false,message="The Windows update failed. Previous program files were restored."}));}catch(IOException){}
            if(!testMode)Restart(actual,target);throw;
        }
        if(managedInstallation&&!testMode){
            try{InstalledShortcuts.Update(Directory.GetParent(target)!.FullName,true,new[]{Environment.GetFolderPath(Environment.SpecialFolder.Programs),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)},existingOnly:true);}
            catch(Exception ex) when(ex is IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException){Console.Error.WriteLine("DeskWing was updated, but an existing shortcut could not be renamed: "+ex.Message);}
        }
        if(args.Contains("--restart")&&!testMode)Restart(actual,target);
        Console.WriteLine("PASS running companion updated; board data and pairing are outside the update target.");
    }
    private static void Restart(string executable,string directory){var start=new ProcessStartInfo(executable){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--tray");start.ArgumentList.Add("--after-update");using var process=Process.Start(start)??throw new IOException("Update installed; open DeskWing from its shortcut to restart.");}
}
