using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
namespace TabletDeck;
internal static class UpdateTests
{
    public static async Task Run()
    {
        var passed=0;void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}void Reject(Action action,string label){try{action();throw new Exception("Accepted invalid update: "+label);}catch(IOException){Check(true,label);}}
        var bytes=Enumerable.Range(0,4096).Select(i=>(byte)i).ToArray();var hash=Convert.ToHexString(SHA256.HashData(bytes));
        string Json(string name="TabletDeck-Setup.exe",string digest="",string tag="v9.0.0",string? url=null)=>JsonSerializer.Serialize(new{tag_name=tag,draft=false,prerelease=false,assets=new[]{new{name,browser_download_url=url??$"https://github.com/tugcantopaloglu/deskwing/releases/download/{tag}/{name}",size=bytes.Length,digest="sha256:"+(digest.Length>0?digest:hash)}}});
        Check(ReleaseUpdates.ParseInstaller(Json(),"1.11.0").Version=="9.0.0","updater selects the newer official installer with SHA256");
        Check(ReleaseUpdates.ParseInstaller(Json(),"1.11.0").Url=="https://github.com/tugcantopaloglu/deskwing/releases/download/v9.0.0/TabletDeck-Setup.exe","renamed repository retains the compatible installer filename");
        foreach(var url in new[]{"https://github.com/tugcantopaloglu/tablet-deck-studio/releases/download/v9.0.0/TabletDeck-Setup.exe","https://github.com/tugcantopaloglu/deskwing-copy/releases/download/v9.0.0/TabletDeck-Setup.exe","https://github.com/another-owner/deskwing/releases/download/v9.0.0/TabletDeck-Setup.exe","https://github.com/tugcantopaloglu/deskwing/releases/download/v8.0.0/TabletDeck-Setup.exe","https://github.com/tugcantopaloglu/deskwing/releases/download/v9.0.0/Other-Setup.exe"})Reject(()=>ReleaseUpdates.ParseInstaller(Json(url:url),"1.11.0"),"asset metadata rejects mismatched repository, tag or filename: "+url);
        Reject(()=>ReleaseUpdates.ParseInstaller(Json(tag:"v1.0.0"),"1.11.0"),"desktop updater rejects downgrades");
        Reject(()=>ReleaseUpdates.ParseInstaller(Json(url:"https://evil.test/TabletDeck-Setup.exe"),"1.11.0"),"updater rejects foreign download URLs");
        Reject(()=>ReleaseUpdates.ParseInstaller(Json(digest:"bad"),"1.11.0"),"updater requires full release digest");
        Reject(()=>ReleaseUpdates.ParseInstaller(Json(tag:"v9.0"),"1.11.0"),"updater rejects noncanonical tags");
        foreach(var url in new[]{"http://github.com/a","https://github.com.evil.test/a","https://user@github.com/a","https://github.com:444/a","https://127.0.0.1/a"})Check(!SelfUpdate.DownloadHost(new Uri(url)),"download policy rejects "+url);
        Check(SelfUpdate.DownloadHost(new Uri("https://release-assets.githubusercontent.com/a")),"download permits GitHub release asset CDN");
        var releaseCalls=0;
        using(var renamedReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(request=>{
            Check(request.RequestUri?.AbsoluteUri=="https://api.github.com/repos/tugcantopaloglu/deskwing/releases/latest","update metadata uses the canonical DeskWing API");
            Check(request.Headers.UserAgent.ToString()=="DeskWing/1.11.0","update requests use the DeskWing product name");
            return new(HttpStatusCode.OK){Content=new StringContent(Json(releaseCalls++==0?"TabletDeck-Setup.exe":"TabletDeck-Studio.apk"))};
        })),"1.11.0")) {
            await renamedReleases.Installer();var apk=await renamedReleases.Apk();
            Check(releaseCalls==2&&apk.Url=="https://github.com/tugcantopaloglu/deskwing/releases/download/v9.0.0/TabletDeck-Studio.apk","Windows and APK metadata use the renamed repository without renaming install identities");
        }
        var root=Path.Combine(Path.GetTempPath(),"tabletdeck-update-unit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var metadata=new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json())});using var releases=new ReleaseUpdates(new HttpClient(metadata),"1.11.0");var verified=0;
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),(file,version,ct)=>{Check(File.ReadAllBytes(file).SequenceEqual(bytes)&&version=="9.0.0","verified bytes and version reach payload verifier");verified++;return Task.CompletedTask;})){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="ready"&&verified==1,"valid download becomes ready without executing installer");}
        using(var badReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json(digest:new string('0',64)))})),"1.11.0"))
        using(var updater=new SelfUpdate(root,badReleases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),(_,_,_)=>throw new Exception("Verifier must not see untrusted bytes"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error"&&updater.Status.Message.Contains("checksum"),"checksum mismatch never reaches installer verifier");}
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>{var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new Uri("https://evil.test/a");return response;})),(_,_,_)=>throw new Exception("Foreign redirect accepted"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error","foreign redirect cannot prepare an update");}
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes[..100])})),(_,_,_)=>throw new Exception("Truncated payload accepted"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error","truncated download cannot prepare an update");}
        Check(UsbApkUpdate.InstalledVersion("versionCode=14\nversionName=1.11.0\n")==new Version(1,11,0),"ADB package version is read from installed metadata");
        var calls=new List<string[]>();var installed="1.0.0";var prefs=new DesktopPreferences(root);prefs.Save(new(UsbSerial:"tablet123",UsbApkReopen:true));
        Task<string> Adb(string[] args){calls.Add(args);if(args.SequenceEqual(new[]{"devices"}))return Task.FromResult("List of devices attached\ntablet123\tdevice\n");if(args.Contains("dumpsys"))return Task.FromResult("versionName="+installed);if(args.Contains("install")){installed="9.0.0";return Task.FromResult("Success");}if(args.Contains("am"))return Task.FromResult("Starting: Intent");throw new Exception("Unexpected ADB command");}
        using var apkReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json("TabletDeck-Studio.apk"))})),"9.0.0");
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),Adb)){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="complete","verified APK performs a successful in-place USB update");Check(calls.Any(c=>c.Contains("install")&&c.Contains("-r")&&c.Contains("--no-incremental")),"USB install preserves app data with fixed safe arguments");Check(calls.Any(c=>c.Contains("am")&&c.Contains("app.tabletdeck.studio/app.tabletdeck.MainActivity")),"saved reopen preference launches only Tablet Deck");calls.Clear();updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="current"&&!calls.Any(c=>c.Contains("install")),"current/newer tablet never receives a redundant or older APK");}
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),args=>Task.FromResult("tablet123\tunauthorized\n"))){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="error","unauthorized tablet cannot be updated");}
        Check(!new DesktopSettings().UsbApkUpdates&&!new DesktopSettings().UsbApkReopen,"automatic USB APK updates and reopening default off");
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),_=>throw new Exception("No ADB expected"),()=>false)){updater.Begin();Check(updater.Status.Phase=="idle","Windows update reservation prevents a competing APK operation");}
        installed="1.0.0";calls.Clear();using(var olderPc=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json("TabletDeck-Studio.apk"))})),"1.11.0"))using(var updater=new UsbApkUpdate(root,prefs,olderPc,new HttpClient(new FixtureHandler(_=>throw new Exception("No APK download expected"))),Adb)){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="windows-first"&&!calls.Any(c=>c.Contains("install")),"APK updates wait for a compatible Windows companion");}
        File.WriteAllText(Path.Combine(root,"updates","last-result.json"),"{\"success\":false}");using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),(_,_,_)=>Task.CompletedTask))Check(updater.Status.Phase=="error"&&updater.Status.Message.Contains("restored"),"reopened companion reports prior installer recovery result");
        Console.WriteLine(passed+" update protocol/download/USB checks passed; mocked HTTPS/ADB only, no executable or APK was run.");
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));}
}
