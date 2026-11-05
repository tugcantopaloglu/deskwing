using System.Net;
using System.Text.Json;

namespace TabletDeck;

public static class HardeningTests
{
    public static async Task Run()
    {
        var passed=0;
        void Check(bool value,string text){if(!value)throw new Exception("FAIL "+text);passed++;Console.WriteLine("PASS "+text);}
        void Reject(Action action,string text){try{action();throw new Exception("Accepted invalid input: "+text);}catch(ArgumentException){Check(true,text);}}
        var now=DateTimeOffset.UtcNow;var events=new List<bool>();
        using(var holds=new HoldKeys((keys,down)=>{if(!keys.SequenceEqual(new ushort[]{17,77}))throw new Exception("Wrong keys");events.Add(down);},()=>now)) {
            var lease=holds.Begin("tablet-a","CTRL+M");Check(events.SequenceEqual(new[]{true}),"hold sends key-down only");
            holds.Release("tablet-b",lease);Check(events.Count==1,"another tablet cannot release a hold");
            Reject(()=>holds.Renew("tablet-b",lease),"another tablet cannot renew a hold");
            try{holds.Begin("tablet-b","CTRL+M");throw new Exception("Concurrent hold allowed");}catch(InvalidOperationException){Check(true,"overlapping hold is rejected");}
            now=now.AddSeconds(2);holds.Renew("tablet-a",lease);now=now.AddSeconds(2);holds.Expire();Check(events.Count==1,"renewal extends the lease");
            now=now.AddSeconds(2);holds.Expire();Check(events.SequenceEqual(new[]{true,false}),"lost connection releases keys after lease expiry");
            Reject(()=>holds.Renew("tablet-a",lease),"expired hold cannot be revived");
            lease=holds.Begin("tablet-a","CTRL+M");holds.ReleaseOwner("tablet-a");Check(events.Count==4&&!events[^1],"revocation releases owner keys");
            lease=holds.Begin("tablet-a","CTRL+M");holds.Release("tablet-a",lease);holds.Release("tablet-a",lease);Check(events.Count==6,"release is idempotent");
            var previous=lease;lease=holds.Begin("tablet-a","CTRL+M");holds.Release("tablet-a",previous);Check(events[^1],"old connection lease cannot release a newer hold");holds.Release("tablet-a",lease);
        }
        var config=DeckConfig.Defaults();var action=config.Pages[0].Buttons[0];action.Kind="holdHotkey";action.Target="F13";DataStore.Validate(config);Check(true,"standalone hold button validates");
        action.Confirm=true;Reject(()=>DataStore.Validate(config),"hold confirmation is rejected");action.Confirm=false;action.Kind="macro";action.Steps=[new(){Kind="holdHotkey",Target="F13"}];Reject(()=>DataStore.Validate(config),"hold macro step is rejected");
        var first=SoundpadLibrary.Parse("<Soundlist><Sound index=\"1\" title=\"A\" url=\"C:\\Sounds\\a.wav\"/></Soundlist>",null);
        var moved=SoundpadLibrary.Parse("<Soundlist><Sound index=\"42\" title=\"A\" url=\"c:/sounds/a.wav\"/></Soundlist>",null);
        Check(first.Sounds[0].Key==moved.Sounds[0].Key&&first.Sounds[0].Key.Length==64,"Soundpad identity survives case, separator, and index changes");
        action=new(){Kind="soundpadPlay",Target="1",SoundpadKey=first.Sounds[0].Key};Check(SoundpadBindings.Resolve(action,moved)==42,"linked sound resolves its current index");
        try{SoundpadBindings.Resolve(action,new([],null));throw new Exception("Missing sound accepted");}catch(InvalidOperationException){Check(true,"missing linked sound cannot play a different index");}
        try{SoundpadBindings.Resolve(action,new([moved.Sounds[0],moved.Sounds[0] with{Index=43}],null));throw new Exception("Duplicate accepted");}catch(InvalidOperationException){Check(true,"ambiguous linked sound is rejected");}
        var bindingPath=Path.Combine(Path.GetTempPath(),"tabletdeck-bindings-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new DataStore(bindingPath);var page=store.Config.Pages[^1];
            store.ImportSounds(page.Id,first.Sounds,"mic");var id=store.Config.Pages[^1].Buttons[0].Id;
            SoundpadBindings.Remap(store,moved,false);var linked=store.Config.Pages[^1].Buttons[0];
            Check(linked.Id==id&&linked.Target=="42"&&linked.SoundpadKey==first.Sounds[0].Key,"Soundpad remapping persists a changed index while preserving button identity");
            store.ImportSounds(page.Id,moved.Sounds,"mic");Check(store.Config.Pages[^1].Buttons.Count==1,"reimport after reordering deduplicates by sound identity");
            store.Modify(c=>c.Pages[^1].Buttons.Add(new DeckAction{Kind="soundpadPlay",Target="42",Label="Legacy"}));
            SoundpadBindings.Remap(store,moved,true);Check(store.Config.Pages[^1].Buttons[1].SoundpadKey==first.Sounds[0].Key,"legacy linking captures current file identity");
        }finally{if(Path.GetFullPath(bindingPath).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(bindingPath).StartsWith("tabletdeck-bindings-",StringComparison.Ordinal)&&Directory.Exists(bindingPath))Directory.Delete(bindingPath,true);}
        Check(VoicemeeterClient.Validate("Strip[0].Gain","-6")==-6&&VoicemeeterClient.Validate("Bus[1].Mute","1")==1,"Voicemeeter gain and mute allowlist");
        Reject(()=>VoicemeeterClient.Validate("Command.Restart","1"),"Voicemeeter arbitrary commands rejected");Reject(()=>VoicemeeterClient.Validate("Strip[0].Gain","NaN"),"Voicemeeter non-finite value rejected");Reject(()=>VoicemeeterClient.Validate("Bus[0].Mute","2"),"Voicemeeter invalid mute rejected");
        Check(UsbReconnect.SelectDevice("List of devices attached\nabc123\tdevice\nemulator-5554\tdevice\n127.0.0.1:123\tdevice\nbad\tunauthorized","")=="abc123","USB selection excludes emulators, network endpoints, and unauthorized devices");
        Check(UsbReconnect.SelectDevice("one\tdevice\ntwo\tdevice","two")=="two","USB explicit serial selection");
        try{UsbReconnect.SelectDevice("one\tdevice\ntwo\tdevice","");throw new Exception("Ambiguous USB accepted");}catch(IOException){Check(true,"multiple USB tablets require selection");}
        Reject(()=>DesktopPreferences.Validate(new(true,"bad\";cmd",false)),"USB serial argument injection rejected");
        Check(DesktopPreferences.StartupCommand(@"C:\Program Files\TabletDeck\deck.exe")=="\"C:\\Program Files\\TabletDeck\\deck.exe\" --tray --no-browser","startup executable path is quoted and starts quietly");
        Check(ReleaseUpdates.Parse("{\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":false}","1.3.0").Available,"stable update comparison");
        Check(!ReleaseUpdates.Parse("{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false}","1.3.0").Available,"older release is not offered as update");
        using(var updates=new ReleaseUpdates(new HttpClient(new FakeReleaseHandler()),"1.3.0")){var result=await updates.Check(true);Check(result.Available&&result.Url=="https://github.com/tugcantopaloglu/deskwing/releases/tag/v2.0.0","update HTTP fixture uses fixed official repository and trusted release URL");}
        Console.WriteLine(passed+" hardening self-tests passed; no GUI, keyboard injection, registry writes, or external app control used.");
    }
    private sealed class FakeReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri?.AbsoluteUri!="https://api.github.com/repos/tugcantopaloglu/deskwing/releases/latest")throw new Exception("Unexpected update endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://invalid.test\"}")});
        }
    }
}
