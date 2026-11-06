namespace TabletDeck;

// Quality controls are deliberately independent of geometry and the requested display rate.
// A session never switches resolution or invents repeated pictures to satisfy a target.
internal sealed record DisplayVideoQuality(string Preset,bool Adaptive,int InitialBitrate,int MinimumBitrate,int TargetRate)
{
    // Set only by the video worker; this is never accepted from or returned to a client.
    internal CancellationToken InitializationCancellation {get;init;}
    internal static DisplayVideoQuality Create(string? preset,bool adaptive,int width,int height,int rate)
    {
        if(preset is not ("balanced" or "quality" or "battery-saver"))throw new ArgumentException("Choose Balanced, Quality, or Battery saver video.");
        if(width is <1 or >4096||height is <1 or >4096||rate is <60 or >240)throw new ArgumentException("Unsupported video quality dimensions or refresh rate.");
        var baseline=Math.Clamp((long)width*height*rate/6,4_000_000L,40_000_000L);
        var factor=preset switch{"quality"=>1.35,"battery-saver"=>0.55,_=>1.0};
        var initial=(int)Math.Clamp(Math.Round(baseline*factor),1_000_000,50_000_000);
        var floor=(int)Math.Clamp(Math.Round(initial*(preset=="quality"?0.55:0.35)),1_000_000,initial);
        return new(preset,adaptive,initial,floor,rate);
    }
}

internal sealed record DisplayQualityDecision(int Bitrate,int CaptureRate,string Reason,bool Changed);

// Uses measured encoder/write pressure, not nominal link names or fabricated delivered FPS.
// Changes are rate-limited and recover gradually after stable work. The caller owns this on
// the sole video worker; it does not add an encoder queue or change the stream's dimensions.
internal sealed class DisplayQualityController(DisplayVideoQuality options)
{
    private long lastAdjustment=-1,lastObservation=-1;
    private double workAverage,writeAverage;
    private int observations,stableWindows;
    private int bitrate=options.InitialBitrate,rate=options.TargetRate;
    internal int CaptureRate=>rate;
    internal int DesiredBitrate=>bitrate;
    internal string Reason {get;private set;}=options.Adaptive?"Measuring video load":"Fixed quality selected";

    internal DisplayQualityDecision Observe(double processingMs,double writingMs,long atMilliseconds)
    {
        if(!double.IsFinite(processingMs)||!double.IsFinite(writingMs)||processingMs<0||writingMs<0||processingMs>60_000||writingMs>60_000||atMilliseconds<0)
            throw new ArgumentException("Invalid video performance observation.");
        if(lastObservation>=0&&atMilliseconds<lastObservation)throw new ArgumentException("Video performance observations must be monotonic.");
        lastObservation=atMilliseconds;
        if(!options.Adaptive)return new(bitrate,rate,Reason,false);
        observations++;
        workAverage=observations==1?processingMs:workAverage*0.85+processingMs*0.15;
        writeAverage=observations==1?writingMs:writeAverage*0.85+writingMs*0.15;
        if(lastAdjustment<0){lastAdjustment=atMilliseconds;return new(bitrate,rate,Reason,false);}
        if(atMilliseconds-lastAdjustment<1000||observations<4)return new(bitrate,rate,Reason,false);
        lastAdjustment=atMilliseconds;
        var previousBitrate=bitrate;var previousRate=rate;
        var budget=1000.0/rate;
        if(workAverage+writeAverage>budget*0.95||writeAverage>budget*0.6)
        {
            bitrate=Math.Max(options.MinimumBitrate,(int)Math.Floor(bitrate*0.8));
            rate=Math.Max(60,(int)Math.Floor(rate*0.85));
            stableWindows=0;
            Reason=writeAverage>budget*0.6?"Reducing load for a slow video connection":"Reducing load for capture and encoding";
        }
        else if(workAverage+writeAverage<budget*0.65)
        {
            if(++stableWindows>=4)
            {
                bitrate=Math.Min(options.InitialBitrate,bitrate+Math.Max(100_000,options.InitialBitrate/10));
                rate=Math.Min(options.TargetRate,rate+Math.Max(1,options.TargetRate/10));
                stableWindows=0;
                Reason=bitrate==options.InitialBitrate&&rate==options.TargetRate?"Selected quality is stable":"Recovering quality after stable video";
            }
        }
        else stableWindows=0;
        return new(bitrate,rate,Reason,bitrate!=previousBitrate||rate!=previousRate);
    }
}
