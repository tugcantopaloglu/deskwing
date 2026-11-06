using System.Diagnostics;
using System.Runtime.InteropServices;
using TabletDeck;

var checks=0;
void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
void Reject(Action action,string label){try{action();throw new Exception("Accepted: "+label);}catch(ArgumentException){checks++;}}
void RejectEncoded(Action action,string label){try{action();throw new Exception("Accepted: "+label);}catch(InvalidOperationException){checks++;}}
foreach(var validRate in new[]{60,90,120,144,165,240})WindowsDisplayEncoder.Validate(1280,800,validRate);
Reject(()=>WindowsDisplayEncoder.Validate(1281,800,60),"odd width");
Reject(()=>WindowsDisplayEncoder.Validate(1280,801,60),"odd height");
Reject(()=>WindowsDisplayEncoder.Validate(1280,800,59),"rate below60");
Reject(()=>WindowsDisplayEncoder.Validate(1280,800,241),"unbounded rate");
Reject(()=>WindowsDisplayEncoder.Validate(5000,3000,60),"unbounded dimensions");
Reject(()=>WindowsDisplayEncoder.ValidateBitrate(999999),"bitrate below limit");
Reject(()=>WindowsDisplayEncoder.ValidateBitrate(50000001),"bitrate above limit");
Reject(()=>WindowsDisplayCapture.ValidateDimensions(1281,800),"odd capture width");
Reject(()=>WindowsDisplayCapture.ValidateDimensions(1280,801),"odd capture height");
Reject(()=>WindowsDisplayCapture.ValidateDimensions(5000,3000),"unbounded capture");
byte[] Solid(byte b,byte g,byte r){var frame=new byte[16];for(var i=0;i<frame.Length;i+=4){frame[i]=b;frame[i+1]=g;frame[i+2]=r;frame[i+3]=255;}return frame;}
var nv12=new byte[6];
WindowsDisplayEncoder.BgraToNv12(Solid(0,0,0),8,2,2,nv12);Check(nv12.SequenceEqual(new byte[]{16,16,16,16,128,128}),"black limitedrange BT709");
WindowsDisplayEncoder.BgraToNv12(Solid(255,255,255),8,2,2,nv12);Check(nv12.SequenceEqual(new byte[]{235,235,235,235,128,128}),"white limitedrange BT709");
WindowsDisplayEncoder.BgraToNv12(Solid(0,0,255),8,2,2,nv12);Check(nv12.SequenceEqual(new byte[]{63,63,63,63,102,240}),"red BT709");
Reject(()=>WindowsDisplayEncoder.BgraToNv12(new byte[15],8,2,2,nv12),"short capture");
Reject(()=>WindowsDisplayEncoder.BgraToNv12(new byte[16],7,2,2,nv12),"short stride");
Reject(()=>WindowsDisplayEncoder.BgraToNv12(new byte[16],8,2,2,new byte[7]),"wrong NV12 size");
Check(WindowsDisplayEncoder.ContainsNal(new byte[]{0,0,0,1,0x67,9,0,0,1,0x68,7},7),"SPS long startcode");
Check(WindowsDisplayEncoder.ContainsNal(new byte[]{0,0,0,1,0x67,9,0,0,1,0x68,7},8),"PPS short startcode");
Check(!WindowsDisplayEncoder.ContainsNal(new byte[]{0,0,0,1},7),"truncated NAL");
var access=new byte[]{0,0,0,2,0x65,9,0,0,0,2,0x06,7};
Check(WindowsDisplayEncoder.NormalizeAccessUnit(access,4).SequenceEqual(new byte[]{0,0,0,1,0x65,9,0,0,0,1,0x06,7}),"length-prefixed hardware access units become AnnexB");
Check(WindowsDisplayEncoder.NormalizeAccessUnit(new byte[]{2,0x65,9},1).SequenceEqual(new byte[]{0,0,0,1,0x65,9}),"one-byte NAL lengths are normalized");
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeAccessUnit(new byte[]{0,0,0,9,0x65},4),"oversized hardware NAL rejected");
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeAccessUnit(new byte[]{0,0,0,0,0x65},4),"empty hardware NAL rejected");
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeAccessUnit(new byte[]{0,0,0,2,0x65,9,0},4),"trailing truncated NAL rejected");
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeAccessUnit(access,0),"unsupported NAL length size rejected");
var avcc=new byte[]{1,66,0,30,255,225,0,2,0x67,9,1,0,2,0x68,7};
var configuration=WindowsDisplayEncoder.NormalizeSequenceHeader(avcc);
Check(configuration.LengthSize==4&&configuration.Bytes.SequenceEqual(new byte[]{0,0,0,1,0x67,9,0,0,0,1,0x68,7}),"AVC hardware configuration becomes bounded SPS/PPS AnnexB");
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeSequenceHeader(avcc[..^1]),"truncated AVC configuration rejected");
var badConfiguration=(byte[])avcc.Clone();badConfiguration[8]=0x65;
RejectEncoded(()=>WindowsDisplayEncoder.NormalizeSequenceHeader(badConfiguration),"SPS slot cannot contain an IDR");
var retryWindow=new HardwareEncoderRetryWindow();var retryAt=Stopwatch.Frequency;
Check(!retryWindow.Blocked("fixture",retryAt),"new hardware candidate is eligible");retryWindow.Failed("fixture",retryAt);
Check(retryWindow.Blocked("fixture",retryAt+HardwareEncoderRetryWindow.RetryTicks-1),"failed hardware format is skipped before cooldown");
Check(!retryWindow.Blocked("fixture",retryAt+HardwareEncoderRetryWindow.RetryTicks),"failed hardware format becomes eligible after cooldown");
Check(!retryWindow.Blocked("other-format",retryAt),"failed format does not block other encoders or dimensions");
for(var index=0;index<100;index++)retryWindow.Failed("bounded-"+index,retryAt+index);
Check(retryWindow.Count==64,"hardware failure inventory stays bounded");
using(var cancelled=new CancellationTokenSource()){
    cancelled.Cancel();
    try{using var ignored=new WindowsDisplayEncoder(1280,800,60,initializationCancellation:cancelled.Token);throw new Exception("A cancelled encoder initialized.");}catch(OperationCanceledException){checks++;}
}
// Native COM reference bookkeeping only: no codec, screen, input, or driver activation.
var anchor=Marshal.GetIUnknownForObject(new object());var activationArray=Marshal.AllocCoTaskMem(300*IntPtr.Size);
try{
    for(var index=0;index<300;index++){Marshal.AddRef(anchor);Marshal.WriteIntPtr(activationArray,index*IntPtr.Size,anchor);}
    WindowsDisplayEncoder.ReleaseActivationArray(activationArray,300);activationArray=IntPtr.Zero;
    var remaining=Marshal.AddRef(anchor);Marshal.Release(anchor);
    Check(remaining==2,"all300 rejected inventory references are released, including entries beyond activation limit");
}finally{if(activationArray!=IntPtr.Zero)Marshal.FreeCoTaskMem(activationArray);Marshal.Release(anchor);}
Check(Marshal.SizeOf<MfNative.OutputDataBuffer>()==(IntPtr.Size==8?32:16),"native output layout");
Check(Marshal.SizeOf<MfNative.OutputStreamInfo>()==12,"native output info layout");
Check(WindowsDisplayCursor.CalculatePlacement(1920,0,1280,800,2000,50,4,7)==new DisplayCursorPlacement(76,43),"cursor hotspot follows physical monitor position");
Check(WindowsDisplayCursor.CalculatePlacement(-1280,-800,1280,800,-1280,-800,10,12)==new DisplayCursorPlacement(-10,-12),"negative monitor origin and off-edge icon clip");
Check(WindowsDisplayCursor.CalculatePlacement(1920,0,1280,800,3199,799,0,0)==new DisplayCursorPlacement(1279,799),"last owned pixel includes cursor");
Check(WindowsDisplayCursor.CalculatePlacement(1920,0,1280,800,3200,799,0,0) is null,"right edge excludes neighboring monitor cursor");
Check(WindowsDisplayCursor.CalculatePlacement(1920,0,1280,800,3199,800,0,0) is null,"bottom edge excludes outside cursor");
Check(WindowsDisplayCursor.CalculatePlacement(1920,0,1280,800,1919,10,100,100) is null,"outside anchor never overlays despite graphic overlap");
Check(WindowsDisplayCursor.CalculatePlacement(int.MinValue,0,1280,800,int.MaxValue,5,0,0) is null,"cursor coordinate difference cannot overflow into owned monitor");
Check(WindowsDisplayCursor.CalculatePlacement(0,0,1280,800,10,10,uint.MaxValue,0) is null,"oversized hotspot rejected");
WindowsDisplayCursorOverlay.ValidateBuffer(new byte[16*16*4],16*4,16,16);
Reject(()=>WindowsDisplayCursorOverlay.ValidateBuffer(new byte[16*16*4-1],16*4,16,16),"short cursor overlay buffer");
Reject(()=>WindowsDisplayCursorOverlay.ValidateBuffer(new byte[16*16*4],16*4-1,16,16),"short cursor overlay stride");
DxgiCaptureChecks.Run(Check);
Console.WriteLine($"Display encoder deterministic checks: {checks} passed; no desktop capture or input.");
if(args.Contains("--native-resources")){
    // Allocation/cleanup only. Capture is deliberately never called in this resource probe.
    using(var warmup=new WindowsDisplayCapture(1280,800)){}
    using var resourceProcess=Process.GetCurrentProcess();
    var baseline=ResourceProbe.GetGuiResources(resourceProcess.Handle,0);
    for(var index=0;index<100;index++)using(var capture=new WindowsDisplayCapture(1280,800)){}
    for(var index=0;index<100;index++)using(var overlay=new WindowsDisplayCursorOverlay(1280,800)){}
    var standardCursor=ResourceProbe.LoadCursor(IntPtr.Zero,new IntPtr(32512));
    if(standardCursor==IntPtr.Zero)throw new Exception("Cannot load shared standard cursor for resource probe.");
    for(var index=0;index<1000;index++){
        if(WindowsDisplayCursor.GetPlacement(standardCursor,1920,0,1280,800,2000,50) is null)throw new Exception("Standard cursor hotspot was unavailable.");
    }
    var after=ResourceProbe.GetGuiResources(resourceProcess.Handle,0);
    if(after!=baseline)throw new Exception($"GDI capture resources leaked: before={baseline}, after={after}.");
    Console.WriteLine($"Persistent capture resource probe: 100 capture and 100 overlay allocations/disposals plus 1000 cursor bitmap queries, GDI handles {baseline} → {after}, no desktop capture.");
}
if(!args.Contains("--native"))return;

if(args.Contains("--native-lifecycle")){
    var synthetic=new byte[320*240*4];Array.Fill(synthetic,(byte)96);
    for(var cycle=0;cycle<8;cycle++){
        using var cancelled=new CancellationTokenSource();
        using var guarded=new WindowsDisplayEncoder(320,240,60,preferHardware:!args.Contains("--software"),initializationCancellation:cancelled.Token);
        var wrongThread=Task.Run(()=>{try{guarded.Encode(synthetic,320*4,0);return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult();
        if(!wrongThread)throw new Exception("A foreign thread used the video encoder.");
        if(guarded.Encode(synthetic,320*4,0) is not {KeyFrame:true})throw new Exception("Lifecycle encoder did not emit its first keyframe.");
        cancelled.Cancel();
        try{guarded.Encode(synthetic,320*4,1);throw new Exception("A cancelled stream encoded another frame.");}catch(OperationCanceledException){}
    }
    Console.WriteLine("Encoder lifecycle probe passed8 create/first-frame/cancel/dispose cycles; foreign-thread input was refused.");return;
}

// This opt-in probe generates synthetic pixels. It never captures any desktop/window or sends input.
var width=1280;var height=800;var rateArg=Array.IndexOf(args,"--rate");var rate=rateArg>=0?int.Parse(args[rateArg+1]):60;
var widthArg=Array.IndexOf(args,"--width");if(widthArg>=0)width=int.Parse(args[widthArg+1]);
var heightArg=Array.IndexOf(args,"--height");if(heightArg>=0)height=int.Parse(args[heightArg+1]);
var countArg=Array.IndexOf(args,"--frames");var count=countArg>=0?int.Parse(args[countArg+1]):180;
var forceKeyFrame=args.Contains("--force-idr");
if(forceKeyFrame&&count<20)throw new ArgumentException("The forced recovery frame probe needs at least 20 frames.");
var outputArg=Array.IndexOf(args,"--output");using var output=outputArg>=0?File.Create(Path.GetFullPath(args[outputArg+1])):null;
var bgra=new byte[checked(width*height*4)];
for(var y=0;y<height;y++)for(var x=0;x<width;x++){var p=(y*width+x)*4;bgra[p]=(byte)(x%256);bgra[p+1]=(byte)(y%256);bgra[p+2]=(byte)((x+y)%256);bgra[p+3]=255;}
using var encoder=new WindowsDisplayEncoder(width,height,rate,preferHardware:!args.Contains("--software"));
var bitrateChange=args.Contains("--change-bitrate");var bitrateChanged=false;
var times=new List<double>();var frames=0;var keyframes=0;long bytes=0;
for(var index=0;index<count;index++){
    // Change a vertical cursor stripe, providing actual inter-frame changes without touching the PC.
    var column=index%width;for(var y=0;y<height;y++){var p=(y*width+column)*4;bgra[p]=(byte)(index%256);bgra[p+1]=255;bgra[p+2]=0;}
    if(forceKeyFrame&&index==17)encoder.RequestKeyFrame();
    if(bitrateChange&&index==12)bitrateChanged=encoder.TrySetBitrate(4_000_000);
    var timestamp=index*10_000_000L/rate+(forceKeyFrame&&index>=17?5_000_000:0);
    var start=Stopwatch.GetTimestamp();var frame=encoder.Encode(bgra,width*4,timestamp);var elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    if(index>=10)times.Add(elapsed);
    if(frame is null)continue;
    frames++;if(frame.KeyFrame)keyframes++;bytes+=frame.Data.Length;output?.Write(frame.Data);
    if(frame.Data.Length>WindowsDisplayEncoder.MaximumFrameBytes||!WindowsDisplayEncoder.ContainsStartCode(frame.Data))throw new Exception("Native encoder returned invalid accessunit.");
    if(frame.Timestamp100ns!=timestamp)throw new Exception("Lowlatency encoder changed the capture timestamp.");
    if(forceKeyFrame&&index==17&&(!frame.KeyFrame||!WindowsDisplayEncoder.ContainsNal(frame.Data,5)||!WindowsDisplayEncoder.ContainsNal(frame.Data,7)||!WindowsDisplayEncoder.ContainsNal(frame.Data,8)))throw new Exception("The first frame after a recovery request was not an IDR with SPS/PPS.");
    if(frame.KeyFrame&&(!WindowsDisplayEncoder.ContainsNal(frame.Data,7)||!WindowsDisplayEncoder.ContainsNal(frame.Data,8)))throw new Exception("Keyframe missingSPS/PPS.");
}
times.Sort();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{width,height,requestedRate=rate,inputFrames=count,encodedFrames=frames,keyframes,bytes,meanEncodeMs=times.Average(),p95EncodeMs=times[(int)((times.Count-1)*.95)],syntheticThroughputFps=1000/times.Average(),encoder=encoder.Name,hardware=encoder.HardwareAccelerated,encoder.FallbackReason,encoder.Bitrate,bitrateChanged}));
if(frames!=count)throw new Exception("Lowlatency native encoder did not emit one accessunitperinputframe.");
if(forceKeyFrame)Console.WriteLine("Forced recovery IDR with SPS/PPS passed immediately after a 500 ms timestamp gap.");

internal static class ResourceProbe
{
    [DllImport("user32.dll")] internal static extern uint GetGuiResources(IntPtr process,uint flags);
    [DllImport("user32.dll",EntryPoint="LoadCursorW")] internal static extern IntPtr LoadCursor(IntPtr instance,IntPtr name);
}
