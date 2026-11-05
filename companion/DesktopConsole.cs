using System.Diagnostics;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Forms=System.Windows.Forms;

namespace TabletDeck;

// Only the local management document is hosted here. No host-object or web-message bridge is exposed.
internal sealed class DesktopConsole : Forms.Form
{
    private readonly Uri address;
    private readonly string dataDirectory;
    private readonly bool inkWindow;
    private readonly WebView2 web=new(){Dock=Forms.DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(16,19,24)};
    private readonly Forms.Label status=new(){Dock=Forms.DockStyle.Fill,Text="Opening your studio…",ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter};
    private bool exiting,initialized;
    public DesktopConsole(string url,string directory,bool inkWindow=false)
    {
        address=new Uri(url);dataDirectory=directory;this.inkWindow=inkWindow;
        Text=inkWindow?"DeskWing Ink":"DeskWing";Icon=AppIcon.Load();BackColor=Color.FromArgb(16,19,24);
        StartPosition=Forms.FormStartPosition.CenterScreen;Size=inkWindow?new Size(1000,740):new Size(1280,860);MinimumSize=new Size(620,480);AutoScaleMode=Forms.AutoScaleMode.Dpi;
        FormBorderStyle=Forms.FormBorderStyle.Sizable;MaximizeBox=true;MinimizeBox=true;SizeGripStyle=Forms.SizeGripStyle.Show;
        if(inkWindow)status.Text="Opening your shared ink…";
        Controls.Add(web);Controls.Add(status);Shown+=async(_,_)=>{if(!initialized){initialized=true;await Initialize();}};
        FormClosing+=(_,e)=>{if(!exiting&&!this.inkWindow&&e.CloseReason==Forms.CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
    internal static string InkWindowAddress(string editor){var uri=new UriBuilder(editor){Query="workspace=ink-window"};return uri.Uri.AbsoluteUri;}
    internal static bool LocalOrigin(Uri expected,string target)=>Uri.TryCreate(target,UriKind.Absolute,out var uri)&&uri.Scheme=="http"&&uri.Host=="127.0.0.1"&&uri.Port==expected.Port&&uri.UserInfo.Length==0;
    internal static bool LocalDocument(Uri expected,string target)=>LocalOrigin(expected,target)&&new Uri(target).AbsolutePath=="/desktop.html";
    internal static bool ExportUri(Uri expected,string target)=>LocalOrigin(expected,target)||target.StartsWith("blob:",StringComparison.Ordinal)&&LocalOrigin(expected,target[5..]);
    internal static string DownloadFilter(string fileName)=>Path.GetExtension(fileName).ToLowerInvariant() switch{".png"=>"PNG images (*.png)|*.png|All files (*.*)|*.*",".txt"=>"Text notes (*.txt)|*.txt|All files (*.*)|*.*",_=>"Board files (*.json;*.deckpack)|*.json;*.deckpack|All files (*.*)|*.*"};
    internal static bool ExternalUri(string target)=>Uri.TryCreate(target,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Port==443&&!uri.IsLoopback&&Uri.CheckHostName(uri.Host)==UriHostNameType.Dns;
    private static void OpenExternal(string target){if(ExternalUri(target))Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}
    private async Task Initialize()
    {
        try{
            var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(dataDirectory,"DesktopWebView"));
            if(IsDisposed||Disposing)return;
            await web.EnsureCoreWebView2Async(environment);var core=web.CoreWebView2;
            if(IsDisposed||Disposing)return;
            core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.IsStatusBarEnabled=false;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.NavigationStarting+=(_,e)=>{if(!LocalDocument(address,e.Uri)){e.Cancel=true;if(e.IsUserInitiated)OpenExternal(e.Uri);}};
            core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(e.IsUserInitiated)OpenExternal(e.Uri);};
            core.DownloadStarting+=(_,e)=>{
                e.Handled=true;
                if(!ExportUri(address,e.DownloadOperation.Uri)){e.Cancel=true;return;}
                var fileName=Path.GetFileName(e.ResultFilePath);
                using var chooser=new Forms.SaveFileDialog{FileName=fileName,OverwritePrompt=true,AddExtension=true,Filter=DownloadFilter(fileName)};
                if(chooser.ShowDialog(this)!=Forms.DialogResult.OK){e.Cancel=true;return;}e.ResultFilePath=chooser.FileName;
            };
            core.NavigationCompleted+=(_,e)=>{if(e.IsSuccess){status.Hide();web.Focus();}else ShowFailure("The local console could not load. Close this window and reopen DeskWing from its tray icon.",false);};
            core.ProcessFailed+=(_,_)=>ShowFailure("The desktop view stopped. Close this window and restart the companion from the tray.",false);
            core.Navigate(address.AbsoluteUri);
        }catch(WebView2RuntimeNotFoundException){if(!IsDisposed&&!Disposing)ShowFailure("Install Microsoft Edge WebView2 Runtime to open the desktop console. Your tablet connection remains running.",true);}
        catch(Exception ex) when(ex is not OutOfMemoryException){if(!IsDisposed&&!Disposing)ShowFailure("The desktop console could not start. Your tablet connection remains running. Close and reopen the companion to retry.",false);Console.Error.WriteLine("Desktop view initialization failed: "+ex.GetType().Name);}
    }
    private void ShowFailure(string message,bool runtime)
    {
        status.Text=message;status.Show();status.BringToFront();
        if(runtime){var button=new Forms.Button{Text="Download Microsoft WebView2 Runtime",AutoSize=true,Dock=Forms.DockStyle.Bottom,Height=56};button.Click+=(_,_)=>OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");Controls.Add(button);button.BringToFront();}
    }
    public void Open(){if(IsDisposed)return;Show();if(WindowState==Forms.FormWindowState.Minimized)WindowState=Forms.FormWindowState.Normal;Activate();}
    internal CoreWebView2? Core=>web.CoreWebView2;
    internal bool ContentReady=>!status.Visible&&web.Visible&&web.Enabled;
    public void Shutdown(){if(IsDisposed)return;exiting=true;Close();Dispose();}
}
