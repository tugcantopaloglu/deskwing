using Microsoft.Win32;
using System.Text.Json;

namespace TabletDeck;

public sealed record DesktopSettings(bool UsbReconnect=false,string UsbSerial="",bool UpdateNotifications=false,bool UsbApkUpdates=false,bool UsbApkReopen=false);
public sealed class DesktopPreferences
{
    private readonly string path;
    private readonly object gate=new();
    private DesktopSettings settings;
    public string RecoveryWarning {get;private set;}="";
    public DesktopPreferences(string directory)
    {
        path=Path.Combine(directory,"desktop.json");
        settings=new();
        if(!File.Exists(path))return;
        try {
            var saved=DataStore.ReadAuxiliaryJson<DesktopSettings>(path,16*1024);
            Validate(saved);settings=saved;
        }catch(Exception ex) when(ex is JsonException or ArgumentException) {
            DataStore.QuarantineAuxiliary(path);
            Save(new());
            RecoveryWarning="Windows options were damaged and reset to safe defaults. The original file was kept privately. Review USB reconnect and update settings before enabling them again.";
        }
    }
    public DesktopSettings Get(){lock(gate)return settings;}
    public void Save(DesktopSettings value)
    {
        Validate(value);
        lock(gate){var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(value,DataStore.Json));File.Move(temp,path,true);settings=value;}
    }
    internal static void Validate(DesktopSettings value)
    {
        if(value is null||value.UsbSerial is null||value.UsbSerial.Length>128||value.UsbSerial.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('.' or '_' or ':' or '-')))throw new ArgumentException("Enter the tablet serial shown by ADB, using letters, numbers, dots, underscores, colons or hyphens.");
    }
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string StartupCommand(string executable)
    {
        if(!Path.IsPathFullyQualified(executable)||executable.Contains('"'))throw new ArgumentException("Invalid companion executable path.");
        var command="\""+executable+"\" --tray --no-browser";
        if(command.Length>260)throw new ArgumentException("Install the companion at a shorter path before enabling startup.");
        return command;
    }
    public static bool StartsWithWindows()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("TabletDeckStudio") as string==StartupCommand(Environment.ProcessPath!);
    }
    public static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        if(enabled)key.SetValue("TabletDeckStudio",StartupCommand(Environment.ProcessPath!),RegistryValueKind.String);
        else key.DeleteValue("TabletDeckStudio",false);
    }
}
