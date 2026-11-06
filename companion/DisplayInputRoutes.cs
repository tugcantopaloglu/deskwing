using Microsoft.AspNetCore.Http.Features;

namespace TabletDeck;

internal static class DisplayInputRoutes
{
    internal const int MaximumRequestBytes=16*1024;
    internal const string InputPath="/api/computer/display/input";
    internal const string AdminPath="/api/admin/computer/display/input";
    internal static void MapDisplayInput(this WebApplication app,DisplayInput input,FastCommands commands,Func<HttpContext,string> principal,Func<bool> available)
    {
        app.MapGet(InputPath,()=>input.Status);
        app.MapGet(AdminPath,()=>input.Status);
        app.MapPost(AdminPath,(Func<HttpContext,Task<IResult>>)(async context=>{
            var settings=await Read<DisplayInputSettings>(context);
            if(settings.Enabled&&!available())throw new InvalidOperationException("The companion is closing or restarting.");
            return Results.Ok(commands.ConfigureDisplayInputSettings(settings));
        }));
        app.MapPost(InputPath,(Func<HttpContext,Task<IResult>>)(async context=>{
            var request=await Read<DisplayInputRequest>(context);
            if(request.Operation is not ("end" or "cancel")&&!available())throw new InvalidOperationException("The companion is closing or restarting. Start a fresh display input session after reconnecting.");
            return Results.Ok(commands.ExecuteDisplayInput(request,principal(context)));
        }));
        app.Lifetime.ApplicationStopping.Register(input.Shutdown);
    }
    private static async Task<T> Read<T>(HttpContext context)
    {
        if(!context.Request.HasJsonContentType())throw new BadHttpRequestException("Send display input as JSON.",415);
        if(context.Request.ContentLength>MaximumRequestBytes)throw new BadHttpRequestException("Keep display input below 16 KB.",413);
        var size=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(size is not null&&!size.IsReadOnly)size.MaxRequestBodySize=MaximumRequestBytes;
        using var bytes=new MemoryStream();var buffer=new byte[4096];
        while(true){var read=await context.Request.Body.ReadAsync(buffer,context.RequestAborted);if(read==0)break;if(bytes.Length+read>MaximumRequestBytes)throw new BadHttpRequestException("Keep display input below 16 KB.",413);bytes.Write(buffer,0,read);}
        try{return System.Text.Json.JsonSerializer.Deserialize<T>(bytes.ToArray(),DataStore.Json)??throw new ArgumentException("Provide display input settings.");}
        catch(System.Text.Json.JsonException ex){throw new ArgumentException("The display input request is not valid JSON.",ex);}
    }
}
