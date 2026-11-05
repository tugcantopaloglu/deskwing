using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Forms=System.Windows.Forms;

namespace TabletDeck;
internal static class DesktopWindowTests
{
    public static Task Run(string directory)
    {
        var root=Path.GetFullPath(directory);var temp=Path.TrimEndingDirectorySeparator(Path.GetTempPath())+Path.DirectorySeparatorChar;
        if(!root.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||File.ReadAllText(Path.Combine(root,".desktop-window-test"))!="isolated-ui-fixture")throw new ArgumentException("Only a marked Windows temporary fixture is allowed.");
        using var session=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"admin-session.json")));var port=session.RootElement.GetProperty("httpPort").GetInt32();var token=session.RootElement.GetProperty("token").GetString()??"";
        if(port is <19000 or >19999||token.Length!=64||!token.All(Uri.IsHexDigit))throw new ArgumentException("Invalid isolated console session.");
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            Forms.Application.EnableVisualStyles();using var window=new DesktopConsole($"http://127.0.0.1:{port}/desktop.html#admin={token}",root){ShowInTaskbar=false,Opacity=0,StartPosition=Forms.FormStartPosition.Manual,Location=new(-30000,-30000)};
            window.Shown+=async(_,_)=>{
                var passed=0;void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed++;Console.WriteLine("PASS "+label);}
                async Task<bool> Script(string expression)=>await window.Core!.ExecuteScriptAsync(expression)=="true";
                try{
                    var deadline=DateTime.UtcNow.AddSeconds(25);
                    while(DateTime.UtcNow<deadline){if(window.ContentReady&&window.Core is not null&&await Script("document.querySelector('#console-board')?.textContent.includes('buttons')===true"))break;await Task.Delay(100);}
                    Check(window.ContentReady&&window.Core is not null&&await Script("document.body.dataset.surface==='desktop'"),"native WebView2 loads the document and removes its blocking loading overlay");
                    Check(window.FormBorderStyle==Forms.FormBorderStyle.Sizable&&window.MaximizeBox&&window.MinimizeBox,"main window exposes resize, maximize and minimize controls");
                    var mainSize=window.Size;window.Size=new(700,520);Check(window.Size.Width==700&&window.Size.Height==520,"main window can resize below the previous 900 by 620 limit");window.Size=mainSize;
                    Check(!window.Core!.Settings.AreHostObjectsAllowed&&!window.Core.Settings.IsWebMessageEnabled,"native view exposes no host-object or message bridge");
                    Check(await Script("typeof document.querySelector('#console-editor').onclick==='function'"),"native sidebar event handlers are installed");
                    await window.Core.ExecuteScriptAsync("document.querySelector('#console-editor').click()");await Task.Delay(500);
                    Check(await Script("document.querySelector('#panel').open && !!document.querySelector('#save-config')"),"native Board editor opens with real fields");
                    await window.Core.ExecuteScriptAsync("document.querySelector('#console-profiles').click()");await Task.Delay(500);
                    Check(await Script("document.querySelector('#panel').open && document.querySelector('#console-overview').hidden"),"native navigation between open tools retains the editor area");
                    Check(await Script("document.querySelector('#panel').getBoundingClientRect().top>=0 && document.querySelector('#panel').getBoundingClientRect().bottom<=innerHeight+1"),"native inline panel remains inside the viewport");
                    await window.Core.ExecuteScriptAsync("window.deckWindowTestMarker=42");window.Close();Check(!window.Visible&&!window.IsDisposed,"closing the window hides it without stopping or disposing the app");
                    window.Open();Check(await Script("window.deckWindowTestMarker===42"),"reopening preserves the existing console session");
                    var address=new Uri($"http://127.0.0.1:{port}/desktop.html");
                    var inkAddress=DesktopConsole.InkWindowAddress($"{address}?section=updates#admin={token}");var inkUri=new Uri(inkAddress);
                    Check(inkUri.Query=="?workspace=ink-window"&&inkUri.Fragment=="#admin="+token&&DesktopConsole.LocalDocument(address,inkAddress),"ink window address preserves local authentication and uses only the local management document");
                    using(var ink=new DesktopConsole(inkAddress,root,true){ShowInTaskbar=false,Opacity=0,StartPosition=Forms.FormStartPosition.Manual,Location=new(-30000,-30000)}){
                        ink.Show();var inkDeadline=DateTime.UtcNow.AddSeconds(25);
                        while(DateTime.UtcNow<inkDeadline){if(ink.ContentReady&&ink.Core is not null&&await ink.Core.ExecuteScriptAsync("document.body.classList.contains('ink-window-mode')&&window.DeskWorkspace?.mode==='pen'")=="true")break;await Task.Delay(100);}
                        Check(ink.ContentReady&&ink.Core is not null&&await ink.Core.ExecuteScriptAsync("document.body.classList.contains('ink-window-mode')&&window.DeskWorkspace?.mode==='pen'")=="true","separate native ink window opens the shared ink workspace");
                        Check(ink.FormBorderStyle==Forms.FormBorderStyle.Sizable&&ink.MaximizeBox&&ink.MinimizeBox,"separate ink window exposes resize, maximize and minimize controls");
                        ink.Size=new(700,520);Check(ink.Size.Width==700&&ink.Size.Height==520,"separate ink window resizes to a compact PC window");
                        Check(!ink.Core!.Settings.AreHostObjectsAllowed&&!ink.Core.Settings.IsWebMessageEnabled,"separate ink window exposes no native bridge");
                        ink.Close();Check(ink.IsDisposed&&!window.IsDisposed,"closing separate ink disposes its view and leaves the main companion window running");
                    }
                    foreach(var bad in new[]{"file:///C:/Windows/win.ini",$"http://localhost:{port}/desktop.html",$"http://127.0.0.1:{port+1}/desktop.html","https://evil.test/desktop.html",$"http://127.0.0.1:{port}/api/admin/info"})Check(!DesktopConsole.LocalDocument(address,bad),"desktop navigation rejects "+new Uri(bad).Scheme+" / "+new Uri(bad).AbsolutePath);
                    Check(DesktopConsole.ExportUri(address,$"blob:http://127.0.0.1:{port}/fixture")&&!DesktopConsole.ExportUri(address,"blob:https://evil.test/fixture"),"only local-origin exports reach the save chooser");
                    using(var export=new Forms.SaveFileDialog{FileName="DeskWing drawing.PNG",AddExtension=true,Filter=DesktopConsole.DownloadFilter("DeskWing drawing.PNG")})Check(export.FileName=="DeskWing drawing.PNG"&&export.Filter.Split('|')[1]=="*.png","ink PNG export preserves its suggested filename and uses an image filter");
                    using(var export=new Forms.SaveFileDialog{FileName="DeskWing notes.txt",AddExtension=true,Filter=DesktopConsole.DownloadFilter("DeskWing notes.txt")})Check(export.FileName=="DeskWing notes.txt"&&export.Filter.Split('|')[1]=="*.txt","notes export preserves its suggested filename and uses a text filter");
                    Check(DesktopConsole.DownloadFilter("backup.deckpack").Split('|')[1]=="*.json;*.deckpack"&&DesktopConsole.DownloadFilter("download")==DesktopConsole.DownloadFilter("backup.json"),"board and unknown export filenames retain the existing board filter");
                    Check(!DesktopConsole.ExternalUri("file:///C:/Windows/win.ini")&&!DesktopConsole.ExternalUri("https://user:pass@example.com/")&&!DesktopConsole.ExternalUri("https://127.0.0.1/"),"external window policy rejects local files, credentials and loopback");
                    Check(window.Icon is not null&&window.Icon.Width>=16,"native window has the embedded application icon");
                    Console.WriteLine(passed+" native desktop window checks passed; isolated offscreen WebView2, no external provider or app control.");completion.SetResult();
                }catch(Exception ex){completion.TrySetException(ex);}finally{window.Shutdown();Forms.Application.ExitThread();}
            };
            Forms.Application.Run(window);
        }){IsBackground=true,Name="Tablet Deck desktop fixture"};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}
