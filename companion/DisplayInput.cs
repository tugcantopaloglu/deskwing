using System.ComponentModel;
using Timer=System.Threading.Timer;

namespace TabletDeck;

internal sealed record DisplayInputSettings(bool Enabled=false,string TargetIdentity="");
internal sealed record DisplayInputStatus(bool Supported,bool Enabled,bool Active,string Mode,string? Error,string SourceName="",string TargetIdentity="",int LeaseTimeoutMs=WindowsPen.TimeoutMs,int MaxBatch=WindowsPen.MaximumBatch);
internal sealed record DisplayInputRequest(string Operation="",string VideoLease="",string Session="",string Lease="",long Sequence=0,double ElapsedMs=0,PenSample[]? Samples=null,string Mode="pen");

// Input has its own PC permission and short lease. A video lease never implicitly authorizes input.
// No board/configuration file is read or written by this service.
internal sealed class DisplayInput : IDisposable
{
    private readonly object gate=new();
    private readonly Func<string,string,DisplayVideoTarget> captureVideo;
    private readonly Func<DisplayVideoTarget> capturePermissionTarget;
    private readonly WindowsPen pen;
    private readonly GuardedPlatform platform;
    private readonly Timer? timer;
    private string owner="",videoLease="",mode="view";
    private DisplayVideoTarget? target,permissionTarget;
    private bool enabled,disposed,shutdown;
    private string? error;

    internal DisplayInput(DisplayStream video,ExtendedDisplay display):this(video.CaptureInputTarget,display.CaptureVideoTarget,new NativeWindowsPenPlatform(),()=>Environment.TickCount64,true){}
    internal DisplayInput(Func<string,string,DisplayVideoTarget> captureVideo,Func<DisplayVideoTarget> capturePermissionTarget,IWindowsPenPlatform platform,Func<long> clock,bool startTimer=false)
    {
        this.captureVideo=captureVideo;this.capturePermissionTarget=capturePermissionTarget;
        this.platform=new GuardedPlatform(platform,ValidateBoundTarget);
        pen=new WindowsPen(this.platform,clock);
        if(startTimer)timer=new Timer(_=>{lock(gate){if(!disposed)Check();}},null,100,100);
    }
    internal bool Active {get{lock(gate){Check();return owner.Length>0;}}}
    internal DisplayInputStatus Status {get{lock(gate){Check();return Snapshot();}}}
    private DisplayInputStatus Snapshot()
    {
        DisplayVideoTarget? current=null;
        try{current=capturePermissionTarget();}catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception or IOException){}
        return new(platform.Supported,enabled,owner.Length>0,mode,error,current?.SourceName??"",current is null?"":Identity(current));
    }
    private static string Identity(DisplayVideoTarget target)=>target.Monitor.ToString("N")+":"+target.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal DisplayInputStatus Configure(DisplayInputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(settings.Enabled&&shutdown)throw new InvalidOperationException("The companion is closing. Reopen it before allowing display input.");
            if(settings.Enabled&&!platform.Supported)throw new InvalidOperationException("This PC does not support Windows pen input.");
            if(!settings.Enabled){enabled=false;permissionTarget=null;Cancel(null);return Snapshot();}
            var current=capturePermissionTarget();
            if(current.Invalidated.IsCancellationRequested||settings.TargetIdentity!=Identity(current))throw new InvalidOperationException("The Second display changed. Review its current input permission on the PC.");
            Cancel(null);permissionTarget=current;enabled=true;return Snapshot();
        }
    }

    internal object Execute(DisplayInputRequest request,string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(string.IsNullOrWhiteSpace(principal)||principal.Length>128)throw new ArgumentException("A paired tablet is required.");
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(request.Operation is "end" or "cancel"){
                if(owner==principal&&videoLease==request.VideoLease){pen.Release(principal,request.Lease);if(!pen.Active)ClearBinding();}
                return new{ok=true};
            }
            Check();
            if(shutdown)throw new InvalidOperationException("The companion is closing. Start a fresh display after reconnecting.");
            if(!enabled)throw new InvalidOperationException("Allow pen and touch input for Second display in the PC settings first.");
            if(request.Operation=="begin")return Begin(request,principal);
            if(request.Operation is not ("samples" or "renew"))throw new ArgumentException("Unknown display input operation.");
            if(owner!=principal||videoLease!=request.VideoLease||owner.Length==0)throw new InvalidOperationException("This display input session ended. Choose Pen or Touch again.");
            try{
                ValidateBoundTarget();
                var samples=request.Samples;
                if(samples is {Length:>WindowsPen.MaximumBatch})throw new ArgumentException("Send at most 32 display input samples.");
                if(mode=="touch"&&samples is not null){
                    foreach(var sample in samples)ValidateTouch(sample);
                    samples=samples.Select(sample=>sample with{Pressure=sample.Phase is "down" or "move"?.5:0,TiltX=0,TiltY=0,Barrel=false,Eraser=false}).ToArray();
                }
                return pen.Execute(new(request.Operation,request.Session,request.Lease,request.Sequence,request.ElapsedMs,samples),principal);
            }catch(Exception ex) when(ex is InvalidOperationException or Win32Exception or IOException){Cancel(ex.Message);throw;}
        }
    }

    private WindowsPen.PenLease Begin(DisplayInputRequest request,string principal)
    {
        if(owner.Length>0)throw new InvalidOperationException("Another display input session is active. Stop it before starting again.");
        if(request.Mode is not ("pen" or "touch"))throw new ArgumentException("Choose Pen or single-finger Touch input.");
        if(request.VideoLease is not {Length:48}||!request.VideoLease.All(char.IsAsciiHexDigit))throw new ArgumentException("Open a fresh display video session before enabling input.");
        var destination=captureVideo(principal,request.VideoLease);
        if(permissionTarget is null||destination!=permissionTarget)throw new InvalidOperationException("Allow input for this exact Second display in the PC settings first.");
        owner=principal;videoLease=request.VideoLease;target=destination;mode=request.Mode;
        try{
            ValidateBoundTarget();
            pen.Configure(new(true,"display:"+destination.SourceName));
            var result=(WindowsPen.PenLease)pen.Execute(new("begin",request.Session),principal);
            error=null;return result;
        }catch{Cancel(null);throw;}
    }

    private DisplayVideoTarget ValidateBoundTarget()
    {
        ValidatePermission();
        if(owner.Length==0||target is null||target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("The shared display or its input permission ended.");
        var current=captureVideo(owner,videoLease);
        if(current!=target)throw new InvalidOperationException("The shared display changed. Start a fresh display and input session.");
        return current;
    }
    private static void ValidateTouch(PenSample sample)
    {
        if(sample is null||!double.IsFinite(sample.X)||!double.IsFinite(sample.Y)||!double.IsFinite(sample.Pressure)||sample.Pressure is <0 or >1||!double.IsFinite(sample.TiltX)||!double.IsFinite(sample.TiltY)||sample.TiltX is <-90 or >90||sample.TiltY is <-90 or >90)throw new ArgumentException("Touch samples must have finite coordinates and valid pressure/tilt fields.");
    }
    private void Check()
    {
        if(disposed)return;
        try{
            if(enabled)ValidatePermission();
            if(owner.Length==0)return;
            ValidateBoundTarget();if(!pen.Active){error=pen.Status.Error;ClearBinding();}
        }
        catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception or IOException){Cancel(ex.Message);}
    }
    private void ValidatePermission()
    {
        if(shutdown||!enabled||permissionTarget is null||permissionTarget.Invalidated.IsCancellationRequested){enabled=false;permissionTarget=null;throw new InvalidOperationException("The shared display or its input permission ended.");}
        DisplayVideoTarget current;
        try{current=capturePermissionTarget();}
        catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or Win32Exception or IOException){enabled=false;permissionTarget=null;throw;}
        if(current!=permissionTarget){enabled=false;permissionTarget=null;throw new InvalidOperationException("The Second display changed. Review and allow its input again on the PC.");}
    }
    internal void Expire(){lock(gate)Check();}
    internal void Revoke(string principal){lock(gate){if(!disposed&&owner==principal)Cancel("The tablet pairing was revoked.");}}
    internal void Shutdown(){lock(gate){if(disposed||shutdown)return;shutdown=true;enabled=false;permissionTarget=null;Cancel("The companion is closing.");}}
    private void Cancel(string? reason){pen.Configure(new(false));ClearBinding();error=reason;}
    private void ClearBinding(){owner="";videoLease="";target=null;mode="view";}
    public void Dispose(){lock(gate){if(disposed)return;Shutdown();disposed=true;timer?.Dispose();pen.Dispose();}}

    // The normal Windows pen platform retains ordinary-window/elevation and native release checks.
    // This wrapper narrows its targets to one validated owned virtual display for every native call.
    private sealed class GuardedPlatform(IWindowsPenPlatform inner,Func<DisplayVideoTarget> authorize) : IWindowsPenPlatform
    {
        public bool Supported=>inner.Supported;
        public PenDestination[] Targets(){var target=authorize();return inner.Targets().Where(item=>Matches(item,target)).ToArray();}
        public PenDestination Resolve(PenDestination destination,bool requireForeground){var target=authorize();if(!Matches(destination,target))throw new InvalidOperationException("Input is restricted to the shared Second display.");var resolved=inner.Resolve(destination,requireForeground);if(!Matches(resolved,target))throw new InvalidOperationException("The shared display geometry changed.");return resolved;}
        public void Begin(){authorize();inner.Begin();}
        public void Inject(PenFrame frame){if(!frame.Cancelled)authorize();inner.Inject(frame);}
        public void Reset()=>inner.Reset();
        public void Dispose()=>inner.Dispose();
        private static bool Matches(PenDestination destination,DisplayVideoTarget target)=>destination.Info.Kind=="display"&&destination.Info.Id=="display:"+target.SourceName&&destination.Window==0&&destination.Bounds==new PenRectangle(target.X,target.Y,target.Width,target.Height);
    }
}
