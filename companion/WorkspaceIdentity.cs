using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TabletDeck;

// An inherited MSIX identity can redirect the same nominal AppData path to another
// package's private copy. Decide before opening DataStore so a developer launch
// cannot create or modify an unexpected production board.
internal static class WorkspaceIdentity
{
    internal const string PackagedStartupMessage="DeskWing was started inside a packaged app, which can open a different saved board. Launch DeskWing from its Windows shortcut. For isolated development or testing, supply an explicit --data-dir.";
    private const string UnknownStartupMessage="DeskWing could not confirm its Windows app context. No saved board was opened. Launch DeskWing from its Windows shortcut, or supply an explicit --data-dir for isolated development or testing.";

    public static string? StartupError(bool explicitDataDirectory)
    {
        if(explicitDataDirectory||!OperatingSystem.IsWindows())return null;
        try {return StartupError(false,CurrentPackageName());}
        catch(Exception ex) when(ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException) {return UnknownStartupMessage;}
    }

    internal static string? StartupError(bool explicitDataDirectory,string? packageName)
        =>explicitDataDirectory||string.IsNullOrEmpty(packageName)?null:PackagedStartupMessage;

    public static string? ExplicitDataDirectory(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? directory=null;
        for(var index=0;index<arguments.Count;index++) {
            if(arguments[index]!="--data-dir")continue;
            if(directory is not null)throw new ArgumentException("Supply --data-dir only once. No saved board was opened.");
            if(index+1>=arguments.Count||string.IsNullOrWhiteSpace(arguments[index+1])||arguments[index+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Supply a non-empty directory after --data-dir. No saved board was opened.");
            // Resolve syntax only; do not inspect, create or migrate the requested directory.
            // A malformed explicit value must never silently select the implicit AppData path.
            try {directory=Path.GetFullPath(arguments[++index]);}
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or PathTooLongException) {throw new ArgumentException("The --data-dir directory is invalid. No saved board was opened.",ex);}
        }
        return directory;
    }

    // This identifies the paired PC certificate, not the filesystem location or a
    // board's content. It contains no paths, tokens, usernames, or private key data.
    public static string ConnectionCode(string fingerprint)
    {
        if(fingerprint is not {Length:64}||!fingerprint.All(Uri.IsHexDigit))throw new ArgumentException("Use the companion certificate's SHA-256 fingerprint.",nameof(fingerprint));
        var shortCode=fingerprint[..12].ToUpperInvariant();
        return shortCode[..4]+"-"+shortCode[4..8]+"-"+shortCode[8..12];
    }

    // https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname
    private static string? CurrentPackageName()
    {
        uint length=0;
        var result=GetCurrentPackageFullName(ref length,null);
        if(result==15700)return null; // APPMODEL_ERROR_NO_PACKAGE
        if(result!=122)throw new Win32Exception(result); // ERROR_INSUFFICIENT_BUFFER
        if(length is <2 or >1024)throw new Win32Exception(13); // Invalid native result; allocate only a bounded buffer.
        var buffer=new StringBuilder((int)length);
        result=GetCurrentPackageFullName(ref length,buffer);
        if(result!=0)throw new Win32Exception(result);
        if(buffer.Length==0)throw new Win32Exception(13);
        return buffer.ToString();
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength,StringBuilder? packageFullName);
}
