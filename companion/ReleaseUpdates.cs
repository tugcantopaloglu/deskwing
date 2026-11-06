using System.Net.Http.Headers;
using System.Text.Json;

namespace TabletDeck;

public sealed record ReleaseStatus(string CurrentVersion,string? LatestVersion,bool Available,string Url,string? Error=null);
public sealed record InstallerRelease(string Version,string Url,long Size,string Sha256);
public sealed class ReleaseUpdates : IDisposable
{
    public const string ReleasesUrl="https://github.com/tugcantopaloglu/deskwing/releases";
    private const string LatestReleaseApi="https://api.github.com/repos/tugcantopaloglu/deskwing/releases/latest";
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly string version;
    private ReleaseStatus status;
    private DateTimeOffset checkedAt;
    public event Action<ReleaseStatus>? Found;
    public ReleaseUpdates():this(new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}),typeof(ReleaseUpdates).Assembly.GetName().Version?.ToString(3)??"1.3.0") { }
    internal ReleaseUpdates(HttpClient client,string version){this.client=client;this.version=version;status=new(version,null,false,ReleasesUrl);client.Timeout=TimeSpan.FromSeconds(8);client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskWing/"+version);client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));}
    public ReleaseStatus Status=>Volatile.Read(ref status);
    internal static InstallerRelease ParseInstaller(string json,string current)
        =>ParseCompatibleAsset(json,current,"DeskWing-Setup.exe","TabletDeck-Setup.exe",250L*1024*1024);
    internal static InstallerRelease ParseApk(string json,string current)
        =>ParseCompatibleAsset(json,current,"DeskWing.apk","TabletDeck-Studio.apk",64L*1024*1024);
    private static InstallerRelease ParseCompatibleAsset(string json,string current,string preferred,string legacy,long limit)
    {
        using var document=JsonDocument.Parse(json);
        // A present but invalid current asset must fail verification rather than fall back.
        var present=document.RootElement.GetProperty("assets").EnumerateArray().Any(asset=>asset.GetProperty("name").GetString()==preferred);
        return ParseAsset(json,current,present?preferred:legacy,limit);
    }
    internal static InstallerRelease ParseAsset(string json,string current,string name,long limit)
    {
        var status=Parse(json,current);if(!status.Available)throw new IOException("The installed version is already current.");
        using var document=JsonDocument.Parse(json);var release=document.RootElement;var tag=release.GetProperty("tag_name").GetString()??"";
        var stable=Version.Parse(status.LatestVersion!);if(tag!="v"+status.LatestVersion||stable.Build<0||stable.Revision>=0)throw new IOException("Unsupported release tag.");
        if(name is not ("DeskWing-Setup.exe" or "DeskWing.apk" or "TabletDeck-Setup.exe" or "TabletDeck-Studio.apk"))throw new IOException("Unsupported update asset.");
        var assets=release.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==name).ToArray();
        if(assets.Length!=1)throw new IOException("This release does not include exactly one supported update asset.");var asset=assets[0];
        var expected=$"{ReleasesUrl}/download/{tag}/{name}";
        var size=asset.GetProperty("size").GetInt64();var digest=asset.GetProperty("digest").GetString()??"";
        if(asset.GetProperty("browser_download_url").GetString()!=expected||size<1024||size>limit||!digest.StartsWith("sha256:",StringComparison.Ordinal)||digest.Length!=71||!digest[7..].All(Uri.IsHexDigit))throw new IOException("The release asset has invalid download or checksum metadata.");
        return new(status.LatestVersion!,expected,size,digest[7..].ToUpperInvariant());
    }
    public Task<InstallerRelease> Installer(CancellationToken ct=default)=>Asset("DeskWing-Setup.exe","TabletDeck-Setup.exe",version,250L*1024*1024,ct);
    public Task<InstallerRelease> Apk(CancellationToken ct=default)=>Asset("DeskWing.apk","TabletDeck-Studio.apk","0.0.0",64L*1024*1024,ct);
    private async Task<InstallerRelease> Asset(string preferred,string legacy,string current,long limit,CancellationToken ct)
    {
        using var response=await client.GetAsync(LatestReleaseApi,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,ct))>0){bytes.Write(buffer,0,count);if(bytes.Length>256*1024)throw new IOException("Release response exceeds the size limit.");}
        return ParseCompatibleAsset(System.Text.Encoding.UTF8.GetString(bytes.ToArray()),current,preferred,legacy,limit);
    }
    internal static ReleaseStatus Parse(string json,string current)
    {
        using var document=JsonDocument.Parse(json);var release=document.RootElement;
        var tag=release.GetProperty("tag_name").GetString()??"";
        if(release.GetProperty("draft").GetBoolean()||release.GetProperty("prerelease").GetBoolean()||!Version.TryParse(tag.TrimStart('v'),out var latest))throw new IOException("No supported stable release was returned.");
        return new(current,latest.ToString(),latest>Version.Parse(current),ReleasesUrl+"/tag/"+Uri.EscapeDataString(tag));
    }
    public async Task<ReleaseStatus> Check(bool force=false,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);
        try {
            if(!force&&DateTimeOffset.UtcNow-checkedAt<TimeSpan.FromMinutes(15))return Status;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(8));ct=deadline.Token;
            try {
                using var response=await client.GetAsync(LatestReleaseApi,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
                await using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
                while((count=await stream.ReadAsync(buffer,ct))>0){bytes.Write(buffer,0,count);if(bytes.Length>256*1024)throw new IOException("Release response exceeds the size limit.");}
                var next=Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()),version);var notify=next.Available&&next.LatestVersion!=Status.LatestVersion;
                Volatile.Write(ref status,next);if(notify)Found?.Invoke(next);
            }catch(Exception ex) when(ex is HttpRequestException or IOException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException){Volatile.Write(ref status,new(version,null,false,ReleasesUrl,"Update check failed. Check your network and try again."));}
            checkedAt=DateTimeOffset.UtcNow;return Status;
        }finally{gate.Release();}
    }
    public void Dispose(){client.Dispose();gate.Dispose();}
}
