using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;

namespace TabletDeck;

public sealed class DesktopTray : IDisposable
{
    private readonly Thread thread;
    private Forms.Control? dispatcher;
    [DllImport("kernel32.dll")]private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    private readonly TaskCompletionSource<Forms.Control> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DesktopConsole? console;
    private readonly string editor,dataDirectory;
    private readonly Action checkOnOpen;
    public DesktopTray(string editor,string dataDirectory,DesktopPreferences preferences,ReleaseUpdates updates,Action stop,bool openWindow=false)
    {
        this.editor=editor;this.dataDirectory=dataDirectory;
        checkOnOpen=()=>{if(preferences.Get().UpdateNotifications)_=updates.Check(false);};
        thread=new Thread(()=>{
            Forms.Application.EnableVisualStyles();
            using var control=new Forms.Control();_ = control.Handle;dispatcher=control;
            using var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("Open DeskWing",null,(_,_)=>ShowConsole());
            var startup=new Forms.ToolStripMenuItem("Start with Windows"){Checked=DesktopPreferences.StartsWithWindows(),CheckOnClick=false};
            startup.Click+=(_,_)=>{try{DesktopPreferences.SetStartup(!startup.Checked);startup.Checked=DesktopPreferences.StartsWithWindows();}catch(Exception ex){Forms.MessageBox.Show(ex.Message,"DeskWing");}};menu.Items.Add(startup);
            var usb=new Forms.ToolStripMenuItem("Reconnect USB automatically"){Checked=preferences.Get().UsbReconnect,CheckOnClick=false};
            usb.Click+=(_,_)=>{preferences.Save(preferences.Get() with{UsbReconnect=!usb.Checked});usb.Checked=preferences.Get().UsbReconnect;};menu.Items.Add(usb);
            using var appIcon=AppIcon.Load();
            using var icon=new Forms.NotifyIcon{Text="DeskWing",Icon=appIcon,ContextMenuStrip=menu,Visible=true};
            icon.DoubleClick+=(_,_)=>ShowConsole();
            void Notify(ReleaseStatus result){if(control.IsDisposed)return;control.BeginInvoke(new Action(()=>{icon.BalloonTipTitle="DeskWing update";icon.BalloonTipText=result.Error??(result.Available?"Version "+result.LatestVersion+" is available. Open Windows options to download and install.":"You have the latest stable version.");icon.ShowBalloonTip(8000);}));}
            updates.Found+=Notify;
            menu.Items.Add("Check for updates",null,async (_,_)=>Notify(await updates.Check(true)));
            menu.Items.Add("Exit",null,(_,_)=>stop());
            ShowWindow(GetConsoleWindow(),0);
            ready.TrySetResult(control);if(openWindow)control.BeginInvoke(new Action(ShowConsole));
            try{Forms.Application.Run();}finally{console?.Shutdown();updates.Found-=Notify;icon.Visible=false;dispatcher=null;}
        }){IsBackground=true,Name="DeskWing tray"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }
    private void ShowConsole(){if(console is null||console.IsDisposed)console=new DesktopConsole(editor,dataDirectory);console.Open();checkOnOpen();}
    public async Task OpenConsole(){var control=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));control.BeginInvoke(new Action(ShowConsole));}
    public void Dispose(){try{dispatcher?.BeginInvoke(new Action(Forms.Application.ExitThread));}catch(InvalidOperationException){}thread.Join(2000);}
}
