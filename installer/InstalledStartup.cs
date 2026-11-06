using Microsoft.Win32;

internal static class InstalledStartup
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string executable)=>"\""+Path.GetFullPath(executable)+"\" --tray --no-browser";
    internal static bool Owned(string? value,string previous,string current)=>value==Command(previous)||value==Command(current);
    public static void Update(string previous,string current,bool remove=false)
    {
        using var key=Registry.CurrentUser.OpenSubKey(RunKey,true);if(key is null)return;
        var oldValue=key.GetValue("TabletDeckStudio") as string;var currentValue=key.GetValue("DeskWing");var newValue=currentValue as string;
        if(remove){if(Owned(oldValue,previous,current))key.DeleteValue("TabletDeckStudio",false);if(Owned(newValue,previous,current))key.DeleteValue("DeskWing",false);return;}
        if(!Owned(oldValue,previous,current)&&!Owned(newValue,previous,current))return;
        if(currentValue is not null&&!Owned(newValue,previous,current))throw new IOException("An unrelated startup entry already uses the DeskWing name. Review Windows startup settings.");
        key.SetValue("DeskWing",Command(current),RegistryValueKind.String);
        if(Owned(oldValue,previous,current))key.DeleteValue("TabletDeckStudio",false);
    }
}
