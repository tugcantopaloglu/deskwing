using System.Diagnostics;

namespace TabletDeck;

public sealed class UsbReconnect(DesktopPreferences preferences,int port,int controlPort=8744) : IDisposable
{
    private readonly CancellationTokenSource stop=new();
    private Task? loop;
    private string status="Automatic USB reconnect is off.";
    private string checkedSerial="";private DateTime checkedAt;private bool installed;
    public string Status=>Volatile.Read(ref status);
    public void Start()=>loop=Task.Run(async()=>{
        while(!stop.IsCancellationRequested) {
            try {if(preferences.Get().UsbReconnect)await Tick();else Volatile.Write(ref status,"Automatic USB reconnect is off.");}
            catch(Exception ex) when(ex is not OutOfMemoryException){Volatile.Write(ref status,"USB: "+ex.Message);}
            try{await Task.Delay(5000,stop.Token);}catch(OperationCanceledException){break;}
        }
    });
    internal static string SelectDevice(string output,string preferred)
    {
        var devices=output.Split('\n').Select(line=>line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)).Where(parts=>parts.Length>=2&&parts[1]=="device"&&!parts[0].StartsWith("emulator-",StringComparison.Ordinal)&&!parts[0].Contains(':')).Select(parts=>parts[0]).ToArray();
        if(preferred.Length>0)return devices.Contains(preferred,StringComparer.Ordinal)?preferred:throw new IOException("Selected USB tablet is not connected and authorized.");
        return devices.Length switch{1=>devices[0],0=>throw new IOException("Connect and authorize a USB tablet; no app will be opened."),_=>throw new IOException("Multiple tablets detected. Choose a serial in Desktop settings.")};
    }
    private async Task Tick()
    {
        var adb=Path.Combine(AppContext.BaseDirectory,"platform-tools","adb.exe");
        if(!File.Exists(adb))throw new IOException("ADB is missing; extract the complete Windows package.");
        var selected=SelectDevice(await Run(adb,["devices"]),preferences.Get().UsbSerial);
        var tunnels=await Run(adb,["-s",selected,"reverse","--list"]);
        foreach(var target in new[]{port,controlPort})if(!tunnels.Split('\n').Any(line=>line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries) is var parts&&parts.Length>=3&&parts[^2]=="tcp:"+target&&parts[^1]=="tcp:"+target))await Run(adb,["-s",selected,"reverse","tcp:"+target,"tcp:"+target]);
        if(checkedSerial!=selected||(DateTime.UtcNow-checkedAt).TotalSeconds>30){installed=(await Run(adb,["-s",selected,"shell","pm","path","app.tabletdeck.studio"])).Contains("package:",StringComparison.Ordinal);checkedSerial=selected;checkedAt=DateTime.UtcNow;}
        Volatile.Write(ref status,"USB tunnels ready. "+(installed?"DeskWing is installed; open it on the tablet and select USB.":"DeskWing is not installed; run Install-APK.cmd from the Windows package.")+" Tunnels checked every 5 seconds; app detection every 30 seconds.");
    }
    private async Task<string> Run(string executable,string[] arguments)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("Cannot start the bundled ADB helper.");
        try {
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var error=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);var text=await output;var detail=await error;
            if(process.ExitCode!=0)throw new IOException("ADB failed. Check USB debugging and authorization. "+detail.Trim());return text;
        }catch(OperationCanceledException){if(!process.HasExited)process.Kill();throw new IOException("ADB timed out. Reconnect the tablet and check its authorization.");}
    }
    public void Dispose(){stop.Cancel();try{loop?.Wait(TimeSpan.FromSeconds(5));}catch(AggregateException){}stop.Dispose();}
}
