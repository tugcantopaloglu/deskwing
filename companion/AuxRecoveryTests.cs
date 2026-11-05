using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabletDeck;

public static class AuxRecoveryTests
{
    public static void Run()
    {
        var passed=0;
        void Check(bool condition,string message){if(!condition)throw new Exception("FAIL "+message);passed++;Console.WriteLine("PASS "+message);}
        void Reject(Action action,string message){try{action();throw new Exception("Accepted invalid input: "+message);}catch(ArgumentException){Check(true,message);}}
        var root=Path.Combine(Path.GetTempPath(),"tabletdeck-aux-recovery-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Fixture(string name){var path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
        const string token="0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var valid=new PairedDevice("fixture","Fixture tablet",hash,DateTimeOffset.UtcNow);
        try {
            var missing=Fixture("missing");var fresh=new DataStore(missing);var desktop=new DesktopPreferences(missing);
            Check(fresh.Devices.Count==0&&fresh.DeviceRecoveryWarning.Length==0&&desktop.Get()==new DesktopSettings()&&desktop.RecoveryWarning.Length==0,"missing auxiliary files load safe defaults without a damage warning");

            var normal=Fixture("normal");var saved=new DesktopSettings(true,"fixture-usb",true,true,true);
            new DesktopPreferences(normal).Save(saved);desktop=new DesktopPreferences(normal);
            Check(desktop.Get()==saved&&desktop.RecoveryWarning.Length==0,"valid opted-in Windows options survive restart");
            var store=new DataStore(normal);store.AddDevice(valid);store=new DataStore(normal);
            Check(store.Devices.Single()==valid&&store.Authenticate(token)&&store.DeviceRecoveryWarning.Length==0,"valid pairing with a non-GUID fixture identity survives restart and authenticates");
            var deviceBefore=File.ReadAllText(Path.Combine(normal,"devices.json"));
            Reject(()=>store.AddDevice(valid with{Id="other",TokenHash=hash.ToLowerInvariant()}),"duplicate token hashes are rejected regardless of hex case");
            Reject(()=>store.AddDevice(valid with{TokenHash=new string('B',64)}),"duplicate device identities are rejected");
            Reject(()=>store.AddDevice(valid with{Id="invalid",TokenHash="not-hex"}),"invalid token hashes cannot enter the live device list");
            Check(store.Devices.Count==1&&store.Authenticate(token)&&File.ReadAllText(Path.Combine(normal,"devices.json"))==deviceBefore,"rejected additions preserve both live and saved pairings");
            Reject(()=>desktop.Save(new DesktopSettings(UsbSerial:null!)),"null USB serial is rejected safely when saving");

            var desktopCases=new Dictionary<string,string>{
                ["malformed"]="{broken",
                ["null"]="null",
                ["null-serial"]="{\"usbReconnect\":true,\"usbApkUpdates\":true,\"usbSerial\":null}",
                ["invalid-serial"]="{\"usbReconnect\":true,\"usbApkUpdates\":true,\"usbSerial\":\"bad;command\"}",
                ["wrong-type"]="{\"usbReconnect\":\"true\"}",
                ["oversized"]=new string(' ',16*1024+1)
            };
            foreach(var fixture in desktopCases) {
                var directory=Fixture("desktop-"+fixture.Key);var file=Path.Combine(directory,"desktop.json");File.WriteAllText(file,fixture.Value);
                desktop=new DesktopPreferences(directory);
                var preserved=Directory.GetFiles(directory,"desktop.corrupt-*.json");
                Check(desktop.Get()==new DesktopSettings()&&desktop.RecoveryWarning.Length>0&&preserved.Length==1&&File.ReadAllText(preserved[0])==fixture.Value,"damaged desktop "+fixture.Key+" is preserved and every optional behavior is disabled");
                var reloaded=new DesktopPreferences(directory);
                Check(reloaded.Get()==new DesktopSettings()&&reloaded.RecoveryWarning.Length==0&&Directory.GetFiles(directory,"desktop.corrupt-*.json").Length==1,"desktop "+fixture.Key+" recovery persists without repeating on restart");
            }

            string Records(params PairedDevice[] devices)=>JsonSerializer.Serialize(devices,DataStore.Json);
            var deviceCases=new Dictionary<string,string>{
                ["malformed"]="[{broken",
                ["null-list"]="null",
                ["null-entry"]="[null]",
                ["missing-fields"]="[{}]",
                ["invalid-hash"]=Records(valid with{TokenHash=new string('G',64)}),
                ["short-hash"]=Records(valid with{TokenHash="AA"}),
                ["null-name"]=Records(valid with{Name=null!}),
                ["long-name"]=Records(valid with{Name=new string('x',81)}),
                ["empty-id"]=Records(valid with{Id=" "}),
                ["control-id"]=Records(valid with{Id="fixture\n"}),
                ["missing-date"]=Records(valid with{Added=default}),
                ["duplicate-id"]=Records(valid,valid with{TokenHash=new string('B',64)}),
                ["duplicate-hash"]=Records(valid,valid with{Id="other",TokenHash=hash.ToLowerInvariant()}),
                ["too-many"]=Records(Enumerable.Range(0,DataStore.MaxPairedDevices+1).Select(i=>valid with{Id="fixture-"+i,TokenHash=i.ToString("X64")}).ToArray()),
                ["oversized"]=new string(' ',1024*1024+1)
            };
            foreach(var fixture in deviceCases) {
                var directory=Fixture("devices-"+fixture.Key);var file=Path.Combine(directory,"devices.json");File.WriteAllText(file,fixture.Value);
                store=new DataStore(directory);
                var preserved=Directory.GetFiles(directory,"devices.corrupt-*.json");
                Check(store.Devices.Count==0&&!store.Authenticate(token)&&store.DeviceRecoveryWarning.Length>0&&preserved.Length==1&&File.ReadAllText(preserved[0])==fixture.Value,"damaged devices "+fixture.Key+" is preserved and all old credentials fail closed");
                var reloaded=new DataStore(directory);
                Check(reloaded.Devices.Count==0&&reloaded.DeviceRecoveryWarning.Length==0&&Directory.GetFiles(directory,"devices.corrupt-*.json").Length==1,"device "+fixture.Key+" recovery persists without repeating on restart");
            }

            var boundary=Fixture("device-count-boundary");
            var full=Enumerable.Range(0,DataStore.MaxPairedDevices).Select(i=>valid with{Id="fixture-"+i,TokenHash=i.ToString("X64")}).ToArray();
            File.WriteAllText(Path.Combine(boundary,"devices.json"),Records(full));store=new DataStore(boundary);
            Check(store.Devices.Count==DataStore.MaxPairedDevices&&store.DeviceRecoveryWarning.Length==0,"maximum supported paired device count loads without recovery");
            Reject(()=>store.AddDevice(valid),"pairing beyond the supported count is rejected before persistence");
            Check(new DataStore(boundary).Devices.Count==DataStore.MaxPairedDevices,"rejected over-capacity pairing preserves all existing records");
        }finally {
            var absolute=Path.GetFullPath(root);var temporary=Path.GetFullPath(Path.GetTempPath());
            if(absolute.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("tabletdeck-aux-recovery-",StringComparison.Ordinal)&&Directory.Exists(absolute))Directory.Delete(absolute,true);
        }
        Console.WriteLine(passed+" auxiliary recovery tests passed; no live app controls, registry writes, or user settings were used.");
    }
}
