using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace TabletDeck;

internal static class InkRoutes
{
    // Mapped behind the companion's existing admin/paired-device authorization middleware.
    public static void MapInk(this WebApplication app,InkWorkspace workspace)
    {
        app.MapGet("/api/ink",(HttpContext context)=>{
            if(context.Request.QueryString.HasValue) {
                var query=context.Request.Query;
                var known=query["revision"];
                if(query.Count!=1||known.Count!=1||known[0] is not {Length:64} revision||!revision.All(Uri.IsHexDigit))return Results.BadRequest(new{error="Use a single scratchpad revision to check for updates."});
                var result=workspace.Poll(revision);
                var currentRevision=result is InkSnapshot full?full.Revision:((InkUnchangedSnapshot)result).Revision;
                context.Response.Headers.ETag="\""+currentRevision+"\"";
                return Results.Ok(result);
            }
            var snapshot=workspace.Snapshot();
            context.Response.Headers.ETag="\""+snapshot.Revision+"\"";
            return Results.Ok(snapshot);
        });
        app.MapPost("/api/ink",(Func<HttpContext,Task<IResult>>)(async context=>{
            var size=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if(size is not null&&!size.IsReadOnly)size.MaxRequestBodySize=InkWorkspace.MaxRequestBytes;
            if(context.Request.ContentLength>InkWorkspace.MaxRequestBytes)return TooLarge();
            if(!context.Request.HasJsonContentType())return Results.Json(new{error="Send the scratchpad as JSON."},statusCode:415);
            try {
                using var output=new MemoryStream();var buffer=new byte[65536];
                while(true) {
                    var count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted);
                    if(count==0)break;
                    if(output.Length+count>InkWorkspace.MaxRequestBytes)return TooLarge();
                    await output.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
                }
                using var json=JsonDocument.Parse(output.ToArray(),new JsonDocumentOptions{MaxDepth=8});
                var request=json.RootElement;InkWorkspace.Fields(request,"expectedRevision","document");
                var expected=InkWorkspace.Text(request.GetProperty("expectedRevision"),64,"revision");
                var snapshot=workspace.Save(expected,request.GetProperty("document"));
                context.Response.Headers.ETag="\""+snapshot.Revision+"\"";
                return Results.Ok(snapshot);
            }catch(InkConflictException conflict) {
                return Results.Json(new{error=conflict.Message,code="ink_conflict",workspaceId=conflict.Current.WorkspaceId,revision=conflict.Current.Revision},statusCode:409);
            }catch(BadHttpRequestException ex) when(ex.StatusCode==413) {return TooLarge();}
            catch(Exception ex) when(ex is JsonException or ArgumentException or InvalidOperationException) {
                return Results.BadRequest(new{error=ex is JsonException?"The scratchpad is not valid JSON.":ex.Message});
            }
        }));
    }
    private static IResult TooLarge()=>Results.Json(new{error="Keep the scratchpad request below 4 MB."},statusCode:413);
}
