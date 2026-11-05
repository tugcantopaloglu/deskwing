using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Timer=System.Threading.Timer;

namespace TabletDeck;

internal sealed record PenSettings(bool Enabled=false,string TargetId="");
internal sealed record PenTarget(string Id,string Label,string Kind,int Width,int Height);
internal sealed record PenStatus(bool Supported,bool Enabled,bool Active,PenTarget? Target,string? Error,int LeaseTimeoutMs=1200,int MaxBatch=32);
internal sealed record PenSample(string Phase="",double X=0,double Y=0,double Pressure=0,double TiltX=0,double TiltY=0,bool Barrel=false,bool Eraser=false);
internal sealed record PenRequest(string Operation="",string Session="",string Lease="",long Sequence=0,double ElapsedMs=0,PenSample[]? Samples=null);
internal readonly record struct PenRectangle(int X,int Y,int Width,int Height);
internal sealed record PenDestination(PenTarget Info,PenRectangle Bounds,long Window=0,uint Process=0);
internal sealed record PenFrame(int X,int Y,uint Pressure,int TiltX,int TiltY,string Phase,bool Barrel=false,bool Eraser=false,bool Cancelled=false);

// The service owns protocol/lease validation; the platform is the only component allowed to inject.
internal interface IWindowsPenPlatform : IDisposable
{
    bool Supported {get;}
    PenDestination[] Targets();
    PenDestination Resolve(PenDestination target,bool requireForeground);
    void Begin();
    void Inject(PenFrame frame);
    void Reset();
}

internal sealed class WindowsPen : IDisposable
{
    internal const int TimeoutMs=1200,MaximumBatch=32;
    internal sealed record PenLease(string Lease,int ExpiresInMs=TimeoutMs,int MaxBatch=MaximumBatch,long Sequence=0);
    private readonly object gate=new();
    private readonly IWindowsPenPlatform platform;
    private readonly Func<long> now;
    private readonly Timer? timer;
    private PenDestination? target,leaseTarget;
    private PenFrame? last;
    private string owner="",lease="";
    private readonly HashSet<string> recentSessions=new(StringComparer.Ordinal);
    private readonly Queue<string> sessionOrder=new();
    private long expires,started,sequence;
    private bool enabled,contact,disposed;
    private string? error;

    public WindowsPen():this(new NativeWindowsPenPlatform(),()=>Environment.TickCount64,true) { }
    internal WindowsPen(IWindowsPenPlatform platform,Func<long> now,bool startTimer=false)
    {
        this.platform=platform;this.now=now;
        if(startTimer)timer=new Timer(_=>{lock(gate){if(!disposed)CheckLease();}},null,100,100);
    }
    public bool Active {get{lock(gate){CheckLease();return lease.Length>0;}}}
    public PenStatus Status {get{lock(gate){CheckLease();return Snapshot();}}}
    private PenStatus Snapshot()=>new(platform.Supported,enabled,lease.Length>0,target?.Info,error);
    public object AdminState(){lock(gate){CheckLease();return new{status=Snapshot(),targets=platform.Supported?platform.Targets().Select(t=>t.Info).ToArray():[]};}}
    public PenStatus Configure(PenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!settings.Enabled){Stop(null);enabled=false;return Snapshot();}
            if(!platform.Supported)throw new InvalidOperationException("Windows pen input requires Windows 10 version 1809 or later on a 64-bit PC.");
            var selected=platform.Targets().FirstOrDefault(t=>t.Info.Id==settings.TargetId)??throw new ArgumentException("Choose an available display or application window on this PC.");
            Stop(null);target=selected;enabled=true;error=null;return Snapshot();
        }
    }
    public object Execute(PenRequest request,string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(string.IsNullOrWhiteSpace(principal)||principal.Length>128)throw new ArgumentException("A paired tablet is required.");
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            CheckLease();
            if(request.Operation is "end" or "cancel"){Release(principal,request.Lease);return new{ok=true};}
            if(request.Operation=="begin")return Begin(request,principal);
            if(request.Operation is not ("samples" or "renew"))throw new ArgumentException("Unknown pen operation.");
            if(lease.Length==0||principal!=owner||request.Lease!=lease)throw new InvalidOperationException("This pen session ended. Tap Start on the tablet to reconnect.");
            if(request.Sequence<=sequence||request.Sequence>9007199254740991)throw new ArgumentException("This pen packet is out of order. Start a fresh pen session.");
            var age=now()-started;
            if(!double.IsFinite(request.ElapsedMs)||request.ElapsedMs<0||request.ElapsedMs>age+250||request.ElapsedMs<age-750)throw new ArgumentException("This pen packet is too old. Start a fresh pen session.");
            var samples=request.Samples??[];
            if(request.Operation=="samples"&&samples.Length is <1 or >MaximumBatch||request.Operation=="renew"&&samples.Length!=0)throw new ArgumentException("Send 1–32 pen samples or an empty renewal.");
            var destination=CurrentTarget();
            var nextContact=contact;
            var frames=new List<PenFrame>(samples.Length);
            foreach(var sample in samples){ValidateSample(sample,ref nextContact);frames.Add(Map(sample,destination.Bounds));}
            // Consume sequence before native input: a failed or partially accepted batch can never replay.
            sequence=request.Sequence;
            try {
                foreach(var frame in frames){
                    CurrentTarget(); // Focus/window geometry can change while a batch is injected.
                    platform.Inject(frame);last=frame;contact=frame.Phase is "down" or "move";
                }
                expires=now()+TimeoutMs;return new{ok=true,sequence,expiresInMs=TimeoutMs};
            }catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception or IOException){Stop(ex.Message);throw new InvalidOperationException(error??"Windows stopped accepting pen input.",ex);}
        }
    }
    private PenLease Begin(PenRequest request,string principal)
    {
        if(!enabled||target is null)throw new InvalidOperationException("Enable Pen tablet and choose its target in the PC settings first.");
        if(lease.Length>0)throw new InvalidOperationException("Another pen session is active. Stop it before starting a new one.");
        if(request.Session is not {Length:>=16 and <=80}||!request.Session.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))throw new ArgumentException("Start a fresh pen session from the tablet.");
        var sessionKey=principal+":"+request.Session;
        if(recentSessions.Contains(sessionKey))throw new ArgumentException("This pen start request was already used. Start a fresh session from the tablet.");
        var resolved=platform.Resolve(target,true);
        try{platform.Begin();}catch(Exception ex) when(ex is Win32Exception or InvalidOperationException){error=ex.Message;throw;}
        leaseTarget=resolved;target=resolved;owner=principal;
        recentSessions.Add(sessionKey);sessionOrder.Enqueue(sessionKey);while(sessionOrder.Count>1024)recentSessions.Remove(sessionOrder.Dequeue());
        lease=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));started=now();expires=started+TimeoutMs;sequence=0;last=null;contact=false;error=null;
        return new(lease);
    }
    private PenDestination CurrentTarget()
    {
        try{
            var current=platform.Resolve(leaseTarget??throw new InvalidOperationException("Choose a pen target again on the PC."),true);
            if(current.Bounds!=leaseTarget.Bounds)throw new InvalidOperationException("The pen target moved or changed size. Tap Start again after arranging the PC window.");
            return current;
        }catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception){Stop(ex.Message);throw;}
    }
    private static void ValidateSample(PenSample sample,ref bool touching)
    {
        if(sample is null||!double.IsFinite(sample.X)||!double.IsFinite(sample.Y)||!double.IsFinite(sample.Pressure)||!double.IsFinite(sample.TiltX)||!double.IsFinite(sample.TiltY)||sample.Pressure is <0 or >1||sample.TiltX is <-90 or >90||sample.TiltY is <-90 or >90)throw new ArgumentException("Pen coordinates, pressure and tilt must be finite and within their supported ranges.");
        switch(sample.Phase){
            case "down" when !touching:touching=true;break;
            case "move" when touching:break;
            case "up" when touching:touching=false;break;
            case "hover" when !touching:break;
            case "leave":touching=false;break;
            default:throw new ArgumentException("Pen sample order is invalid. Start a fresh stroke.");
        }
    }
    internal static PenFrame Map(PenSample sample,PenRectangle bounds)
    {
        if(bounds.Width<1||bounds.Height<1)throw new ArgumentException("The pen target has no drawable area.");
        return new(bounds.X+(int)Math.Round(Math.Clamp(sample.X,0,1)*(bounds.Width-1)),bounds.Y+(int)Math.Round(Math.Clamp(sample.Y,0,1)*(bounds.Height-1)),sample.Phase is "down" or "move"?(uint)Math.Round(sample.Pressure*1024):0,(int)Math.Round(sample.TiltX),(int)Math.Round(sample.TiltY),sample.Phase,sample.Barrel,sample.Eraser);
    }
    internal void Expire(){lock(gate)CheckLease();}
    private void CheckLease()
    {
        if(lease.Length==0)return;
        if(now()>=expires){Stop("The tablet stopped sending pen input. Tap Start to reconnect.");return;}
        try{CurrentTarget();}catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception){/* CurrentTarget already cancelled safely. */}
    }
    public void Release(string principal,string token){lock(gate){if(lease.Length>0&&owner==principal&&lease==token)Stop(null);}}
    public void ReleaseOwner(string principal){lock(gate){if(owner==principal)Stop(null);}}
    public void CancelAll(string reason){lock(gate)Stop(reason);}
    private void Stop(string? reason)
    {
        // Invalidate ownership first; neither a delayed batch nor a failed native cleanup can renew it.
        lease="";owner="";leaseTarget=null;error=reason;
        try{
            if(last is not null){
                if(contact)platform.Inject(last with{Phase="up",Pressure=0,Barrel=false,Eraser=false,Cancelled=true});
                platform.Inject(last with{Phase="leave",Pressure=0,Barrel=false,Eraser=false,Cancelled=true});
            }
        }catch(Exception ex) when(ex is InvalidOperationException or Win32Exception or IOException){error="Pen input stopped; Windows rejected its final release. The synthetic pen was removed. "+ex.Message;}
        finally{contact=false;last=null;platform.Reset();}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;timer?.Dispose();Stop(null);platform.Dispose();}}
}

internal sealed class NativeWindowsPenPlatform : IWindowsPenPlatform
{
    private IntPtr device;
    private bool touching;
    private PenDestination? selected;
    public bool Supported=>Environment.Is64BitProcess&&OperatingSystem.IsWindowsVersionAtLeast(10,0,17763);
    private PenDestination[] DisplayTargets()
    {
        if(!Supported)return [];
        using var dpi=new DpiScope();
        var targets=new List<PenDestination>();
        Native.EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr monitor,IntPtr dc,ref Native.Rect rectangle,IntPtr data)=>{
            var info=new Native.MonitorInfo{Size=Marshal.SizeOf<Native.MonitorInfo>()};
            if(Native.GetMonitorInfo(monitor,ref info)){var bounds=Bounds(info.Monitor);targets.Add(new(new("display:"+info.Device,$"Display {targets.Count+1} · {bounds.Width} × {bounds.Height}"+(info.Flags==1?" (main)":""),"display",bounds.Width,bounds.Height),bounds));}return true;
        },IntPtr.Zero);
        return targets.ToArray();
    }
    public PenDestination[] Targets()
    {
        if(!Supported)return [];
        using var dpi=new DpiScope();
        var targets=DisplayTargets().ToList();
        Native.EnumWindows((window,data)=>{
            if(targets.Count>=132)return false;
            Native.GetWindowThreadProcessId(window,out var process);
            if(process==Environment.ProcessId||!Native.IsWindowVisible(window)||Native.IsIconic(window)||Native.GetWindow(window,4)!=IntPtr.Zero)return true;
            if(Native.DwmGetWindowAttribute(window,14,out int cloaked,sizeof(int))==0&&cloaked!=0)return true;
            var title=new StringBuilder(161);Native.GetWindowText(window,title,title.Capacity);
            if(string.IsNullOrWhiteSpace(title.ToString())||!ClientBounds(window,out var bounds))return true;
            var id=$"window:{window.ToInt64():X}:{process}";
            targets.Add(new(new(id,title.ToString(),"window",bounds.Width,bounds.Height),bounds,window.ToInt64(),process));return true;
        },IntPtr.Zero);
        return targets.ToArray();
    }
    public PenDestination Resolve(PenDestination target,bool requireForeground)
    {
        using var dpi=new DpiScope();
        if(target.Info.Kind=="display"){
            var current=DisplayTargets().FirstOrDefault(t=>t.Info.Id==target.Info.Id)??throw new InvalidOperationException("The selected display disconnected. Choose a pen target again on the PC.");
            selected=current;return current;
        }
        var window=new IntPtr(target.Window);
        Native.GetWindowThreadProcessId(window,out var process);
        if(!Native.IsWindow(window)||!Native.IsWindowVisible(window)||Native.IsIconic(window)||process!=target.Process||!ClientBounds(window,out var bounds))throw new InvalidOperationException("The selected application window is unavailable. Choose a pen target again on the PC.");
        if(requireForeground&&Native.GetAncestor(Native.GetForegroundWindow(),2)!=window)throw new InvalidOperationException("Focus the selected PC application, then tap Start on the tablet.");
        RequireOrdinaryWindow(window);
        selected=target with{Bounds=bounds,Info=target.Info with{Width=bounds.Width,Height=bounds.Height}};return selected;
    }
    public void Begin()
    {
        Reset();
        if(!Supported)throw new InvalidOperationException("This PC does not support Windows pen injection.");
        device=Native.CreateSyntheticPointerDevice(3,1,3);
        if(device==IntPtr.Zero)throw Error("create a Windows pen");
    }
    public void Inject(PenFrame frame)
    {
        if(device==IntPtr.Zero)throw new InvalidOperationException("The Windows pen session ended.");
        using var dpi=new DpiScope();
        if(!frame.Cancelled){
            var hit=Native.GetAncestor(Native.WindowFromPoint(new(frame.X,frame.Y)),2);
            if(selected?.Window is >0&&hit!=new IntPtr(selected.Window))throw new InvalidOperationException("Another window covers the pen target. Uncover and focus the selected application, then tap Start.");
            RequireOrdinaryWindow(hit);
        }
        var input=Packet(frame,touching,Native.GetSystemMetrics(76),Native.GetSystemMetrics(77));
        for(var attempt=0;;attempt++){
            if(Native.InjectSyntheticPointerInput(device,ref input,1))break;
            var error=Marshal.GetLastWin32Error();
            if(error!=21||attempt>=3)throw Error("send pen input",error);
            Thread.Sleep(1); // Retry the exact same frame after a Windows timestamp collision.
        }
        touching=frame.Phase is "down" or "move";
    }
    internal static Native.PointerTypeInfo Packet(PenFrame frame,bool touching,int virtualLeft,int virtualTop)
    {
        var flags=frame.Phase switch{
            "down"=>0x00010000u|0x2u|0x4u|0x10u,
            "move"=>0x00020000u|0x2u|0x4u|0x10u,
            "up"=>0x00040000u|0x2u,
            "hover"=>0x00020000u|0x2u,
            "leave"=>touching?0x00040000u:0x00020000u,
            _=>throw new ArgumentException("Unsupported pen sample.")
        };
        if(frame.Barrel&&frame.Phase is "down" or "move")flags=(flags&~0x10u)|0x20u;
        if(frame.Cancelled)flags|=0x8000;
        // Win32 synthetic injection takes pixels from the virtual-screen origin, not normalized SendInput units.
        var point=VirtualPoint(frame.X,frame.Y,virtualLeft,virtualTop);
        return new Native.PointerTypeInfo{Type=3,Pen=new(){Pointer=new(){Type=3,Id=1,Flags=flags,Pixel=point},Flags=(frame.Barrel?1u:0)|(frame.Eraser?6u:0),Mask=13,Pressure=frame.Pressure,TiltX=frame.TiltX,TiltY=frame.TiltY}};
    }
    internal static Native.Point VirtualPoint(int x,int y,int virtualLeft,int virtualTop)=>new(x-virtualLeft,y-virtualTop);
    private static InvalidOperationException Error(string operation,int? code=null)
    {
        var error=code??Marshal.GetLastWin32Error();
        return new($"Windows could not {operation} (error {error}). Keep the target app running normally, without Administrator privileges, and return to the unlocked desktop.",new Win32Exception(error));
    }
    private static PenRectangle Bounds(Native.Rect value)=>new(value.Left,value.Top,value.Right-value.Left,value.Bottom-value.Top);
    private static bool ClientBounds(IntPtr window,out PenRectangle bounds)
    {
        bounds=default;
        if(!Native.GetClientRect(window,out var area)||area.Right<1||area.Bottom<1)return false;
        var origin=new Native.Point(0,0);if(!Native.ClientToScreen(window,ref origin))return false;
        bounds=new(origin.X,origin.Y,area.Right,area.Bottom);return true;
    }
    private static void RequireOrdinaryWindow(IntPtr window)
    {
        if(window==IntPtr.Zero)return;
        Native.GetWindowThreadProcessId(window,out var pid);
        using var process=Native.OpenProcess(0x1000,false,pid);
        if(process.IsInvalid)throw new InvalidOperationException("Windows cannot inspect the target app. Use an ordinary desktop app without Administrator privileges.");
        if(!Native.OpenProcessToken(process,8,out var token))throw Error("check the target app");
        using(token){
            if(!Native.GetTokenInformation(token,20,out var elevation,sizeof(int),out _))throw Error("check the target app");
            if(elevation!=0)throw new InvalidOperationException("Pen input is unavailable for apps running as Administrator. Restart the target app normally.");
        }
    }
    public void Reset(){if(device!=IntPtr.Zero){Native.DestroySyntheticPointerDevice(device);device=IntPtr.Zero;}touching=false;}
    public void Dispose()=>Reset();
    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr previous=Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose(){if(previous!=IntPtr.Zero)Native.SetThreadDpiAwarenessContext(previous);}
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;public Point(int x,int y){X=x;Y=y;}}
        [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
        [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo {public uint Type,Id,Frame,Flags;public IntPtr Source,Window;public Point Pixel,Himetric,PixelRaw,HimetricRaw;public uint Time,History;public int InputData;public uint Keys;public ulong Performance;public uint ButtonChange;}
        [StructLayout(LayoutKind.Sequential)] internal struct PointerPenInfo {public PointerInfo Pointer;public uint Flags,Mask,Pressure,Rotation;public int TiltX,TiltY;}
        // POINTER_TYPE_INFO's union includes the larger POINTER_TOUCH_INFO even for PT_PEN (x64 size152).
        [StructLayout(LayoutKind.Explicit,Size=152)] internal struct PointerTypeInfo {[FieldOffset(0)]public uint Type;[FieldOffset(8)]public PointerPenInfo Pen;}
        internal delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref Rect rectangle,IntPtr data);
        internal delegate bool WindowCallback(IntPtr window,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr CreateSyntheticPointerDevice(uint type,uint count,uint feedback);
        [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool InjectSyntheticPointerInput(IntPtr device,ref PointerTypeInfo pointers,uint count);
        [DllImport("user32.dll")]internal static extern void DestroySyntheticPointerDevice(IntPtr device);
        [DllImport("user32.dll")]internal static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")]internal static extern bool EnumWindows(WindowCallback callback,IntPtr data);
        [DllImport("user32.dll")]internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]internal static extern IntPtr GetWindow(IntPtr window,uint command);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
        [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")]internal static extern bool GetClientRect(IntPtr window,out Rect rectangle);
        [DllImport("user32.dll")]internal static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")]internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")]internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("dwmapi.dll")]internal static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out int value,int size);
        [DllImport("kernel32.dll",SetLastError=true)]internal static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access,bool inherit,uint process);
        [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process,uint access,out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
        [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool GetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token,int kind,out int elevation,int size,out int needed);
    }
}
