using System.Text.RegularExpressions;

namespace TabletDeck;

internal enum AndroidUpdateChannel { Direct, Play, Store, Unknown, NotInstalled }
internal sealed record AndroidInstallSource(AndroidUpdateChannel Channel,string Identity);

internal static class AndroidInstallSources
{
    private enum SourceField { Unknown, Installer, Initiator, UpdateOwner, PackageSource }
    internal const string Package="app.tabletdeck.studio";
    private const int MaximumReportCharacters=256*1024;
    private static readonly HashSet<string> PackageInstallers=new(StringComparer.Ordinal){"com.android.packageinstaller","com.google.android.packageinstaller","com.miui.packageinstaller","com.samsung.android.packageinstaller"};

    // The shell's package filter is a substring match. Only an exact, unique package row is evidence.
    internal static AndroidInstallSource Parse(string packages,string dump)
    {
        if(packages.Length>MaximumReportCharacters||dump.Length>MaximumReportCharacters)return Unknown();
        var rows=packages.Split('\n').Select(value=>value.Trim()).Where(value=>value.Length>0).ToArray();
        if(rows.Length==0)return new(AndroidUpdateChannel.NotInstalled,"");
        var exact=new List<string>();
        foreach(var row in rows){
            var match=Regex.Match(row,@"\Apackage:(?<package>[a-zA-Z0-9_.]+)[ \t]+installer=(?<installer>null|[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z][a-zA-Z0-9_]*)+)\z",RegexOptions.CultureInvariant);
            if(!match.Success)return Unknown();
            if(match.Groups["package"].Value==Package)exact.Add(match.Groups["installer"].Value);
        }
        if(exact.Count==0)return new(AndroidUpdateChannel.NotInstalled,"");
        var source=new Dictionary<SourceField,string>();
        var conflicting=false;
        var fields=Regex.Matches(dump,@"\b(?<field>installerPackageName|installingPackageName|initiatingPackageName|updateOwnerPackageName|packageSource)\s*=\s*(?<value>[^\s,}]+)",RegexOptions.CultureInvariant);
        if(fields.Count!=Regex.Matches(dump,@"\b(?:installerPackageName|installingPackageName|initiatingPackageName|updateOwnerPackageName|packageSource)\b",RegexOptions.CultureInvariant).Count)return Unknown();
        foreach(Match field in fields){
            var schemaField=field.Groups["field"].Value switch {
                "installerPackageName" or "installingPackageName"=>SourceField.Installer,
                "initiatingPackageName"=>SourceField.Initiator,
                "updateOwnerPackageName"=>SourceField.UpdateOwner,
                "packageSource"=>SourceField.PackageSource,
                _=>SourceField.Unknown
            };
            var value=field.Groups["value"].Value;
            if(schemaField==SourceField.Unknown||(schemaField==SourceField.PackageSource?value is not ("0" or "1" or "2" or "3" or "4"):!ValidPackage(value)))return Unknown();
            if(source.TryGetValue(schemaField,out var prior)&&prior!=value)conflicting=true;
            source[schemaField]=value;
        }
        if(exact.Contains("com.android.vending",StringComparer.Ordinal)||source.Values.Contains("com.android.vending",StringComparer.Ordinal))return new(AndroidUpdateChannel.Play,"com.android.vending");
        if(exact.Count!=1||conflicting)return Unknown();
        var installer=exact[0];
        if(source.TryGetValue(SourceField.Installer,out var recorded)&&recorded!=installer)return Unknown();
        var initiating=source.GetValueOrDefault(SourceField.Initiator,"null");
        var owner=source.GetValueOrDefault(SourceField.UpdateOwner,"null");
        if(owner is not ("null" or "com.android.shell")||source.GetValueOrDefault(SourceField.PackageSource)=="2")return new(AndroidUpdateChannel.Store,"store-managed");
        // Null can also mean that an installer was removed. It never authorizes a USB overwrite by itself.
        var shell=installer=="com.android.shell"&&initiating is "null" or "com.android.shell"
            ||initiating=="com.android.shell"&&(installer is "null" or "com.android.shell"||PackageInstallers.Contains(installer));
        var local=source.GetValueOrDefault(SourceField.PackageSource)=="3"&&PackageInstallers.Contains(installer)&&(initiating=="null"||PackageInstallers.Contains(initiating));
        if(!shell&&!local)return Unknown();
        var identity=string.Join('|',installer,initiating,owner,source.GetValueOrDefault(SourceField.PackageSource,"0"));
        return new(AndroidUpdateChannel.Direct,identity);
    }

    private static bool ValidPackage(string value)=>value=="null"||Regex.IsMatch(value,@"\A[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z][a-zA-Z0-9_]*)+\z",RegexOptions.CultureInvariant);
    private static AndroidInstallSource Unknown()=>new(AndroidUpdateChannel.Unknown,"");
}
