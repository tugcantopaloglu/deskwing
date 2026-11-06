using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabletDeck;

var checks = 0;
var root = Path.Combine(Path.GetTempPath(), "deskwing-notebook-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label) { try { action(); throw new Exception("Accepted invalid request: " + label); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Check(true, label); } }
void Conflict(Action action, string label) { try { action(); throw new Exception("Accepted stale request: " + label); } catch (InvalidOperationException) { Check(true, label); } }
JsonElement Snapshot(object value) => JsonSerializer.SerializeToElement(value, jsonOptions);
JsonElement Parse(string value) { using var parsed = JsonDocument.Parse(value); return parsed.RootElement.Clone(); }
JsonElement Document(string notes, bool ink = false) => Parse(JsonSerializer.Serialize(new { version = 1, strokes = ink ? new[] { new { tool = "pen", color = "#295a95", width = 3, points = new[] { new[] { 0.1, 0.2, 0.4 }, new[] { 0.7, 0.8, 0.9 } } } } : [], notes, penOnly = true, updatedAt = "2026-11-06T15:00:00.000Z" }));
string Revision(NotebookWorkspace workspace) => Snapshot(workspace.Catalog()).GetProperty("revision").GetString()!;
JsonElement Request(string operation, string revision, params (string Name, object? Value)[] values) {
    var fields = new Dictionary<string, object?> { ["operation"] = operation, ["expectedRevision"] = revision };
    foreach (var value in values) fields.Add(value.Name, value.Value);
    return JsonSerializer.SerializeToElement(fields);
}
JsonElement Change(NotebookWorkspace workspace, string operation, params (string Name, object? Value)[] values) => Snapshot(workspace.Change(Request(operation, Revision(workspace), values)));
JsonElement[] Notebooks(NotebookWorkspace workspace) => Snapshot(workspace.Catalog()).GetProperty("notebooks").EnumerateArray().ToArray();
JsonElement FindNotebook(NotebookWorkspace workspace, string name) => Notebooks(workspace).Single(value => value.GetProperty("name").GetString() == name);
JsonElement[] Pages(JsonElement notebook) => notebook.GetProperty("pages").EnumerateArray().ToArray();
string Id(JsonElement item) => item.GetProperty("id").GetString()!;
string PageRevision(NotebookWorkspace workspace, string notebook, string page) => Snapshot(workspace.Page(notebook, page)).GetProperty("revision").GetString()!;
string Notes(object page) => Snapshot(page).GetProperty("document").GetProperty("notes").GetString()!;
string Fixture(string name) { var directory = Path.Combine(root, name); Directory.CreateDirectory(directory); return directory; }
NotebookWorkspace Workspace(string name, out InkWorkspace legacy) { var path = Fixture(name); legacy = new InkWorkspace(path); return new NotebookWorkspace(path, legacy); }
string CataloguePath(string directory) => Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Single(path => Path.GetFileName(path) != InkWorkspace.FileName && File.ReadAllText(path).Contains("\"notebooks\"", StringComparison.Ordinal));

try {
    var data = Fixture("legacy");
    var legacy = new InkWorkspace(data);
    var original = legacy.Save(legacy.Snapshot().Revision, Document("Existing notes: şğü 🖊", true));
    var originalBytes = File.ReadAllBytes(Path.Combine(data, InkWorkspace.FileName));
    var workspace = new NotebookWorkspace(data, legacy);
    var catalog = Snapshot(workspace.Catalog());
    Check(catalog.GetProperty("revision").GetString()!.Length == 64, "catalogue has an optimistic revision token");
    Check(Guid.TryParseExact(catalog.GetProperty("libraryId").GetString(), "N", out _), "library identity is an opaque persistent GUID");
    var first = Notebooks(workspace).Single();
    var initialPage = Pages(first).Single();
    var initialNotebookId = Id(first); var initialPageId = Id(initialPage);
    Check(initialPage.GetProperty("legacy").GetBoolean(), "initial scratchpad page is explicitly legacy-linked");
    var linked = Snapshot(workspace.Page(initialNotebookId, initialPageId));
    Check(linked.GetProperty("workspaceId").GetString() == original.WorkspaceId && linked.GetProperty("revision").GetString() == original.Revision, "opening notebooks preserves the original scratchpad identity and revision");
    Check(Notes(linked) == "Existing notes: şğü 🖊" && linked.GetProperty("document").GetProperty("strokes").GetArrayLength() == 1, "existing Unicode notes and pressure ink remain readable");
    Check(File.ReadAllBytes(Path.Combine(data, InkWorkspace.FileName)).SequenceEqual(originalBytes), "constructor, catalogue and page reads leave the legacy file byte-for-byte unchanged");
    var legacySave = workspace.SavePage(initialNotebookId, initialPageId, original.Revision, Document("Edited through notebook", true));
    Check(legacy.Snapshot().Document.GetProperty("notes").GetString() == "Edited through notebook", "notebook edits to the linked scratchpad remain visible to legacy clients");
    var oldClient = legacy.Save(legacy.Snapshot().Revision, Document("Edited through legacy", true));
    Check(Notes(workspace.Page(initialNotebookId, initialPageId)) == "Edited through legacy", "legacy client edits remain visible in the notebook page");
    var legacyVersions = Snapshot(workspace.History(initialNotebookId, initialPageId)).GetProperty("versions").EnumerateArray().ToArray();
    Check(legacyVersions.Any(value => value.GetProperty("id").GetString() == Snapshot(legacySave).GetProperty("revision").GetString()), "legacy API saves retain the prior document even while the UI stays in the original scratchpad");
    Conflict(() => workspace.SavePage(initialNotebookId, initialPageId, original.Revision, Document("stale")), "stale notebook writes cannot overwrite a newer legacy client");
    Check(legacy.Snapshot().Revision == oldClient.Revision, "rejected stale writes preserve current scratchpad revision");

    var beforeCatalog = Revision(workspace);
    Change(workspace, "createNotebook", ("name", "Project notes"));
    var created = FindNotebook(workspace, "Project notes");
    var notebookId = Id(created); var pageId = Id(Pages(created).Single());
    Check(notebookId != initialNotebookId && pageId != initialPageId, "new notebook and page have independent identities");
    Check(Notes(workspace.Page(notebookId, pageId)) == "", "new notebooks begin with an empty page");
    Conflict(() => workspace.Change(Request("renameNotebook", beforeCatalog, ("notebookId", notebookId), ("name", "Lost rename"))), "stale catalogue changes cannot overwrite concurrent library edits");
    Check(FindNotebook(workspace, "Project notes").GetProperty("name").GetString() == "Project notes", "rejected catalogue change preserves the current notebook name");
    var pageBefore = PageRevision(workspace, notebookId, pageId);
    workspace.SavePage(notebookId, pageId, pageBefore, Document("First page revision", true));
    var pageSecond = workspace.SavePage(notebookId, pageId, PageRevision(workspace, notebookId, pageId), Document("Second page revision", true));
    Check(legacy.Snapshot().Revision == oldClient.Revision, "saving another notebook does not mutate the existing scratchpad");
    Conflict(() => workspace.SavePage(notebookId, pageId, pageBefore, Document("stale")), "page revision guard rejects a delayed edit");
    Check(Notes(workspace.Page(notebookId, pageId)) == "Second page revision", "rejected page edit leaves the newer document intact");
    var unchanged = Snapshot(workspace.Page(notebookId, pageId, PageRevision(workspace, notebookId, pageId)));
    Check(unchanged.GetProperty("unchanged").GetBoolean() && !unchanged.TryGetProperty("document", out _), "unchanged page polling avoids returning document content");
    var versions = Snapshot(workspace.History(notebookId, pageId)).GetProperty("versions").EnumerateArray().ToArray();
    Check(versions.Length >= 2, "document history keeps prior page revisions");
    var restoreId = versions[0].GetProperty("id").GetString()!;
    var restoreRevision = PageRevision(workspace, notebookId, pageId);
    workspace.Restore(notebookId, pageId, restoreRevision, restoreId);
    Check(Notes(workspace.Page(notebookId, pageId)) != "Second page revision", "explicit history restore replaces the selected page with a retained version");
    Conflict(() => workspace.Restore(notebookId, pageId, restoreRevision, restoreId), "history restore also rejects a stale current revision");
    Reject(() => workspace.Restore(notebookId, pageId, PageRevision(workspace, notebookId, pageId), "../history"), "history identifiers cannot become filesystem paths");

    Change(workspace, "createPage", ("notebookId", notebookId), ("name", "Meeting"));
    var meeting = Pages(FindNotebook(workspace, "Project notes")).Single(value => value.GetProperty("name").GetString() == "Meeting");
    var meetingId = Id(meeting);
    Check(Notes(workspace.Page(notebookId, meetingId)) == "", "adding a page preserves existing page contents");
    Change(workspace, "renamePage", ("notebookId", notebookId), ("pageId", meetingId), ("name", "Meeting notes"));
    Check(Pages(FindNotebook(workspace, "Project notes")).Any(value => value.GetProperty("name").GetString() == "Meeting notes"), "page rename is visible in the catalogue");
    workspace.SavePage(notebookId, meetingId, PageRevision(workspace, notebookId, meetingId), Document("Meeting ink", true));
    Change(workspace, "duplicatePage", ("notebookId", notebookId), ("pageId", meetingId), ("name", "Meeting copy"));
    var duplicate = Pages(FindNotebook(workspace, "Project notes")).Single(value => value.GetProperty("name").GetString() == "Meeting copy");
    Check(Id(duplicate) != meetingId && Notes(workspace.Page(notebookId, Id(duplicate))) == "Meeting ink", "duplicating a page retains content with an independent page identity");
    workspace.SavePage(notebookId, Id(duplicate), PageRevision(workspace, notebookId, Id(duplicate)), Document("Independent copy"));
    Check(Notes(workspace.Page(notebookId, meetingId)) == "Meeting ink", "editing a duplicate cannot alter its source page");
    Change(workspace, "archivePage", ("notebookId", notebookId), ("pageId", Id(duplicate)));
    Reject(() => workspace.Page(notebookId, Id(duplicate)), "archived pages cannot be edited through their old route");
    Check(Pages(FindNotebook(workspace, "Project notes")).Single(value => Id(value) == Id(duplicate)).GetProperty("archived").GetBoolean(), "archived page metadata remains available for recovery");
    Change(workspace, "restorePage", ("notebookId", notebookId), ("pageId", Id(duplicate)));
    Check(Notes(workspace.Page(notebookId, Id(duplicate))) == "Independent copy", "restoring an archived page retains its independent ink");
    Change(workspace, "archiveNotebook", ("notebookId", notebookId));
    Check(!Notebooks(workspace).Any(value => Id(value) == notebookId), "archived notebook leaves the active list");
    var archived = Snapshot(workspace.Catalog()).GetProperty("archivedNotebooks").EnumerateArray().ToArray();
    Check(archived.Any(value => Id(value) == notebookId), "archived notebook is retained for recovery");
    Change(workspace, "restoreNotebook", ("notebookId", notebookId));
    Check(Notes(workspace.Page(notebookId, meetingId)) == "Meeting ink", "restoring an archived notebook preserves its pages and ink");
    Reject(() => workspace.Change(Request("archiveNotebook", Revision(workspace), ("notebookId", initialNotebookId))), "legacy scratchpad cannot disappear from old clients through notebook archive");
    Reject(() => workspace.Change(Request("archivePage", Revision(workspace), ("notebookId", initialNotebookId), ("pageId", initialPageId))), "legacy-linked page cannot be archived through the new page API");

    foreach (var invalid in new[] { "../outside", "C:\\outside", "a/b", new string('a', 31), new string('g', 32) }) {
        Reject(() => workspace.Page(invalid, pageId), "invalid notebook identifier is rejected: " + invalid);
        Reject(() => workspace.Page(notebookId, invalid), "invalid page identifier is rejected: " + invalid);
    }
    Reject(() => workspace.Change(Request("createNotebook", Revision(workspace), ("name", ""))), "empty notebook names are rejected");
    Reject(() => workspace.Change(Request("createNotebook", Revision(workspace), ("name", new string('n', 81)))), "oversized notebook names are rejected");
    Reject(() => workspace.Change(Request("createNotebook", Revision(workspace), ("name", "line\nbreak"))), "control characters cannot enter notebook names");
    Reject(() => workspace.Change(Request("runCommand", Revision(workspace), ("name", "ignored"))), "unknown catalogue operations are rejected");
    Reject(() => workspace.Change(Request("renameNotebook", Revision(workspace), ("notebookId", notebookId), ("name", "Safe"), ("command", "ignored"))), "unknown catalogue fields are rejected");
    var duplicateField = "{\"operation\":\"renameNotebook\",\"expectedRevision\":\"" + Revision(workspace) + "\",\"notebookId\":\"" + notebookId + "\",\"name\":\"first\",\"name\":\"second\"}";
    Reject(() => workspace.Change(Parse(duplicateField)), "duplicate JSON properties cannot ambiguously rename a notebook");
    Reject(() => workspace.SavePage(notebookId, pageId, PageRevision(workspace, notebookId, pageId), Document(new string('n', 8001))), "notebook pages retain the existing notes quota");
    Check(Notes(workspace.Page(notebookId, meetingId)) == "Meeting ink", "invalid operations preserve unrelated existing pages");

    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=";
    object Background(string image, int width = 1, int height = 1) => new { kind = "png", image, width, height, sourceName = "sample.pdf", sourcePage = 1 };
    Change(workspace, "createPage", ("notebookId", notebookId), ("name", "PDF page"), ("background", Background("data:image/png;base64," + png)));
    var pdfPage = Pages(FindNotebook(workspace, "Project notes")).Single(value => value.GetProperty("name").GetString() == "PDF page");
    Check(!pdfPage.GetProperty("background").TryGetProperty("image", out var catalogImage) || catalogImage.ValueKind == JsonValueKind.Null, "catalogue does not embed every PDF image");
    var detail = Snapshot(workspace.Page(notebookId, Id(pdfPage)));
    Check(detail.GetProperty("background").GetProperty("image").GetString() == "data:image/png;base64," + png, "page detail returns its validated PDF background");
    workspace.SavePage(notebookId, Id(pdfPage), detail.GetProperty("revision").GetString()!, Document("PDF annotation", true));
    Check(Snapshot(workspace.Page(notebookId, Id(pdfPage))).GetProperty("background").GetProperty("image").GetString() == "data:image/png;base64," + png, "saving annotations preserves the page background");
    Reject(() => workspace.Change(Request("createPage", Revision(workspace), ("notebookId", notebookId), ("name", "Bad image"), ("background", Background("data:image/png;base64,not-base64")))), "invalid background encoding is rejected");
    Reject(() => workspace.Change(Request("createPage", Revision(workspace), ("notebookId", notebookId), ("name", "Mismatched dimensions"), ("background", Background("data:image/png;base64," + png, 2, 1)))), "declared image dimensions must agree with the PNG header");
    Reject(() => workspace.Change(Request("createPage", Revision(workspace), ("notebookId", notebookId), ("name", "Too large"), ("background", Background("data:image/png;base64," + Convert.ToBase64String(new byte[1024 * 1024 + 1]))))), "background decoded bytes are bounded");

    var concurrentRevision = PageRevision(workspace, notebookId, meetingId); var successes = 0; var conflicts = 0;
    Parallel.For(0, 8, index => { try { workspace.SavePage(notebookId, meetingId, concurrentRevision, Document("Concurrent writer " + index)); Interlocked.Increment(ref successes); } catch (InvalidOperationException) { Interlocked.Increment(ref conflicts); } });
    Check(successes == 1 && conflicts == 7, "only one concurrent page writer can commit the same revision");
    var catalogRevision = Revision(workspace); successes = 0; conflicts = 0;
    Parallel.For(0, 8, index => { try { workspace.Change(Request("renamePage", catalogRevision, ("notebookId", notebookId), ("pageId", meetingId), ("name", "Concurrent name " + index))); Interlocked.Increment(ref successes); } catch (InvalidOperationException) { Interlocked.Increment(ref conflicts); } });
    Check(successes == 1 && conflicts == 7, "only one concurrent catalogue writer can commit the same revision");
    var persistedCatalog = Snapshot(workspace.Catalog()).GetRawText();
    var persistedPage = Snapshot(workspace.Page(notebookId, meetingId)).GetRawText();
    var reloaded = new NotebookWorkspace(data, new InkWorkspace(data));
    Check(Snapshot(reloaded.Catalog()).GetRawText() == persistedCatalog, "notebook names, pages, archives and catalogue revision survive restart");
    Check(Snapshot(reloaded.Page(notebookId, meetingId)).GetRawText() == persistedPage, "page contents and revision survive restart");
    Check(Snapshot(reloaded.Page(notebookId, Id(pdfPage))).GetProperty("background").GetProperty("image").GetString() == "data:image/png;base64," + png, "PDF background survives restart");
    Check(Directory.GetFiles(data, "*.tmp", SearchOption.AllDirectories).Length == 0, "successful and rejected writes leave no temporary files");

    for (var index = 0; index < 25; index++) workspace.SavePage(notebookId, meetingId, PageRevision(workspace, notebookId, meetingId), Document("Retained history " + index));
    Check(Snapshot(workspace.History(notebookId, meetingId)).GetProperty("versions").GetArrayLength() == 20, "page history is bounded to its twenty most recent recovery versions");
    var retained = Snapshot(workspace.History(notebookId, meetingId)).GetProperty("versions")[0].GetProperty("id").GetString()!;
    workspace.Restore(notebookId, meetingId, PageRevision(workspace, notebookId, meetingId), retained);
    Check(Notes(workspace.Page(notebookId, meetingId)) == "Retained history 23", "bounded history still restores the preceding document version");
    var historyPath = Path.Combine(data, "notebooks", "history", meetingId + ".json");
    var historyBytes = File.ReadAllBytes(historyPath);
    var pageBeforeCorruptHistory = PageRevision(workspace, notebookId, meetingId);
    File.WriteAllText(historyPath, "{ damaged history");
    try { workspace.History(notebookId, meetingId); throw new Exception("Damaged history was silently replaced"); } catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { Check(true, "damaged document history fails without silently dropping versions"); }
    Check(File.ReadAllText(historyPath) == "{ damaged history" && PageRevision(workspace, notebookId, meetingId) == pageBeforeCorruptHistory, "history corruption leaves its original bytes and current page intact");
    File.WriteAllBytes(historyPath, historyBytes);
    var assetPath = Directory.GetFiles(Path.Combine(data, "notebooks", "assets"), "*.png").Single();
    var assetBytes = File.ReadAllBytes(assetPath);
    File.WriteAllBytes(assetPath, assetBytes.Concat(new byte[] { 0 }).ToArray());
    Reject(() => workspace.Page(notebookId, Id(pdfPage)), "a changed PDF background cannot be returned as the original page asset");
    Check(File.ReadAllBytes(assetPath).Length == assetBytes.Length + 1, "failed PDF validation leaves the original evidence available for recovery");
    File.WriteAllBytes(assetPath, assetBytes);

    var quota = Workspace("quota", out _);
    for (var index = 1; index < 20; index++) Change(quota, "createNotebook", ("name", "Notebook " + index));
    Reject(() => quota.Change(Request("createNotebook", Revision(quota), ("name", "Notebook beyond limit"))), "active notebook count is bounded at twenty");
    Check(Notebooks(quota).Length == 20, "quota rejection preserves existing notebooks");
    var fullData = Fixture("store-full"); var full = new NotebookWorkspace(fullData, new InkWorkspace(fullData));
    var fullBefore = Snapshot(full.Catalog()).GetRawText();
    using (var large = new FileStream(Path.Combine(fullData, "notebooks", "quota-fixture.bin"), FileMode.CreateNew, FileAccess.Write)) large.SetLength(128L * 1024 * 1024);
    Reject(() => full.Change(Request("createNotebook", Revision(full), ("name", "Must not fit"))), "library storage quota blocks writes before committing more content");
    Check(Snapshot(full.Catalog()).GetRawText() == fullBefore, "storage quota rejection preserves the existing library");

    var exactQuotaData = Fixture("exact-quota"); var exactQuota = new NotebookWorkspace(exactQuotaData, new InkWorkspace(exactQuotaData));
    Change(exactQuota, "createNotebook", ("name", "QuotaAAAAA"));
    var exactQuotaId = Id(FindNotebook(exactQuota, "QuotaAAAAA"));
    var exactQuotaRoot = Path.Combine(exactQuotaData, "notebooks");
    var usedBytes = Directory.GetFiles(exactQuotaRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    using (var filler = new FileStream(Path.Combine(exactQuotaRoot, "quota-fixture.bin"), FileMode.CreateNew, FileAccess.Write)) filler.SetLength(128L * 1024 * 1024 - usedBytes);
    Change(exactQuota, "renameNotebook", ("notebookId", exactQuotaId), ("name", "QuotaBBBBB"));
    Check(Id(FindNotebook(exactQuota, "QuotaBBBBB")) == exactQuotaId, "same-size metadata rename remains possible at the exact storage quota");
    Check(Directory.GetFiles(exactQuotaRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) == 128L * 1024 * 1024, "quota-bound metadata rename adds no reserved document bytes");

    var purgeData = Fixture("purge-history"); var purge = new NotebookWorkspace(purgeData, new InkWorkspace(purgeData));
    Change(purge, "createNotebook", ("name", "History controls"));
    var purgeNotebook = FindNotebook(purge, "History controls"); var purgeNotebookId = Id(purgeNotebook); var purgePageId = Id(Pages(purgeNotebook).Single());
    var stalePurgePageRevision = PageRevision(purge, purgeNotebookId, purgePageId);
    purge.SavePage(purgeNotebookId, purgePageId, stalePurgePageRevision, Document("Kept current"));
    var purgeCurrent = Snapshot(purge.Page(purgeNotebookId, purgePageId)).GetRawText();
    Reject(() => purge.Change(Request("purgeHistory", Revision(purge), ("notebookId", purgeNotebookId), ("pageId", purgePageId), ("expectedPageRevision", PageRevision(purge, purgeNotebookId, purgePageId)), ("confirmed", false))), "history removal requires explicit confirmation");
    Conflict(() => purge.Change(Request("purgeHistory", Revision(purge), ("notebookId", purgeNotebookId), ("pageId", purgePageId), ("expectedPageRevision", stalePurgePageRevision), ("confirmed", true))), "stale page confirmation cannot purge newer recovery history");
    Check(Snapshot(purge.History(purgeNotebookId, purgePageId)).GetProperty("versions").GetArrayLength() > 0, "rejected history removal retains the existing versions");
    Change(purge, "purgeHistory", ("notebookId", purgeNotebookId), ("pageId", purgePageId), ("expectedPageRevision", PageRevision(purge, purgeNotebookId, purgePageId)), ("confirmed", true));
    Check(Snapshot(purge.History(purgeNotebookId, purgePageId)).GetProperty("versions").GetArrayLength() == 0, "confirmed history purge clears retained versions");
    Check(Snapshot(purge.Page(purgeNotebookId, purgePageId)).GetRawText() == purgeCurrent, "history purge preserves the current document and revision exactly");

    var observerData = Fixture("legacy-history-failure"); var observerLegacy = new InkWorkspace(observerData); var observer = new NotebookWorkspace(observerData, observerLegacy);
    observerLegacy.Save(observerLegacy.Snapshot().Revision, Document("Saved before observer failure"));
    var observerRevision = observerLegacy.Snapshot().Revision; var observerInkPath = Path.Combine(observerData, InkWorkspace.FileName); var observerBytes = File.ReadAllBytes(observerInkPath);
    var observerHistoryPath = Path.Combine(observerData, "notebooks", "history", observerLegacy.Snapshot().WorkspaceId + ".json"); var observerHistoryBackup = observerHistoryPath + ".saved";
    File.Move(observerHistoryPath, observerHistoryBackup); Directory.CreateDirectory(observerHistoryPath);
    try { observerLegacy.Save(observerRevision, Document("Must not replace existing ink")); throw new Exception("Expected history observer failure"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Check(true, "legacy history observer I/O failure surfaces before the ink commit"); }
    Check(observerLegacy.Snapshot().Revision == observerRevision && File.ReadAllBytes(observerInkPath).SequenceEqual(observerBytes), "failed legacy history capture preserves current scratchpad bytes and revision");
    Directory.Delete(observerHistoryPath); File.Move(observerHistoryBackup, observerHistoryPath);

    var guardedData = Fixture("checked-revision-lock"); var guarded = new InkWorkspace(guardedData);
    var guardedRevision = guarded.Snapshot().Revision;
    using (var callbackEntered = new ManualResetEventSlim())
    using (var releaseCallback = new ManualResetEventSlim())
    using (var writerAttempted = new ManualResetEventSlim()) {
        var guardedAction = Task.Run(() => guarded.WithCheckedRevision(guardedRevision, () => {
            callbackEntered.Set();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Checked-revision callback was not released.");
        }));
        Task<InkSnapshot>? waitingWriter = null;
        try {
            Check(callbackEntered.Wait(TimeSpan.FromSeconds(5)), "checked-revision callback enters while the current ink revision is locked");
            waitingWriter = Task.Run(() => { writerAttempted.Set(); return guarded.Save(guardedRevision, Document("Writer after protected callback")); });
            Check(writerAttempted.Wait(TimeSpan.FromSeconds(5)), "concurrent legacy writer starts while the checked callback is active");
            Check(!waitingWriter.Wait(TimeSpan.FromMilliseconds(150)), "legacy writer cannot commit between a revision check and its protected callback");
        } finally { releaseCallback.Set(); }
        Check(guardedAction.Wait(TimeSpan.FromSeconds(5)) && waitingWriter is not null && waitingWriter.Wait(TimeSpan.FromSeconds(5)), "checked callback and waiting legacy writer finish without deadlock after release");
    }
    Check(guarded.Snapshot().Document.GetProperty("notes").GetString() == "Writer after protected callback", "waiting legacy writer commits after the checked operation releases its revision lock");
    var staleCallbackRan = false;
    Conflict(() => guarded.WithCheckedRevision(guardedRevision, () => staleCallbackRan = true), "checked-revision action rejects a stale revision");
    Check(!staleCallbackRan, "stale checked-revision requests cannot execute their destructive callback");

    var raceData = Fixture("concurrent-storage-quota"); var raceLegacy = new InkWorkspace(raceData);
    raceLegacy.Save(raceLegacy.Snapshot().Revision, Document(new string('h', 7000)));
    var race = new NotebookWorkspace(raceData, raceLegacy);
    Change(race, "createNotebook", ("name", "Quota race"));
    var raceNotebookId = Id(FindNotebook(race, "Quota race")); var raceLegacyId = raceLegacy.Snapshot().WorkspaceId;
    var raceStore = Path.Combine(raceData, "notebooks");
    long RaceBytes() => Directory.GetFiles(raceStore, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    var raceBaseline = RaceBytes();
    raceLegacy.Save(raceLegacy.Snapshot().Revision, Document("Calibration saved"));
    var legacyHistoryGrowth = RaceBytes() - raceBaseline;
    raceLegacy.Save(raceLegacy.Snapshot().Revision, Document(new string('h', 7000)));
    Change(race, "purgeHistory", ("notebookId", raceLegacyId), ("pageId", raceLegacyId), ("expectedPageRevision", raceLegacy.Snapshot().Revision), ("confirmed", true));
    Check(RaceBytes() == raceBaseline, "quota-race history calibration restores the original notebook storage size");
    Change(race, "createPage", ("notebookId", raceNotebookId), ("name", "Race page"), ("background", Background("data:image/png;base64," + png)));
    var pageAndPngGrowth = RaceBytes() - raceBaseline;
    var calibrationPageId = Id(Pages(FindNotebook(race, "Quota race")).Single(value => value.GetProperty("name").GetString() == "Race page"));
    Change(race, "archivePage", ("notebookId", raceNotebookId), ("pageId", calibrationPageId));
    Change(race, "deletePage", ("notebookId", raceNotebookId), ("pageId", calibrationPageId), ("confirmed", true));
    Check(RaceBytes() == raceBaseline && legacyHistoryGrowth > 0 && pageAndPngGrowth > 0, "quota-race page calibration also restores its source and measures both independent write costs");
    var availableForRace = Math.Max(legacyHistoryGrowth, pageAndPngGrowth) + Math.Min(legacyHistoryGrowth, pageAndPngGrowth) / 2;
    Check(availableForRace >= legacyHistoryGrowth && availableForRace >= pageAndPngGrowth && availableForRace < legacyHistoryGrowth + pageAndPngGrowth, "quota-race free space fits either write independently but cannot fit both");
    using (var raceFiller = new FileStream(Path.Combine(raceStore, "quota-fixture.bin"), FileMode.CreateNew, FileAccess.Write)) raceFiller.SetLength(128L * 1024 * 1024 - RaceBytes() - availableForRace);
    var raceCatalogueRevision = Revision(race); var racePageRevision = raceLegacy.Snapshot().Revision;
    var raceSuccesses = 0; var raceCapacityRejections = 0;
    var raceErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    using (var beginRace = new ManualResetEventSlim()) {
        void Attempt(Action action) {
            beginRace.Wait();
            try { action(); Interlocked.Increment(ref raceSuccesses); }
            catch (ArgumentException error) when (error.Message.Contains("storage", StringComparison.OrdinalIgnoreCase)) { Interlocked.Increment(ref raceCapacityRejections); }
            catch (Exception error) { raceErrors.Enqueue(error); }
        }
        var historyWriter = Task.Run(() => Attempt(() => raceLegacy.Save(racePageRevision, Document("Concurrent legacy save"))));
        var pageWriter = Task.Run(() => Attempt(() => race.Change(Request("createPage", raceCatalogueRevision, ("notebookId", raceNotebookId), ("name", "Race page"), ("background", Background("data:image/png;base64," + png))))));
        beginRace.Set();
        Check(Task.WaitAll(new[] { historyWriter, pageWriter }, TimeSpan.FromSeconds(10)), "concurrent legacy history and page creation finish within their bounded timeout without deadlock");
    }
    Check(raceErrors.IsEmpty, "concurrent quota check fails only through its documented capacity rejection");
    Check(raceSuccesses == 1 && raceCapacityRejections == 1, "one competing write succeeds and the other is rejected at the shared notebook quota");
    Check(RaceBytes() <= 128L * 1024 * 1024, "concurrent legacy history and PNG page writes cannot exceed the 128 MiB notebook storage bound");

    var deletionData = Fixture("confirmed-deletion"); var deletionLegacy = new InkWorkspace(deletionData); var deletion = new NotebookWorkspace(deletionData, deletionLegacy);
    var deletionLegacyBytes = File.ReadAllBytes(Path.Combine(deletionData, InkWorkspace.FileName));
    Change(deletion, "createNotebook", ("name", "Disposable"), ("background", Background("data:image/png;base64," + png)), ("firstPageName", "PDF original"));
    var disposableNotebook = FindNotebook(deletion, "Disposable"); var disposableNotebookId = Id(disposableNotebook); var disposablePageId = Id(Pages(disposableNotebook).Single());
    Change(deletion, "createPage", ("notebookId", disposableNotebookId), ("name", "PDF kept"), ("background", Background("data:image/png;base64," + png)));
    var keptPageId = Id(Pages(FindNotebook(deletion, "Disposable")).Single(value => value.GetProperty("name").GetString() == "PDF kept"));
    var deduplicatedAssets = Path.Combine(deletionData, "notebooks", "assets");
    Check(Directory.GetFiles(deduplicatedAssets, "*.png").Length == 1, "identical PDF backgrounds share one bounded asset");
    Reject(() => deletion.Change(Request("deletePage", Revision(deletion), ("notebookId", disposableNotebookId), ("pageId", disposablePageId), ("confirmed", true))), "active pages cannot be permanently removed");
    Change(deletion, "archivePage", ("notebookId", disposableNotebookId), ("pageId", disposablePageId));
    Reject(() => deletion.Change(Request("deletePage", Revision(deletion), ("notebookId", disposableNotebookId), ("pageId", disposablePageId), ("confirmed", false))), "permanent page deletion requires explicit confirmation");
    Change(deletion, "deletePage", ("notebookId", disposableNotebookId), ("pageId", disposablePageId), ("confirmed", true));
    Check(!Pages(FindNotebook(deletion, "Disposable")).Any(value => Id(value) == disposablePageId), "confirmed archived-page deletion removes only its catalogue entry");
    Check(!File.Exists(Path.Combine(deletionData, "notebooks", "pages", disposablePageId, InkWorkspace.FileName)), "confirmed page deletion removes its private page file");
    Check(Snapshot(deletion.Page(disposableNotebookId, keptPageId)).GetProperty("background").GetProperty("image").GetString() == "data:image/png;base64," + png && Directory.GetFiles(deduplicatedAssets, "*.png").Length == 1, "deleting a page preserves the shared PNG used by another page");
    Reject(() => deletion.Change(Request("deleteNotebook", Revision(deletion), ("notebookId", disposableNotebookId), ("confirmed", true))), "active notebooks cannot be permanently removed");
    Change(deletion, "archiveNotebook", ("notebookId", disposableNotebookId));
    Reject(() => deletion.Change(Request("deleteNotebook", Revision(deletion), ("notebookId", disposableNotebookId), ("confirmed", false))), "permanent notebook deletion requires explicit confirmation");
    Change(deletion, "deleteNotebook", ("notebookId", disposableNotebookId), ("confirmed", true));
    Check(!Snapshot(deletion.Catalog()).GetProperty("archivedNotebooks").EnumerateArray().Any(value => Id(value) == disposableNotebookId), "confirmed archived-notebook deletion removes its retained entry");
    Check(Directory.GetFiles(deduplicatedAssets, "*.png").Length == 0, "unreferenced PDF assets are removed after the last owning notebook is deleted");
    var deletionOriginalId = Id(Notebooks(deletion).Single());
    Reject(() => deletion.Change(Request("deleteNotebook", Revision(deletion), ("notebookId", deletionOriginalId), ("confirmed", true))), "the original legacy-linked notebook cannot be deleted");
    Check(File.ReadAllBytes(Path.Combine(deletionData, InkWorkspace.FileName)).SequenceEqual(deletionLegacyBytes), "all permanent-removal operations preserve the original scratchpad bytes");

    var corruptData = Fixture("corrupt"); var corruptLegacy = new InkWorkspace(corruptData); var corrupt = new NotebookWorkspace(corruptData, corruptLegacy);
    Change(corrupt, "createNotebook", ("name", "Persisted")); var catalogPath = CataloguePath(corruptData);
    var legacyBeforeCorruption = File.ReadAllBytes(Path.Combine(corruptData, InkWorkspace.FileName));
    const string malformed = "{ damaged notebook catalog";
    File.WriteAllText(catalogPath, malformed);
    try { _ = new NotebookWorkspace(corruptData, corruptLegacy); throw new Exception("Corrupt catalogue was silently replaced"); } catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or JsonException) { Check(true, "damaged catalogue fails closed rather than silently replacing notebooks"); }
    Check(File.ReadAllText(catalogPath) == malformed && File.ReadAllBytes(Path.Combine(corruptData, InkWorkspace.FileName)).SequenceEqual(legacyBeforeCorruption), "corrupt catalogue handling preserves original bytes and legacy ink");

    var failureData = Fixture("atomic-failure"); var failure = new NotebookWorkspace(failureData, new InkWorkspace(failureData));
    Change(failure, "createNotebook", ("name", "Saved")); var failurePath = CataloguePath(failureData); var fileBackup = failurePath + ".saved";
    var failureBefore = Snapshot(failure.Catalog()).GetRawText();
    File.Move(failurePath, fileBackup); Directory.CreateDirectory(failurePath);
    try { failure.Change(Request("renameNotebook", Revision(failure), ("notebookId", Id(FindNotebook(failure, "Saved"))), ("name", "Not committed"))); throw new Exception("Expected failed atomic replacement"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Check(true, "failed atomic catalogue replacement surfaces an I/O failure"); }
    Check(Snapshot(failure.Catalog()).GetRawText() == failureBefore, "failed persistence leaves in-memory catalogue unchanged");
    Check(Directory.GetFiles(failureData, "*.tmp", SearchOption.AllDirectories).Length == 0, "failed persistence cleans only its temporary file");
    Directory.Delete(failurePath); File.Move(fileBackup, failurePath);
    Console.WriteLine(checks + " notebook backend checks passed.");
} finally {
    var absolute = Path.GetFullPath(root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    if (absolute.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("deskwing-notebook-tests-", StringComparison.Ordinal)) Directory.Delete(absolute, true);
}
