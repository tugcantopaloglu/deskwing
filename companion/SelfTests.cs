using System.Security.Cryptography;
using System.Text;
using System.IO.Pipes;
using NAudio.Wave;

namespace TabletDeck;
public static class SelfTests
{
    public static void Run()
    {
        var passed=0;
        void Check(bool condition,string label) {if(!condition) throw new Exception("FAILED: "+label);passed++;Console.WriteLine("PASS "+label);}
        void Reject(Action a,string label) {try {a();throw new Exception("FAILED: accepted "+label);}catch(ArgumentException) {passed++;Console.WriteLine("PASS rejects "+label);} }
        var c=DeckConfig.Defaults();DataStore.Validate(c);
        Check(c.Pages.Count==5&&c.Pages[0].Buttons.Count==12,"Studio defaults: 12 keys, 5 pages");
        Check(c.Pages.SelectMany(p=>p.Buttons).Where(a=>a.Kind=="sound").All(a=>File.Exists(a.Target)),"included demo sounds exist beside the executable");
        c.Pages[0].Buttons[0].Kind="shell";Reject(()=>DataStore.Validate(c),"arbitrary shell action");c=DeckConfig.Defaults();
        c.Pages[0].Buttons[8].Target="cmd.exe";Reject(()=>DataStore.Validate(c),"relative app executable");
        c=DeckConfig.Defaults();c.Pages[0].Buttons[0].Id=c.Pages[0].Buttons[1].Id;Reject(()=>DataStore.Validate(c),"duplicate action IDs");
        Check(WindowsActions.ParseHotkey("CTRL+SHIFT+M").SequenceEqual(new ushort[]{17,16,77}),"hotkey key mapping");
        Reject(()=>WindowsActions.ParseHotkey("CTRL+BADKEY"),"invalid shortcut");
        c=DeckConfig.Defaults();c.Pages[0].Buttons[0].Kind="url";c.Pages[0].Buttons[0].Target="file:///C:/Windows/system32/cmd.exe";Reject(()=>DataStore.Validate(c),"file protocol URL");
        var macro=new DeckAction {Kind="macro",Steps=[new(){Kind="stream"}]};
        Check(DeckEngine.NeedsConfirm(macro),"macro inherits stream confirmation");
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var salt="salt";var challenge="challenge";
        var expected=Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password+salt)))+challenge)));
        Check(ObsClient.Auth(password,salt,challenge)==expected,"OBS v5 authentication challenge");
        Check(SoundpadClient.BuildCommand("soundpadPlay","7","mic")=="DoPlaySound(7,false,true)","Soundpad routes sound to microphone");
        Check(SoundpadClient.BuildCommand("soundpadPlay","2","both")=="DoPlaySound(2,true,true)","Soundpad speakers and microphone routing");
        Check(SoundpadClient.BuildCommand("soundpadPlay","1","")=="DoPlaySound(1)","Soundpad default play mode");
        Check(SoundpadClient.BuildCommand("soundpadStop","","")=="DoStopSound()"&&SoundpadClient.BuildCommand("soundpadPause","","")=="DoTogglePause()"&&SoundpadClient.BuildCommand("soundpadNext","","")=="DoPlayNextSound()"&&SoundpadClient.BuildCommand("soundpadPrevious","","")=="DoPlayPreviousSound()","Soundpad transport controls are allowlisted");
        Reject(()=>SoundpadClient.BuildCommand("soundpadPlay","1);DoStopSound()","mic"),"Soundpad command injection");
        Reject(()=>SoundpadClient.BuildCommand("soundpadPlay","0","mic"),"Soundpad zero index");
        Reject(()=>SoundpadClient.BuildCommand("soundpadPlay","1","bad-output"),"unknown Soundpad output");
        Reject(()=>SoundpadClient.BuildCommand("arbitrary","",""),"unknown Soundpad command");
        TestSoundpadPipe().GetAwaiter().GetResult();Check(true,"Soundpad named-pipe status framing, fragmentation, and rejection");
        var sampleXml="<Soundlist><Sound index=\"1\" title=\"Air &amp; horn\" url=\"C:\\Private\\horn.wav\" artist=\"Demo\"/><Sound index=\"2\" url=\"C:\\Private\\clap.wav\"/></Soundlist>";
        var categoryXml="<Categories><Category name=\"Alerts\"><Sound index=\"1\" url=\"C:\\Private\\horn.wav\"/></Category></Categories>";
        var library=SoundpadLibrary.Parse(sampleXml,categoryXml);
        Check(library.Sounds.Count==2&&library.Sounds[0].Name=="Air & horn"&&library.Sounds[0].Categories.SequenceEqual(new[]{"Alerts"})&&library.Sounds[1].Name=="clap","Soundpad names, categories, and file-name fallback");
        Check(!System.Text.Json.JsonSerializer.Serialize(library).Contains("Private"),"Soundpad library never exposes PC file paths");
        var unsafeXml="<!DOCTYPE Soundlist [<!ENTITY x SYSTEM 'file:///C:/private'>]><Soundlist>&x;</Soundlist>";
        try {SoundpadLibrary.ParseXml(unsafeXml);throw new Exception("Unsafe XML was accepted.");}catch(System.Xml.XmlException){Check(true,"Soundpad XML external entities are prohibited");}
        TestLibraryPipe(sampleXml).GetAwaiter().GetResult();Check(true,"fragmented Soundpad XML responses are fully read");
        var endpointChecks=TestLibraryEndpoints().GetAwaiter().GetResult();
        Check(endpointChecks.Get,"library GET returns serialized JSON through the actual route mapping");
        Check(endpointChecks.Post,"library refresh POST returns JSON and requests a fresh library");
        TestAppMixer().GetAwaiter().GetResult();Check(true,"app mixer set/mute, invalid volume, and stale sessions");
        TestReorder();Check(true,"layout reorder preserves actions and rejects duplicate IDs atomically");
        var silentPath=Path.Combine(Path.GetTempPath(),"tabletdeck-silence-"+Guid.NewGuid().ToString("N")+".wav");
        try {
            WriteSilentWave(silentPath);
            using var reader=new WaveFileReader(silentPath);var samples=new byte[checked((int)reader.Length)];
            Check(reader.WaveFormat.Encoding==WaveFormatEncoding.Pcm&&reader.WaveFormat.SampleRate==44100&&reader.WaveFormat.Channels==1&&reader.WaveFormat.BitsPerSample==16&&samples.Length==8820&&reader.Read(samples,0,samples.Length)==samples.Length&&samples.All(value=>value==0),"generated WAV format and silent sample data validate without an audio device");
        } finally {File.Delete(silentPath);}
        Console.WriteLine($"{passed} self-tests passed.");
    }
    private static void WriteSilentWave(string path)
    {
        using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
        var data=new byte[8820];writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+data.Length);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(44100);writer.Write(88200);writer.Write((short)2);writer.Write((short)16);writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(data.Length);writer.Write(data);
    }
    private static async Task<(bool Get,bool Post)> TestLibraryEndpoints()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {Args=[],ContentRootPath=AppContext.BaseDirectory});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();var refreshes=new List<bool>();
        app.MapSoundpadLibrary((refresh,ct)=>{refreshes.Add(refresh);return Task.FromResult(new SoundpadLibraryResult([new(1,"A <horn>","","0:01",["Test"])],null));});
        await app.StartAsync();
        try {
            using var client=new HttpClient {BaseAddress=new Uri(app.Urls.Single()),Timeout=TimeSpan.FromSeconds(5)};
            async Task<bool> Response(HttpResponseMessage response) {
                using(response) {if(!response.IsSuccessStatusCode||response.Content.Headers.ContentType?.MediaType!="application/json")return false;
                var data=await response.Content.ReadFromJsonAsync<SoundpadLibraryResult>();return data?.Sounds.Count==1&&data.Sounds[0].Name=="A <horn>";}
            }
            var get=await Response(await client.GetAsync("/api/soundpad/library"));
            var post=await Response(await client.PostAsync("/api/soundpad/library",null));
            return (get&&refreshes.Count==2&&!refreshes[0],post&&refreshes.Count==2&&refreshes[1]);
        } finally {await app.StopAsync();}
    }
    private static async Task TestLibraryPipe(string xml)
    {
        var name="tabletdeck-library-test-"+Guid.NewGuid().ToString("N");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var peer=Task.Run(async()=>{await server.WaitForConnectionAsync(timeout.Token);var request=new byte[14];await server.ReadExactlyAsync(request,timeout.Token);if(Encoding.UTF8.GetString(request)!="GetSoundlist()")throw new Exception("Unexpected library command.");var data=Encoding.UTF8.GetBytes(xml);await server.WriteAsync(data.AsMemory(0,25),timeout.Token);await Task.Delay(15,timeout.Token);await server.WriteAsync(data.AsMemory(25),timeout.Token);await server.FlushAsync(timeout.Token);});
        var received=await SoundpadLibrary.Query("GetSoundlist()",name,timeout.Token);await peer;
        if(received!=xml)throw new Exception("Soundpad XML was truncated.");
    }
    private sealed class FakeAppBackend : IAppAudioBackend
    {
        public AppAudioSession Session=new(new string('A',64),"Test app","Test output",80,false,true);
        public IReadOnlyList<AppAudioSession> List()=>[Session];
        public void Set(string id,double? percent,bool? muted){if(id!=Session.Id)throw new InvalidOperationException("Stale session");Session=Session with{Percent=percent??Session.Percent,Muted=muted??Session.Muted};}
    }
    private static async Task TestAppMixer()
    {
        using var mixer=new AppAudioMixer(new FakeAppBackend());var first=(await mixer.List())[0];await mixer.Set(first.Id,37,true);var changed=(await mixer.List())[0];
        if(changed.Percent!=37||!changed.Muted)throw new Exception("App mixer did not update.");
        try{await mixer.Set(first.Id,-1,null);throw new Exception("Invalid app volume accepted.");}catch(ArgumentException){}
        try{await mixer.Set(first.Id,double.NaN,null);throw new Exception("Non-finite app volume accepted.");}catch(ArgumentException){}
        try{await mixer.Set(new string('B',64),50,null);throw new Exception("Stale app session accepted.");}catch(InvalidOperationException){}
    }
    private static void TestReorder()
    {
        var temp=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"tabletdeck-layout-"+Guid.NewGuid().ToString("N")));
        try {
            var store=new DataStore(temp);var original=store.Config.Pages[0].Buttons[0];
            var order=store.Config.Pages.Select(p=>new PageOrder(p.Id,p.Buttons.Select(a=>a.Id).Reverse().ToArray())).ToArray();store.Reorder(order);
            if(store.Config.Pages[0].Buttons[^1].Id!=original.Id||store.Config.Pages[0].Buttons[^1].Target!=original.Target)throw new Exception("Layout changed an action.");
            var before=System.Text.Json.JsonSerializer.Serialize(store.Config);order[0].ButtonIds[0]=order[0].ButtonIds[1];
            try{store.Reorder(order);throw new Exception("Duplicate layout IDs accepted.");}catch(ArgumentException){}
            if(System.Text.Json.JsonSerializer.Serialize(store.Config)!=before)throw new Exception("Failed layout was partly saved.");
            var pageId=store.Config.Pages[^1].Id;var sound=new LibrarySound(42,"Library sound","","",[]);
            store.ImportSounds(pageId,[sound],"mic");store.ImportSounds(pageId,[sound],"mic");
            var imported=store.Config.Pages[^1].Buttons;if(imported.Count!=1||imported[0].Kind!="soundpadPlay"||imported[0].Target!="42"||imported[0].Value!="mic")throw new Exception("Soundpad import did not preserve routing or deduplicate.");
            before=System.Text.Json.JsonSerializer.Serialize(store.Config);
            try{store.ImportSounds(pageId,Enumerable.Range(1,120).Select(i=>new LibrarySound(i,"Sound","","",[])).ToArray(),"both");throw new Exception("Overflowing page import accepted.");}catch(ArgumentException){}
            if(System.Text.Json.JsonSerializer.Serialize(store.Config)!=before)throw new Exception("Overflowing import was partly saved.");
        } finally {if(temp.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    public static async Task AudioSmokeTest()
    {
        var silentPath=Path.Combine(Path.GetTempPath(),"tabletdeck-audio-smoke-"+Guid.NewGuid().ToString("N")+".wav");
        try {
            WriteSilentWave(silentPath);WindowsActions.PlaySound(silentPath);WindowsActions.StopSounds();
            Console.WriteLine("PASS Windows WAV playback and stop using a silent test file.");
        }finally{WindowsActions.StopSounds();File.Delete(silentPath);}
        // The only volume modified is a silent stream in this test process.
        using var output=new WasapiOut();output.Init(new SilenceProvider(new WaveFormat(48000,16,2)));output.Play();
        using var mixer=new AppAudioMixer();AppAudioSession? own=null;
        try {
            for(var attempt=0;attempt<10&&own is null;attempt++){await Task.Delay(100);own=(await mixer.List()).FirstOrDefault(s=>s.ProcessId==Environment.ProcessId);}
            if(own is null)throw new Exception("The silent test audio session was not enumerated.");
            await mixer.Set(own.Id,37,true);var changed=(await mixer.List()).First(s=>s.Id==own.Id);
            if(Math.Abs(changed.Percent-37)>1||!changed.Muted)throw new Exception("Windows did not apply the test session's volume/mute.");
            Console.WriteLine("PASS actual Windows session enumeration, volume, and mute using silence.");
        } finally {if(own is not null)await mixer.Set(own.Id,own.Percent,own.Muted);output.Stop();}
    }
    private static async Task TestSoundpadPipe()
    {
        foreach(var response in new[]{"R-200","R-404"}) {
            var name="tabletdeck-selftest-"+Guid.NewGuid().ToString("N");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            var command="DoPlaySound(7,false,true)";
            var peer=Task.Run(async()=>{
                await server.WaitForConnectionAsync(timeout.Token);
                var input=new byte[Encoding.UTF8.GetByteCount(command)];await server.ReadExactlyAsync(input,timeout.Token);
                if(Encoding.UTF8.GetString(input)!=command)throw new Exception("Soundpad protocol request mismatch.");
                await server.WriteAsync(Encoding.ASCII.GetBytes(response[..2]),timeout.Token);await server.FlushAsync(timeout.Token);
                await Task.Delay(15,timeout.Token);
                await server.WriteAsync(Encoding.ASCII.GetBytes(response[2..]),timeout.Token);await server.FlushAsync(timeout.Token);
            });
            var rejected=false;
            try {await SoundpadClient.Send(command,name,timeout.Token);}catch(InvalidOperationException){rejected=true;}
            await peer;
            if(rejected!=(response!="R-200"))throw new Exception("Soundpad status handling mismatch.");
        }
    }
}
