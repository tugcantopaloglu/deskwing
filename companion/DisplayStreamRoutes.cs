using Microsoft.AspNetCore.Http.Features;

namespace TabletDeck;

// Video travels on a dedicated authenticated HTTPS response, never on the input link.
internal static class DisplayStreamRoutes
{
    internal const int MaximumRequestBytes=4*1024;
    internal const string StreamPath="/api/computer/display/stream";
    private sealed record LeaseRequest(string Lease="");

    public static void MapDisplayStream(this WebApplication app,DisplayStream video,Func<HttpContext,string> principal,Func<HttpContext,bool> authorized,Func<bool> available,Func<HttpContext,bool>? administrator=null)
    {
        app.MapGet(StreamPath+"/status",(HttpContext ctx)=>Results.Ok(video.Status(principal(ctx),administrator?.Invoke(ctx)??false)));
        app.MapPost(StreamPath+"/start",(Func<HttpContext,Task<IResult>>)(async ctx=>{
            var request=await Read<DisplayVideoStart>(ctx);
            RequireAvailable(available);
            return Results.Ok(video.Start(request,principal(ctx)));
        }));
        app.MapPost(StreamPath,(Func<HttpContext,Task>)(async ctx=>{
            var request=await Read<LeaseRequest>(ctx);
            RequireAvailable(available);
            ctx.Response.ContentType="application/vnd.deskwing.h264";
            ctx.Response.Headers.CacheControl="no-store";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                // Pairing can be revoked after the initial middleware check. The
                // producer checks this again before publishing every access unit.
                await video.StreamAsync(request.Lease,principal(ctx),ctx.Response.Body,()=>available()&&authorized(ctx),ctx.RequestAborted);
            }
            catch(Exception ex) when(ctx.Response.HasStarted&&ex is not OutOfMemoryException)
            {
                // A binary stream cannot receive the API middleware's JSON error.
                // Closing it makes the native decoder discard this exact session.
                ctx.Abort();
            }
        }));
        app.MapPost(StreamPath+"/stop",(Func<HttpContext,Task<IResult>>)(async ctx=>{
            var request=await Read<LeaseRequest>(ctx);
            video.Stop(request.Lease,principal(ctx));
            return Results.Ok(new{ok=true});
        }));
    }

    private static async Task<T> Read<T>(HttpContext ctx)
    {
        if(ctx.Request.ContentLength>MaximumRequestBytes)throw new BadHttpRequestException("Keep display stream requests below 4 KB.",StatusCodes.Status413PayloadTooLarge);
        var size=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if(size is not null&&!size.IsReadOnly)size.MaxRequestBodySize=MaximumRequestBytes;
        return await ctx.Request.ReadFromJsonAsync<T>(DataStore.Json,ctx.RequestAborted)??throw new ArgumentException("Provide display stream settings.");
    }

    private static void RequireAvailable(Func<bool> available)
    {
        if(!available())throw new InvalidOperationException("The companion is closing or restarting. Reconnect before viewing Second display.");
    }
}
