using System.Runtime.InteropServices;

// Visible branding may change; shortcut ownership is always the complete installed target path.
internal static class InstalledShortcuts
{
    private const string CurrentName="DeskWing.lnk",LegacyName="Tablet Deck Studio.lnk";
    public static void Update(string root,bool create,IEnumerable<string> folders,bool existingOnly=false)
    {
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("Windows shortcut support is unavailable.");
        dynamic shell=Activator.CreateInstance(type)!;
        var target=Path.GetFullPath(Path.Combine(root,"Windows","DeskWing-Companion.exe"));
        // Both paths identify this installation; only the DeskWing path is written.
        var ownedTargets=new[]{target,Path.GetFullPath(Path.Combine(root,"Windows","TabletDeck-Companion.exe"))};
        try{
            foreach(var folder in folders){
                if(string.IsNullOrWhiteSpace(folder))throw new IOException("The Windows shortcut folder is unavailable.");
                string current=Path.Combine(folder,CurrentName),legacy=Path.Combine(folder,LegacyName);
                if(create){
                    if(existingOnly&&!Owned(shell,current,ownedTargets)&&!Owned(shell,legacy,ownedTargets))continue;
                    if(File.Exists(current)&&!Owned(shell,current,ownedTargets))throw new IOException("An unrelated shortcut already uses the DeskWing name. Rename that shortcut before installing.");
                    dynamic link=shell.CreateShortcut(current);
                    try{link.TargetPath=target;link.WorkingDirectory=Path.GetDirectoryName(target);link.Description="DeskWing desktop companion";link.IconLocation=Path.Combine(root,"Windows","DeskWing.ico")+",0";link.WindowStyle=7;link.Save();}
                    finally{Marshal.FinalReleaseComObject(link);}
                    // Retire the old name only after the replacement was saved, and only if it belongs to this installation.
                    if(Owned(shell,legacy,ownedTargets))File.Delete(legacy);
                }else{
                    if(Owned(shell,current,ownedTargets))File.Delete(current);
                    if(Owned(shell,legacy,ownedTargets))File.Delete(legacy);
                }
            }
        }finally{Marshal.FinalReleaseComObject(shell);}
    }
    private static bool Owned(dynamic shell,string shortcut,string[] targets)
    {
        if(!File.Exists(shortcut)||(File.GetAttributes(shortcut)&FileAttributes.ReparsePoint)!=0)return false;
        dynamic link=shell.CreateShortcut(shortcut);
        try{
            var saved=(string)link.TargetPath;
            return !string.IsNullOrWhiteSpace(saved)&&targets.Contains(Path.GetFullPath(saved),StringComparer.OrdinalIgnoreCase);
        }catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(IOException){return false;}
        finally{Marshal.FinalReleaseComObject(link);}
    }
}
