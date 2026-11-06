using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabletDeck;

internal sealed record NotebookBackground(string Kind,int Width,int Height,string SourceName,int SourcePage,string? Image=null);
internal sealed record NotebookPageSummary(string Id,string Name,bool Legacy,bool Archived,string UpdatedAt,int StrokeCount,int NoteLength,NotebookBackground? Background);
internal sealed record NotebookSummary(string Id,string Name,NotebookPageSummary[] Pages);
internal sealed record NotebookCatalog(string LibraryId,string Revision,NotebookSummary[] Notebooks,NotebookSummary[] ArchivedNotebooks);
internal sealed record NotebookPageSnapshot(string NotebookId,string PageId,string WorkspaceId,string Revision,JsonElement Document,string RecoveryWarning,NotebookBackground? Background);
internal sealed record NotebookUnchangedSnapshot(string NotebookId,string PageId,string WorkspaceId,string Revision,string RecoveryWarning,bool Unchanged=true);
internal sealed record NotebookHistoryVersion(string Id,string Created,int StrokeCount,int NoteLength);
internal sealed record NotebookHistorySnapshot(NotebookHistoryVersion[] Versions);
internal sealed class NotebookConflictException(string message,string revision) : InvalidOperationException(message) { public string Revision {get;}=revision; }

// The catalog is separate from boards and the original scratchpad. The original page still
// delegates directly to InkWorkspace, so old clients and local recovery keys remain valid.
internal sealed class NotebookWorkspace
{
    internal const int MaxBackgroundBytes=1024*1024,MaxStoreBytes=128*1024*1024,MaxRequestBytes=4*1024*1024;
    internal const int MaxNotebooks=20,MaxPages=100,MaxHistory=20;
    internal const string CatalogFileName="catalog.json";
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=16};
    private readonly object gate=new();
    // All file admission and writes share this lock. Never acquire the legacy ink lock while
    // holding it: legacy BeforeSave takes its ink lock first and then this storage lock.
    private readonly object storageGate=new();
    private readonly string root,catalogPath,assetsPath,pagesPath,historyPath;
    private readonly InkWorkspace legacy;
    private readonly string legacyId;
    private readonly Dictionary<string,InkWorkspace> pages=new(StringComparer.Ordinal);
    private State state;

    private sealed class State { public int Version {get;set;}=1;public string LibraryId {get;set;}="";public List<Book> Notebooks {get;set;}=[]; }
    private sealed class Book { public string Id {get;set;}="";public string Name {get;set;}="";public bool Archived {get;set;} public List<PageEntry> Pages {get;set;}=[]; }
    private sealed class PageEntry { public string Id {get;set;}="";public string Name {get;set;}="";public bool Legacy {get;set;} public bool Archived {get;set;} public BackgroundEntry? Background {get;set;} }
    private sealed class BackgroundEntry { public string Asset {get;set;}="";public int Width {get;set;} public int Height {get;set;} public string SourceName {get;set;}="";public int SourcePage {get;set;} }
    private sealed class HistoryEntry { public string Id {get;set;}="";public string Created {get;set;}="";public JsonElement Document {get;set;} }
    private sealed record PendingBackground(BackgroundEntry Entry,byte[] Bytes);

    internal NotebookWorkspace(string directory,InkWorkspace legacy)
    {
        this.legacy=legacy;legacyId=legacy.Snapshot().WorkspaceId;root=Path.Combine(Path.GetFullPath(directory),"notebooks");
        assetsPath=Path.Combine(root,"assets");pagesPath=Path.Combine(root,"pages");historyPath=Path.Combine(root,"history");catalogPath=Path.Combine(root,CatalogFileName);
        foreach(var path in new[]{root,assetsPath,pagesPath,historyPath}){Directory.CreateDirectory(path);NoLink(path);}
        if(File.Exists(catalogPath)) {
            var bytes=ReadBounded(catalogPath,1024*1024);using var parsed=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});NoDuplicateKeys(parsed.RootElement);
            state=JsonSerializer.Deserialize<State>(bytes,Json)??throw Invalid("The notebook catalog is empty. Its original file was retained.");ValidateState(state);
            if(state.LibraryId!=legacy.Snapshot().WorkspaceId)throw Invalid("This notebook catalog belongs to another scratchpad. Its original files were retained.");
            foreach(var page in state.Notebooks.SelectMany(book=>book.Pages).Where(page=>!page.Legacy))if(!File.Exists(Path.Combine(pagesPath,page.Id,InkWorkspace.FileName)))throw Invalid("A saved notebook page is missing. The catalog was kept unchanged; restore the page file before continuing.");
        } else {
            var id=legacy.Snapshot().WorkspaceId;
            state=new(){LibraryId=id,Notebooks=[new(){Id=id,Name="Scratchpad",Pages=[new(){Id=id,Name="Original scratchpad",Legacy=true}]}]};
            // Opening Notebooks does not rewrite either the existing scratchpad or a new catalog.
        }
        legacy.BeforeSave+=CaptureLegacyHistory;
    }

    public NotebookCatalog Catalog(){lock(gate)return CatalogLocked();}
    public NotebookCatalog Change(JsonElement request)
    {
        lock(gate) {
            var operation=RequiredText(request,"operation",30);var expected=RequiredText(request,"expectedRevision",64);Expect(expected,CatalogRevision());
            var next=Copy(state);PendingBackground? background=null;InkSnapshot? copySource=null;PageEntry? created=null;
            switch(operation) {
                case "createNotebook": {
                    Fields(request,["operation","expectedRevision","name"],["background","firstPageName"]);
                    var name=Name(request.GetProperty("name"));var first=request.TryGetProperty("firstPageName",out var firstValue)?Name(firstValue):"Page 1";
                    background=ReadBackground(request);created=new(){Id=NewId(),Name=first,Background=background?.Entry};
                    next.Notebooks.Add(new(){Id=NewId(),Name=name,Pages=[created]});break;
                }
                case "renameNotebook":Fields(request,["operation","expectedRevision","notebookId","name"]);BookFor(next,Id(request,"notebookId"),true).Name=Name(request.GetProperty("name"));break;
                case "archiveNotebook":case "restoreNotebook": {
                    Fields(request,["operation","expectedRevision","notebookId"]);var book=BookFor(next,Id(request,"notebookId"),true);
                    if(book.Pages.Any(p=>p.Legacy))throw Invalid("The original Scratchpad stays available for older clients. Archive its other pages individually.");
                    book.Archived=operation=="archiveNotebook";break;
                }
                case "createPage": {
                    Fields(request,["operation","expectedRevision","notebookId","name"],["background"]);var book=BookFor(next,Id(request,"notebookId"));
                    background=ReadBackground(request);created=new(){Id=NewId(),Name=Name(request.GetProperty("name")),Background=background?.Entry};book.Pages.Add(created);break;
                }
                case "renamePage": {
                    Fields(request,["operation","expectedRevision","notebookId","pageId","name"]);PageFor(next,Id(request,"notebookId"),Id(request,"pageId"),true).Name=Name(request.GetProperty("name"));break;
                }
                case "archivePage":case "restorePage": {
                    Fields(request,["operation","expectedRevision","notebookId","pageId"]);var page=PageFor(next,Id(request,"notebookId"),Id(request,"pageId"),true);
                    if(page.Legacy)throw Invalid("The original scratchpad stays available for older clients.");page.Archived=operation=="archivePage";break;
                }
                case "duplicatePage": {
                    Fields(request,["operation","expectedRevision","notebookId","pageId"],["name"]);var notebookId=Id(request,"notebookId");var page=PageFor(next,notebookId,Id(request,"pageId"));
                    copySource=Workspace(page).Snapshot();created=new(){Id=NewId(),Name=request.TryGetProperty("name",out var newName)?Name(newName):(page.Name.Length>75?page.Name[..75]:page.Name)+" copy",Background=page.Background};
                    BookFor(next,notebookId).Pages.Add(created);break;
                }
                case "purgeHistory": {
                    Fields(request,["operation","expectedRevision","notebookId","pageId","expectedPageRevision","confirmed"]);Confirmed(request);
                    var page=PageFor(next,Id(request,"notebookId"),Id(request,"pageId"));var workspace=Workspace(page);
                    workspace.WithCheckedRevision(RequiredText(request,"expectedPageRevision",64),()=>{lock(storageGate){var file=HistoryFile(page.Id);if(File.Exists(file)){NoLink(file);File.Delete(file);}}});
                    return CatalogLocked();
                }
                case "deletePage": {
                    Fields(request,["operation","expectedRevision","notebookId","pageId","confirmed"]);Confirmed(request);
                    var book=BookFor(next,Id(request,"notebookId"),true);var page=PageFor(next,book.Id,Id(request,"pageId"),true);
                    if(page.Legacy||!page.Archived||book.Pages.Count==1)throw Invalid("Permanently remove an archived non-original page, leaving at least one page in its notebook.");book.Pages.Remove(page);
                    CommitCatalog(next);DeletePageData(page);return CatalogLocked();
                }
                case "deleteNotebook": {
                    Fields(request,["operation","expectedRevision","notebookId","confirmed"]);Confirmed(request);var book=BookFor(next,Id(request,"notebookId"),true);
                    if(!book.Archived||book.Pages.Any(p=>p.Legacy))throw Invalid("Permanently remove an archived notebook other than the original Scratchpad.");next.Notebooks.Remove(book);
                    CommitCatalog(next);foreach(var page in book.Pages)DeletePageData(page);return CatalogLocked();
                }
                default:throw Invalid("Choose a supported notebook operation.");
            }
            ValidateState(next);var bytes=JsonSerializer.SerializeToUtf8Bytes(next,Json);
            lock(storageGate){var assetBytes=background is not null&&!File.Exists(AssetFile(background.Entry.Asset))?background.Bytes.Length:0;var pageBytes=created is null?0:copySource is null?512:Encoding.UTF8.GetByteCount(copySource.Document.GetRawText())+512;EnsureCapacity(bytes.Length+assetBytes+pageBytes,catalogPath);
                var createdAsset=false;var createdPage=false;
                try {
                    if(background is not null){WriteAsset(background);createdAsset=assetBytes>0;}
                    if(created is not null){if(Directory.Exists(Path.Combine(pagesPath,created.Id)))throw Invalid("A new notebook identity already exists. Retry the operation.");createdPage=true;var workspace=Workspace(created,true);if(copySource is not null)workspace.Save(workspace.Snapshot().Revision,copySource.Document);}
                    WriteAtomic(catalogPath,bytes);state=next;
                } catch {
                    // Only resources made by this uncommitted operation are candidates for cleanup.
                    // Existing pages and deduplicated/shared PDF assets are never removed here.
                    try {if(createdPage&&created is not null)RemoveUncommittedPage(created.Id);if(createdAsset&&background is not null){var asset=AssetFile(background.Entry.Asset);if(File.Exists(asset)){NoLink(asset);File.Delete(asset);}}}catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException){/* Retain bounded orphan data rather than masking the original failed commit. */}
                    throw;
                }
            }return CatalogLocked();
        }
    }

    public object Page(string notebookId,string pageId,string? knownRevision=null)
    {
        lock(gate) {
            var page=PageFor(state,ValidId(notebookId),ValidId(pageId));var snapshot=Workspace(page).Snapshot();
            if(knownRevision is not null){ValidRevision(knownRevision);if(knownRevision==snapshot.Revision)return new NotebookUnchangedSnapshot(notebookId,pageId,snapshot.WorkspaceId,snapshot.Revision,snapshot.RecoveryWarning);}
            return Snapshot(notebookId,page,snapshot);
        }
    }
    public NotebookPageSnapshot SavePage(string notebookId,string pageId,string? expectedRevision,JsonElement document)
    {
        lock(gate){var page=PageFor(state,ValidId(notebookId),ValidId(pageId));return SaveLocked(notebookId,page,expectedRevision,document);}
    }
    public NotebookHistorySnapshot History(string notebookId,string pageId)
    {
        lock(gate){PageFor(state,ValidId(notebookId),ValidId(pageId));return new(ReadHistory(pageId).Select(h=>new NotebookHistoryVersion(h.Id,h.Created,h.Document.GetProperty("strokes").GetArrayLength(),h.Document.GetProperty("notes").GetString()!.Length)).ToArray());}
    }
    public NotebookPageSnapshot Restore(string notebookId,string pageId,string? expectedRevision,string versionId)
    {
        lock(gate){var page=PageFor(state,ValidId(notebookId),ValidId(pageId));ValidRevision(versionId);var entry=ReadHistory(pageId).FirstOrDefault(h=>h.Id==versionId)??throw Invalid("This page version is no longer in its retained history.");return SaveLocked(notebookId,page,expectedRevision,entry.Document);}
    }

    private NotebookPageSnapshot SaveLocked(string notebookId,PageEntry page,string? expectedRevision,JsonElement document)
    {
        var validated=InkWorkspace.ValidateDocument(document);var workspace=Workspace(page);var current=workspace.Snapshot();
        ValidRevision(expectedRevision);if(expectedRevision!=current.Revision)throw new NotebookConflictException("This notebook page changed. Reload or keep a recovery copy before replacing it.",current.Revision);
        using var parsed=JsonDocument.Parse(validated);if(current.Document.GetRawText()==parsed.RootElement.GetRawText())return Snapshot(notebookId,page,current);
        CaptureHistory(page.Id,current,0);
        InkSnapshot saved;
        if(page.Legacy)saved=workspace.Save(expectedRevision,parsed.RootElement);
        else lock(storageGate){EnsureCapacity(validated.Length+512,Path.Combine(pagesPath,page.Id,InkWorkspace.FileName));saved=workspace.Save(expectedRevision,parsed.RootElement);}
        return Snapshot(notebookId,page,saved);
    }
    private void CaptureLegacyHistory(InkSnapshot previous)=>CaptureHistory(previous.WorkspaceId,previous,0);
    private void CaptureHistory(string pageId,InkSnapshot previous,int incomingBytes)
    {
        lock(storageGate){var history=ReadHistory(pageId);if(history.Any(h=>h.Id==previous.Revision))return;
            history.Insert(0,new(){Id=previous.Revision,Created=DateTimeOffset.UtcNow.ToString("o"),Document=previous.Document});if(history.Count>MaxHistory)history.RemoveRange(MaxHistory,history.Count-MaxHistory);
            var file=HistoryFile(pageId);var bytes=JsonSerializer.SerializeToUtf8Bytes(history,Json);while(bytes.Length>16*1024*1024&&history.Count>1){history.RemoveAt(history.Count-1);bytes=JsonSerializer.SerializeToUtf8Bytes(history,Json);}
            EnsureCapacity(bytes.Length+incomingBytes,file);WriteAtomic(file,bytes);
        }
    }
    private void CommitCatalog(State next){ValidateState(next);var bytes=JsonSerializer.SerializeToUtf8Bytes(next,Json);lock(storageGate){EnsureCapacity(bytes.Length,catalogPath);WriteAtomic(catalogPath,bytes);state=next;}}
    private void DeletePageData(PageEntry page)
    {
        if(page.Legacy)throw Invalid("The original scratchpad cannot be deleted.");
        var directory=Path.Combine(pagesPath,ValidId(page.Id));var prefix=pagesPath+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(directory).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw Invalid("Invalid notebook storage path.");
        lock(storageGate){
            if(Directory.Exists(directory)){NoLink(directory);foreach(var file in Directory.EnumerateFiles(directory)){var name=Path.GetFileName(file);if(name==InkWorkspace.FileName||name.StartsWith("ink-workspace.corrupt-",StringComparison.Ordinal)&&name.EndsWith(".json",StringComparison.Ordinal)){NoLink(file);File.Delete(file);}}if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);}
            var history=HistoryFile(page.Id);if(File.Exists(history)){NoLink(history);File.Delete(history);}
            pages.Remove(page.Id);
            if(page.Background is { } background&&!state.Notebooks.SelectMany(book=>book.Pages).Any(other=>other.Background?.Asset==background.Asset)){var asset=AssetFile(background.Asset);if(File.Exists(asset)){NoLink(asset);File.Delete(asset);}}
        }
    }
    private static void Confirmed(JsonElement request){if(request.GetProperty("confirmed").ValueKind!=JsonValueKind.True)throw Invalid("Explicitly confirm permanent removal of notebook data.");}
    private void RemoveUncommittedPage(string pageId)
    {
        var directory=Path.GetFullPath(Path.Combine(pagesPath,ValidId(pageId)));if(!directory.StartsWith(pagesPath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw Invalid("Invalid notebook storage path.");
        if(Directory.Exists(directory)){NoLink(directory);var file=Path.Combine(directory,InkWorkspace.FileName);if(File.Exists(file)){NoLink(file);File.Delete(file);}if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);}
        pages.Remove(pageId);
    }
    private NotebookCatalog CatalogLocked()
    {
        NotebookSummary Summarize(Book book)=>new(book.Id,book.Name,book.Pages.Select(page=>{
            var snapshot=Workspace(page).Snapshot();return new NotebookPageSummary(page.Id,page.Name,page.Legacy,page.Archived,snapshot.Document.GetProperty("updatedAt").GetString()??"",snapshot.Document.GetProperty("strokes").GetArrayLength(),snapshot.Document.GetProperty("notes").GetString()!.Length,Background(page,false));
        }).ToArray());
        return new(state.LibraryId,CatalogRevision(),state.Notebooks.Where(n=>!n.Archived).Select(Summarize).ToArray(),state.Notebooks.Where(n=>n.Archived).Select(Summarize).ToArray());
    }
    private NotebookPageSnapshot Snapshot(string notebookId,PageEntry page,InkSnapshot snapshot)=>new(notebookId,page.Id,snapshot.WorkspaceId,snapshot.Revision,snapshot.Document,snapshot.RecoveryWarning,Background(page,true));
    private NotebookBackground? Background(PageEntry page,bool includeImage)
    {
        var background=page.Background;if(background is null)return null;
        string? image=null;if(includeImage){var bytes=ReadBounded(AssetFile(background.Asset),MaxBackgroundBytes);ValidatePng(bytes,background.Width,background.Height);if(Hash(bytes)!=background.Asset)throw Invalid("The saved PDF background changed. Its original file was retained.");image="data:image/png;base64,"+Convert.ToBase64String(bytes);}
        return new("png",background.Width,background.Height,background.SourceName,background.SourcePage,image);
    }
    private InkWorkspace Workspace(PageEntry page,bool create=false)
    {
        if(page.Legacy)return legacy;
        if(!pages.TryGetValue(page.Id,out var workspace)){var directory=Path.Combine(pagesPath,ValidId(page.Id));lock(storageGate){if(!create&&!File.Exists(Path.Combine(directory,InkWorkspace.FileName)))throw Invalid("This notebook page is missing. Its catalog entry was retained for recovery.");Directory.CreateDirectory(directory);NoLink(directory);workspace=new InkWorkspace(directory);pages.Add(page.Id,workspace);}}return workspace;
    }
    private List<HistoryEntry> ReadHistory(string pageId)
    {
        lock(storageGate){var file=HistoryFile(pageId);if(!File.Exists(file))return [];
        var bytes=ReadBounded(file,16*1024*1024);using var parsed=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});NoDuplicateKeys(parsed.RootElement);
        var values=JsonSerializer.Deserialize<List<HistoryEntry>>(bytes,Json)??throw Invalid("This page history is unreadable.");if(values.Count>MaxHistory)throw Invalid("This page history exceeds its limit.");
        foreach(var entry in values){ValidRevision(entry.Id);if(!DateTimeOffset.TryParse(entry.Created,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _))throw Invalid("This page history has an invalid date.");InkWorkspace.ValidateDocument(entry.Document);}return values;}
    }
    private void WriteAsset(PendingBackground background)
    {
        var file=AssetFile(background.Entry.Asset);if(File.Exists(file)){if(Hash(ReadBounded(file,MaxBackgroundBytes))!=background.Entry.Asset)throw Invalid("A saved PDF background changed.");return;}WriteAtomic(file,background.Bytes);
    }
    private static PendingBackground? ReadBackground(JsonElement request)
    {
        if(!request.TryGetProperty("background",out var value)||value.ValueKind==JsonValueKind.Null)return null;
        Fields(value,["kind","image","width","height","sourceName","sourcePage"]);
        if(RequiredText(value,"kind",3)!="png")throw Invalid("PDF pages must use a PNG background.");
        var encoded=RequiredText(value,"image",((MaxBackgroundBytes+2)/3)*4+22);const string prefix="data:image/png;base64,";
        if(!encoded.StartsWith(prefix,StringComparison.Ordinal))throw Invalid("Use a PNG image background.");
        byte[] bytes;try{bytes=Convert.FromBase64String(encoded[prefix.Length..]);}catch(FormatException){throw Invalid("The PDF page background is not valid base64.");}
        var width=Integer(value,"width",1,2048);var height=Integer(value,"height",1,2048);ValidatePng(bytes,width,height);
        var name=RequiredText(value,"sourceName",120);if(name.Any(char.IsControl))throw Invalid("The PDF source name contains unsupported characters.");
        var number=Integer(value,"sourcePage",1,10000);return new(new(){Asset=Hash(bytes),Width=width,Height=height,SourceName=name,SourcePage=number},bytes);
    }
    private static void ValidatePng(byte[] bytes,int width,int height)
    {
        ReadOnlySpan<byte> signature=[137,80,78,71,13,10,26,10];
        if(bytes.Length is <45 or >MaxBackgroundBytes||!bytes.AsSpan(0,8).SequenceEqual(signature)||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8,4))!=13||!bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4))!=width||BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4))!=height||width is <1 or >2048||height is <1 or >2048||(long)width*height>4_000_000)throw Invalid("The PDF page background must be a PNG up to 1 MB and 2048 pixels per side.");
        // Check chunk lengths, their checksums, and the terminal IEND so appended content and
        // truncated/forged containers are rejected before native/browser image decoding.
        var offset=8;var data=false;
        while(offset<bytes.Length){if(bytes.Length-offset<12)throw Invalid("The PNG background is incomplete.");var length=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset,4));if(length>MaxBackgroundBytes||length>bytes.Length-offset-12)throw Invalid("The PNG background has an invalid chunk.");var count=checked((int)length);var type=bytes.AsSpan(offset+4,4);if(Crc32(bytes.AsSpan(offset+4,count+4))!=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset+8+count,4)))throw Invalid("The PNG background checksum is invalid.");if(type.SequenceEqual("IDAT"u8))data=true;offset+=count+12;if(type.SequenceEqual("IEND"u8)){if(count!=0||offset!=bytes.Length||!data)throw Invalid("The PNG background has trailing or missing image data.");return;}}
        throw Invalid("The PNG background has no complete image.");
    }
    private static uint Crc32(ReadOnlySpan<byte> bytes){uint crc=0xffffffff;foreach(var value in bytes){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}return ~crc;}
    private void ValidateState(State value)
    {
        if(value.Version!=1||value.LibraryId!=legacyId||value.Notebooks is null||value.Notebooks.Count is <1 or >40||value.Notebooks.Any(n=>n is null)||value.Notebooks.Count(n=>!n.Archived)>MaxNotebooks)throw Invalid("The notebook catalog is unsupported or exceeds its document limit.");
        var ids=new HashSet<string>(StringComparer.Ordinal);var pageIds=new HashSet<string>(StringComparer.Ordinal);var legacyCount=0;
        foreach(var book in value.Notebooks){ValidId(book.Id);if(!ids.Add(book.Id))throw Invalid("Notebook identities must be unique.");ValidateName(book.Name);if(book.Pages is null||book.Pages.Count==0||book.Pages.Any(p=>p is null))throw Invalid("Every notebook needs a page.");foreach(var page in book.Pages){ValidId(page.Id);ValidateName(page.Name);if(!pageIds.Add(page.Id))throw Invalid("Page identities must be unique.");if(page.Legacy){legacyCount++;if(page.Id!=value.LibraryId||page.Background is not null||page.Archived||book.Archived)throw Invalid("The original scratchpad reference is invalid.");}if(page.Background is { } background){ValidAsset(background.Asset);if(background.Width is <1 or >2048||background.Height is <1 or >2048||(long)background.Width*background.Height>4_000_000||background.SourcePage is <1 or >10000||background.SourceName is null||background.SourceName.Length>120||background.SourceName.Any(char.IsControl))throw Invalid("The saved PDF page metadata is invalid.");}}}
        if(legacyCount!=1||pageIds.Count>MaxPages)throw Invalid("Keep the original scratchpad and at most 100 notebook pages.");
    }
    private void EnsureCapacity(long additional,string? replacing=null)
    {
        long total=0;foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){NoLink(file);if(!string.Equals(file,replacing,StringComparison.OrdinalIgnoreCase))total+=new FileInfo(file).Length;if(total+additional>MaxStoreBytes)throw Invalid("Notebook storage is full (128 MB). Export work before adding more pages.");}if(total+additional>MaxStoreBytes)throw Invalid("Notebook storage is full (128 MB). Export work before adding more pages.");
    }
    private static void WriteAtomic(string path,byte[] bytes)
    {
        var temporary=path+"."+NewId()+".tmp";try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static byte[] ReadBounded(string path,int limit){NoLink(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>limit)throw Invalid("A notebook file exceeds its supported size. Its original file was retained.");var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);return bytes;}
    private string CatalogRevision()=>Hash(JsonSerializer.SerializeToUtf8Bytes(state,Json));
    private static State Copy(State value)=>JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(value,Json),Json)!;
    private static void Expect(string expected,string current){ValidRevision(expected);if(expected!=current)throw new NotebookConflictException("The notebook library changed. Refresh it before making this change.",current);}
    private static Book BookFor(State value,string id,bool archived=false)=>value.Notebooks.FirstOrDefault(n=>n.Id==id&&(archived||!n.Archived))??throw Invalid("This notebook is unavailable or archived.");
    private static PageEntry PageFor(State value,string notebookId,string pageId,bool archived=false)=>BookFor(value,notebookId,archived).Pages.FirstOrDefault(p=>p.Id==pageId&&(archived||!p.Archived))??throw Invalid("This notebook page is unavailable or archived.");
    private string HistoryFile(string id)=>Path.Combine(historyPath,ValidId(id)+".json");
    private string AssetFile(string id)=>Path.Combine(assetsPath,ValidAsset(id)+".png");
    private static string ValidId(string id){if(id is null||!Guid.TryParseExact(id,"N",out _))throw Invalid("Choose a valid notebook and page identity.");return id;}
    private static string Id(JsonElement value,string name)=>ValidId(RequiredText(value,name,32));
    private static string ValidAsset(string id){ValidRevision(id);return id;}
    private static void ValidRevision(string? value){if(value is not {Length:64}||!value.All(Uri.IsHexDigit))throw Invalid("Reload the current notebook revision before saving.");}
    private static string Name(JsonElement value){var name=InkWorkspace.Text(value,80,"page name").Trim();ValidateName(name);return name;}
    private static void ValidateName(string? name){if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl))throw Invalid("Use a name of 1–80 characters without control characters.");}
    private static string RequiredText(JsonElement value,string property,int max){if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(property,out var found))throw Invalid("The notebook request is missing "+property+".");return InkWorkspace.Text(found,max,property);}
    private static int Integer(JsonElement value,string name,int minimum,int maximum){if(!value.TryGetProperty(name,out var found)||!found.TryGetInt32(out var number)||number<minimum||number>maximum)throw Invalid("The notebook "+name+" is outside its supported range.");return number;}
    internal static void Fields(JsonElement value,string[] required,string[]? optional=null){if(value.ValueKind!=JsonValueKind.Object)throw Invalid("Send a notebook JSON object.");var allowed=new HashSet<string>(required.Concat(optional??[]),StringComparer.Ordinal);var missing=new HashSet<string>(required,StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!allowed.Remove(property.Name))throw Invalid("The notebook request contains an unknown or duplicate field.");missing.Remove(property.Name);}if(missing.Count>0)throw Invalid("The notebook request is missing required fields.");}
    private static void NoDuplicateKeys(JsonElement value){if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw Invalid("The notebook file contains duplicate fields. Its original file was retained.");NoDuplicateKeys(property.Value);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())NoDuplicateKeys(item);}
    private static void NoLink(string path){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Invalid("Notebook files must be stored in ordinary private folders.");}
    private static string NewId()=>Guid.NewGuid().ToString("N");
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    private static ArgumentException Invalid(string message)=>new(message);
}
