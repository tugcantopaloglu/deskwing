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
    private const string StartupName="DeskWing",LegacyStartupName="TabletDeckStudio";
    internal static string StartupCommand(string executable)
    {
        if(!Path.IsPathFullyQualified(executable)||executable.Contains('"'))throw new ArgumentException("Invalid companion executable path.");
        var command="\""+executable+"\" --tray --no-browser";
        if(command.Length>260)throw new ArgumentException("Install the companion at a shorter path before enabling startup.");
        return command;
    }
    public static bool StartsWithWindows()
    {
        using var key=Registry.CurrentUser.OpenSubKey(RunKey);return StartupOwned(key?.GetValue(StartupName) as string,Environment.ProcessPath!)||StartupOwned(key?.GetValue(LegacyStartupName) as string,Environment.ProcessPath!);
    }
    internal static bool StartupOwned(string? command,string executable)
    {
        if(command==StartupCommand(executable))return true;
        return Path.GetFileName(executable)=="DeskWing-Companion.exe"&&command==StartupCommand(Path.Combine(Path.GetDirectoryName(executable)!,"TabletDeck-Companion.exe"));
    }
    public static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        var executable=Environment.ProcessPath!;var currentValue=key.GetValue(StartupName);var current=currentValue as string;
        if(enabled){if(currentValue is not null&&!StartupOwned(current,executable))throw new IOException("An unrelated startup entry already uses the DeskWing name. Review Windows startup settings first.");key.SetValue(StartupName,StartupCommand(executable),RegistryValueKind.String);}
        else if(StartupOwned(current,executable))key.DeleteValue(StartupName,false);
        if(StartupOwned(key.GetValue(LegacyStartupName) as string,executable))key.DeleteValue(LegacyStartupName,false);
    }
}
