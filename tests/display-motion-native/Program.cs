using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try {
            if(args.SequenceEqual(["--self-test"])){PureTests.Run();return 0;}
            var options=Options.Parse(args);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            TargetPolicy.Require(options,Screen.AllScreens.Select(screen=>new DisplayTarget(screen.DeviceName,screen.Bounds,screen.Primary)).ToArray());
            using var form=new MotionWindow(options);
            Application.Run(form);
            return form.Failure?1:0;
        }catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}

internal sealed record Options(string Source,Rectangle Bounds,int Hertz,int Seconds)
{
    internal static Options Parse(string[] args)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var index=0;index<args.Length;index+=2){
            if(index+1>=args.Length||args[index] is not ("--source" or "--bounds" or "--hz" or "--seconds")||!values.TryAdd(args[index],args[index+1]))throw new ArgumentException(Usage);
        }
        if(!values.TryGetValue("--source",out var source)||!values.TryGetValue("--bounds",out var boundsText)||!values.TryGetValue("--hz",out var hertzText))throw new ArgumentException(Usage);
        if(source.Length is <1 or >80||source.Any(char.IsControl))throw new ArgumentException("Use the exact source name returned for the owned virtual display.");
        var coordinates=boundsText.Split(',');
        if(coordinates.Length!=4||coordinates.Any(item=>!int.TryParse(item,NumberStyles.Integer,CultureInfo.InvariantCulture,out _)))throw new ArgumentException("Bounds must be x,y,width,height in physical pixels.");
        var numbers=coordinates.Select(item=>int.Parse(item,CultureInfo.InvariantCulture)).ToArray();
        if(numbers[2] is <160 or >8192||numbers[3] is <160 or >8192||(long)numbers[2]*numbers[3]>16*1024*1024||Math.Abs((long)numbers[0])>65536||Math.Abs((long)numbers[1])>65536)throw new ArgumentException("The owned display bounds exceed the diagnostic limit.");
        if(!int.TryParse(hertzText,out var hertz)||hertz is not (60 or 120 or 165))throw new ArgumentException("Use --hz 60, 120, or 165.");
        var seconds=60;
        if(values.TryGetValue("--seconds",out var duration)&&(!int.TryParse(duration,out seconds)||seconds is <5 or >600))throw new ArgumentException("Use a duration between 5 and 600 seconds.");
        return new(source,new Rectangle(numbers[0],numbers[1],numbers[2],numbers[3]),hertz,seconds);
    }
    private const string Usage="Explicit target required: --source <owned virtual display source> --bounds x,y,width,height --hz 60|120|165 [--seconds 60]. Use --self-test for noninteractive checks.";
}

internal sealed record DisplayTarget(string Source,Rectangle Bounds,bool Primary);
internal static class TargetPolicy
{
    internal static void Require(Options options,IEnumerable<DisplayTarget> screens)
    {
        var matches=screens.Where(screen=>string.Equals(screen.Source,options.Source,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1||matches[0].Primary||matches[0].Bounds!=options.Bounds)throw new InvalidOperationException("The exact nonprimary owned-display source and bounds must still match Windows. No window was moved to another screen.");
    }
}

// Keeps only the newest due frame: time spent painting never creates a catch-up queue.
internal sealed class FramePacer(long frequency,int hertz,long started)
{
    internal long Period {get;}=Math.Max(1,frequency/hertz);
    internal long Next {get;private set;}=started;
    internal long Skipped {get;private set;}
    internal bool Consume(long now)
    {
        if(now<Next)return false;
        var elapsed=(now-Next)/Period;
        Skipped+=elapsed;
        Next+=(elapsed+1)*Period;
        return true;
    }
}
internal sealed class FrameGate
{
    private int pending;
    internal bool TryEnter()=>Interlocked.CompareExchange(ref pending,1,0)==0;
    internal void Leave()=>Volatile.Write(ref pending,0);
}

internal sealed class MotionWindow : Form
{
    private readonly Options options;
    private readonly CancellationTokenSource stop=new();
    private readonly FrameGate frameGate=new();
    private readonly Font headingFont=new("Segoe UI",24,FontStyle.Bold);
    private readonly Font counterFont=new("Consolas",40,FontStyle.Bold);
    private readonly Font noteFont=new("Segoe UI",15);
    private readonly Stopwatch elapsed=new();
    private Thread? worker;
    private long paints,reportedPaints,skippedUi;
    private double reportedAt,paintRate;
    private bool checkingDisplay,resourcesDisposed;
    internal bool Failure {get;private set;}

    internal MotionWindow(Options options)
    {
        this.options=options;
        Text="DeskWing owned-display motion test";
        FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual;
        Bounds=options.Bounds;
        TopMost=true;
        ShowInTaskbar=true;
        DoubleBuffered=true;
        BackColor=Color.Black;
        KeyPreview=true;
        KeyDown+=(_,eventArgs)=>{if(eventArgs.KeyCode==Keys.Escape)Close();};
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        Shown+=(_,_)=>Start();
    }
    protected override bool ShowWithoutActivation=>true;
    private void Start()
    {
        if(Bounds!=options.Bounds){Failure=true;Console.Error.WriteLine("Windows changed the diagnostic bounds. The test stopped.");Close();return;}
        elapsed.Start();
        worker=new Thread(Pace){Name="DeskWing motion diagnostic pacing",IsBackground=true};
        worker.Start();
        Console.WriteLine($"Motion diagnostic on explicitly selected nonprimary display: {options.Bounds.Width}×{options.Bounds.Height}, requested {options.Hertz} Hz, {options.Seconds} seconds. No monitor is created or captured.");
    }
    private void Pace()
    {
        try {
            using var timer=new PreciseWaiter();
            var pacer=new FramePacer(Stopwatch.Frequency,options.Hertz,Stopwatch.GetTimestamp());
            while(!stop.IsCancellationRequested){
                var now=Stopwatch.GetTimestamp();
                if(pacer.Consume(now)){
                    if(frameGate.TryEnter()){
                        try{BeginInvoke((System.Action)(()=>{
                            try{
                                if(stop.IsCancellationRequested)return;
                                if(elapsed.Elapsed.TotalSeconds>=options.Seconds){Close();return;}
                                Invalidate();Update();
                            }finally{frameGate.Leave();}
                        }));}catch(InvalidOperationException){frameGate.Leave();break;}
                    }else Interlocked.Increment(ref skippedUi);
                }
                timer.Wait(Math.Max(1,pacer.Next-Stopwatch.GetTimestamp()),Stopwatch.Frequency);
            }
        }catch(Exception error){
            if(!stop.IsCancellationRequested){
                Failure=true;Console.Error.WriteLine("Motion pacing failed: "+error.Message);
                try{BeginInvoke((System.Action)Close);}catch(InvalidOperationException){}
            }
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if(resourcesDisposed||stop.IsCancellationRequested)return;
        if(Bounds!=options.Bounds){Failure=true;Console.Error.WriteLine("The diagnostic window moved or resized outside its explicit target. It stopped before painting.");Close();return;}
        base.OnPaint(e);
        var seconds=elapsed.Elapsed.TotalSeconds;
        paints++;
        if(seconds-reportedAt>=1){
            paintRate=(paints-reportedPaints)/(seconds-reportedAt);reportedPaints=paints;reportedAt=seconds;
            Console.WriteLine($"MOTION requested={options.Hertz} painted={paintRate:F1}/s total={paints} dropped-ui={Interlocked.Read(ref skippedUi)} elapsed={seconds:F1}s");
            CheckTarget();
            if(stop.IsCancellationRequested)return;
        }
        e.Graphics.Clear(Color.FromArgb(12,18,26));
        var width=ClientSize.Width;var height=ClientSize.Height;
        var tile=Math.Max(20,width/32);
        using(var stripe=new SolidBrush(Color.FromArgb(45,65,85)))for(var x=0;x<width;x+=tile*2)e.Graphics.FillRectangle(stripe,x,0,tile,height);
        var position=(int)((seconds*width/2)%Math.Max(1,width));
        e.Graphics.FillRectangle(Brushes.White,position,0,Math.Max(6,width/100),height);
        e.Graphics.FillEllipse(Brushes.Cyan,(float)(width*.5+Math.Sin(seconds*2)*width*.35)-24,(float)(height*.55+Math.Cos(seconds*3)*height*.23)-24,48,48);
        using(var panel=new SolidBrush(Color.FromArgb(235,6,9,14)))e.Graphics.FillRectangle(panel,24,24,Math.Min(width-48,960),230);
        e.Graphics.DrawString($"DeskWing · {options.Hertz} Hz motion target",headingFont,Brushes.White,new PointF(42,38));
        e.Graphics.DrawString(paints.ToString("D8",CultureInfo.InvariantCulture),counterFont,Brushes.Cyan,new PointF(42,90));
        e.Graphics.DrawString($"Painted {paintRate:F1}/s · UI frames dropped {Interlocked.Read(ref skippedUi)}",noteFont,Brushes.White,new PointF(42,160));
        e.Graphics.DrawString("Observe motion on the tablet. Paint rate is not decoded or presented FPS.",noteFont,Brushes.LightGray,new PointF(42,196));
        using(var border=new Pen(Color.Yellow,2))e.Graphics.DrawRectangle(border,1,1,Math.Max(1,width-3),Math.Max(1,height-3));
        e.Graphics.DrawString($"Full target {width}×{height} · Esc closes · Auto-close {options.Seconds}s",noteFont,Brushes.White,new PointF(24,Math.Max(260,height-40)));
    }
    private void DisplayChanged(object? sender,EventArgs args)
    {
        try{if(IsHandleCreated&&!stop.IsCancellationRequested)BeginInvoke((System.Action)CheckTarget);}catch(InvalidOperationException){}
    }
    private void CheckTarget()
    {
        if(checkingDisplay||stop.IsCancellationRequested)return;
        checkingDisplay=true;
        try{TargetPolicy.Require(options,Screen.AllScreens.Select(screen=>new DisplayTarget(screen.DeviceName,screen.Bounds,screen.Primary)));}
        catch(InvalidOperationException error){Failure=true;Console.Error.WriteLine(error.Message);Close();}
        finally{checkingDisplay=false;}
    }
    protected override void OnFormClosing(FormClosingEventArgs e){stop.Cancel();base.OnFormClosing(e);}
    protected override void Dispose(bool disposing)
    {
        if(disposing&&!resourcesDisposed){
            resourcesDisposed=true;
            stop.Cancel();SystemEvents.DisplaySettingsChanged-=DisplayChanged;
            worker?.Join(100);
            headingFont.Dispose();counterFont.Dispose();noteFont.Dispose();stop.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class PreciseWaiter : IDisposable
{
    private readonly SafeWaitHandle timer;
    internal PreciseWaiter()
    {
        timer=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x1F0003);
        if(timer.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"A high-resolution waitable timer is unavailable.");
    }
    internal void Wait(long ticks,long frequency)
    {
        var due=-Math.Max(1,(long)Math.Ceiling(ticks*(10000000d/frequency)));
        if(!SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,false))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(WaitForSingleObject(timer,100)!=0)throw new InvalidOperationException("The diagnostic frame timer did not signal within its bound.");
    }
    public void Dispose()=>timer.Dispose();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long due,int period,IntPtr callback,IntPtr argument,[MarshalAs(UnmanagedType.Bool)]bool resume);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeWaitHandle handle,uint milliseconds);
}

internal static class PureTests
{
    internal static void Run()
    {
        var options=Options.Parse(["--source","owned-test","--bounds","-1280,0,1280,800","--hz","120"]);
        TargetPolicy.Require(options,[new("owned-test",new(-1280,0,1280,800),false)]);
        Reject(()=>TargetPolicy.Require(options,[new("owned-test",options.Bounds,true)]),"primary display");
        Reject(()=>TargetPolicy.Require(options,[new("other",options.Bounds,false)]),"source mismatch");
        Reject(()=>TargetPolicy.Require(options,[new("owned-test",new(-1280,0,1280,720),false)]),"bounds change");
        Reject(()=>Options.Parse(["--source","owned-test","--bounds","0,0,99999,99999","--hz","120"]),"unbounded dimensions");
        Reject(()=>Options.Parse(["--source","owned-test","--bounds","0,0,1280,800","--hz","1000"]),"unsupported pacing");
        Reject(()=>Options.Parse(["--source","owned-test","--bounds","0,0,1280,800","--hz","60","--seconds","999999"]),"unbounded duration");
        foreach(var hertz in new[]{60,120,165}){
            var frequency=1980000L;var pacer=new FramePacer(frequency,hertz,0);var count=0;
            for(var now=0L;now<frequency;now++)if(pacer.Consume(now))count++;
            Require(count==hertz,$"{hertz} Hz one-second schedule");
            var delayed=new FramePacer(frequency,hertz,0);Require(delayed.Consume(0),"initial frame");
            Require(delayed.Consume(delayed.Period*10),"latest delayed frame");
            Require(delayed.Skipped==9&&!delayed.Consume(delayed.Period*10),"no delayed catch-up queue");
        }
        var gate=new FrameGate();Require(gate.TryEnter(),"first frame queues");
        for(var attempt=0;attempt<1000;attempt++)Require(!gate.TryEnter(),"pending frame bounds queue");
        gate.Leave();Require(gate.TryEnter(),"completed frame releases gate");gate.Leave();
        Console.WriteLine("PASS owned-source/bounds/nonprimary checks, bounded geometry/duration, 60/120/165 Hz pacing, dropped backlog and single queued repaint.");
    }
    private static void Reject(System.Action action,string label){try{action();}catch(Exception error)when(error is ArgumentException or InvalidOperationException){return;}throw new InvalidOperationException("Expected rejection: "+label);}
    private static void Require(bool value,string label){if(!value)throw new InvalidOperationException("Failed: "+label);}
}
