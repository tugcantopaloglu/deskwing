using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace TabletDeck;

internal static class NotebookRoutes
{
    // Existing PC-admin/paired-device middleware authorizes these routes. No board route or
    // configuration mutation is involved in notebook persistence.
    internal static void MapNotebooks(this WebApplication app,NotebookWorkspace notebooks)=>app.MapNotebooks(()=>notebooks);
    internal static void MapNotebooks(this WebApplication app,Func<NotebookWorkspace> factory)
    {
        var notebooks=new Lazy<NotebookWorkspace>(factory,LazyThreadSafetyMode.ExecutionAndPublication);
        IResult Catalog()=>Handle(()=>notebooks.Value.Catalog());
        Task<IResult> Change(HttpContext context)=>Read(context,request=>notebooks.Value.Change(request));
        IResult Page(string notebookId,string pageId,HttpContext context)=>Handle(()=>{
            string? revision=null;
            if(context.Request.QueryString.HasValue){var query=context.Request.Query;var values=query["revision"];if(query.Count!=1||values.Count!=1)throw new ArgumentException("Use a single notebook page revision.");revision=values[0];}
            var result=notebooks.Value.Page(notebookId,pageId,revision);context.Response.Headers.ETag="\""+(result is NotebookPageSnapshot snapshot?snapshot.Revision:((NotebookUnchangedSnapshot)result).Revision)+"\"";return result;
        });
        Task<IResult> Save(string notebookId,string pageId,HttpContext context)=>Read(context,request=>{
            NotebookWorkspace.Fields(request,["expectedRevision","document"]);return notebooks.Value.SavePage(notebookId,pageId,InkWorkspace.Text(request.GetProperty("expectedRevision"),64,"revision"),request.GetProperty("document"));
        });
        IResult History(string notebookId,string pageId)=>Handle(()=>notebooks.Value.History(notebookId,pageId));
        Task<IResult> Restore(string notebookId,string pageId,HttpContext context)=>Read(context,request=>{
            NotebookWorkspace.Fields(request,["expectedRevision","versionId"]);return notebooks.Value.Restore(notebookId,pageId,InkWorkspace.Text(request.GetProperty("expectedRevision"),64,"revision"),InkWorkspace.Text(request.GetProperty("versionId"),64,"version identity"));
        });
        app.MapGet("/api/notebooks",Catalog);app.MapPost("/api/notebooks",(Delegate)(Func<HttpContext,Task<IResult>>)Change);
        app.MapGet("/api/notebooks/{notebookId}/pages/{pageId}",Page);app.MapPost("/api/notebooks/{notebookId}/pages/{pageId}",(Func<string,string,HttpContext,Task<IResult>>)Save);
        app.MapGet("/api/notebooks/{notebookId}/pages/{pageId}/history",History);app.MapPost("/api/notebooks/{notebookId}/pages/{pageId}/history/restore",(Func<string,string,HttpContext,Task<IResult>>)Restore);
    }
    private static IResult Handle(Func<object> action)
    {
        try{return Results.Ok(action());}
        catch(NotebookConflictException conflict){return Results.Json(new{error=conflict.Message,code="notebook_conflict",revision=conflict.Revision},statusCode:409);}
        catch(InkConflictException conflict){return Results.Json(new{error=conflict.Message,code="notebook_conflict",revision=conflict.Current.Revision},statusCode:409);}
        catch(JsonException){return Results.Json(new{error="Saved notebook data could not be read. Its original files were retained; restore a recovery copy and reopen DeskWing.",code="notebook_storage"},statusCode:503);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return Results.Json(new{error="Notebook storage is unavailable. Existing files were retained; check private storage and retry.",code="notebook_storage"},statusCode:503);}
        catch(Exception error) when(error is ArgumentException or InvalidOperationException){return Results.BadRequest(new{error=error.Message});}
    }
    private static async Task<IResult> Read(HttpContext context,Func<JsonElement,object> action)
    {
        if(!context.Request.HasJsonContentType())return Results.Json(new{error="Send notebook changes as JSON."},statusCode:415);
        var limit=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=NotebookWorkspace.MaxRequestBytes;
        if(context.Request.ContentLength>NotebookWorkspace.MaxRequestBytes)return TooLarge();
        try{
            using var output=new MemoryStream();var buffer=new byte[65536];int count;
            while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0){if(output.Length+count>NotebookWorkspace.MaxRequestBytes)return TooLarge();await output.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);}
            using var json=JsonDocument.Parse(output.ToArray(),new JsonDocumentOptions{MaxDepth=16});return Handle(()=>action(json.RootElement));
        }catch(BadHttpRequestException error) when(error.StatusCode==413){return TooLarge();}
        catch(JsonException){return Results.BadRequest(new{error="The notebook request is not valid JSON."});}
    }
    private static IResult TooLarge()=>Results.Json(new{error="Keep each notebook request below 4 MB."},statusCode:413);
}
