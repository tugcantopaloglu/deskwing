using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabletDeck;

internal static class InkWorkspaceTests
{
    public static void Run()
    {
        var passed=0;
        void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
        void Reject(Action action,string label){try{action();throw new Exception("Accepted invalid input: "+label);}catch(ArgumentException){Check(true,label);}}
        var root=Path.Combine(Path.GetTempPath(),"deskwing-ink-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Fixture(string name){var directory=Path.Combine(root,name);Directory.CreateDirectory(directory);return directory;}
        static JsonElement Parse(string value){using var json=JsonDocument.Parse(value);return json.RootElement.Clone();}
        static JsonObject Document(string notes=""){return JsonNode.Parse("{\"version\":1,\"strokes\":[{\"tool\":\"pen\",\"color\":\"#A85A16\",\"width\":3,\"points\":[[0,1,0],[1,0,1]]}],\"notes\":\"\",\"penOnly\":true,\"updatedAt\":\"2026-11-05T12:00:00.000Z\"}")!.AsObject().WithNotes(notes);}
        static JsonElement Element(JsonNode node)=>Parse(node.ToJsonString());
        try {
            var directory=Fixture("normal");var workspace=new InkWorkspace(directory);var initial=workspace.Snapshot();
            Check(initial.Document.GetProperty("strokes").GetArrayLength()==0&&initial.Document.GetProperty("notes").GetString()==""&&initial.RecoveryWarning=="","new shared scratchpad starts empty without a recovery warning");
            Check(Guid.TryParseExact(initial.WorkspaceId,"N",out _)&&initial.Revision.Length==64,"snapshot supplies a stable workspace identity and revision");
            var freshReload=new InkWorkspace(directory).Snapshot();
            Check(freshReload.WorkspaceId==initial.WorkspaceId&&freshReload.Revision==initial.Revision,"empty workspace identity survives restart before its first edit");
            var saved=workspace.Save(initial.Revision,Element(Document("Tablet notes: şğü 🖊")));
            Check(saved.Revision!=initial.Revision&&saved.Document.GetProperty("strokes")[0].GetProperty("color").GetString()=="#a85a16","save canonicalizes ink color and advances revision");
            Check(initial.Document.GetProperty("notes").GetString()=="","previous snapshot remains detached from later saves");
            var reloaded=new InkWorkspace(directory).Snapshot();
            Check(reloaded.Revision==saved.Revision&&reloaded.WorkspaceId==saved.WorkspaceId&&reloaded.Document.GetRawText()==saved.Document.GetRawText(),"strokes, pressure, Unicode notes and revision survive restart exactly");
            var noOp=workspace.Save(saved.Revision,saved.Document);
            Check(noOp.Revision==saved.Revision,"saving the same canonical document does not churn the revision");
            Check(workspace.Poll(saved.Revision) is InkUnchangedSnapshot unchanged&&unchanged.Revision==saved.Revision&&unchanged.WorkspaceId==saved.WorkspaceId,"unchanged polling returns metadata without copying the drawing");
            Check(workspace.Poll(initial.Revision) is InkSnapshot changed&&changed.Revision==saved.Revision,"polling an older revision returns the current full document");
            try{workspace.Save(initial.Revision,Element(Document("stale")));throw new Exception("Accepted stale save");}
            catch(InkConflictException error){Check(error.Current.Revision==saved.Revision&&workspace.Snapshot().Document.GetProperty("notes").GetString()=="Tablet notes: şğü 🖊","stale saves return current revision and preserve newer ink");}
            Reject(()=>workspace.Save(null,saved.Document),"a missing revision cannot overwrite shared ink");
            Reject(()=>workspace.Save("../not-a-revision",saved.Document),"an invalid revision cannot enter persistence");
            var other=new InkWorkspace(Fixture("other"));
            Check(other.Snapshot().WorkspaceId!=initial.WorkspaceId&&other.Snapshot().Revision!=initial.Revision,"different PCs have distinct revision identities even for identical empty documents");

            var invalid=new Dictionary<string,Action<JsonObject>> {
                ["unsupported version"]=d=>d["version"]=2,
                ["string version"]=d=>d["version"]="1",
                ["missing required field"]=d=>d.Remove("penOnly"),
                ["unexpected command field"]=d=>d["command"]="example",
                ["non-array strokes"]=d=>d["strokes"]=new JsonObject(),
                ["too many strokes"]=d=>d["strokes"]=new JsonArray(Enumerable.Range(0,InkWorkspace.MaxStrokes+1).Select(_=>Document()["strokes"]![0]!.DeepClone()).ToArray()),
                ["invalid tool"]=d=>d["strokes"]![0]!["tool"]="run",
                ["invalid color"]=d=>d["strokes"]![0]!["color"]="red",
                ["unknown stroke field"]=d=>d["strokes"]![0]!["path"]="example",
                ["zero width"]=d=>d["strokes"]![0]!["width"]=0,
                ["too wide"]=d=>d["strokes"]![0]!["width"]=41,
                ["empty stroke"]=d=>d["strokes"]![0]!["points"]=new JsonArray(),
                ["too few point coordinates"]=d=>d["strokes"]![0]!["points"]![0]=new JsonArray(0,1),
                ["negative coordinate"]=d=>d["strokes"]![0]!["points"]![0]![0]=-0.01,
                ["coordinate beyond canvas"]=d=>d["strokes"]![0]!["points"]![0]![1]=1.01,
                ["pressure beyond range"]=d=>d["strokes"]![0]!["points"]![0]![2]=1.01,
                ["text pressure"]=d=>d["strokes"]![0]!["points"]![0]![2]="0.5",
                ["long notes"]=d=>d["notes"]=new string('n',InkWorkspace.MaxNotes+1),
                ["non-boolean palm protection"]=d=>d["penOnly"]="true",
                ["invalid timestamp"]=d=>d["updatedAt"]="not-a-date"
            };
            var before=File.ReadAllBytes(Path.Combine(directory,InkWorkspace.FileName));
            foreach(var item in invalid){var document=Document();item.Value(document);Reject(()=>workspace.Save(saved.Revision,Element(document)),item.Key+" is rejected");}
            var duplicate=Document().ToJsonString().Replace("\"version\":1","\"version\":1,\"version\":1",StringComparison.Ordinal);
            Reject(()=>workspace.Save(saved.Revision,Parse(duplicate)),"duplicate JSON fields are rejected");
            var infinite=Document().ToJsonString().Replace("\"width\":3","\"width\":1e999",StringComparison.Ordinal);
            Reject(()=>workspace.Save(saved.Revision,Parse(infinite)),"overflowing finite JSON numbers are rejected");
            Check(File.ReadAllBytes(Path.Combine(directory,InkWorkspace.FileName)).SequenceEqual(before)&&workspace.Snapshot().Revision==saved.Revision,"all invalid saves preserve both disk contents and memory revision");

            var maxPoints=Document(new string('n',InkWorkspace.MaxNotes));
            maxPoints["strokes"]![0]!["points"]=new JsonArray(Enumerable.Range(0,InkWorkspace.MaxPoints).Select(i=>(JsonNode)new JsonArray((double)i/InkWorkspace.MaxPoints,0.5,0.5)).ToArray());
            var maximum=workspace.Save(saved.Revision,Element(maxPoints));
            Check(maximum.Document.GetProperty("strokes")[0].GetProperty("points").GetArrayLength()==InkWorkspace.MaxPoints,"maximum supported points and notes save successfully");
            maxPoints["strokes"]!.AsArray().Add(Document()["strokes"]![0]!.DeepClone());
            Reject(()=>workspace.Save(maximum.Revision,Element(maxPoints)),"point limit is enforced across all strokes");
            var padded=Document("ş").ToJsonString();padded=padded.Insert(1,new string(' ',InkWorkspace.MaxDocumentBytes-Encoding.UTF8.GetByteCount(padded)+1));
            Reject(()=>workspace.Save(maximum.Revision,Parse(padded)),"document UTF-8 byte limit includes whitespace and Unicode");

            var concurrent=workspace.Snapshot().Revision;var success=0;var conflicts=0;
            Parallel.For(0,8,i=>{try{workspace.Save(concurrent,Element(Document("writer "+i)));Interlocked.Increment(ref success);}catch(InkConflictException){Interlocked.Increment(ref conflicts);}});
            Check(success==1&&conflicts==7,"exactly one simultaneous writer can commit the same revision");
            Check(new InkWorkspace(directory).Snapshot().Revision==workspace.Snapshot().Revision&&Directory.GetFiles(directory,"*.tmp").Length==0,"concurrent saves leave one complete file and no temporary files");

            var failureDir=Fixture("write-failure");var failure=new InkWorkspace(failureDir);var failureBefore=failure.Snapshot();var target=Path.Combine(failureDir,InkWorkspace.FileName);var preserved=target+".saved";
            File.Move(target,preserved);Directory.CreateDirectory(target);
            try{failure.Save(failureBefore.Revision,Element(Document("must not commit")));throw new Exception("Expected disk write failure");}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){Check(failure.Snapshot().Revision==failureBefore.Revision&&Directory.GetFiles(failureDir,"*.tmp").Length==0,"failed atomic replace leaves in-memory state unchanged and cleans its temporary file");}
            Directory.Delete(target);File.Move(preserved,target);

            foreach(var corrupt in new[]{"{damaged","null","{\"version\":1,\"workspaceId\":\"../bad\",\"document\":{}}",new string(' ',InkWorkspace.MaxRequestBytes+1)}) {
                var corruptDir=Fixture("damaged-"+Guid.NewGuid().ToString("N"));var corruptPath=Path.Combine(corruptDir,InkWorkspace.FileName);File.WriteAllText(corruptPath,corrupt);
                var recovered=new InkWorkspace(corruptDir).Snapshot();var copies=Directory.GetFiles(corruptDir,"ink-workspace.corrupt-*.json");
                Check(recovered.RecoveryWarning.Length>0&&recovered.Document.GetProperty("strokes").GetArrayLength()==0&&copies.Length==1&&File.ReadAllText(copies[0])==corrupt,"damaged saved ink is quarantined verbatim before a clean workspace is created");
                Check(new InkWorkspace(corruptDir).Snapshot().WorkspaceId==recovered.WorkspaceId&&Directory.GetFiles(corruptDir,"ink-workspace.corrupt-*.json").Length==1,"recovered workspace persists without repeated quarantine");
            }
            Console.WriteLine(passed+" shared ink backend checks passed.");
        }finally {
            var absolute=Path.GetFullPath(root);var temporary=Path.GetFullPath(Path.GetTempPath());
            if(absolute.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("deskwing-ink-tests-",StringComparison.Ordinal))Directory.Delete(absolute,true);
        }
    }
    private static JsonObject WithNotes(this JsonObject document,string notes){document["notes"]=notes;return document;}
}
