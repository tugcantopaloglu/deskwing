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
        Check(ReleaseUpdates.ParseInstaller(Json("DeskWing-Setup.exe"),"1.11.0").Url.EndsWith("/DeskWing-Setup.exe",StringComparison.Ordinal),"current Windows releases use the DeskWing installer name");
        Check(ReleaseUpdates.ParseApk(Json("DeskWing.apk"),"1.11.0").Url.EndsWith("/DeskWing.apk",StringComparison.Ordinal),"current Android releases use the DeskWing download name");
        Check(ReleaseUpdates.ParseApk(Json("TabletDeck-Studio.apk"),"1.11.0").Url.EndsWith("/TabletDeck-Studio.apk",StringComparison.Ordinal),"older Android releases retain their strict compatible download path");
        string Combined(string preferred,string legacy,string? preferredUrl=null,bool duplicate=false){using var first=JsonDocument.Parse(Json(preferred,url:preferredUrl));using var second=JsonDocument.Parse(Json(legacy));var items=new List<JsonElement>{first.RootElement.GetProperty("assets")[0].Clone(),second.RootElement.GetProperty("assets")[0].Clone()};if(duplicate)items.Add(items[0]);return JsonSerializer.Serialize(new{tag_name="v9.0.0",draft=false,prerelease=false,assets=items});}
        Check(ReleaseUpdates.ParseInstaller(Combined("DeskWing-Setup.exe","TabletDeck-Setup.exe"),"1.11.0").Url.EndsWith("/DeskWing-Setup.exe",StringComparison.Ordinal),"new installer is preferred when compatibility bridge is also present");
        Check(ReleaseUpdates.ParseApk(Combined("DeskWing.apk","TabletDeck-Studio.apk"),"1.11.0").Url.EndsWith("/DeskWing.apk",StringComparison.Ordinal),"new APK is preferred when legacy download alias is also present");
        Reject(()=>ReleaseUpdates.ParseInstaller(Combined("DeskWing-Setup.exe","TabletDeck-Setup.exe","https://evil.test/DeskWing-Setup.exe"),"1.11.0"),"invalid current installer cannot fall back to a valid legacy asset");
        Reject(()=>ReleaseUpdates.ParseApk(Combined("DeskWing.apk","TabletDeck-Studio.apk","https://evil.test/DeskWing.apk"),"1.11.0"),"invalid current APK cannot fall back to a valid legacy asset");
        Reject(()=>ReleaseUpdates.ParseInstaller(Combined("DeskWing-Setup.exe","TabletDeck-Setup.exe",duplicate:true),"1.11.0"),"duplicate current installer assets are rejected instead of falling back");
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
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),(file,version,ct)=>{Check(File.ReadAllBytes(file).SequenceEqual(bytes)&&version=="9.0.0","verified bytes and version reach payload verifier");Check(Path.GetFileName(file)=="DeskWing-Setup.exe","staged Windows installer keeps the DeskWing name even for a legacy release");verified++;return Task.CompletedTask;})){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="ready"&&verified==1,"valid download becomes ready without executing installer");}
        using(var badReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json(digest:new string('0',64)))})),"1.11.0"))
        using(var updater=new SelfUpdate(root,badReleases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),(_,_,_)=>throw new Exception("Verifier must not see untrusted bytes"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error"&&updater.Status.Message.Contains("checksum"),"checksum mismatch never reaches installer verifier");}
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>{var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new Uri("https://evil.test/a");return response;})),(_,_,_)=>throw new Exception("Foreign redirect accepted"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error","foreign redirect cannot prepare an update");}
        using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes[..100])})),(_,_,_)=>throw new Exception("Truncated payload accepted"))){updater.BeginDownload();await updater.WaitForDownload();Check(updater.Status.Phase=="error","truncated download cannot prepare an update");}
        Check(UsbApkUpdate.InstalledVersion("versionCode=14\nversionName=1.11.0\n")==new Version(1,11,0),"ADB package version is read from installed metadata");
        string PackageRow(string installer="com.android.shell")=>"package:app.tabletdeck.studio installer="+installer+"\n";
        foreach(var (list,dump,expected,label) in new[]{
            (PackageRow(),"versionName=1.0.0",AndroidUpdateChannel.Direct,"legacy explicit shell installer"),
            (PackageRow(),"installingPackageName=com.android.shell",AndroidUpdateChannel.Direct,"installing-package alias uses the canonical installer schema"),
            (PackageRow(),"installerPackageName=com.android.shell installingPackageName=com.example.changed",AndroidUpdateChannel.Unknown,"conflicting installer aliases remain refused"),
            (PackageRow("null"),"installerPackageName=null initiatingPackageName=com.android.shell packageSource=0",AndroidUpdateChannel.Direct,"modern ADB initiator with unrecorded installer"),
            (PackageRow("com.android.packageinstaller"),"installerPackageName=com.android.packageinstaller initiatingPackageName=null packageSource=3",AndroidUpdateChannel.Direct,"explicit local-file package installer"),
            (PackageRow("com.android.vending"),"",AndroidUpdateChannel.Play,"Play installer"),
            (PackageRow(),"initiatingPackageName=com.android.vending",AndroidUpdateChannel.Play,"original Play initiator survives installer reassignment"),
            (PackageRow(),"initiatingPackageName = com.android.vending",AndroidUpdateChannel.Play,"spaced origin assignments cannot hide Play"),
            (PackageRow("null"),"installerPackageName = null initiatingPackageName = com.android.shell",AndroidUpdateChannel.Direct,"spaced direct-origin metadata remains readable"),
            (PackageRow(),"initiatingPackageName: com.android.vending",AndroidUpdateChannel.Unknown,"unconsumed known origin fields cannot fall back to direct"),
            (PackageRow(),"updateOwnerPackageName=com.android.vending",AndroidUpdateChannel.Play,"Play update owner"),
            (PackageRow("null"),"installerPackageName=null",AndroidUpdateChannel.Unknown,"null installer is not direct-install proof"),
            (PackageRow(),"installerPackageName=com.android.packageinstaller",AndroidUpdateChannel.Unknown,"disagreeing installer metadata"),
            (PackageRow()+PackageRow(),"",AndroidUpdateChannel.Unknown,"duplicate exact package rows"),
            ("package:app.tabletdeck.studio.development installer=com.android.shell\n","",AndroidUpdateChannel.NotInstalled,"development suffix does not match production"),
            ("", "",AndroidUpdateChannel.NotInstalled,"absent package"),
            ("Error: package query failed","",AndroidUpdateChannel.Unknown,"shell error output is not package metadata"),
            ("package:app.tabletdeck.studio installer=\n","",AndroidUpdateChannel.Unknown,"empty installer value"),
            (PackageRow(),"initiatingPackageName=invalid-value",AndroidUpdateChannel.Unknown,"malformed origin field"),
            (PackageRow(),"initiatingPackageName=com.android.shell initiatingPackageName=com.example.changed",AndroidUpdateChannel.Unknown,"conflicting origin fields"),
            (PackageRow(),"updateOwnerPackageName=com.example.store",AndroidUpdateChannel.Store,"non-Play update owner remains store managed"),
            (PackageRow(),"packageSource=2",AndroidUpdateChannel.Store,"explicit store source"),
            (PackageRow("com.example.unknown"),"",AndroidUpdateChannel.Unknown,"unrecognized installer")
        })Check(AndroidInstallSources.Parse(list,dump).Channel==expected,"installed-source policy: "+label);
        Check(AndroidInstallSources.Parse(new string('x',256*1024+1),"").Channel==AndroidUpdateChannel.Unknown,"oversized source report is refused");
        var calls=new List<string[]>();var installed="1.0.0";var prefs=new DesktopPreferences(root);prefs.Save(new(UsbSerial:"tablet123",UsbApkReopen:true));
        Task<string> Adb(string[] args){calls.Add(args);if(args.SequenceEqual(new[]{"devices"}))return Task.FromResult("List of devices attached\ntablet123\tdevice\n");if(args.Contains("list"))return Task.FromResult(PackageRow());if(args.Contains("dumpsys"))return Task.FromResult("versionName="+installed);if(args.Contains("install")){installed="9.0.0";return Task.FromResult("Success");}if(args.Contains("am"))return Task.FromResult("Starting: Intent");throw new Exception("Unexpected ADB command");}
        using var apkReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json("TabletDeck-Studio.apk"))})),"9.0.0");
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),Adb)){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="complete","verified APK performs a successful in-place USB update");Check(calls.Any(c=>c.Contains("install")&&c.Contains("-r")&&c.Contains("--no-incremental")),"USB install preserves app data with fixed safe arguments");Check(calls.Any(c=>c.Contains("am")&&c.Contains("app.tabletdeck.studio/app.tabletdeck.MainActivity")),"saved reopen preference launches only DeskWing's existing installation");calls.Clear();updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="current"&&!calls.Any(c=>c.Contains("install")),"current/newer tablet never receives a redundant or older APK");}
        foreach(var force in new[]{false,true}){
            var directVersion="1.0.0";var directInstalls=0;
            Task<string> ModernDirectAdb(string[] args){if(args.SequenceEqual(new[]{"devices"}))return Task.FromResult("tablet123\tdevice\n");if(args.Contains("list"))return Task.FromResult(PackageRow("null"));if(args.Contains("dumpsys"))return Task.FromResult("versionName="+directVersion+" installerPackageName=null initiatingPackageName=com.android.shell packageSource=0");if(args.Contains("install")){directInstalls++;directVersion="9.0.0";return Task.FromResult("Success");}if(args.Contains("am"))return Task.FromResult("Starting: Intent");throw new Exception("Unexpected modern direct fixture command");}
            using var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})),ModernDirectAdb);
            updater.Begin(force);await updater.WaitForUpdate();Check(updater.Status.Phase=="complete"&&directInstalls==1,"explicit modern ADB provenance retains the direct update path (manual="+force+")");
        }
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),args=>Task.FromResult("tablet123\tunauthorized\n"))){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="error","unauthorized tablet cannot be updated");}
        Check(!new DesktopSettings().UsbApkUpdates&&!new DesktopSettings().UsbApkReopen,"automatic USB APK updates and reopening default off");
        using(var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),_=>throw new Exception("No ADB expected"),()=>false)){updater.Begin();Check(updater.Status.Phase=="idle","Windows update reservation prevents a competing APK operation");}
        installed="1.0.0";calls.Clear();using(var olderPc=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Json("TabletDeck-Studio.apk"))})),"1.11.0"))using(var updater=new UsbApkUpdate(root,prefs,olderPc,new HttpClient(new FixtureHandler(_=>throw new Exception("No APK download expected"))),Adb)){updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="windows-first"&&!calls.Any(c=>c.Contains("install")),"APK updates wait for a compatible Windows companion");}
        foreach(var force in new[]{false,true})foreach(var (list,dump,phase) in new[]{
            (PackageRow("com.android.vending"),"versionName=1.0.0","play-managed"),
            (PackageRow(),"versionName=1.0.0 initiatingPackageName=com.android.vending","play-managed"),
            (PackageRow("null"),"versionName=1.0.0","source-unknown"),
            (PackageRow(),"versionName=1.0.0 updateOwnerPackageName=com.example.store","store-managed"),
            ("","","not-installed")
        }){
            var installAttempted=false;var metadataFetched=false;
            using var isolatedReleases=new ReleaseUpdates(new HttpClient(new FixtureHandler(_=>{metadataFetched=true;throw new Exception("Blocked channels must not query release metadata");})),"9.0.0");
            Task<string> BlockedAdb(string[] args){if(args.SequenceEqual(new[]{"devices"}))return Task.FromResult("tablet123\tdevice\n");if(args.Contains("list"))return Task.FromResult(list);if(args.Contains("dumpsys"))return Task.FromResult(dump);if(args.Contains("install"))installAttempted=true;throw new Exception("Blocked channel reached a mutating command");}
            using var updater=new UsbApkUpdate(root,prefs,isolatedReleases,new HttpClient(new FixtureHandler(_=>throw new Exception("Blocked channel must not download"))),BlockedAdb);
            updater.Begin(force);await updater.WaitForUpdate();Check(updater.Status.Phase==phase&&!installAttempted&&!metadataFetched,"USB channel refusal precedes all downloads/installs (manual="+force+", "+phase+")");
        }
        foreach(var failure in new[]{"source-query","play-after-download","unknown-after-download","newer-after-download","version-changed","origin-changed","tablet-selection-changed","tablet-selection-during-recheck","tablet-selection-during-devices"}){
            var seenDevices=0;var seenLists=0;var seenDumps=0;var installAttempted=false;var downloaded=false;
            Task<string> RaceAdb(string[] args){
                if(args.SequenceEqual(new[]{"devices"})){seenDevices++;if(seenDevices>1&&failure=="tablet-selection-during-devices")prefs.Save(new(UsbSerial:"tablet456"));return Task.FromResult("tablet123\tdevice\n");}
                if(args.Contains("list")){seenLists++;if(failure=="source-query")throw new IOException("Installer query failed");return Task.FromResult(PackageRow(seenLists>1&&failure=="play-after-download"?"com.android.vending":seenLists>1&&failure is "unknown-after-download" or "origin-changed"?"null":"com.android.shell"));}
                if(args.Contains("dumpsys")){seenDumps++;if(seenDumps>1&&failure=="tablet-selection-during-recheck")prefs.Save(new(UsbSerial:"tablet456"));return Task.FromResult("versionName="+(seenDumps>1&&failure=="newer-after-download"?"10.0.0":seenDumps>1&&failure=="version-changed"?"2.0.0":"1.0.0")+(seenDumps>1&&failure=="origin-changed"?" initiatingPackageName=com.android.shell":""));}
                if(args.Contains("install"))installAttempted=true;throw new Exception("Race fixture must not reach install/reopen");
            }
            prefs.Save(new(UsbSerial:"tablet123",UsbApkReopen:true));
            using var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>{downloaded=true;if(failure=="tablet-selection-changed")prefs.Save(new(UsbSerial:"tablet456"));return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};})),RaceAdb);
            updater.Begin(true);await updater.WaitForUpdate();
            var expected=failure switch{"source-query"=>"error","play-after-download"=>"play-managed","unknown-after-download"=>"source-unknown","newer-after-download"=>"current",_=>"changed"};
            Check(updater.Status.Phase==expected&&!installAttempted&&(failure!="source-query"||!downloaded),"USB update revalidates source/version/tablet before replacement: "+failure);
        }
        prefs.Save(new(UsbSerial:"tablet123",UsbApkReopen:true));
        {
            var safe=true;var becamePlay=false;var downloads=0;var installs=0;
            Task<string> CachedAdb(string[] args){if(args.SequenceEqual(new[]{"devices"}))return Task.FromResult("tablet123\tdevice\n");if(args.Contains("list"))return Task.FromResult(PackageRow(becamePlay?"com.android.vending":"com.android.shell"));if(args.Contains("dumpsys"))return Task.FromResult("versionName=1.0.0");if(args.Contains("install"))installs++;throw new Exception("A cached APK must not bypass channel checks");}
            using var updater=new UsbApkUpdate(root,prefs,apkReleases,new HttpClient(new FixtureHandler(_=>{downloads++;safe=false;return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};})),CachedAdb,()=>safe);
            updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="waiting"&&downloads==1&&installs==0,"a verified direct APK waits while controls become active");
            safe=true;becamePlay=true;updater.Begin();await updater.WaitForUpdate();Check(updater.Status.Phase=="play-managed"&&downloads==1&&installs==0,"cached APK cannot overwrite a tablet that became Play managed while waiting");
        }
        File.WriteAllText(Path.Combine(root,"updates","last-result.json"),"{\"success\":false}");using(var updater=new SelfUpdate(root,releases,new HttpClient(new FixtureHandler(_=>throw new Exception("No download expected"))),(_,_,_)=>Task.CompletedTask))Check(updater.Status.Phase=="error"&&updater.Status.Message.Contains("restored"),"reopened companion reports prior installer recovery result");
        Console.WriteLine(passed+" update protocol/download/USB checks passed; mocked HTTPS/ADB only, no executable or APK was run.");
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));}
}
