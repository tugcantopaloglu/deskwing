using System.Diagnostics;
using System.Text.RegularExpressions;
namespace TabletDeck;
public sealed class UsbApkUpdate : IDisposable
{
    private readonly string directory,adb;private readonly DesktopPreferences preferences;private readonly ReleaseUpdates releases;private readonly HttpClient client;private readonly Func<string[],Task<string>>? operation;private readonly CancellationTokenSource stop=new();private readonly object gate=new();private Task? work,loop;private InstallationStatus status=new(Message:"USB APK updates are optional. Android may ask for installation approval.");private InstallerRelease? asset;private DateTimeOffset checkedAt;
    public InstallationStatus Status=>Volatile.Read(ref status);
    private readonly Func<bool> allowed;
    private string? cachedApk,cachedDigest;
    public UsbApkUpdate(string directory,DesktopPreferences preferences,ReleaseUpdates releases,Func<bool>? allowed=null):this(directory,preferences,releases,new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}),null,allowed){}
    internal UsbApkUpdate(string directory,DesktopPreferences preferences,ReleaseUpdates releases,HttpClient client,Func<string[],Task<string>>? operation,Func<bool>? allowed=null){this.directory=Path.Combine(directory,"updates");adb=Path.Combine(AppContext.BaseDirectory,"platform-tools","adb.exe");this.preferences=preferences;this.releases=releases;this.client=client;client.Timeout=Timeout.InfiniteTimeSpan;this.operation=operation;this.allowed=allowed??(()=>true);}
    private long retryAfter;
    public void Start()=>loop=Task.Run(async()=>{while(!stop.IsCancellationRequested){if(preferences.Get().UsbApkUpdates&&DateTime.UtcNow.Ticks>=Volatile.Read(ref retryAfter))Begin();try{await Task.Delay(TimeSpan.FromMinutes(1),stop.Token);}catch(OperationCanceledException){break;}}});
    public InstallationStatus Begin(bool force=false){lock(gate){if(work is {IsCompleted:false})return Status;Volatile.Write(ref status,new("checking",Message:"Checking the authorized USB tablet…"));if(!allowed()){Volatile.Write(ref status,new(Message:"Tablet updates wait while running controls, held keys or tablet edits are active."));return Status;}work=Update(force);return Status;}}
    internal async Task WaitForUpdate(){if(work is not null)await work;}
    internal static Version InstalledVersion(string output){var match=Regex.Match(output,@"\bversionName=(\d+\.\d+\.\d+)\b",RegexOptions.CultureInvariant);return match.Success&&Version.TryParse(match.Groups[1].Value,out var version)?version:throw new IOException("DeskWing is not installed or its version could not be read.");}
    private async Task<(AndroidInstallSource Source,Version? Version)> Installed(string serial)
    {
        var packages=await Run("-s",serial,"shell","pm","list","packages","-i","--user","current",AndroidInstallSources.Package);
        var hint=AndroidInstallSources.Parse(packages,"");
        if(hint.Channel is AndroidUpdateChannel.Play or AndroidUpdateChannel.NotInstalled)return(hint,null);
        var dump=await Run("-s",serial,"shell","dumpsys","package",AndroidInstallSources.Package);
        var source=AndroidInstallSources.Parse(packages,dump);
        return(source,source.Channel==AndroidUpdateChannel.Direct?InstalledVersion(dump):null);
    }
    private bool RefuseChannel(AndroidInstallSource source)
    {
        var next=source.Channel switch{
            AndroidUpdateChannel.Play=>new InstallationStatus("play-managed",Message:"This DeskWing installation is managed by Google Play. Update it in Google Play; USB APK replacement is disabled."),
            AndroidUpdateChannel.Store=>new InstallationStatus("store-managed",Message:"An app store manages this DeskWing installation. Update it through that store; USB APK replacement is disabled."),
            AndroidUpdateChannel.NotInstalled=>new InstallationStatus("not-installed",Message:"DeskWing is not installed for the current Android user. Use the original app store or the separate direct-install helper for initial installation."),
            AndroidUpdateChannel.Unknown=>new InstallationStatus("source-unknown",Message:"The tablet's installation source could not be verified. USB APK replacement is disabled; use its original distribution channel."),
            _=>null};
        if(next is null)return false;Volatile.Write(ref status,next);return true;
    }
    private async Task<string> Run(params string[] args){if(operation is not null)return await operation(args);if(!File.Exists(adb))throw new IOException("ADB is missing. Extract the complete Windows package.");var start=new ProcessStartInfo(adb){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);using var process=Process.Start(start)??throw new IOException("ADB could not start.");using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromMinutes(2));var output=process.StandardOutput.ReadToEndAsync(deadline.Token);var error=process.StandardError.ReadToEndAsync(deadline.Token);try{await process.WaitForExitAsync(deadline.Token);}catch{if(!process.HasExited)process.Kill();throw;}var text=await output;var failure=await error;if(process.ExitCode!=0)throw new IOException("USB operation failed. Check tablet debugging/installation approval and retry. "+failure[..Math.Min(failure.Length,240)]);return text;}
    private async Task Update(bool force)
    {
        try{
            var selectedSerial=preferences.Get().UsbSerial;
            var serial=UsbReconnect.SelectDevice(await Run("devices"),selectedSerial);var initial=await Installed(serial);
            if(RefuseChannel(initial.Source))return;
            var installed=initial.Version!;
            if(force||asset is null||DateTimeOffset.UtcNow-checkedAt>TimeSpan.FromMinutes(15)){asset=await releases.Apk(stop.Token);checkedAt=DateTimeOffset.UtcNow;}
            if(Version.Parse(asset.Version)<=installed){Volatile.Write(ref status,new("current",installed.ToString(),Message:"Tablet is already at this release or newer. No APK was installed."));return;}
            if(Version.Parse(asset.Version)>Version.Parse(releases.Status.CurrentVersion)){Volatile.Write(ref status,new("windows-first",asset.Version,Message:"Update the Windows companion first, then update the tablet so both components stay compatible."));return;}
            if(cachedApk is null||cachedDigest!=asset.Sha256||!File.Exists(cachedApk)){
                Directory.CreateDirectory(directory);var stage=Path.Combine(directory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);var file=Path.Combine(stage,"DeskWing.apk");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromMinutes(5));
                Volatile.Write(ref status,new("downloading",asset.Version,0,asset.Size,"Downloading the release APK…"));await ReleaseDownload.Fetch(client,asset,file,bytes=>Volatile.Write(ref status,new("downloading",asset.Version,bytes,asset.Size,"Downloading the release APK…")),timeout.Token);cachedApk=file;cachedDigest=asset.Sha256;
            }
            if(!allowed()){Volatile.Write(ref status,new("waiting",asset.Version,Message:"APK verified. Waiting for running controls, held keys or tablet edits to finish."));return;}
            using(var input=File.OpenRead(cachedApk))if(!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(input),Convert.FromHexString(asset.Sha256))){cachedApk=null;cachedDigest=null;throw new IOException("The cached APK changed. Retry to download it again.");}
            // Downloads and user activity can take time. Recheck the exact tablet, channel and version
            // immediately before replacement, including when a cached APK is reused.
            if(preferences.Get().UsbSerial!=selectedSerial||UsbReconnect.SelectDevice(await Run("devices"),selectedSerial)!=serial){Volatile.Write(ref status,new("changed",Message:"The selected USB tablet changed. Check its connection and start the update again."));return;}
            var current=await Installed(serial);if(RefuseChannel(current.Source))return;
            if(Version.Parse(asset.Version)<=current.Version){Volatile.Write(ref status,new("current",current.Version!.ToString(),Message:"Tablet is already at this release or newer. No APK was installed."));return;}
            if(current.Version!=installed||current.Source.Identity!=initial.Source.Identity){Volatile.Write(ref status,new("changed",Message:"The tablet's installed version or source changed. Check its distribution channel and start the update again."));return;}
            if(!allowed()){Volatile.Write(ref status,new("waiting",asset.Version,Message:"APK verified. Waiting for running controls, held keys or tablet edits to finish."));return;}
            if(preferences.Get().UsbSerial!=selectedSerial){Volatile.Write(ref status,new("changed",Message:"The selected USB tablet changed. Check its connection and start the update again."));return;}
            Volatile.Write(ref status,new("installing",asset.Version,asset.Size,asset.Size,"Installing over USB. Approve Android's installation prompt if shown; the app briefly stops."));
            var result=await Run("-s",serial,"install","-r","--no-incremental",cachedApk);if(!result.Contains("Success",StringComparison.Ordinal))throw new IOException("Android did not confirm installation. Check its prompt and retry.");
            if(InstalledVersion(await Run("-s",serial,"shell","dumpsys","package","app.tabletdeck.studio"))!=Version.Parse(asset.Version))throw new IOException("Android reported a different version after installation.");
            if(preferences.Get().UsbApkReopen)await Run("-s",serial,"shell","am","start","-n","app.tabletdeck.studio/app.tabletdeck.MainActivity");
            Volatile.Write(ref status,new("complete",asset.Version,asset.Size,asset.Size,preferences.Get().UsbApkReopen?"Tablet updated and DeskWing reopened. Pairing was retained.":"Tablet updated. Open DeskWing to reconnect; pairing was retained."));
        }catch(Exception ex) when(ex is not OutOfMemoryException){var installing=Status.Phase=="installing";if(installing)Volatile.Write(ref retryAfter,DateTime.UtcNow.AddMinutes(15).Ticks);Volatile.Write(ref status,new("error",Message:(ex is OperationCanceledException?"USB update timed out or was cancelled. Check the tablet and retry.":ex.Message)+(installing?" Automatic installation retries pause for fifteen minutes; you can retry manually.":"")));}
    }
    public void Dispose(){stop.Cancel();client.Dispose();}
}
