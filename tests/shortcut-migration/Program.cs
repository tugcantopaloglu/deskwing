using System.Runtime.InteropServices;

var fixture=Path.Combine(Path.GetTempPath(),"deskwing-shortcuts-"+Guid.NewGuid().ToString("N"));
string links=Path.Combine(fixture,"links"),root=Path.Combine(fixture,"install");
Directory.CreateDirectory(links);Directory.CreateDirectory(Path.Combine(root,"Windows"));
string target=Path.Combine(root,"Windows","DeskWing-Companion.exe"),legacyTarget=Path.Combine(root,"Windows","TabletDeck-Companion.exe");
string other=Path.Combine(fixture,"other","TabletDeck-Companion.exe"),otherNew=Path.Combine(fixture,"other","DeskWing-Companion.exe");
string current=Path.Combine(links,"DeskWing.lnk"),legacy=Path.Combine(links,"Tablet Deck Studio.lnk");
var type=Type.GetTypeFromProgID("WScript.Shell")??throw new Exception("Windows shortcut support required for this test.");
dynamic shell=Activator.CreateInstance(type)!;var passed=0;
void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
void Write(string path,string saved){dynamic link=shell.CreateShortcut(path);try{link.TargetPath=saved;link.Save();}finally{Marshal.FinalReleaseComObject(link);}}
string Target(string path){dynamic link=shell.CreateShortcut(path);try{return (string)link.TargetPath;}finally{Marshal.FinalReleaseComObject(link);}}
string Icon(string path){dynamic link=shell.CreateShortcut(path);try{return (string)link.IconLocation;}finally{Marshal.FinalReleaseComObject(link);}}
try{
    Check(InstalledStartup.Owned(InstalledStartup.Command(legacyTarget),legacyTarget,target),"startup ownership recognizes the exact legacy executable command");
    Check(InstalledStartup.Owned(InstalledStartup.Command(target),legacyTarget,target),"startup ownership recognizes the exact DeskWing executable command");
    Check(!InstalledStartup.Owned(null,legacyTarget,target),"absent startup value never enables or grants ownership");
    Check(!InstalledStartup.Owned(InstalledStartup.Command(other),legacyTarget,target),"startup ownership rejects the legacy executable in another folder");
    Check(!InstalledStartup.Owned(InstalledStartup.Command(otherNew),legacyTarget,target),"startup ownership rejects the DeskWing executable in another folder");
    Check(!InstalledStartup.Owned(InstalledStartup.Command(Path.Combine(root,"Windows","DeskWing-Companion-copy.exe")),legacyTarget,target),"startup ownership rejects a similar executable basename");
    Check(!InstalledStartup.Owned("\""+target+"\" --tray",legacyTarget,target),"startup ownership rejects missing arguments");
    Check(!InstalledStartup.Owned(InstalledStartup.Command(target)+" --data-dir other",legacyTarget,target),"startup ownership rejects extra arguments");
    Check(!InstalledStartup.Owned(target+" --tray --no-browser",legacyTarget,target),"startup ownership rejects a different command serialization");
    InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(!File.Exists(current),"update leaves absent shortcuts absent");
    Write(legacy,legacyTarget);InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(File.Exists(current)&&!File.Exists(legacy)&&Target(current).Equals(target,StringComparison.OrdinalIgnoreCase),"legacy shortcut and executable migrate together to DeskWing");
    Check(Icon(current).Equals(Path.Combine(root,"Windows","DeskWing.ico")+",0",StringComparison.OrdinalIgnoreCase),"migrated shortcut uses the DeskWing icon");
    Write(current,legacyTarget);InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(Target(current).Equals(target,StringComparison.OrdinalIgnoreCase),"existing DeskWing-named shortcut migrates its legacy executable target");
    Write(legacy,target);InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(!File.Exists(legacy)&&Target(current).Equals(target,StringComparison.OrdinalIgnoreCase),"old shortcut name targeting the new executable is safely retired");
    Write(legacy,other);InstalledShortcuts.Update(root,true,new[]{links});
    Check(File.Exists(legacy)&&Target(legacy).Equals(other,StringComparison.OrdinalIgnoreCase),"same executable filename in another folder does not grant ownership");
    InstalledShortcuts.Update(root,false,new[]{links});
    Check(!File.Exists(current)&&File.Exists(legacy),"uninstall removes only exact-target owned shortcuts");
    Write(current,otherNew);Write(legacy,legacyTarget);var rejected=false;
    try{InstalledShortcuts.Update(root,true,new[]{links});}catch(IOException){rejected=true;}
    Check(rejected&&Target(current).Equals(otherNew,StringComparison.OrdinalIgnoreCase)&&File.Exists(legacy),"new-name collision preserves unrelated and legacy shortcuts");
    InstalledShortcuts.Update(root,false,new[]{links});
    Check(File.Exists(current)&&!File.Exists(legacy),"uninstall preserves unrelated new-name shortcut");
    Write(current,legacyTarget);Write(legacy,target);InstalledShortcuts.Update(root,false,new[]{links});
    Check(!File.Exists(current)&&!File.Exists(legacy),"uninstall recognizes both exact legacy and current executable targets");
    Write(legacy,otherNew);InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(!File.Exists(current)&&Target(legacy).Equals(otherNew,StringComparison.OrdinalIgnoreCase),"new executable filename in another folder does not create ownership");
    Console.WriteLine(passed+" shortcut/startup migration checks passed; only isolated temporary shortcuts and pure startup predicates used.");
}finally{
    Marshal.FinalReleaseComObject(shell);
    // No recursive deletion: every file and folder removed below was created by this fixture.
    foreach(var path in new[]{current,legacy})if(File.Exists(path))File.Delete(path);
    Directory.Delete(links);Directory.Delete(Path.Combine(root,"Windows"));Directory.Delete(root);Directory.Delete(fixture);
}
