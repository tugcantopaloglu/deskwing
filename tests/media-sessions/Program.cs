using System.Text.Json;
using System.Diagnostics;
using TabletDeck;

if(args.Contains("--probe")){
    // Explicitly read-only diagnostic. Never calls Execute/Operate or starts an app.
    using var real=new MediaSessions();var first=await real.Snapshot();await Task.Delay(600);var snapshot=await real.Snapshot();
    Console.WriteLine(JsonSerializer.Serialize(new{sessions=snapshot.Sessions.Select(s=>new{s.Id,s.Source,s.Kind,s.Title,s.Artist,s.Album,artworkCharacters=s.Artwork.Length,s.PositionMs,s.DurationMs,s.Playing,s.CanPlay,s.CanPause,s.CanNext,s.CanPrevious,s.CanSeek,s.ContextToken}),snapshot.Error,snapshot.Warning,sameSessionIds=first.Sessions.Select(s=>s.Id).Order().SequenceEqual(snapshot.Sessions.Select(s=>s.Id).Order()),readOnly=true},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));return;
}
var passed=0;
void Check(bool value,string message){if(!value)throw new Exception("FAIL "+message);passed++;Console.WriteLine("PASS "+message);}
async Task Reject(Func<Task> action,string message){try{await action();throw new Exception("Accepted: "+message);}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){Check(true,message);}}
var spotify=new FakeSession("Spotify.exe","Song A");var browser=new FakeSession("chrome.exe","Video B");
var platform=new FakePlatform{Handles=[spotify,browser]};using var media=new MediaSessions(platform);
var state=await media.Snapshot();var song=state.Sessions.Single(s=>s.Kind=="spotify");var video=state.Sessions.Single(s=>s.Kind=="browser");
MediaSessionActionRequest Req(MediaSessionInfo view,string operation="toggle",double? position=null,string preset="spotify")=>new(preset,view.Id,view.ContextToken,operation,position);
Check(state.Sessions.Length==2&&spotify.Calls.Count==0&&browser.Calls.Count==0,"snapshot reads two players without changing either playback");
Check(song.Title=="Song A"&&video.Title=="Video B"&&song.Artist=="Artist"&&song.Album=="Album","snapshot exposes each selected player's actual metadata");
Check(song.DurationMs==180000&&song.PositionMs==30000,"timeline progress is relative to track start");
Check(song.Artwork.StartsWith("data:image/png;base64,")&&song.CanSeek,"bounded album artwork and capabilities are exposed");
Check(song.Id!=video.Id&&song.ContextToken!=video.ContextToken,"session and track identities do not collide between concurrent players");
var reads=platform.Reads;var repeated=await media.Snapshot();Check(platform.Reads==reads&&repeated.Sessions[0].Id==state.Sessions[0].Id,"500ms read cache keeps session identities stable");
repeated.Sessions[0]=video;Check((await media.Snapshot()).Sessions.Count(s=>s.Kind=="spotify")==1,"snapshot array copies cannot corrupt shared cache");
await media.Execute(Req(song));Check(spotify.Calls.SequenceEqual(new[]{("pause",0L)})&&browser.Calls.Count==0,"Spotify Pause reaches only Spotify while a browser player is also present");
await media.Execute(Req(video,preset:"youtube"));Check(browser.Calls.SequenceEqual(new[]{("pause",0L)})&&spotify.Calls.Count==1,"explicit browser selection controls only that browser session");
await Reject(()=>media.Execute(Req(video)),"Spotify preset cannot control a browser player");
await Reject(()=>media.Execute(Req(song,preset:"youtube")),"YouTube preset cannot control the Spotify player");
await Reject(()=>media.Execute(Req(song,preset:"desktop")),"other controller presets cannot enter targeted media API");
await Reject(()=>media.Execute(Req(song) with{SessionId=Guid.NewGuid().ToString("N")}),"unknown session ID is rejected without fallback");
await Reject(()=>media.Execute(Req(song) with{SessionId="Spotify.exe"}),"source name cannot substitute for opaque selected session ID");
await Reject(()=>media.Execute(Req(song) with{ContextToken="invalid"}),"malformed track token is rejected");
await Reject(()=>media.Execute(Req(song) with{Operation="volume"}),"unsupported media operation is rejected");
await Reject(()=>media.Execute(Req(song,position:1000)),"position is accepted only for a Seek request");
await Reject(()=>media.Execute(Req(song,"seek",double.NaN)),"non-finite seek position is rejected");
await Reject(()=>media.Execute(Req(song,"seek",-1)),"negative seek position is rejected");
await Reject(()=>media.Execute(Req(song,"seek",180001)),"seek outside current track duration is rejected");
await media.Execute(Req(song,"seek",60000));Check(spotify.Calls[^1]==("seek",650000000L)&&browser.Calls.Count==1,"seek converts selected track-relative milliseconds to absolute Windows timeline ticks");
spotify.Data=spotify.Data with{CanSeek=false};await Reject(()=>media.Execute(Req(song,"seek",1000)),"seek capability is checked fresh before dispatch");spotify.Data=spotify.Data with{CanSeek=true,CanPause=false};
await Reject(()=>media.Execute(Req(song)),"Pause capability is checked fresh before dispatch");spotify.Data=spotify.Data with{CanPause=true,Playing=false};
await media.Execute(Req(song));Check(spotify.Calls[^1].Item1=="play","toggle resolves to selected session's explicit Play when paused");
spotify.Data=spotify.Data with{CanNext=false};await Reject(()=>media.Execute(Req(song,"next")),"unsupported Next is rejected");spotify.Data=spotify.Data with{CanNext=true};
await media.Execute(Req(song,"next"));await Reject(()=>media.Execute(Req(song,"seek",1000)),"accepted Next immediately invalidates old track controls before metadata arrives");
song=(await media.Snapshot()).Sessions.Single(s=>s.Kind=="spotify");
spotify.Data=spotify.Data with{Title="Song C",TrackRevision=spotify.Data.TrackRevision+1};await Reject(()=>media.Execute(Req(song,"seek",1000)),"track change rejects stale seek token even before another snapshot");
song=(await media.Snapshot()).Sessions.Single(s=>s.Kind=="spotify");
Check(song.Title=="Song C","stale-track rejection invalidates read cache so immediate refresh returns the new track");
spotify.Data=spotify.Data with{TrackRevision=spotify.Data.TrackRevision+1};await Reject(()=>media.Execute(Req(song,"seek",1000)),"new media-properties revision rejects stale token even with identical song text");
platform.Handles=[browser,new FakeSession("Spotify.exe","Song C")];await Reject(()=>media.Execute(Req(song)),"replaced Spotify session with same source cannot receive old session's request");
platform.Handles=[browser];await Reject(()=>media.Execute(Req(song)),"disconnected selected player never falls back to the remaining browser player");
Check(browser.Calls.Count==1,"all rejected Spotify actions leave browser playback untouched");
Check(MediaSessions.Kind("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")=="spotify"&&MediaSessions.Kind("notSpotify.exe")=="other"&&MediaSessions.Kind("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Fake")=="other","Spotify source matching accepts exact identities and rejects lookalikes");
Check(MediaSessions.Kind("msedge.exe")=="browser"&&MediaSessions.Kind("Chrome")=="browser"&&MediaSessions.Kind("unknownplayer.exe")=="other","browser classification supports observed Chrome AUMID without guessing its website");
Check(MediaSessions.Artwork(new byte[MediaSessions.MaxArtworkBytes+1])==""&&MediaSessions.Artwork("<svg onload='alert(1)'/>"u8.ToArray())=="","artwork drops oversized payloads and active-content image formats");
var oversized=spotify.Data.Artwork!.ToArray();oversized[16]=1;Check(MediaSessions.Artwork(oversized)=="","artwork rejects oversized image dimensions");
var fake=new FakeSession("Spotify.exe","Limited");fake.Data=fake.Data with{MinSeekTicks=200000000,MaxSeekTicks=800000000};
using(var limited=new MediaSessions(new FakePlatform{Handles=[fake]})){var view=(await limited.Snapshot()).Sessions[0];await Reject(()=>limited.Execute(Req(view,"seek",1000)),"player's narrower seekable range is respected");}
var slow=new FakePlatform{Handles=[new FakeSession("Spotify.exe","Slow")],Barrier=new(TaskCreationOptions.RunContinuationsAsynchronously)};
using(var sharing=new MediaSessions(slow)){var one=sharing.Snapshot();var two=sharing.Snapshot();await Task.Delay(30);Check(slow.Reads==1,"concurrent snapshots share one in-flight platform read");slow.Barrier.SetResult();await Task.WhenAll(one,two);}
var waiting=new FakeSession("Spotify.exe","Waiting"){Barrier=new(TaskCreationOptions.RunContinuationsAsynchronously)};
using(var busy=new MediaSessions(new FakePlatform{Handles=[waiting]})){var view=(await busy.Snapshot()).Sessions[0];var first=busy.Execute(Req(view));await waiting.Entered.Task;await Reject(()=>busy.Execute(Req(view)),"overlapping writes are rejected without queueing another playback command");waiting.Barrier.SetResult();await first;}
var declining=new FakeSession("Spotify.exe","Decline"){Accepted=false};using(var refused=new MediaSessions(new FakePlatform{Handles=[declining,browser]})){var view=(await refused.Snapshot()).Sessions[0];await Reject(()=>refused.Execute(Req(view)),"player refusal is reported without trying another session");}
using(var unavailable=new MediaSessions(new FakePlatform{Failure=true})){var empty=await unavailable.Snapshot();Check(empty.Sessions.Length==0&&empty.Error is not null&&empty.Warning is null,"platform failure returns an honest empty/error state without a mock/current player");}
var validSpotify=new FakeSession("Spotify.exe","Usable song");var validBrowser=new FakeSession("Chrome","Usable video");var brokenPlayer=new FakeSession("unrelated.exe","Unreadable"){ReadFailure=true};
using(var partial=new MediaSessions(new FakePlatform{Handles=[validSpotify,brokenPlayer,validBrowser]})){
    var usable=await partial.Snapshot();Check(usable.Sessions.Length==2&&usable.Error is null&&usable.Warning is not null,"unrelated metadata failure preserves two valid players with Warning only");
    var payload=JsonSerializer.SerializeToElement(usable,new JsonSerializerOptions(JsonSerializerDefaults.Web));Check(payload.GetProperty("error").ValueKind==JsonValueKind.Null&&payload.GetProperty("warning").GetString() is not null,"partial success serializes no truthy error for the native bridge");
    await partial.Execute(Req(usable.Sessions.Single(s=>s.Kind=="spotify")));Check(validSpotify.Calls.Count==1&&validBrowser.Calls.Count==0&&brokenPlayer.Calls.Count==0,"valid Spotify player remains usable after unrelated metadata failure without fallback");
}
using(var unreadable=new MediaSessions(new FakePlatform{Handles=[brokenPlayer]})){var empty=await unreadable.Snapshot();Check(empty.Sessions.Length==0&&empty.Error is not null&&empty.Warning is null,"complete metadata failure remains an error for the native bridge");}
using(var noPlayers=new MediaSessions(new FakePlatform())){var empty=await noPlayers.Snapshot();Check(empty.Sessions.Length==0&&empty.Error is null&&empty.Warning is null,"ordinary empty session enumeration has neither an error nor a warning");}
var brokenIdentity=new FakeSession("unrelated.exe","Broken source"){SourceFailure=true};
using(var sourceFailure=new MediaSessions(new FakePlatform{Handles=[brokenIdentity,new FakeSession("Spotify.exe","Readable")]})){var readable=await sourceFailure.Snapshot();Check(readable.Sessions.Length==1&&readable.Sessions[0].Title=="Readable"&&readable.Warning is not null,"a failing source getter cannot hide another player's metadata");}
var hanging=new FakeSession("unrelated.exe","Never ready"){ReadBarrier=new(TaskCreationOptions.RunContinuationsAsynchronously),IgnoreReadCancellation=true};
var isolatedSpotify=new FakeSession("Spotify.exe","Still usable");var isolatedBrowser=new FakeSession("Chrome","Still visible");
using(var independent=new MediaSessions(new FakePlatform{Handles=[hanging,isolatedSpotify,isolatedBrowser]},TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(800))){
    var started=Stopwatch.StartNew();var usable=await independent.Snapshot();
    Check(started.Elapsed<TimeSpan.FromMilliseconds(700)&&usable.Sessions.Length==2&&usable.Warning is not null,"a cancellation-ignoring first player cannot consume healthy players' independent read budgets");
    var healthyIds=usable.Sessions.Select(s=>s.Id).ToArray();await Task.Delay(550);var again=await independent.Snapshot();
    Check(hanging.ReadCount==1&&again.Sessions.Select(s=>s.Id).SequenceEqual(healthyIds),"refresh reuses an outstanding timed-out provider read and retains healthy session IDs");
    await independent.Execute(Req(again.Sessions.Single(s=>s.Kind=="spotify")));
    Check(isolatedSpotify.Calls.Count==1&&isolatedBrowser.Calls.Count==0&&hanging.Calls.Count==0,"a healthy selected player remains actionable despite a non-cooperative peer");
    hanging.ReadBarrier.SetResult();
}
var intermittent=new FakeSession("Spotify.exe","Before interruption");var recoveryPlatform=new FakePlatform{Handles=[intermittent]};
using(var recovering=new MediaSessions(recoveryPlatform,TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(800))){
    var original=(await recovering.Snapshot()).Sessions[0];intermittent.ReadBarrier=new(TaskCreationOptions.RunContinuationsAsynchronously);intermittent.IgnoreReadCancellation=true;
    await Task.Delay(550);var missing=(await recovering.Snapshot()).Sessions.Single();
    Check(missing.Id==original.Id&&!missing.Available&&!missing.CanPlay&&!missing.CanPause&&!missing.CanNext&&!missing.CanPrevious&&!missing.CanSeek&&missing.ContextToken!=original.ContextToken,"a temporarily unreadable known player keeps its ID while all controls and its old token are invalidated");
    await Reject(()=>recovering.Execute(Req(original)),"old controls cannot act on a temporarily unreadable player");
    intermittent.Data=intermittent.Data with{Title="After interruption",TrackRevision=1};intermittent.ReadBarrier.SetResult();await Task.Delay(30);
    Check(!(await recovering.Snapshot()).Sessions[0].Available,"late timed-out metadata cannot repopulate the unavailable snapshot cache");
    intermittent.ReadBarrier=null;await Task.Delay(550);var restored=(await recovering.Snapshot()).Sessions.Single();
    Check(restored.Id==original.Id&&restored.Available&&restored.Title=="After interruption"&&restored.ContextToken!=original.ContextToken&&restored.ContextToken!=missing.ContextToken,"fresh metadata restores the same session identity with new authority and current title");
    await Reject(()=>recovering.Execute(Req(original)),"pre-interruption context token stays rejected after recovery");
    recoveryPlatform.Failure=true;await Task.Delay(550);Check((await recovering.Snapshot()).Error is not null,"temporary enumeration failure exposes an error");
    recoveryPlatform.Failure=false;await Task.Delay(550);var enumerated=(await recovering.Snapshot()).Sessions.Single();
    Check(enumerated.Id==original.Id&&enumerated.ContextToken!=restored.ContextToken,"enumeration recovery retains identity but invalidates old playback authority");
    recoveryPlatform.Handles=[];await Task.Delay(550);await recovering.Snapshot();recoveryPlatform.Handles=[intermittent];await Task.Delay(550);
    Check((await recovering.Snapshot()).Sessions.Single().Id!=original.Id,"a confirmed disconnection retires its ID even if the source object later returns");
}
var sharedPlatform=new FakePlatform{Handles=[new FakeSession("Spotify.exe","Shared read")],Barrier=new(TaskCreationOptions.RunContinuationsAsynchronously)};
using(var sharedCancellation=new MediaSessions(sharedPlatform)){
    using var caller=new CancellationTokenSource();var cancelled=sharedCancellation.Snapshot(caller.Token);var survivor=sharedCancellation.Snapshot();caller.Cancel();
    try{await cancelled;throw new Exception("Cancelled waiter was accepted");}catch(OperationCanceledException){Check(true,"a snapshot caller can cancel its own wait");}
    sharedPlatform.Barrier.SetResult();Check((await survivor).Sessions.Length==1&&sharedPlatform.Reads==1,"cancelling one waiter does not cancel shared metadata work for another");
}
var stalledCommand=new FakeSession("Spotify.exe","Slow command metadata");
using(var guardedCommands=new MediaSessions(new FakePlatform{Handles=[stalledCommand]},TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(800))){
    var selected=(await guardedCommands.Snapshot()).Sessions.Single();stalledCommand.CommandReadBarrier=new(TaskCreationOptions.RunContinuationsAsynchronously);stalledCommand.IgnoreReadCancellation=true;
    await Reject(()=>guardedCommands.Execute(Req(selected)),"a cancellation-ignoring command metadata read has a bounded caller-facing deadline");
    selected=(await guardedCommands.Snapshot()).Sessions.Single();await Reject(()=>guardedCommands.Execute(Req(selected)),"refresh cannot start duplicate command metadata reads while the timed-out provider is still running");
    Check(stalledCommand.CommandReadCount==1&&stalledCommand.Calls.Count==0,"repeated requests do not accumulate abandoned command reads or dispatch stale controls");stalledCommand.CommandReadBarrier.SetResult();
}
var disposalPlayer=new FakeSession("Spotify.exe","Before dispose");var disposalMedia=new MediaSessions(new FakePlatform{Handles=[disposalPlayer]});var disposalView=(await disposalMedia.Snapshot()).Sessions.Single();
disposalPlayer.ReadBarrier=new(TaskCreationOptions.RunContinuationsAsynchronously);disposalPlayer.IgnoreReadCancellation=true;disposalPlayer.ReadEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingControl=disposalMedia.Execute(Req(disposalView));await disposalPlayer.ReadEntered.Task;disposalMedia.Dispose();disposalPlayer.ReadBarrier.SetResult();
try{await pendingControl;throw new Exception("Disposed control was accepted");}catch(Exception ex) when(ex is OperationCanceledException or ObjectDisposedException){Check(disposalPlayer.Calls.Count==0,"disposing during a metadata read prevents subsequent playback dispatch");}
try{await disposalMedia.Snapshot();throw new Exception("Disposed snapshot was accepted");}catch(ObjectDisposedException){Check(true,"disposed media service cannot recreate a snapshot");}
var disposalPlatform=new FakePlatform{Handles=[new FakeSession("Spotify.exe","Late enumeration")],Barrier=new(TaskCreationOptions.RunContinuationsAsynchronously)};var disposedRead=new MediaSessions(disposalPlatform);var pendingSnapshot=disposedRead.Snapshot();await Task.Delay(20);disposedRead.Dispose();disposalPlatform.Barrier.SetResult();
try{await pendingSnapshot;throw new Exception("Disposed in-flight snapshot was accepted");}catch(ObjectDisposedException){Check(true,"disposing an in-flight snapshot cannot repopulate cleared state");}
var now=new DateTimeOffset(2026,11,2,12,0,0,TimeSpan.Zero);long T(double seconds)=>TimeSpan.FromSeconds(seconds).Ticks;
long Estimate(bool playing=true,double? rate=1,DateTimeOffset? updated=null,long? start=null,long? end=null,long? position=null)=>WindowsMediaSession.EstimatePosition(position??T(35),start??T(5),end??T(185),playing,updated??now.AddSeconds(-5),rate,now);
Check(Estimate()==T(40),"playing progress advances by elapsed UTC time from reported timeline timestamp");
Check(Estimate(rate:1.5)==T(42.5),"estimated progress honors the player's playback rate");
Check(Estimate(playing:false)==T(35),"paused progress remains exactly the reported position");
Check(Estimate(end:T(5))==T(35),"unknown live duration is not extrapolated");
Check(Estimate(updated:now.AddSeconds(1))==T(35),"future timeline timestamp is not extrapolated");
Check(Estimate(updated:DateTimeOffset.MinValue)==T(35),"unset timeline timestamp is not extrapolated");
Check(Estimate(rate:null)==T(35)&&Estimate(rate:0)==T(35),"missing or stopped playback rate leaves raw position unchanged");
Check(Estimate(rate:double.NaN)==T(35)&&Estimate(rate:double.PositiveInfinity)==T(35),"non-finite playback rate leaves raw position unchanged");
Check(Estimate(position:T(184))==T(185),"estimated forward progress clamps at track end");
Check(Estimate(position:T(6),rate:-1)==T(5),"reverse playback estimate clamps at track start");
Check(Estimate(rate:double.MaxValue)==T(35),"overflowing rate estimate falls back to reported position");
Check(Estimate(updated:now)==T(35),"just-updated timeline has no artificial position jump");
Check(WindowsMediaSession.EstimatePosition(T(35),T(5),T(185),true,now.AddSeconds(-5).ToOffset(TimeSpan.FromHours(3)),1,now)==T(40),"timeline timestamps with equivalent timezone offsets use the same UTC elapsed time");
Console.WriteLine($"{passed} isolated media-session checks passed; no Windows playback was changed.");

namespace TabletDeck {
internal sealed class FakePlatform : IMediaSessionPlatform {
    public IMediaSessionHandle[] Handles=[];public int Reads;public TaskCompletionSource? Barrier;public bool Failure;
    public async Task<IReadOnlyList<IMediaSessionHandle>> Sessions(CancellationToken ct){Interlocked.Increment(ref Reads);if(Failure)throw new InvalidOperationException("Fixture failure");if(Barrier is not null)await Barrier.Task.WaitAsync(ct);return Handles;}
    public void Dispose(){}
}
internal sealed class FakeSession : IMediaSessionHandle {
    public object Identity{get;}=new();private readonly string source;public string Source=>SourceFailure?throw new InvalidOperationException("Fixture source failure"):source;public MediaSessionData Data;public List<(string,long)> Calls=[];public bool Accepted=true,ReadFailure,IgnoreReadCancellation,SourceFailure;public int ReadCount,CommandReadCount;public TaskCompletionSource? Barrier,ReadBarrier,CommandReadBarrier;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReadEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeSession(string source,string title){this.source=source;Data=new(title,"Artist","Album",0,50000000,1850000000,350000000,50000000,1850000000,true,true,true,true,true,true,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2V8AAAAASUVORK5CYII="));}
    public async Task<MediaSessionData> Read(bool artwork,CancellationToken ct){Interlocked.Increment(ref ReadCount);if(!artwork)Interlocked.Increment(ref CommandReadCount);ReadEntered.TrySetResult();if(ReadFailure)throw new InvalidOperationException("Fixture metadata failure");var data=Data;var barrier=artwork?ReadBarrier:CommandReadBarrier??ReadBarrier;if(barrier is not null)await barrier.Task.WaitAsync(IgnoreReadCancellation?CancellationToken.None:ct);return data;}
    public async Task<bool> Operate(string operation,long absoluteTicks,CancellationToken ct){Calls.Add((operation,absoluteTicks));Entered.TrySetResult();if(Barrier is not null)await Barrier.Task.WaitAsync(ct);return Accepted;}
}
}
