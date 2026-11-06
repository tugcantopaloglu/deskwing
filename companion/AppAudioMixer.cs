using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TabletDeck;

public sealed record AppAudioSession(string Id,string Name,string Device,double Percent,bool Muted,bool Active,int ProcessId=0);
internal interface IAppAudioBackend
{
    IReadOnlyList<AppAudioSession> List();
    void Set(string id,double? percent,bool? muted);
}
public sealed class AppAudioMixer : IDisposable
{
    private readonly BlockingCollection<Action> queue=new(128);
    private readonly Thread worker;
    private readonly IAppAudioBackend backend;
    public AppAudioMixer():this(new WindowsAppAudioBackend()) { }
    internal AppAudioMixer(IAppAudioBackend backend)
    {
        this.backend=backend;
        worker=new Thread(()=>{foreach(var job in queue.GetConsumingEnumerable())job();}) {IsBackground=true,Name="DeskWing audio mixer"};
        worker.SetApartmentState(ApartmentState.MTA);worker.Start();
    }
    private Task<T> Dispatch<T>(Func<T> operation)
    {
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!queue.TryAdd(()=>{try{result.SetResult(operation());}catch(Exception ex){result.SetException(ex);}}))throw new InvalidOperationException("The app mixer is busy. Try again.");
        return result.Task;
    }
    public Task<IReadOnlyList<AppAudioSession>> List()=>Dispatch(()=>backend.List());
    public Task Set(string id,double? percent,bool? muted)
    {
        if(id.Length!=64||!id.All(Uri.IsHexDigit))throw new ArgumentException("Choose an app from the current mixer list.");
        if(percent is not null&&(!double.IsFinite(percent.Value)||percent<0||percent>100))throw new ArgumentException("App volume must be between 0 and 100 percent.");
        if(percent is null&&muted is null)throw new ArgumentException("Choose an app volume or mute state.");
        return Dispatch(()=>{backend.Set(id,percent,muted);return true;});
    }
    public void Dispose(){queue.CompleteAdding();worker.Join(1500);}
}
internal sealed class WindowsAppAudioBackend : IAppAudioBackend
{
    internal static string SessionId(string device,string instance)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device+"\n"+instance)));
    private static void Visit(Action<MMDevice,AudioSessionControl> action)
    {
        using var enumerator=new MMDeviceEnumerator();
        foreach(var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
        {
            using(device) {
                try {
                    var sessions=device.AudioSessionManager.Sessions;
                    for(var index=0;index<sessions.Count;index++) {
                        using var session=sessions[index];
                        if(session.State==AudioSessionState.AudioSessionStateExpired||session.IsSystemSoundsSession||session.GetProcessID==0)continue;
                        action(device,session);
                    }
                } catch(System.Runtime.InteropServices.COMException) { /* A removed device is retried on the next refresh. */ }
            }
        }
    }
    public IReadOnlyList<AppAudioSession> List()
    {
        var result=new List<AppAudioSession>();
        Visit((device,session)=>{
            string name;
            try {using var process=Process.GetProcessById(checked((int)session.GetProcessID));name=process.ProcessName;}
            catch(ArgumentException){return;}
            var id=SessionId(device.ID,session.GetSessionInstanceIdentifier);
            result.Add(new(id,name,device.FriendlyName,Math.Round(session.SimpleAudioVolume.Volume*100,1),session.SimpleAudioVolume.Mute,session.State==AudioSessionState.AudioSessionStateActive,checked((int)session.GetProcessID)));
        });
        return result.OrderBy(s=>s.Name,StringComparer.OrdinalIgnoreCase).ThenBy(s=>s.Device,StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
    }
    public void Set(string id,double? percent,bool? muted)
    {
        var found=false;
        Visit((device,session)=>{
            if(SessionId(device.ID,session.GetSessionInstanceIdentifier)!=id)return;
            if(percent is not null)session.SimpleAudioVolume.Volume=(float)(percent.Value/100);
            if(muted is not null)session.SimpleAudioVolume.Mute=muted.Value;
            found=true;
        });
        if(!found)throw new InvalidOperationException("This audio session ended or its device changed. Refresh the app mixer and try again.");
    }
}
