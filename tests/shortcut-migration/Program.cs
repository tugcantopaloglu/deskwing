using System.Runtime.InteropServices;

var fixture=Path.Combine(Path.GetTempPath(),"deskwing-shortcuts-"+Guid.NewGuid().ToString("N"));
string links=Path.Combine(fixture,"links"),root=Path.Combine(fixture,"install");
Directory.CreateDirectory(links);Directory.CreateDirectory(Path.Combine(root,"Windows"));
string target=Path.Combine(root,"Windows","TabletDeck-Companion.exe"),other=Path.Combine(fixture,"other","TabletDeck-Companion.exe");
string current=Path.Combine(links,"DeskWing.lnk"),legacy=Path.Combine(links,"Tablet Deck Studio.lnk");
var type=Type.GetTypeFromProgID("WScript.Shell")??throw new Exception("Windows shortcut support required for this test.");
dynamic shell=Activator.CreateInstance(type)!;var passed=0;
void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
void Write(string path,string saved){dynamic link=shell.CreateShortcut(path);try{link.TargetPath=saved;link.Save();}finally{Marshal.FinalReleaseComObject(link);}}
string Target(string path){dynamic link=shell.CreateShortcut(path);try{return (string)link.TargetPath;}finally{Marshal.FinalReleaseComObject(link);}}
try{
    InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(!File.Exists(current),"update leaves absent shortcuts absent");
    Write(legacy,target);InstalledShortcuts.Update(root,true,new[]{links},existingOnly:true);
    Check(File.Exists(current)&&!File.Exists(legacy)&&Target(current).Equals(target,StringComparison.OrdinalIgnoreCase),"owned legacy shortcut becomes DeskWing");
    Write(legacy,other);InstalledShortcuts.Update(root,true,new[]{links});
    Check(File.Exists(legacy)&&Target(legacy).Equals(other,StringComparison.OrdinalIgnoreCase),"same executable filename in another folder does not grant ownership");
    InstalledShortcuts.Update(root,false,new[]{links});
    Check(!File.Exists(current)&&File.Exists(legacy),"uninstall removes only exact-target owned shortcuts");
    Write(current,other);Write(legacy,target);var rejected=false;
    try{InstalledShortcuts.Update(root,true,new[]{links});}catch(IOException){rejected=true;}
    Check(rejected&&Target(current).Equals(other,StringComparison.OrdinalIgnoreCase)&&File.Exists(legacy),"new-name collision preserves unrelated and legacy shortcuts");
    InstalledShortcuts.Update(root,false,new[]{links});
    Check(File.Exists(current)&&!File.Exists(legacy),"uninstall preserves unrelated new-name shortcut");
    Console.WriteLine(passed+" shortcut migration checks passed; only isolated temporary shortcuts used.");
}finally{
    Marshal.FinalReleaseComObject(shell);
    // No recursive deletion: every file and folder removed below was created by this fixture.
    foreach(var path in new[]{current,legacy})if(File.Exists(path))File.Delete(path);
    Directory.Delete(links);Directory.Delete(Path.Combine(root,"Windows"));Directory.Delete(root);Directory.Delete(fixture);
}
