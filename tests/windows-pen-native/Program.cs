using System.Runtime.InteropServices;
using TabletDeck;

// Explicit local diagnostic: synthetic pen events can only target this process's own window.
// This is not part of unattended regression because it requires an unlocked interactive desktop.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Contains("--geometry-self-test")){ManualInkGeometry.Verify();return 0;}
        var manual=args.Contains("--manual");
        if (!manual&&!args.Contains("--run-native")) {
            Console.Error.WriteLine("Use --run-native for the automatic isolated diagnostic, or --manual for a drawing receiver without synthetic input.");
            return 2;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if(manual) {
            using var receiver=new ManualPenReceiver();
            Application.Run(receiver);
            return 0;
        }
        using var form = new PenReceiver();
        Application.Run(form);
        return form.Result;
    }
}

internal sealed class PenReceiver : Form
{
    private const int PointerUpdate=0x245,PointerDown=0x246,PointerUp=0x247,PointerEnter=0x249,PointerLeave=0x24A;
    private const uint InContact=4;
    private readonly List<ObservedPen> observed=[];
    private readonly NativeWindowsPenPlatform platform=new();
    private PenFrame? last;
    private int creatorThread;
    internal int Result {get;private set;}=1;

    internal PenReceiver()
    {
        Text="DeskWing Windows Ink verification";
        ClientSize=new Size(620,420);
        StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.FixedSingle;
        MaximizeBox=false;
        BackColor=Color.FromArgb(16,19,24);
        ForeColor=Color.FromArgb(230,237,243);
        Font=new Font("Segoe UI",12);
        Shown+=async (_,_)=>await Verify();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawString("Checking pressure and tilt inside this test window only.\nThe window will close automatically.",Font,Brushes.White,new PointF(24,24));
        using var pen=new Pen(Color.Cyan,4);
        e.Graphics.DrawRectangle(pen,90,145,440,190);
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg is PointerUpdate or PointerDown or PointerUp or PointerEnter or PointerLeave) {
            var id=(uint)(message.WParam.ToInt64()&0xffff);
            if(GetPointerPenInfo(id,out var info)) {
                observed.Add(new(message.Msg,info.Pointer.Pixel.X,info.Pointer.Pixel.Y,info.Pressure,info.TiltX,info.TiltY,info.Pointer.Flags,info.Flags,info.Mask));
                message.Result=IntPtr.Zero;
                return;
            }
        }
        base.WndProc(ref message);
    }
    private async Task Verify()
    {
        try {
            await Task.Delay(350);
            Require(GetForegroundWindow()==Handle,"The diagnostic must own the foreground; no input was sent elsewhere.");
            var origin=PointToScreen(Point.Empty);
            var bounds=new PenRectangle(origin.X,origin.Y,ClientSize.Width,ClientSize.Height);
            var target=new PenDestination(new("native-test","Private test receiver","window",bounds.Width,bounds.Height),bounds,Handle.ToInt64(),(uint)Environment.ProcessId);
            platform.Resolve(target,true);
            await OnWorker(()=>{platform.Begin();creatorThread=Environment.CurrentManagedThreadId;});
            var first=new PenFrame(origin.X+150,origin.Y+220,256,21,-17,"down");
            await Send(first);
            RequireSample(PointerDown,first,"native pen down with pressure and tilt");
            var move=first with{X=origin.X+340,Y=origin.Y+280,Pressure=819,TiltX=-30,TiltY=12,Phase="move"};
            await Send(move);
            RequireSample(PointerUpdate,move,"native pen move with changing pressure and tilt");
            var up=move with{Pressure=0,Phase="up"};
            await Send(up);
            RequireSample(PointerUp,up,"native pen up reaches the same test receiver");
            Require((observed.Last(item=>item.Message==PointerUp).PointerFlags&InContact)==0,"pen up clears contact");
            await Send(up with{Phase="leave"});
            await OnWorker(platform.Reset);
            last=null;
            await Task.Delay(120);
            Require(observed.Count>0&&(observed[^1].PointerFlags&InContact)==0,"teardown leaves no pen contact in the receiver");
            Console.WriteLine("Native Windows Ink verification passed: cross-thread creation/injection, down, move, up, pressure, tilt, coordinates and release.");
            Result=0;
        } catch(Exception error) {
            Console.Error.WriteLine("Native Windows Ink verification failed: "+error.Message);
            Console.Error.WriteLine("Observed: "+System.Text.Json.JsonSerializer.Serialize(observed));
        } finally {
            if(last is not null) {
                try {platform.Inject(last with{Phase="up",Pressure=0,Cancelled=true});platform.Inject(last with{Phase="leave",Pressure=0,Cancelled=true});}catch(Exception error){Console.Error.WriteLine("Diagnostic release: "+error.Message);}
            }
            platform.Dispose();
            Close();
        }
    }
    private async Task Send(PenFrame frame)
    {
        Require(GetForegroundWindow()==Handle,"The test lost foreground; injection stopped.");
        Require(WindowFromPoint(new(frame.X,frame.Y))==Handle,"The test surface is covered; injection stopped.");
        var targetWindow=Handle;
        await OnWorker(()=>{
            Require(Environment.CurrentManagedThreadId!=creatorThread,"The injection worker must differ from the device creation worker.");
            Require(GetForegroundWindow()==targetWindow&&WindowFromPoint(new(frame.X,frame.Y))==targetWindow,"The diagnostic lost its target before worker dispatch; injection stopped.");
            platform.Inject(frame);
        });
        last=frame;
        await Task.Delay(110);
    }
    private static Task OnWorker(System.Action action)=>Task.Factory.StartNew(action,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private void RequireSample(int message,PenFrame expected,string label)
    {
        Require(observed.Any(sample=>sample.Message==message&&sample.X==expected.X&&sample.Y==expected.Y&&sample.Pressure==expected.Pressure&&sample.TiltX==expected.TiltX&&sample.TiltY==expected.TiltY&&(sample.Mask&13)==13),label);
        Console.WriteLine($"PASS {label}: ({expected.X}, {expected.Y}), pressure {expected.Pressure}, tilt ({expected.TiltX}, {expected.TiltY})");
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed record ObservedPen(int Message,int X,int Y,uint Pressure,int TiltX,int TiltY,uint PointerFlags,uint PenFlags,uint Mask);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetPointerPenInfo(uint pointer,out NativeWindowsPenPlatform.Native.PointerPenInfo info);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(NativeWindowsPenPlatform.Native.Point point);
}
