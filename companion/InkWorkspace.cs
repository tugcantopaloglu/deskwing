using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabletDeck;

internal sealed record InkSnapshot(string WorkspaceId,string Revision,JsonElement Document,string RecoveryWarning);
internal sealed record InkUnchangedSnapshot(string WorkspaceId,string Revision,string RecoveryWarning,bool Unchanged=true);
internal sealed class InkConflictException(InkSnapshot current) : InvalidOperationException("The PC scratchpad changed. Review the PC copy before replacing it.")
{
    public InkSnapshot Current {get;}=current;
}

// One private shared scratchpad per companion. Board restores deliberately do not touch this file.
internal sealed class InkWorkspace
{
    internal const int MaxRequestBytes=4*1024*1024;
    internal const int MaxDocumentBytes=3_500_000;
    internal const int MaxStrokes=1200,MaxPoints=60000,MaxNotes=8000;
    internal const string FileName="ink-workspace.json";
    private readonly object gate=new();
    private readonly string path;
    private string workspaceId="",revision="",recoveryWarning="";
    private byte[] documentBytes=[];

    public InkWorkspace(string directory)
    {
        path=Path.Combine(Path.GetFullPath(directory),FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(File.Exists(path)) {
            try {
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(file.Length>MaxRequestBytes)throw Invalid("The saved scratchpad exceeds its size limit.");
                using var saved=JsonDocument.Parse(file,new JsonDocumentOptions{MaxDepth=8});
                var root=saved.RootElement;
                Fields(root,"version","workspaceId","document");
                var savedVersion=root.GetProperty("version");
                if(savedVersion.ValueKind!=JsonValueKind.Number||!savedVersion.TryGetInt32(out var version)||version!=1)throw Invalid("The saved scratchpad version is unsupported.");
                var identity=Text(root.GetProperty("workspaceId"),32,"workspace identity");
                if(!Guid.TryParseExact(identity,"N",out _))throw Invalid("The saved scratchpad identity is invalid.");
                var validated=ValidateDocument(root.GetProperty("document"));
                workspaceId=identity;documentBytes=validated;revision=Hash(validated);
                return;
            }catch(Exception ex) when(ex is JsonException or ArgumentException or InvalidOperationException) {
                // If preservation fails, leave the original untouched and let startup report the failure.
                var preserved=Path.Combine(Path.GetDirectoryName(path)!,"ink-workspace.corrupt-"+Guid.NewGuid().ToString("N")+".json");
                File.Copy(path,preserved);
                recoveryWarning="The saved PC scratchpad was damaged. Its original file was kept privately. Review your tablet copy before saving it to the PC.";
            }
        }
        workspaceId=Guid.NewGuid().ToString("N");
        documentBytes=Encoding.UTF8.GetBytes("{\"version\":1,\"strokes\":[],\"notes\":\"\",\"penOnly\":true,\"updatedAt\":\"1970-01-01T00:00:00.000Z\"}");
        revision=Hash(documentBytes);Write(documentBytes);
    }

    public InkSnapshot Snapshot(){lock(gate)return SnapshotLocked();}

    public object Poll(string knownRevision)
    {
        lock(gate) {
            if(string.Equals(knownRevision,revision,StringComparison.Ordinal))return new InkUnchangedSnapshot(workspaceId,revision,recoveryWarning);
            return SnapshotLocked();
        }
    }

    public InkSnapshot Save(string? expectedRevision,JsonElement document)
    {
        if(expectedRevision is not {Length:64}||!expectedRevision.All(Uri.IsHexDigit))throw Invalid("Reload the PC scratchpad before saving.");
        var validated=ValidateDocument(document);
        lock(gate) {
            if(!string.Equals(expectedRevision,revision,StringComparison.Ordinal))throw new InkConflictException(SnapshotLocked());
            var nextRevision=Hash(validated);
            if(nextRevision!=revision) {
                // Commit disk first. A failed write must leave the current in-memory revision unchanged.
                Write(validated);documentBytes=validated;revision=nextRevision;
            }
            return SnapshotLocked();
        }
    }

    private InkSnapshot SnapshotLocked()
    {
        using var parsed=JsonDocument.Parse(documentBytes);
        return new(workspaceId,revision,parsed.RootElement.Clone(),recoveryWarning);
    }

    private string Hash(byte[] bytes)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(workspaceId));hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void Write(byte[] bytes)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                using(var writer=new Utf8JsonWriter(file)) {
                    writer.WriteStartObject();writer.WriteNumber("version",1);writer.WriteString("workspaceId",workspaceId);
                    writer.WritePropertyName("document");writer.WriteRawValue(bytes,skipInputValidation:true);writer.WriteEndObject();writer.Flush();
                }
                file.Flush(flushToDisk:true);
            }
            File.Move(temporary,path,overwrite:true);
        }finally {if(File.Exists(temporary))File.Delete(temporary);}
    }

    internal static byte[] ValidateDocument(JsonElement document)
    {
        Fields(document,"version","strokes","notes","penOnly","updatedAt");
        if(Encoding.UTF8.GetByteCount(document.GetRawText())>MaxDocumentBytes)throw Invalid("Keep the scratchpad below 3.5 MB.");
        var versionValue=document.GetProperty("version");
        if(versionValue.ValueKind!=JsonValueKind.Number||!versionValue.TryGetInt32(out var version)||version!=1)throw Invalid("This scratchpad version is unsupported.");
        var strokes=document.GetProperty("strokes");
        if(strokes.ValueKind!=JsonValueKind.Array||strokes.GetArrayLength()>MaxStrokes)throw Invalid("Keep at most 1,200 strokes in one scratchpad.");
        var notes=Text(document.GetProperty("notes"),MaxNotes,"notes");
        var penOnly=document.GetProperty("penOnly");
        if(penOnly.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid("Choose whether the scratchpad accepts fingers.");
        var updatedAt=Text(document.GetProperty("updatedAt"),40,"update time");
        if(!DateTimeOffset.TryParse(updatedAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _))throw Invalid("The scratchpad update time is invalid.");
        using var output=new MemoryStream();
        using(var writer=new Utf8JsonWriter(output)) {
            writer.WriteStartObject();writer.WriteNumber("version",1);writer.WriteStartArray("strokes");
            var totalPoints=0;
            foreach(var stroke in strokes.EnumerateArray()) {
                Fields(stroke,"tool","color","width","points");
                var tool=Text(stroke.GetProperty("tool"),6,"tool");
                if(tool is not ("pen" or "eraser"))throw Invalid("Choose a pen or eraser.");
                var color=Text(stroke.GetProperty("color"),7,"color");
                if(color.Length!=7||color[0]!='#'||!color.AsSpan(1).ToArray().All(Uri.IsHexDigit))throw Invalid("Use a six-digit hex ink color.");
                var width=Number(stroke.GetProperty("width"),1,40,"stroke width");
                var points=stroke.GetProperty("points");
                if(points.ValueKind!=JsonValueKind.Array||points.GetArrayLength()<1||points.GetArrayLength()>MaxPoints-totalPoints)throw Invalid("Keep at most 60,000 points in one scratchpad, with at least one per stroke.");
                totalPoints+=points.GetArrayLength();
                writer.WriteStartObject();writer.WriteString("tool",tool);writer.WriteString("color",color.ToLowerInvariant());writer.WriteNumber("width",width);writer.WriteStartArray("points");
                foreach(var point in points.EnumerateArray()) {
                    if(point.ValueKind!=JsonValueKind.Array||point.GetArrayLength()!=3)throw Invalid("Ink points require x, y and pressure.");
                    writer.WriteStartArray();
                    foreach(var value in point.EnumerateArray())writer.WriteNumberValue(Number(value,0,1,"point or pressure"));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();writer.WriteEndObject();
            }
            writer.WriteEndArray();writer.WriteString("notes",notes);writer.WriteBoolean("penOnly",penOnly.GetBoolean());writer.WriteString("updatedAt",updatedAt);writer.WriteEndObject();writer.Flush();
        }
        if(output.Length>MaxDocumentBytes)throw Invalid("Keep the scratchpad below 3.5 MB.");
        return output.ToArray();
    }

    // Exact fields prevent hidden command/path payloads and ambiguous duplicate JSON keys.
    internal static void Fields(JsonElement element,params string[] names)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw Invalid("The scratchpad contains an invalid object.");
        var remaining=new HashSet<string>(names,StringComparer.Ordinal);
        foreach(var property in element.EnumerateObject())if(!remaining.Remove(property.Name))throw Invalid("The scratchpad contains an unknown or duplicate field.");
        if(remaining.Count!=0)throw Invalid("The scratchpad is missing required fields.");
    }
    internal static string Text(JsonElement element,int max,string name)
    {
        if(element.ValueKind!=JsonValueKind.String)throw Invalid("The scratchpad "+name+" must be text.");
        var value=element.GetString()!;
        if(value.Length>max)throw Invalid("The scratchpad "+name+" is too long.");
        return value;
    }
    private static double Number(JsonElement element,double min,double max,string name)
    {
        if(element.ValueKind!=JsonValueKind.Number||!element.TryGetDouble(out var value)||!double.IsFinite(value)||value<min||value>max)throw Invalid("The scratchpad "+name+" is outside its allowed range.");
        return value;
    }
    private static ArgumentException Invalid(string message)=>new(message);
}
