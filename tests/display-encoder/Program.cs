using System.Diagnostics;
using System.Runtime.InteropServices;
using TabletDeck;

var checks=0;
void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
void Reject(Action action,string label){try{action();throw new Exception("Accepted: "+label);}catch(ArgumentException){checks++;}}
foreach(var validRate in new[]{60,90,120,144,165,240})WindowsDisplayEncoder.Validate(1280,800,validRate);
Reject(()=>WindowsDisplayEncoder.Validate(1281,800,60),"odd width");
Reject(()=>WindowsDisplayEncoder.Validate(1280,801,60),"odd height");
Reject(()=>WindowsDisplayEncoder.Validate(1280,800,59),"rate below60");
Reject(()=>WindowsDisplayEncoder.Validate(1280,800,241),"unbounded rate");
Reject(()=>WindowsDisplayEncoder.Validate(5000,3000,60),"unbounded dimensions");
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
Check(Marshal.SizeOf<MfNative.OutputDataBuffer>()==(IntPtr.Size==8?32:16),"native output layout");
Check(Marshal.SizeOf<MfNative.OutputStreamInfo>()==12,"native output info layout");
Console.WriteLine($"Display encoder pure checks: {checks} passed.");
if(!args.Contains("--native"))return;

// This opt-in probe generates synthetic pixels. It never captures any desktop/window or sends input.
var width=1280;var height=800;var rateArg=Array.IndexOf(args,"--rate");var rate=rateArg>=0?int.Parse(args[rateArg+1]):60;
var widthArg=Array.IndexOf(args,"--width");if(widthArg>=0)width=int.Parse(args[widthArg+1]);
var heightArg=Array.IndexOf(args,"--height");if(heightArg>=0)height=int.Parse(args[heightArg+1]);
var countArg=Array.IndexOf(args,"--frames");var count=countArg>=0?int.Parse(args[countArg+1]):180;
var outputArg=Array.IndexOf(args,"--output");using var output=outputArg>=0?File.Create(Path.GetFullPath(args[outputArg+1])):null;
var bgra=new byte[checked(width*height*4)];
for(var y=0;y<height;y++)for(var x=0;x<width;x++){var p=(y*width+x)*4;bgra[p]=(byte)(x%256);bgra[p+1]=(byte)(y%256);bgra[p+2]=(byte)((x+y)%256);bgra[p+3]=255;}
using var encoder=new WindowsDisplayEncoder(width,height,rate);
var times=new List<double>();var frames=0;var keyframes=0;long bytes=0;
for(var index=0;index<count;index++){
    // Change a vertical cursor stripe, providing actual inter-frame changes without touching the PC.
    var column=index%width;for(var y=0;y<height;y++){var p=(y*width+column)*4;bgra[p]=(byte)(index%256);bgra[p+1]=255;bgra[p+2]=0;}
    var start=Stopwatch.GetTimestamp();var frame=encoder.Encode(bgra,width*4,index*10_000_000L/rate);var elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    if(index>=10)times.Add(elapsed);
    if(frame is null)continue;
    frames++;if(frame.KeyFrame)keyframes++;bytes+=frame.Data.Length;output?.Write(frame.Data);
    if(frame.Data.Length>WindowsDisplayEncoder.MaximumFrameBytes||!WindowsDisplayEncoder.ContainsStartCode(frame.Data))throw new Exception("Native encoder returned invalid accessunit.");
    if(frame.Timestamp100ns!=index*10_000_000L/rate)throw new Exception("Lowlatency encoder changed the capture timestamp.");
    if(frame.KeyFrame&&(!WindowsDisplayEncoder.ContainsNal(frame.Data,7)||!WindowsDisplayEncoder.ContainsNal(frame.Data,8)))throw new Exception("Keyframe missingSPS/PPS.");
}
times.Sort();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{width,height,requestedRate=rate,inputFrames=count,encodedFrames=frames,keyframes,bytes,meanEncodeMs=times.Average(),p95EncodeMs=times[(int)((times.Count-1)*.95)],syntheticThroughputFps=1000/times.Average(),encoder=encoder.Name,hardware=encoder.HardwareAccelerated}));
if(frames!=count)throw new Exception("Lowlatency native encoder did not emit one accessunitperinputframe.");
