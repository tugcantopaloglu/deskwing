namespace TabletDeck;

internal static class QualityChecks
{
    internal static int Run()
    {
        var checks=0;
        void Check(bool value,string label){if(!value)throw new Exception("Video quality: "+label);checks++;}
        void Reject(Action action,string label){try{action();throw new Exception("Accepted invalid quality: "+label);}catch(ArgumentException){checks++;}}
        var balanced=DisplayVideoQuality.Create("balanced",true,1280,800,120);
        var high=DisplayVideoQuality.Create("quality",true,1280,800,120);
        var battery=DisplayVideoQuality.Create("battery-saver",true,1280,800,120);
        Check(battery.InitialBitrate<balanced.InitialBitrate&&balanced.InitialBitrate<high.InitialBitrate,"presets offer distinct bandwidth budgets");
        Check(new[]{balanced,high,battery}.All(item=>item.TargetRate==120),"presets preserve the explicitly chosen refresh rate");
        Check(new[]{balanced,high,battery}.All(item=>item.MinimumBitrate>=1_000_000&&item.InitialBitrate<=50_000_000&&item.MinimumBitrate<=item.InitialBitrate),"quality budgets remain bounded");
        Reject(()=>DisplayVideoQuality.Create("../quality",true,1280,800,120),"unknown preset");
        Reject(()=>DisplayVideoQuality.Create(null,true,1280,800,120),"null preset");
        Reject(()=>DisplayVideoQuality.Create("balanced",true,int.MaxValue,800,120),"overflow dimensions");
        Reject(()=>DisplayVideoQuality.Create("balanced",true,1280,0,120),"empty dimensions");
        Reject(()=>DisplayVideoQuality.Create("balanced",true,1280,800,30),"unsupported target");
        var fixedQuality=new DisplayQualityController(balanced with{Adaptive=false});
        for(var i=0;i<50;i++)Check(!fixedQuality.Observe(1000,1000,i*100).Changed,"fixed quality never adapts without consent");
        Check(fixedQuality.CaptureRate==120&&fixedQuality.DesiredBitrate==balanced.InitialBitrate,"fixed policy retains target and selected bitrate");
        var adaptive=new DisplayQualityController(balanced);
        Check(!adaptive.Observe(12,12,0).Changed,"one sample cannot trigger a profile change");
        for(var i=1;i<100;i++)Check(!adaptive.Observe(12,12,i*10).Changed,"adaptation is rate limited during the first second");
        var first=adaptive.Observe(12,12,1000);
        Check(first.Changed&&first.CaptureRate<120&&first.CaptureRate>=60&&first.Bitrate<balanced.InitialBitrate,"actual write/encode pressure reduces work after measured evidence");
        Check(first.Reason.Contains("connection"),"network pressure gets actionable wording");
        for(var i=101;i<=1000;i++)adaptive.Observe(100,100,i*10);
        Check(adaptive.CaptureRate==60&&adaptive.DesiredBitrate==balanced.MinimumBitrate,"sustained pressure respects the bitrate floor and 60 Hz capture floor");
        for(var i=101;i<=800;i++)adaptive.Observe(0.1,0.1,i*100);
        Check(adaptive.CaptureRate==120&&adaptive.DesiredBitrate==balanced.InitialBitrate,"quality recovers gradually after sustained measured headroom");
        Reject(()=>adaptive.Observe(double.NaN,0,90_000),"NaN work time");
        Reject(()=>adaptive.Observe(0,double.PositiveInfinity,90_000),"infinite write time");
        Reject(()=>adaptive.Observe(-1,0,90_000),"negative work time");
        Reject(()=>adaptive.Observe(0,0,1),"regressing observation clock");
        var encoderPressure=new DisplayQualityController(high);
        for(var i=0;i<=20;i++)encoderPressure.Observe(20,0,i*100);
        Check(encoderPressure.Reason.Contains("capture and encoding"),"processing pressure is distinguished from connection pressure");
        Console.WriteLine(checks+" video quality policy checks passed; no desktop capture or network connection used.");
        return checks;
    }
}
