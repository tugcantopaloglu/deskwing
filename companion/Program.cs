using TabletDeck;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

var flagOptions=new HashSet<string>(StringComparer.Ordinal){"--self-test","--ink-self-test","--pen-self-test","--pen-integration-self-test","--reliability-self-test","--aux-recovery-self-test","--boards-self-test","--studio-self-test","--monitor-self-test","--extended-display-self-test","--live-soundpad-self-test","--update-self-test","--desktop-window-self-test","--audio-smoke-test","--hardening-self-test","--no-browser","--no-tray","--tray","--after-update"};
var valueOptions=new HashSet<string>(StringComparer.Ordinal){"--data-dir","--http-port","--https-port","--control-port"};
for(var i=0;i<args.Length;i++){
    if(flagOptions.Contains(args[i]))continue;
    if(valueOptions.Contains(args[i])&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal)){i++;continue;}
    Console.Error.WriteLine("Unknown or incomplete command-line option. No companion or control panel was started.");Environment.ExitCode=2;return;
}
if(args.Contains("--self-test")) { SelfTests.Run();return; }
if(args.Contains("--ink-self-test")) {InkWorkspaceTests.Run();return;}
if(args.Contains("--pen-self-test")) {WindowsPenTests.Run();return;}
if(args.Contains("--pen-integration-self-test")) {await PenIntegrationTests.Run();return;}
if(args.Contains("--reliability-self-test")) {await ReliabilityTests.Run();return;}
if(args.Contains("--aux-recovery-self-test")) {AuxRecoveryTests.Run();return;}
if(args.Contains("--boards-self-test")) {BoardStorageTests.Run();return;}
if(args.Contains("--studio-self-test")) {await StudioTests.Run();return;}
if(args.Contains("--monitor-self-test")) {await MonitorTests.Run();return;}
if(args.Contains("--extended-display-self-test")) {await ExtendedDisplayTests.Run();return;}
if(args.Contains("--live-soundpad-self-test")) {await LiveSoundpadTests.Run();return;}
if(args.Contains("--update-self-test")){await UpdateTests.Run();return;}
if(args.Contains("--desktop-window-self-test")){var testDir=Array.IndexOf(args,"--data-dir");if(testDir<0)throw new ArgumentException("An isolated test data directory is required.");await DesktopWindowTests.Run(args[testDir+1]);return;}
if(args.Contains("--audio-smoke-test")) {await SelfTests.AudioSmokeTest();return;}
if(args.Contains("--hardening-self-test")) {await HardeningTests.Run();return;}
string Option(string name,string fallback) {var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
var dataDir=Option("--data-dir",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabletDeck"));
var store=new DataStore(dataDir);
var httpPort=int.Parse(Option("--http-port","8740"),CultureInfo.InvariantCulture);
var httpsPort=int.Parse(Option("--https-port","8743"),CultureInfo.InvariantCulture);
var controlPort=int.Parse(Option("--control-port",(httpsPort+1).ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
var certPath=Path.Combine(store.DirectoryPath,"companion.pfx");
X509Certificate2 cert;
if(File.Exists(certPath)) cert=X509CertificateLoader.LoadPkcs12FromFile(certPath,null,X509KeyStorageFlags.UserKeySet);
else {
    using var rsa=RSA.Create(3072);
    var request=new CertificateRequest("CN=Tablet Deck local companion",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
    var usages=new System.Security.Cryptography.OidCollection {new Oid("1.3.6.1.5.5.7.3.1")};request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages,true));
    using var made=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
    File.WriteAllBytes(certPath,made.Export(X509ContentType.Pfx));
    cert=X509CertificateLoader.LoadPkcs12FromFile(certPath,null,X509KeyStorageFlags.UserKeySet);
}
var fingerprint=Convert.ToHexString(SHA256.HashData(cert.RawData));
var adminToken=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var pairing=new Pairing();
using var singleton=AcquireLock(Path.Combine(store.DirectoryPath,"runtime.lock"));
if(singleton is null) {
    if(!args.Contains("--no-browser"))try{using var current=JsonDocument.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath,"admin-session.json")));var port=current.RootElement.GetProperty("httpPort").GetInt32();var token=current.RootElement.GetProperty("token").GetString()??"";if(port is <1 or >65535||token.Length!=64||!token.All(Uri.IsHexDigit))throw new ArgumentException("Invalid local console session.");using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};client.DefaultRequestHeaders.Add("X-Deck-Admin",token);using var response=await client.PostAsync($"http://127.0.0.1:{port}/api/admin/window",new StringContent("{}",System.Text.Encoding.UTF8,"application/json"));response.EnsureSuccessStatusCode();}catch(Exception ex) when(ex is not OutOfMemoryException){Console.Error.WriteLine("The companion is running, but its desktop window could not be opened. Stop the headless companion and start it normally to enable the tray and window.");}
    else Console.Error.WriteLine("Tablet Deck is already running. No second service was started.");
    return;
}
var builder=WebApplication.CreateBuilder(new WebApplicationOptions {Args=[],ContentRootPath=AppContext.BaseDirectory,WebRootPath=Path.Combine(AppContext.BaseDirectory,"wwwroot")});
builder.Logging.ClearProviders();builder.Logging.AddSimpleConsole(o=>o.SingleLine=true);builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k=>{k.Limits.MaxRequestBodySize=1024*1024;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);k.Listen(IPAddress.Loopback,httpPort);k.Listen(IPAddress.Any,httpsPort,l=>l.UseHttps(cert));});
var app=builder.Build();
await using var obs=new ObsClient(()=>store.Config);
await using var previewObs=new ObsClient(()=>store.Config);
var livePreview=new LivePreview((type,data,ct)=>previewObs.Call(type,data,ct));
var engine=new DeckEngine(store,obs);
using var appMixer=new AppAudioMixer();
using var holds=new HoldKeys();
var fastCommands=new FastCommands(store,engine,holds,appMixer);
using var windowsPen=new WindowsPen();fastCommands.ConfigurePen(windowsPen);
var inkWorkspace=new InkWorkspace(store.DirectoryPath);
var computer=new ComputerCopilot(store.DirectoryPath,fastCommands.ExecuteConfigured);
using var mediaSessions=new MediaSessions();
using var extendedDisplay=new ExtendedDisplay();
await using var stateUpdates=new StateUpdates(engine);
await using var fastControl=new FastControlServer(store,fastCommands,stateUpdates,cert,controlPort);
var desktop=new DesktopPreferences(store.DirectoryPath);
using var usb=new UsbReconnect(desktop,httpsPort,controlPort);
using var updates=new ReleaseUpdates();
using var selfUpdate=new SelfUpdate(store.DirectoryPath,updates);
using var usbApkUpdate=new UsbApkUpdate(store.DirectoryPath,desktop,updates,()=>fastCommands.CanUpdateTablet);
using var autoProfiles=new AutoProfiles(store.DirectoryPath,id=>store.ProfileList().Any(p=>p.Id==id),fastCommands.AutoProfile);
var boardPacks=new BoardPacks(store);
using var backgroundStop=new CancellationTokenSource();
DesktopTray? tray=null;
bool IsAdmin(HttpContext c)=>c.Connection.LocalPort==httpPort&&IPAddress.IsLoopback(c.Connection.RemoteIpAddress??IPAddress.None)&&SafeEqual(c.Request.Headers["X-Deck-Admin"].ToString(),adminToken);
bool IsAuthorized(HttpContext c)=>IsAdmin(c)||store.Authenticate(c.Request.Headers.Authorization.ToString().Replace("Bearer ","",StringComparison.Ordinal));
string Principal(HttpContext c)=>IsAdmin(c)?"admin":Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Request.Headers.Authorization.ToString().Replace("Bearer ","",StringComparison.Ordinal))));
app.Use(async (ctx,next)=>{
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    ctx.Response.Headers["X-Frame-Options"]="DENY";
    ctx.Response.Headers["Referrer-Policy"]="no-referrer";
    ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-src https://www.twitch.tv https://kick.com; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    ctx.Response.Headers.CacheControl="no-store";
    if(ctx.Request.Path.StartsWithSegments("/api")) {
        if(ctx.Connection.LocalPort==httpPort&&ctx.Request.Host.Value!=$"127.0.0.1:{httpPort}"&&ctx.Request.Host.Value!=$"localhost:{httpPort}") {ctx.Response.StatusCode=403;return;}
        var origin=ctx.Request.Headers.Origin.ToString();
        if(origin.Length>0&&origin!=$"http://127.0.0.1:{httpPort}"&&origin!=$"http://localhost:{httpPort}") {ctx.Response.StatusCode=403;return;}
        if(ctx.Request.Headers["Sec-Fetch-Site"]=="cross-site") {ctx.Response.StatusCode=403;return;}
        if(ctx.Request.Path.StartsWithSegments("/api/admin")&&!IsAdmin(ctx)) {ctx.Response.StatusCode=403;return;}
        if(ctx.Request.Path!="/api/pair"&&!IsAuthorized(ctx)) {ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="Pair this tablet with the companion first."});return;}
        if(ctx.Request.Path=="/api/pen"&&ctx.Request.Method=="POST"){
            if(ctx.Request.ContentLength>FastCommands.PenRequestBytes){ctx.Response.StatusCode=413;await ctx.Response.WriteAsJsonAsync(new{error="Keep pen packets below 16 KB."});return;}
            var size=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();if(size is not null&&!size.IsReadOnly)size.MaxRequestBodySize=FastCommands.PenRequestBytes;
        }
        if(fastCommands.Maintenance&&ctx.Request.Method!="GET"&&ctx.Request.Path!="/api/hold"&&ctx.Request.Path!="/api/cancel"&&ctx.Request.Path!="/api/pen"){ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="The companion is restarting for an update."});return;}
        try {await next(ctx);} catch(Exception ex) when(ex is not OutOfMemoryException) {
            ctx.Response.StatusCode=ex is BadHttpRequestException bad?bad.StatusCode:ex is JsonException or ArgumentException ? 400 : 409;
            await ctx.Response.WriteAsJsonAsync(new {error=ex is OperationCanceledException?"The action timed out. Check OBS and try again.":ex.Message});
        }
    } else {
        if(ctx.Connection.LocalPort!=httpPort) {ctx.Response.StatusCode=404;return;}
        if((ctx.Request.Path=="/"||ctx.Request.Path=="/index.html")&&ctx.Request.Query["preview"]!="1"){ctx.Response.Redirect("/desktop.html");return;}
        await next(ctx);
    }
});
app.UseDefaultFiles();app.UseStaticFiles();
app.MapInk(inkWorkspace);
app.MapGet("/api/pen",()=>windowsPen.Status);
app.MapPost("/api/pen",(PenRequest req,HttpContext ctx)=>fastCommands.Pen(req,Principal(ctx)));
app.MapGet("/api/admin/pen",()=>windowsPen.AdminState());
app.MapPost("/api/admin/pen",(PenSettings req)=>fastCommands.ConfigurePenSettings(req));
app.MapStudio(store,engine,fastCommands,autoProfiles,boardPacks,Principal,()=>pairing.Open(),NetworkAddresses,fingerprint,httpsPort,controlPort,obs);
app.MapPost("/api/monitor/frame",(PreviewRequest req,CancellationToken ct)=>livePreview.Frame(req,ct));
app.MapGet("/api/computer",()=>computer.Snapshot());
app.MapPost("/api/computer/preset",(CopilotPresetRequest req)=>{computer.SelectPreset(req.PresetId);return Results.Ok(new{ok=true});});
app.MapPost("/api/computer/follow",(CopilotFollowRequest req)=>{computer.Follow(req.Automatic);return Results.Ok(new{ok=true});});
app.MapPost("/api/computer/action",(CopilotActionRequest req)=>computer.Execute(req));
app.MapGet("/api/computer/media",async(CancellationToken ct)=>computer.Settings.Enabled?await mediaSessions.Snapshot(ct):new MediaSessionsSnapshot([],"Enable Computer Copilot in the PC settings first."));
app.MapPost("/api/computer/media/action",async(MediaSessionActionRequest req)=>{
    await fastCommands.ExecuteExternal("Control selected player",async ct=>{
        var current=computer.Snapshot();
        if(!current.Settings.Enabled||current.ActivePresetId!=req.PresetId||req.PresetId is not ("spotify" or "youtube"))throw new InvalidOperationException("Select the matching player layout on the tablet before controlling it.");
        await mediaSessions.Execute(req,ct);
    });
    return Results.Ok(new{ok=true});
});
app.MapGet("/api/admin/computer",()=>computer.Settings);
app.MapPost("/api/admin/computer",(CopilotSettings settings)=>{computer.Save(settings);return Results.Ok(new{ok=true});});
app.MapGet("/api/computer/display",()=>extendedDisplay.Status());
app.MapPost("/api/admin/computer/display",async (JsonElement req,CancellationToken ct)=>{
    if(req.ValueKind!=JsonValueKind.Object||!req.TryGetProperty("operation",out var choice)||choice.ValueKind!=JsonValueKind.String)throw new ArgumentException("Choose create, remove, or share.");
    var operation=choice.GetString();
    if(operation=="create")return await extendedDisplay.StartAsync(req.Deserialize<ExtendedDisplayStart>(DataStore.Json)??throw new ArgumentException("Choose display settings."),ct);
    if(operation=="remove"){extendedDisplay.Stop();return extendedDisplay.Status();}
    if(operation=="share"){if(!req.TryGetProperty("enabled",out var enabled)||enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Choose whether to share this display.");extendedDisplay.SetSharing(enabled.GetBoolean());return extendedDisplay.Status();}
    throw new ArgumentException("Choose create, remove, or share.");
});
app.MapPost("/api/computer/display/frame",(ExtendedDisplayFrameRequest req,CancellationToken ct)=>extendedDisplay.FrameAsync(req,ct));
app.MapGet("/api/board",async(CancellationToken ct)=>{
    var config=store.Config;var live=config.Pages.Any(p=>p.LiveScenes)?await engine.State():null;
    SoundpadLibraryResult? library=null;string? soundpadWarning=null;
    if(config.Pages.Any(p=>p.LiveSoundpad))try{library=await SoundpadLibrary.Get(false,ct);soundpadWarning=library.Warning;}catch(Exception ex) when(ex is IOException or InvalidOperationException){soundpadWarning=ex.Message;}
    var sounds=library?.Sounds.ToDictionary(s=>s.Index)??[];
    var identities=library?.Sounds.GroupBy(s=>s.Key).ToDictionary(g=>g.Key,g=>g.Count())??[];
    return new {profileId=config.ProfileId,profileName=config.ProfileName,sceneCatalogRevision=live?["sceneCatalogRevision"]?.GetValue<string>(),pages=config.Pages.Select(p=>{
        var actions=p.LiveSoundpad?LiveSoundpad.Buttons(p,library):p.LiveScenes?LiveScenes.Buttons(live?["scenes"]):(IEnumerable<DeckAction>)p.Buttons;
        return new {p.Id,p.Name,p.Icon,p.LiveScenes,p.LiveSoundpad,soundpadWarning=p.LiveSoundpad?soundpadWarning:null,buttons=actions.Select(a=>{
            LibrarySound? sound=null;if(p.LiveSoundpad&&int.TryParse(a.Target,out var index))sounds.TryGetValue(index,out sound);
            return new{a.Id,a.Label,a.Kind,a.Icon,a.IconImage,a.Color,confirm=DeckEngine.NeedsConfirm(a),target=a.Kind is "scene" or "preview"?a.Target:"",categories=sound?.Categories??[],searchText=sound is null?"":sound.Name+" "+sound.Artist,configured=p.LiveSoundpad?sound is {Key.Length:64}&&identities[sound.Key]==1:!(a.Kind is "app" or "sound" or "url" or "hotkey" or "soundpadPlay")||a.Target.Length>0};
        })};
    })};
});
app.MapGet("/api/profiles",()=>new{profiles=store.ProfileList(),warning=store.RecoveryWarning});
app.MapPost("/api/profile",(ProfileRequest req)=>{fastCommands.ManualProfile(req.Id);return Results.Ok(new{ok=true});});
app.MapPost("/api/admin/profiles",(ManageProfileRequest req)=>{
    string? id=null;
    fastCommands.ChangeBoard(()=>{switch(req.Operation){case "create":id=store.CreateProfile(req.Name,req.Duplicate);break;case "rename":store.RenameProfile(req.Id,req.Name);break;case "delete":if(!req.Confirmed)throw new ArgumentException("Confirm profile deletion first.");store.DeleteProfile(req.Id);break;default:throw new ArgumentException("Unknown profile operation.");}});
    return Results.Ok(new{ok=true,id});
});
app.MapGet("/api/admin/profiles/{id}/config",(string id)=>store.ProfileSnapshot(id));
app.MapPost("/api/admin/profiles/transfer",(BoardTransferRequest req)=>{object? result=null;fastCommands.ChangeBoard(()=>result=store.TransferButtons(req));return Results.Ok(result);});
app.MapGet("/api/admin/backups",()=>new{backups=store.Backups(),limit=20});
app.MapPost("/api/admin/backups",()=>store.BackupNow());
app.MapPost("/api/admin/backups/preview",(BackupRequest req)=>store.PreviewBackup(req.Id));
app.MapPost("/api/admin/backups/restore",(BackupRequest req)=>{if(!req.Confirmed)throw new ArgumentException("Review and confirm this restore first.");fastCommands.ChangeBoard(()=>store.RestoreBackup(req.Id));return Results.Ok(new{ok=true});});
app.MapGet("/api/soundpad/favourites",()=>new{favourites=store.Favourites()});
app.MapPost("/api/soundpad/favourites",async (FavouriteRequest req,CancellationToken ct)=>{if(!DataStore.ValidSoundKey(req.Key))throw new ArgumentException("Choose a sound with a stable library identity.");var library=req.Selected?await SoundpadLibrary.CurrentSounds(ct):null;store.Favourite(req.Key,req.Selected,library);return Results.Ok(new{ok=true});});
app.MapGet("/api/state",()=>engine.State());
app.MapGet("/api/execution",()=>engine.Execution.Status);
app.MapPost("/api/cancel",(CancelRequest req)=>new{cancelled=engine.Execution.Cancel(req.Id)});
app.MapPost("/api/hold",(HoldRequest req,HttpContext ctx)=>fastCommands.Hold(req,Principal(ctx)));
app.MapGet("/api/events",async (HttpContext ctx)=>{
    ctx.Response.ContentType="text/event-stream";ctx.Response.Headers["X-Accel-Buffering"]="no";
    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    var subscription=stateUpdates.Subscribe();
    try{await foreach(var bytes in subscription.Reader.ReadAllAsync(ctx.RequestAborted)){if(!IsAuthorized(ctx))break;await ctx.Response.WriteAsync("data: "+System.Text.Encoding.UTF8.GetString(bytes)+"\n\n",ctx.RequestAborted);await ctx.Response.Body.FlushAsync(ctx.RequestAborted);}}
    catch(OperationCanceledException) when(ctx.RequestAborted.IsCancellationRequested){}
    finally{stateUpdates.Unsubscribe(subscription.Id);}
});
app.MapGet("/api/apps",async ()=>new {apps=await appMixer.List()});
app.MapPost("/api/apps",async (AppAudioRequest req)=>{await fastCommands.SetAppAudio(req);return Results.Ok(new{ok=true});});
app.MapPost("/api/layout",(LayoutRequest req)=>{store.Reorder(req.Pages);return Results.Ok(new{ok=true});});
app.MapSoundpadLibrary(SoundpadLibrary.Get);
app.MapPost("/api/soundpad/play",async (SoundpadPlayRequest req,HttpContext ctx)=>{
    if(req.Key.Length>0){if(req.Key.Length!=64||!req.Key.All(Uri.IsHexDigit))throw new ArgumentException("Invalid library sound identity.");await fastCommands.ExecuteConfigured(new DeckAction{Kind="soundpadPlay",Target=req.Index.ToString(CultureInfo.InvariantCulture),Value=req.Output,SoundpadKey=req.Key});return Results.Ok(new{ok=true});}
    var library=await SoundpadLibrary.Get(false,ctx.RequestAborted);
    if(!library.Sounds.Any(s=>s.Index==req.Index))throw new ArgumentException("This sound is no longer in the library. Refresh Soundpad.");
    await fastCommands.ExecuteConfigured(new DeckAction{Kind="soundpadPlay",Target=req.Index.ToString(CultureInfo.InvariantCulture),Value=req.Output});
    return Results.Ok(new{ok=true});
});
app.MapPost("/api/action",(ActionRequest req)=>fastCommands.Action(req));
app.MapPost("/api/audio",async (AudioRequest req)=>{await fastCommands.SetVolume(req);return Results.Ok(new{ok=true});});
app.MapPost("/api/mute",async (MuteRequest req)=>{await fastCommands.ToggleMute(req);return Results.Ok(new{ok=true});});
app.MapPost("/api/pair",(PairRequest req)=>{
    if(!pairing.Accept(req.Code)) return Results.Json(new{error="Pairing code expired or incorrect. Generate a new one on the PC."},statusCode:403);
    var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var name=(req.Name??"Android tablet").Trim();if(name.Length>80) name=name[..80];
    store.AddDevice(new(Guid.NewGuid().ToString("N"),name,Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),DateTimeOffset.UtcNow));
    return Results.Ok(new{token,fingerprint,name});
});
app.MapGet("/api/admin/info",()=>new {httpPort,httpsPort,controlPort,fingerprint,certificateCode=fingerprint[..24],addresses=NetworkAddresses(),lanAddresses=NetworkLanAddresses(),connectedTablets=fastControl.ConnectedDevices,dataDirectory=store.DirectoryPath,deviceRecoveryWarning=store.DeviceRecoveryWarning,devices=store.Devices.Select(d=>new{d.Id,d.Name,d.Added,lastSeen=store.LastSeen(d),fastConnected=fastControl.Connected(d.TokenHash)})});
app.MapPost("/api/admin/pairing",()=>new{code=pairing.Open(),expiresIn=300,certificateCode=fingerprint[..24]});
app.MapPost("/api/admin/revoke",(RevokeRequest req)=>{var owner=store.Devices.FirstOrDefault(device=>device.Id==req.Id)?.TokenHash;store.Revoke(req.Id);if(owner is not null){holds.ReleaseOwner(owner);fastCommands.RevokePen(owner);fastControl.Revoke(owner);}return Results.Ok(new{ok=true});});
app.MapGet("/api/admin/desktop",()=>new{settings=desktop.Get(),recoveryWarning=desktop.RecoveryWarning,startWithWindows=DesktopPreferences.StartsWithWindows(),usbStatus=usb.Status,update=updates.Status,installation=selfUpdate.Status,tabletUpdate=usbApkUpdate.Status});
app.MapPost("/api/admin/desktop",(DesktopRequest req)=>{DesktopPreferences.Validate(req.Settings);if(req.StartWithWindows is not null)DesktopPreferences.SetStartup(req.StartWithWindows.Value);desktop.Save(req.Settings);return Results.Ok(new{ok=true});});
app.MapPost("/api/admin/updates",async (CancellationToken ct)=>await updates.Check(true,ct));
app.MapGet("/api/admin/updates/status",()=>selfUpdate.Status);
app.MapPost("/api/admin/updates/download",()=>selfUpdate.BeginDownload());
app.MapGet("/api/admin/updates/tablet",()=>usbApkUpdate.Status);
app.MapPost("/api/admin/updates/tablet",(InstallUpdateRequest req)=>{if(!req.Confirmed)throw new ArgumentException("Confirm the optional USB APK update first.");return usbApkUpdate.Begin(true);});
app.MapPost("/api/admin/updates/install",(InstallUpdateRequest req,HttpContext ctx,IHostApplicationLifetime lifetime)=>{
    if(!req.Confirmed)throw new ArgumentException("Confirm installation and restart first.");
    if(usbApkUpdate.Status.Phase is "checking" or "downloading" or "installing")throw new InvalidOperationException("Wait for the tablet update to finish before restarting Windows.");fastCommands.BeginMaintenance();
    try{if(usbApkUpdate.Status.Phase is "checking" or "downloading" or "installing")throw new InvalidOperationException("Wait for the tablet update to finish before restarting Windows.");selfUpdate.LaunchInstaller();}catch{fastCommands.EndMaintenance();throw;}
    ctx.Response.OnCompleted(()=>{lifetime.StopApplication();return Task.CompletedTask;});return Results.Ok(new{ok=true});
});
app.MapPost("/api/admin/soundpad/remap",async (RemapRequest req,HttpContext ctx)=>{if(req.LinkLegacy&&!req.Confirmed)throw new ArgumentException("Verify existing Soundpad indices before linking old buttons.");return SoundpadBindings.Remap(store,await SoundpadLibrary.Get(true,ctx.RequestAborted),req.LinkLegacy);});
app.MapGet("/api/admin/config",()=>{
    var snapshot=store.ActiveSnapshot();var current=snapshot.Config;var result=JsonSerializer.SerializeToNode(current,DataStore.Json)!.AsObject();result.Remove("profiles");result.Remove("favourites");result["obsPassword"]="";result["passwordConfigured"]=current.ObsPassword.Length>0;result["boardRevision"]=snapshot.Revision;return result;
});
app.MapPost("/api/admin/config",(ConfigRequest req)=>{
    string? revision=null;fastCommands.ChangeBoard(()=>{store.SaveActive(req.Config,req.ReplacePassword,req.ExpectedRevision);revision=store.BoardRevision;});return Results.Ok(new{ok=true,boardRevision=revision});
});
app.MapPost("/api/admin/soundpad/import",async (SoundpadImportRequest req,HttpContext ctx)=>{
    if(req.Indices.Length is <1 or >120)throw new ArgumentException("Select between 1 and 120 sounds.");
    var library=await SoundpadLibrary.Get(false,ctx.RequestAborted);
    var sounds=library.Sounds.ToDictionary(s=>s.Index);
    var selected=req.Indices.Distinct().Select(index=>sounds.TryGetValue(index,out var sound)?sound:throw new ArgumentException("A selected sound is no longer in the library. Refresh Soundpad.")).ToArray();
    store.ImportSounds(req.PageId,selected,req.Output);
    return Results.Ok(new{ok=true});
});
app.MapGet("/api/admin/obs",async ()=>new {scenes=await obs.Call("GetSceneList"),inputs=await obs.Call("GetInputList")});
app.MapPost("/api/admin/obs-settings",(ObsSettingsRequest req)=>{
    if(req.Port is <1 or >65535||req.Password is null||req.Password.Length>512||req.MicInput is null||req.DesktopInput is null||req.MicInput.Length>200||req.DesktopInput.Length>200||req.PasswordMode is not ("keep" or "replace" or "clear"))throw new ArgumentException("Check the OBS port, password choice, and input names.");
    fastCommands.ChangeBoard(()=>store.Modify(c=>{
        c.ObsPort=req.Port;c.MicInput=req.MicInput.Trim();c.DesktopInput=req.DesktopInput.Trim();
        if(req.PasswordMode=="replace")c.ObsPassword=req.Password;
        else if(req.PasswordMode=="clear")c.ObsPassword="";
    }));
    return Results.Ok(new{ok=true});
});
app.MapPost("/api/admin/import-scenes",async (ImportScenesRequest? req)=>{
    var initial=store.ActiveSnapshot();
    var expectedProfile=req?.ProfileId??initial.Config.ProfileId;var expectedRevision=req?.ExpectedRevision??initial.Revision;
    if(initial.Config.ProfileId!=expectedProfile||initial.Revision!=expectedRevision)throw new InvalidOperationException("The board changed. Reload before importing scenes.");
    await obs.Call("GetSceneList");
    fastCommands.ChangeBoard(()=>{
    if(store.Config.ProfileId!=expectedProfile||store.BoardRevision!=expectedRevision)throw new InvalidOperationException("The board changed. Reload before importing scenes.");
    var c=JsonSerializer.Deserialize<DeckConfig>(JsonSerializer.Serialize(store.Config,DataStore.Json),DataStore.Json)!;
    var page=c.Pages.FirstOrDefault(p=>p.Name=="Scenes");if(page==null) {page=new DeckPage{Name="Scenes",Icon="clapper"};c.Pages.Add(page);}
    page.LiveScenes=true;
    store.Save(c);});return Results.Ok(new{ok=true});
});
app.MapPost("/api/admin/window",async()=>{if(tray is null)return Results.Conflict(new{error="The companion is running headlessly. Restart it normally to enable the desktop window."});await tray.OpenConsole();return Results.Ok(new{ok=true});});
app.MapPost("/api/admin/ink/window",async()=>{if(tray is null)return Results.Conflict(new{error="The companion is running headlessly. Restart it normally to enable the ink window."});await tray.OpenInkWindow();return Results.Ok(new{ok=true});});
app.MapPost("/api/admin/shutdown",(IHostApplicationLifetime lifetime)=>{lifetime.StopApplication();return Results.Ok(new{ok=true});});
var panel=$"http://127.0.0.1:{httpPort}/desktop.html"+(args.Contains("--after-update")?"?section=updates":"")+$"#admin={adminToken}";
File.WriteAllText(Path.Combine(store.DirectoryPath,"admin-session.json"),JsonSerializer.Serialize(new{token=adminToken,httpPort,httpsPort},DataStore.Json));
app.Lifetime.ApplicationStopping.Register(fastCommands.BeginShutdown);
app.Lifetime.ApplicationStarted.Register(()=>{
    try{fastControl.Start();}catch(SocketException){Console.Error.WriteLine("Fast control port is unavailable. HTTPS fallback remains active.");}
    Console.WriteLine($"Tablet Deck Studio is running. PC editor: http://127.0.0.1:{httpPort}");
    Console.WriteLine($"Tablet encrypted port: {httpsPort}. Tray menu > Exit, or Ctrl+C, closes the companion.");
    if(!args.Contains("--no-tray")&&(args.Contains("--tray")||!args.Contains("--no-browser")))tray=new DesktopTray(panel,store.DirectoryPath,desktop,updates,()=>app.Lifetime.StopApplication(),!args.Contains("--no-browser"));
    usb.Start();
    usbApkUpdate.Start();
    autoProfiles.Start();
});
var updateLoop=Task.Run(async()=>{while(!backgroundStop.IsCancellationRequested){try{if(desktop.Get().UpdateNotifications)await updates.Check(false,backgroundStop.Token);await Task.Delay(TimeSpan.FromMinutes(15),backgroundStop.Token);}catch(OperationCanceledException){break;}}});
try {await app.RunAsync();} finally {backgroundStop.Cancel();await updateLoop;tray?.Dispose();windowsPen.CancelAll("The companion is closing.");holds.ReleaseAll();VoicemeeterClient.Shutdown();WindowsActions.StopSounds();cert.Dispose();}
static FileStream? AcquireLock(string path) {try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return null;}}
static bool SafeEqual(string a,string b)=>a.Length==b.Length&&CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a),System.Text.Encoding.UTF8.GetBytes(b));
static string[] NetworkAddresses()=>NetworkLanAddresses().Concat(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address)).Select(a=>a.Address.ToString())).Distinct(StringComparer.Ordinal).ToArray();
static string[] NetworkLanAddresses()=>System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up&&n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211&&n.GetIPProperties().GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork&&!g.Address.Equals(IPAddress.Any))).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address)).Select(a=>a.Address.ToString()).Distinct(StringComparer.Ordinal).ToArray();
record ActionRequest(string Id,bool Confirmed=false);
record ProfileRequest(string Id);
record ManageProfileRequest(string Operation,string Id="",string Name="",bool Duplicate=false,bool Confirmed=false);
record BackupRequest(string Id,bool Confirmed=false);
record FavouriteRequest(string Key,bool Selected);
record CancelRequest(string Id);
record AudioRequest(string Channel,double Db);
record MuteRequest(string Channel);
record PairRequest(string Code,string? Name);
record RevokeRequest(string Id);
record ConfigRequest(DeckConfig Config,bool ReplacePassword=false,string? ExpectedRevision=null);
record ImportScenesRequest(string? ProfileId=null,string? ExpectedRevision=null);
record ObsSettingsRequest(int Port,string MicInput,string DesktopInput,string PasswordMode="keep",string Password="");
record AppAudioRequest(string Id,double? Percent=null,bool? Muted=null);
record LayoutRequest(PageOrder[] Pages);
record SoundpadPlayRequest(int Index,string Output="mic",string Key="");
record SoundpadImportRequest(string PageId,int[] Indices,string Output="mic");
record HoldRequest(string Operation,string Id="",string Lease="");
record DesktopRequest(DesktopSettings Settings,bool? StartWithWindows=null);
record InstallUpdateRequest(bool Confirmed=false);
record RemapRequest(bool LinkLegacy=false,bool Confirmed=false);
sealed class Pairing
{
    private readonly object gate=new();private string code="";private DateTime expires=DateTime.MinValue;private int attempts;
    public string Open() {lock(gate) {code=RandomNumberGenerator.GetInt32(100000,1000000).ToString(CultureInfo.InvariantCulture);expires=DateTime.UtcNow.AddMinutes(5);attempts=0;return code;} }
    public bool Accept(string submitted) {lock(gate) {if(DateTime.UtcNow>expires||++attempts>5||submitted!=code) return false;expires=DateTime.MinValue;return true;} }
}
