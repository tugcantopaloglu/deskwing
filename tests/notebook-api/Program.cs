using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TabletDeck;

var root=Path.Combine(Path.GetTempPath(),"deskwing-notebook-api-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);var checks=0;
void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);checks++;Console.WriteLine("PASS "+label);}
var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app=builder.Build();var fixtureKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
app.Use(async(context,next)=>{if(context.Request.Headers["X-Notebook-Fixture"]!=fixtureKey){context.Response.StatusCode=403;return;}await next(context);});
var legacy=new InkWorkspace(root);var original=File.ReadAllBytes(Path.Combine(root,InkWorkspace.FileName));var factories=0;
app.MapGet("/health",()=>new{ok=true});app.MapNotebooks(()=>{factories++;return new NotebookWorkspace(root,legacy);});
await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single()),Timeout=TimeSpan.FromSeconds(8)};
async Task<JsonElement> Json(HttpResponseMessage response){using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return json.RootElement.Clone();}
async Task<HttpResponseMessage> Post(string route,object body)=>await client.PostAsJsonAsync(route,body);
try {
    Check((await client.GetAsync("/api/notebooks")).StatusCode==HttpStatusCode.Forbidden,"notebook routes remain behind the host authorization middleware");
    client.DefaultRequestHeaders.Add("X-Notebook-Fixture",fixtureKey);
    Check((await client.GetAsync("/health")).IsSuccessStatusCode&&factories==0,"ordinary companion routes do not initialize notebook storage");
    var initialResponse=await client.GetAsync("/api/notebooks");var initial=await Json(initialResponse);Check(initialResponse.IsSuccessStatusCode&&factories==1,"first library request initializes the notebook workspace once");
    Check(File.ReadAllBytes(Path.Combine(root,InkWorkspace.FileName)).SequenceEqual(original),"opening the notebook API preserves the existing scratchpad bytes");
    var notebookId=initial.GetProperty("notebooks")[0].GetProperty("id").GetString();var pageId=initial.GetProperty("notebooks")[0].GetProperty("pages")[0].GetProperty("id").GetString();var route=$"/api/notebooks/{notebookId}/pages/{pageId}";
    var pageResponse=await client.GetAsync(route);var page=await Json(pageResponse);var revision=page.GetProperty("revision").GetString();
    Check(pageResponse.Headers.ETag?.Tag=="\""+revision+"\"","page responses expose the current optimistic ETag");
    var same=await Json(await client.GetAsync(route+"?revision="+revision));Check(same.GetProperty("unchanged").GetBoolean()&&!same.TryGetProperty("document",out _),"matching page polling returns bounded metadata without document bytes");
    Check((await client.GetAsync(route+"?revision="+revision+"&revision="+revision)).StatusCode==HttpStatusCode.BadRequest,"duplicate page polling parameters are rejected");
    Check((await client.GetAsync(route+"?other=x")).StatusCode==HttpStatusCode.BadRequest,"unknown polling parameters are rejected");
    var changed=JsonSerializer.Deserialize<Dictionary<string,object>>(page.GetProperty("document").GetRawText())!;changed["notes"]="API notebook notes";
    var save=await Post(route,new{expectedRevision=revision,document=changed});var saved=await Json(save);Check(save.IsSuccessStatusCode&&saved.GetProperty("document").GetProperty("notes").GetString()=="API notebook notes","revision-checked page HTTP saves reach only the selected page");
    var stale=await Post(route,new{expectedRevision=revision,document=changed});var conflict=await Json(stale);Check(stale.StatusCode==HttpStatusCode.Conflict&&conflict.GetProperty("code").GetString()=="notebook_conflict","a stale HTTP save returns an explicit notebook conflict");
    Check((await Post(route,new{expectedRevision=saved.GetProperty("revision").GetString(),document=changed,command="unexpected"})).StatusCode==HttpStatusCode.BadRequest,"unknown page-save fields cannot enter persistence");
    var history=await Json(await client.GetAsync(route+"/history"));Check(history.GetProperty("versions").GetArrayLength()==1,"the page history API exposes its retained prior version");
    var restored=await Post(route+"/history/restore",new{expectedRevision=saved.GetProperty("revision").GetString(),versionId=history.GetProperty("versions")[0].GetProperty("id").GetString()});Check(restored.IsSuccessStatusCode&&(await Json(restored)).GetProperty("document").GetProperty("notes").GetString()=="","history restore requires and applies the selected current page revision");
    Check((await client.PostAsync("/api/notebooks",new StringContent("{}",Encoding.UTF8,"text/plain"))).StatusCode==HttpStatusCode.UnsupportedMediaType,"notebook writes require JSON content type");
    Check((await client.PostAsync("/api/notebooks",new StringContent("{broken",Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.BadRequest,"malformed notebook JSON fails without changing the library");
    var catalog=await Json(await client.GetAsync("/api/notebooks"));var create=await Post("/api/notebooks",new{operation="createNotebook",expectedRevision=catalog.GetProperty("revision").GetString(),name="A second notebook"});Check(create.IsSuccessStatusCode&&(await Json(create)).GetProperty("notebooks").GetArrayLength()==2,"catalogue changes return the updated library for selecting created IDs");
    Check(factories==1,"all page and catalog requests reuse the same notebook service");
    Check((await client.PostAsync("/api/notebooks",new StringContent(new string(' ',NotebookWorkspace.MaxRequestBytes+1),Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.RequestEntityTooLarge,"known oversized notebook requests are bounded before JSON parsing");
    Console.WriteLine(checks+" notebook API checks passed.");
} finally {
    await app.StopAsync();
    var absolute=Path.GetFullPath(root);var expected=Path.GetFullPath(Path.GetTempPath());if(absolute.StartsWith(expected,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("deskwing-notebook-api-",StringComparison.Ordinal))Directory.Delete(absolute,true);
}
