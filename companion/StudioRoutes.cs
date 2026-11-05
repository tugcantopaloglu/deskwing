using Microsoft.AspNetCore.Http.Features;

namespace TabletDeck;
internal static class StudioRoutes
{
    public static void MapStudio(this WebApplication app,DataStore store,DeckEngine engine,FastCommands commands,AutoProfiles profiles,BoardPacks packs,Func<HttpContext,string> principal,Func<string> pairingCode,Func<string[]> addresses,string fingerprint,int httpsPort,int controlPort,ObsClient obs){
        app.MapGet("/api/ping",()=>new{pong=true});
        app.MapPost("/api/ping",()=>new{pong=true});
        app.MapPost("/api/profile-lock",(ProfileLockRequest req,HttpContext ctx)=>{commands.ProfileLock(principal(ctx),req.Active);return Results.Ok(new{ok=true});});
        app.MapGet("/api/mixer",()=>new{channels=engine.Channels()});
        app.MapGet("/api/soundpad/recent",()=>new{sounds=store.Recents(),warning=store.RecentWarning});
        app.MapPost("/api/soundpad/recent/clear",()=>{store.ClearRecents();return Results.Ok(new{ok=true});});
        app.MapGet("/api/admin/installed-apps",()=>new{apps=StudioTools.InstalledApps()});
        app.MapPost("/api/admin/pick-file",async (PickRequest req)=>Results.Ok(new{path=await StudioTools.PickFile(req.Kind)}));
        app.MapPost("/api/admin/action-check",async (ActionCheckRequest req,HttpContext ctx)=>Results.Ok(await ProfileReadiness.CheckAction(req.Action,store.ActiveSnapshot().Config,(type,data,ct)=>obs.Call(type,data,ct),SoundpadLibrary.CurrentSounds,ctx.RequestAborted)));
        app.MapGet("/api/admin/readiness",(Func<HttpContext,Task<IResult>>)(async ctx=>Results.Ok(await ProfileReadiness.Check(store.ActiveSnapshot().Config,(type,data,ct)=>obs.Call(type,data,ct),SoundpadLibrary.CurrentSounds,ctx.RequestAborted))));
        app.MapGet("/api/admin/obs/targets",(Func<HttpContext,Task<IResult>>)(async ctx=>Results.Ok(await ProfileReadiness.Targets((type,data,ct)=>obs.Call(type,data,ct),ctx.Request.Query["scene"].ToString(),ctx.Request.Query["source"].ToString(),ctx.RequestAborted))));
        app.MapPost("/api/admin/backups/compare",(BackupCompareRequest req)=>store.CompareBackup(req.Id));
        app.MapPost("/api/admin/backups/restore-profile",(BackupProfileRestoreRequest req)=>{
            if(!req.Confirmed)throw new ArgumentException("Review and confirm the new profile first.");
            string? id=null;commands.ChangeBoard(()=>id=store.RestoreBackupProfile(req.Id,req.ProfileId,req.Name));return Results.Ok(new{ok=true,id});
        });
        app.MapGet("/api/admin/automation",()=>profiles.Snapshot());
        app.MapPost("/api/admin/automation",(AutoProfileSettings req)=>{profiles.Save(req);return Results.Ok(new{ok=true});});
        app.MapPost("/api/admin/mixer",(MixerSettingsRequest req)=>{if(req.Channels is null)throw new ArgumentException("Choose mixer channels.");StudioTools.ValidateMixer(req.Channels);commands.ChangeBoard(()=>store.Modify(c=>c.MixerInputs=req.Channels));return Results.Ok(new{ok=true});});
        app.MapPost("/api/admin/pairing-qr",(QrRequest req)=>{
            var host=req.Mode=="usb"?"127.0.0.1":req.Host;
            if(req.Mode!="usb"&&!addresses().Contains(host,StringComparer.Ordinal))throw new ArgumentException("Choose this PC's current private IPv4 address.");
            if(!PairingQr.PrivateHost(host))throw new ArgumentException("Choose a private IPv4 network address.");
            var code=pairingCode();var payload=PairingQr.Payload(host,req.Mode,code,fingerprint[..24],httpsPort,controlPort);
            return Results.Ok(new{image=PairingQr.Image(payload),payload,code,expiresIn=300});
        });
        app.MapPost("/api/admin/packs/manifest",(PackManifestRequest req)=>packs.Manifest(req.ProfileId));
        app.MapPost("/api/admin/packs/export",(PackExportRequest req)=>Results.File(packs.Export(req.ProfileId,req.SoundIds),"application/zip","TabletDeck-Board.deckpack"));
        app.MapPost("/api/admin/packs/preview",async (HttpContext ctx)=>{
            var size=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();if(size is not null&&!size.IsReadOnly)size.MaxRequestBodySize=BoardPacks.MaxBytes;
            using var output=new MemoryStream();var buffer=new byte[81920];while(true){var count=await ctx.Request.Body.ReadAsync(buffer,ctx.RequestAborted);if(count==0)break;if(output.Length+count>BoardPacks.MaxBytes)throw new ArgumentException("Keep the pack below 64 MB.");await output.WriteAsync(buffer.AsMemory(0,count),ctx.RequestAborted);}return Results.Ok(packs.Preview(output.ToArray()));
        });
        app.MapPost("/api/admin/packs/import",(PackImportRequest req)=>{if(!req.Confirmed)throw new ArgumentException("Review and confirm the imported pack first.");string? id=null;commands.ChangeBoard(()=>id=packs.Import(req.Id,req.Name));return Results.Ok(new{ok=true,id});});
    }
}
internal sealed record ProfileLockRequest(bool Active);
internal sealed record PickRequest(string Kind);
internal sealed record ActionCheckRequest(DeckAction Action);
internal sealed record BackupCompareRequest(string Id);
internal sealed record BackupProfileRestoreRequest(string Id,string ProfileId,string Name,bool Confirmed=false);
internal sealed record MixerSettingsRequest(List<MixerChannel> Channels);
internal sealed record QrRequest(string Mode,string Host="");
internal sealed record PackManifestRequest(string ProfileId);
internal sealed record PackExportRequest(string ProfileId,string[] SoundIds);
internal sealed record PackImportRequest(string Id,string Name,bool Confirmed=false);
