using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TabletDeck;
public sealed record InstallationStatus(string Phase="idle",string? Version=null,long Received=0,long Total=0,string Message="Choose Check for updates.");
public sealed class SelfUpdate : IDisposable
{
    private readonly string directory;
    private readonly ReleaseUpdates releases;
    private readonly HttpClient client;
    private readonly Func<string,string,CancellationToken,Task> verify;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private InstallationStatus status=new();private string? installer;private InstallerRelease? candidate;private Task? download;
    public InstallationStatus Status=>Volatile.Read(ref status);
    public SelfUpdate(string directory,ReleaseUpdates releases):this(directory,releases,new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}),VerifyInstaller){}
    internal SelfUpdate(string directory,ReleaseUpdates releases,HttpClient client,Func<string,string,CancellationToken,Task> verify){this.directory=Path.Combine(directory,"updates");this.releases=releases;this.client=client;this.verify=verify;client.Timeout=Timeout.InfiniteTimeSpan;client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskWing-Updater");ReadLastResult();}
    private void ReadLastResult(){try{var file=Path.Combine(directory,"last-result.json");if(!File.Exists(file)||new FileInfo(file).Length>4096)return;using var data=JsonDocument.Parse(File.ReadAllText(file));var success=data.RootElement.GetProperty("success").GetBoolean();var value=data.RootElement.TryGetProperty("version",out var item)?item.GetString():null;status=new(success?"complete":"error",Version.TryParse(value,out var version)?version.ToString():null,Message:success?"The last Windows update completed successfully.":"The last Windows update failed. Previous program files were retained/restored; retry or use View release for a manual installer.");}catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException){}}
    internal static bool DownloadHost(Uri uri)=>uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Port==443&&uri.Host is "github.com" or "release-assets.githubusercontent.com";
    public InstallationStatus BeginDownload()
    {
        lock(stop){if(Status.Phase is "downloading" or "ready" or "installing")return Status;Volatile.Write(ref status,new("downloading",Message:"Checking the latest Windows release…"));download=Download();return Status;}
    }
    internal async Task WaitForDownload(){if(download is not null)await download;}
    private async Task Download()
    {
        await gate.WaitAsync(stop.Token);
        try{
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromMinutes(5));var ct=deadline.Token;
            var next=await releases.Installer(ct);Directory.CreateDirectory(directory);var stage=Path.Combine(directory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);var file=Path.Combine(stage,"DeskWing-Setup.exe");
            Volatile.Write(ref status,new("downloading",next.Version,0,next.Size,"Downloading the verified Windows installer…"));
            await ReleaseDownload.Fetch(client,next,file,bytes=>Volatile.Write(ref status,new("downloading",next.Version,bytes,next.Size,"Downloading Windows update…")),ct);
            await verify(file,next.Version,ct);installer=file;candidate=next;Volatile.Write(ref status,new("ready",next.Version,next.Size,next.Size,"Download verified. Save your edits, then choose Install and restart."));
        }catch(Exception ex) when(ex is not OutOfMemoryException){installer=null;candidate=null;Volatile.Write(ref status,new("error",Message:ex is OperationCanceledException?"Update download cancelled or timed out. Nothing was installed.":ex.Message));}
        finally{gate.Release();}
    }
    private static async Task VerifyInstaller(string file,string version,CancellationToken ct)
    {
        var info=FileVersionInfo.GetVersionInfo(file);if(info.ProductName is not ("DeskWing Installer" or "TabletDeck-Setup")||new Version(info.FileMajorPart,info.FileMinorPart,info.FileBuildPart)!=Version.Parse(version))throw new IOException("The installer identity/version does not match the release.");
        var start=new ProcessStartInfo(file){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--verify-payload");
        using var process=Process.Start(start)??throw new IOException("The installer could not verify its package.");var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        try{await process.WaitForExitAsync(ct);}catch{if(!process.HasExited)process.Kill();throw;}
        if(process.ExitCode!=0||!(await output).Contains("PASS embedded payload",StringComparison.Ordinal))throw new IOException("The downloaded installer payload could not be verified.");await error;
    }
    public void LaunchInstaller()
    {
        if(Status.Phase!="ready"||installer is null||candidate is null)throw new InvalidOperationException("Download and verify an update first.");
        if(Environment.GetCommandLineArgs().Skip(1).Any(arg=>arg is "--data-dir" or "--http-port" or "--https-port" or "--control-port"))throw new InvalidOperationException("Use a manual update for development instances with custom data directories or ports, then restart with the same options.");
        using(var file=File.OpenRead(installer))if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file),Convert.FromHexString(candidate.Sha256)))throw new IOException("The staged installer changed. Download it again.");
        var start=new ProcessStartInfo(installer){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(installer)!};
        foreach(var arg in new[]{"--quiet","--update-running","--wait-pid",Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),"--expected-exe",Environment.ProcessPath!,"--expected-version",candidate.Version,"--restart"})start.ArgumentList.Add(arg);
        var ready=Path.Combine(Path.GetDirectoryName(installer)!,"update-ready-"+Environment.ProcessId);if(File.Exists(ready))File.Delete(ready);
        using var process=Process.Start(start)??throw new IOException("The update installer could not start.");var deadline=DateTime.UtcNow.AddSeconds(20);
        while(!process.HasExited&&!File.Exists(ready)&&DateTime.UtcNow<deadline)Thread.Sleep(100);
        if(!File.Exists(ready)){if(!process.HasExited)process.Kill();throw new IOException("The update helper could not initialize. The companion is still running; retry or use the release installer.");}
        Volatile.Write(ref status,Status with{Phase="installing",Message="Installing and restarting. Tablet controls will reconnect shortly."});
    }
    public void Dispose(){stop.Cancel();client.Dispose();stop.Dispose();}
}
