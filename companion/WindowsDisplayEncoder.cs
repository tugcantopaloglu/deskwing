using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TabletDeck;

internal sealed record EncodedVideoFrame(byte[] Data,bool KeyFrame,long Timestamp100ns);

/// <summary>Bounded, low-latency H.264 access units using the Windows inbox encoder.
/// One streaming worker owns an instance; no desktop capture or network access occurs here.</summary>
internal sealed class WindowsDisplayEncoder : IDisposable
{
    internal const int MaximumFrameBytes=2*1024*1024;
    private MfNative.IMFTransform transform=null!;
    private MfNative.IMFActivate? activation;
    private MfNative.IMFMediaEventGenerator? events;
    private int inputCredits,outputCredits;
    private readonly bool ownsComApartment;
    private readonly CancellationToken cancellation;
    private int bitrate;
    private readonly byte[] nv12;
    private readonly int width,height,rate;
    private byte[] sequenceHeader=[];
    private bool disposed;
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private long lastTimestamp=-1;
    private long emittedFrames;
    private int nalLengthSize=4;
    private static readonly HardwareEncoderRetryWindow hardwareFailures=new();
    internal string Name{get;private set;}="Windows H.264 (software, low latency)";
    internal bool HardwareAccelerated{get;private set;}
    internal string? FallbackReason{get;private set;}
    internal int Bitrate=>bitrate;
    internal byte[] SequenceHeader=>(byte[])sequenceHeader.Clone();

    internal WindowsDisplayEncoder(int width,int height,int frameRate,int bitrate=0,bool preferHardware=true,CancellationToken initializationCancellation=default)
    {
        initializationCancellation.ThrowIfCancellationRequested();cancellation=initializationCancellation;
        Validate(width,height,frameRate);
        this.width=width;this.height=height;rate=frameRate;
        nv12=new byte[checked(width*height*3/2)];
        var selectedBitrate=bitrate==0?Math.Clamp((long)width*height*frameRate/6,4_000_000,40_000_000):bitrate;
        ValidateBitrate((int)selectedBitrate);this.bitrate=(int)selectedBitrate;
        var apartment=MfNative.CoInitializeEx(IntPtr.Zero,0);
        ownsComApartment=apartment>=0;
        if(apartment<0&&apartment!=unchecked((int)0x80010106))MfNative.Check(apartment);
        var started=false;
        try{
            MfNative.Check(MfNative.MFStartup(MfNative.Version,0));started=true;
            if(preferHardware&&TryHardware())return;
            cancellation.ThrowIfCancellationRequested();
            FallbackReason=preferHardware?"No compatible hardware H.264 encoder was available; using software encoding.":"Software encoding selected.";
            StartSoftware();
        }catch{
            try{ReleaseTransform();}finally{if(started)MfNative.MFShutdown();if(ownsComApartment)MfNative.CoUninitialize();}throw;
        }
    }

    private bool TryHardware()
    {
        IntPtr array=IntPtr.Zero;var count=0;
        var discoveryStarted=Stopwatch.GetTimestamp();
        try{
            cancellation.ThrowIfCancellationRequested();
            var input=new MfNative.RegisterTypeInfo{MajorType=MfNative.Video,Subtype=MfNative.NV12};
            var output=new MfNative.RegisterTypeInfo{MajorType=MfNative.Video,Subtype=MfNative.H264};
            MfNative.Check(MfNative.MFTEnumEx(MfNative.VideoEncoderCategory,0x44,ref input,ref output,out array,out count));
            if(count is <0 or >256)throw new InvalidOperationException("The hardware encoder inventory exceeded its limit.");
            for(var index=0;index<Math.Min(count,4);index++){
                cancellation.ThrowIfCancellationRequested();
                if(Stopwatch.GetElapsedTime(discoveryStarted)>=TimeSpan.FromMilliseconds(1500))break;
                MfNative.IMFActivate? candidate=null;MfNative.IMFTransform? created=null;string? candidateName=null;
                try{
                    var pointer=Marshal.ReadIntPtr(array,index*IntPtr.Size);if(pointer==IntPtr.Zero)continue;
                    candidate=(MfNative.IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    candidateName=FriendlyEncoderName(candidate);
                    if(hardwareFailures.Blocked(HardwareKey(candidateName),Stopwatch.GetTimestamp()))continue;
                    var instance=IntPtr.Zero;
                    try{MfNative.Check(candidate.ActivateObject(ref MfNative.TransformInterface,out instance));created=(MfNative.IMFTransform)Marshal.GetObjectForIUnknown(instance);}finally{if(instance!=IntPtr.Zero)Marshal.Release(instance);}
                    MfNative.IMFAttributes? attributes=null;
                    try{
                        MfNative.Check(created.GetAttributes(out attributes));
                        MfNative.Check(attributes.GetUINT32(ref MfNative.TransformAsync,out var asynchronous));
                        if(asynchronous!=1)throw new InvalidOperationException("A hardware encoder must expose asynchronous processing.");
                        MfNative.Check(attributes.SetUINT32(ref MfNative.TransformAsyncUnlock,1));
                    }finally{MfNative.Release(attributes);}
                    if(created is not MfNative.IMFMediaEventGenerator generator)throw new InvalidOperationException("The hardware encoder has no event source.");
                    Configure(created);transform=created;events=generator;
                    var remaining=TimeSpan.FromMilliseconds(1500)-Stopwatch.GetElapsedTime(discoveryStarted);
                    if(remaining<=TimeSpan.Zero)throw new TimeoutException("Hardware encoder initialization exceeded its budget.");
                    WaitForEvent(input:true,remaining<TimeSpan.FromMilliseconds(750)?remaining:TimeSpan.FromMilliseconds(750));
                    activation=candidate;Name=candidateName;HardwareAccelerated=true;
                    created=null;candidate=null;return true;
                }catch(Exception ex) when(ex is COMException or InvalidOperationException or ArgumentException or TimeoutException){
                    if(candidateName is not null)hardwareFailures.Failed(HardwareKey(candidateName),Stopwatch.GetTimestamp());
                }finally{
                    // Also retire a candidate when cancellation interrupts setup; that path
                    // deliberately bypasses the compatibility-failure catch above.
                    if(created is not null&&ReferenceEquals(transform,created)){events=null;transform=null!;inputCredits=outputCredits=0;sequenceHeader=[];}
                    try{if(candidate is not null)candidate.ShutdownObject();}
                    finally{MfNative.Release(created);MfNative.Release(candidate);}
                }
            }
            return false;
        }catch(Exception ex) when(ex is COMException or InvalidOperationException or ArgumentException){return false;}
        finally{
            ReleaseActivationArray(array,count);
        }
    }

    private static string FriendlyEncoderName(MfNative.IMFActivate candidate)
    {
        IntPtr pointer=IntPtr.Zero;
        try{if(candidate.GetAllocatedString(ref MfNative.FriendlyName,out pointer,out var length)>=0&&pointer!=IntPtr.Zero&&length is >0 and <=128)return Marshal.PtrToStringUni(pointer,length)+" (hardware)";}
        finally{if(pointer!=IntPtr.Zero)Marshal.FreeCoTaskMem(pointer);}
        return "Windows H.264 (hardware)";
    }

    private void StartSoftware()
    {
        MfNative.IMFTransform? created=null;
        try{
            cancellation.ThrowIfCancellationRequested();
            var type=Type.GetTypeFromCLSID(MfNative.H264Encoder,throwOnError:true)!;
            created=(MfNative.IMFTransform)Activator.CreateInstance(type)!;
            Configure(created);transform=created;created=null;
            HardwareAccelerated=false;Name="Windows H.264 (software, low latency)";
        }finally{MfNative.Release(created);}
    }

    private void Configure(MfNative.IMFTransform created)
    {
            // These settings prevent B-frame reordering and prefer per-picture latency over complexity.
            SetCodec(created,MfNative.LowLatency,true);
            SetCodec(created,MfNative.QualityVsSpeed,(uint)0);
            SetCodec(created,MfNative.RateControlMode,(uint)0);
            SetCodec(created,MfNative.MeanBitrate,(uint)bitrate);
            SetCodec(created,MfNative.BPictureCount,(uint)0);
            SetCodec(created,MfNative.GopSize,(uint)rate);
            MfNative.IMFMediaType? output=null,input=null;
            try{
                MfNative.Check(MfNative.MFCreateMediaType(out output));
                ConfigureType(output,MfNative.H264);
                Set(output,MfNative.AverageBitrate,bitrate);
                Set(output,MfNative.Profile,66); // Baseline: no reordering, broad tablet decoder support.
                MfNative.Check(created.SetOutputType(0,output,0));
                MfNative.Check(MfNative.MFCreateMediaType(out input));
                ConfigureType(input,MfNative.NV12);
                MfNative.Check(created.SetInputType(0,input,0));
            }finally{MfNative.Release(input);MfNative.Release(output);}
            MfNative.Check(created.ProcessMessage(MfNative.NotifyBeginStreaming,IntPtr.Zero));
            MfNative.Check(created.ProcessMessage(MfNative.NotifyStartOfStream,IntPtr.Zero));
    }

    internal static void ValidateBitrate(int value){if(value is <1_000_000 or >50_000_000)throw new ArgumentOutOfRangeException(nameof(value),"Video bitrate must be between 1 and 50 Mbps.");}

    internal static void Validate(int width,int height,int frameRate)
    {
        if(width is <16 or >4096||height is <16 or >2160||(width&1)!=0||(height&1)!=0||(long)width*height>8_847_360)
            throw new ArgumentException("Choose even video dimensions no larger than 4096 × 2160.");
        if(frameRate is <60 or >240)throw new ArgumentException("Video refresh must be between 60 and 240 Hz.");
    }

    internal EncodedVideoFrame? Encode(byte[] bgra,int stride,long timestamp100ns)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        cancellation.ThrowIfCancellationRequested();
        try{return EncodeCurrent(bgra,stride,timestamp100ns);}
        catch(Exception ex) when(HardwareAccelerated&&ex is COMException or InvalidOperationException or TimeoutException){
            hardwareFailures.Failed(HardwareKey(Name),Stopwatch.GetTimestamp());
            if(emittedFrames>0)throw new IOException("The hardware encoder stopped. Start Second display again to use a compatible encoder.",ex);
            // No configuration or picture has escaped yet. Recreate the known software encoder
            // and encode this same input; never change codec implementation inside a live stream.
            ReleaseTransform();lastTimestamp=-1;sequenceHeader=[];nalLengthSize=4;
            FallbackReason="The hardware encoder could not start this format; using software encoding.";
            StartSoftware();return EncodeCurrent(bgra,stride,timestamp100ns);
        }
    }

    private string HardwareKey(string name)=>name+"|"+width+"x"+height+"@"+rate;

    private EncodedVideoFrame? EncodeCurrent(byte[] bgra,int stride,long timestamp100ns)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        if(timestamp100ns<0||timestamp100ns<=lastTimestamp)throw new ArgumentException("Video timestamps must increase.");
        BgraToNv12(bgra,stride,width,height,nv12);
        MfNative.IMFSample? input=null;MfNative.IMFMediaBuffer? buffer=null;
        try{
            MfNative.Check(MfNative.MFCreateMemoryBuffer(nv12.Length,out buffer));
            MfNative.Check(buffer.Lock(out var pointer,out _,out _));
            try{Marshal.Copy(nv12,0,pointer,nv12.Length);}finally{MfNative.Check(buffer.Unlock());}
            MfNative.Check(buffer.SetCurrentLength(nv12.Length));
            MfNative.Check(MfNative.MFCreateSample(out input));
            MfNative.Check(input.AddBuffer(buffer));
            MfNative.Check(input.SetSampleTime(timestamp100ns));
            MfNative.Check(input.SetSampleDuration(10_000_000L/rate));
            if(events is not null)WaitForEvent(input:true,TimeSpan.FromMilliseconds(500));
            MfNative.Check(transform.ProcessInput(0,input,0));
            if(events is not null)inputCredits--;
            lastTimestamp=timestamp100ns;
        }finally{MfNative.Release(buffer);MfNative.Release(input);}
        return ReadOutput(timestamp100ns);
    }

    // The Windows encoder applies this request to the next ProcessInput and then resets it.
    // Recovery callers use it after idle gaps so a decoder awaiting an IDR need not wait a full GOP.
    internal void RequestKeyFrame()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        if(transform is not MfNative.ICodecAPI codec)throw new InvalidOperationException("The Windows encoder cannot request a recovery frame.");
        object value=(uint)1;
        MfNative.Check(codec.SetValue(ref MfNative.ForceKeyFrame,ref value));
    }

    internal bool TrySetBitrate(int value)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ValidateBitrate(value);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must stay on its owning stream worker.");
        cancellation.ThrowIfCancellationRequested();
        if(value==bitrate)return true;
        if(transform is not MfNative.ICodecAPI codec)return false;
        // Some codecs implement SetValue but not the optional modifiability query.
        // Respect an explicit S_FALSE; otherwise let the setter report support.
        if(codec.IsModifiable(ref MfNative.MeanBitrate)==1)return false;
        object selected=(uint)value;
        if(codec.SetValue(ref MfNative.MeanBitrate,ref selected)<0)return false;
        bitrate=value;return true;
    }

    private void WaitForEvent(bool input,TimeSpan timeout)
    {
        if(events is null)return;
        var started=Stopwatch.GetTimestamp();
        do{
            cancellation.ThrowIfCancellationRequested();
            PumpEvents();if(input?inputCredits>0:outputCredits>0)return;
            if(Stopwatch.GetElapsedTime(started)>=timeout)throw new TimeoutException(input?"The hardware encoder did not accept a video frame in time.":"The hardware encoder did not produce a video frame in time.");
            Thread.Sleep(1);
        }while(true);
    }

    private void PumpEvents()
    {
        if(events is null)return;
        for(var index=0;index<64;index++){
            MfNative.IMFMediaEvent? mediaEvent=null;
            try{
                var result=events.GetEvent(MfNative.EventNoWait,out mediaEvent);
                if(result==MfNative.NoEventsAvailable)return;
                MfNative.Check(result);MfNative.Check(mediaEvent.GetStatus(out var status));MfNative.Check(status);MfNative.Check(mediaEvent.GetType(out var kind));
                if(kind==MfNative.TransformNeedInput){if(inputCredits>=16)throw new InvalidOperationException("The hardware encoder exceeded its input queue limit.");inputCredits++;}
                else if(kind==MfNative.TransformHaveOutput){if(outputCredits>=16)throw new InvalidOperationException("The hardware encoder exceeded its output queue limit.");outputCredits++;}
            }finally{MfNative.Release(mediaEvent);}
        }
        throw new InvalidOperationException("The hardware encoder event queue exceeded its processing limit.");
    }

    private EncodedVideoFrame? ReadOutput(long fallbackTimestamp,int formatChanges=0)
    {
        if(events is not null)WaitForEvent(input:false,TimeSpan.FromMilliseconds(500));
        MfNative.Check(transform.GetOutputStreamInfo(0,out var info));
        if(info.Size is <0 or >16*1024*1024)throw new InvalidOperationException("The video encoder requested an oversized frame buffer.");
        MfNative.IMFSample? supplied=null,returned=null;MfNative.IMFMediaBuffer? buffer=null,contiguous=null;
        IntPtr suppliedPointer=IntPtr.Zero;
        var output=new MfNative.OutputDataBuffer[1];
        try{
            if((info.Flags&0x100)==0){
                MfNative.Check(MfNative.MFCreateSample(out supplied));
                MfNative.Check(MfNative.MFCreateMemoryBuffer(Math.Max(info.Size,4096),out buffer));
                MfNative.Check(supplied.AddBuffer(buffer));
                suppliedPointer=Marshal.GetComInterfaceForObject(supplied,typeof(MfNative.IMFSample));output[0].Sample=suppliedPointer;
            }
            var result=transform.ProcessOutput(0,1,output,out _);
            if(result==MfNative.NeedMoreInput){if(events is not null)throw new InvalidOperationException("The hardware encoder reported output before producing a frame.");return null;}
            if(result==MfNative.StreamChange){
                if(formatChanges>=2)throw new InvalidOperationException("The video encoder repeatedly changed its output format.");
                MfNative.IMFMediaType? updated=null;
                try{
                    MfNative.Check(transform.GetOutputAvailableType(0,0,out updated));
                    ConfigureType(updated,MfNative.H264);Set(updated,MfNative.AverageBitrate,bitrate);Set(updated,MfNative.Profile,66);
                    MfNative.Check(transform.SetOutputType(0,updated,0));RefreshSequenceHeader();
                }finally{MfNative.Release(updated);}
                return ReadOutput(fallbackTimestamp,formatChanges+1);
            }
            MfNative.Check(result);
            if(events is not null)outputCredits--;
            if(output[0].Sample==IntPtr.Zero)throw new InvalidOperationException("The video encoder returned no sample.");
            // Re-importing our own sample pointer would increment its RCW count on every frame.
            returned=output[0].Sample==suppliedPointer?(supplied??throw new InvalidOperationException("The encoder sample is unavailable.")):(MfNative.IMFSample)Marshal.GetObjectForIUnknown(output[0].Sample);
            MfNative.Check(returned.ConvertToContiguousBuffer(out contiguous));
            MfNative.Check(contiguous.Lock(out var data,out _,out var length));
            byte[] encoded;
            try{
                if(length is <4 or >MaximumFrameBytes)throw new InvalidOperationException("The video encoder returned an invalid frame size.");
                encoded=new byte[length];Marshal.Copy(data,encoded,0,length);
            }finally{MfNative.Check(contiguous.Unlock());}
            if(sequenceHeader.Length==0)RefreshSequenceHeader();
            encoded=NormalizeAccessUnit(encoded,nalLengthSize);
            var key=ContainsNal(encoded,5);
            if(returned.GetUINT32(ref MfNative.CleanPoint,out var clean)>=0)key|=clean!=0;
            if(!ContainsStartCode(encoded))throw new InvalidOperationException("The video encoder returned an unsupported H.264 format.");
            if(sequenceHeader.Length==0)RefreshSequenceHeader();
            if(key&&!ContainsNal(encoded,7)){
                if(sequenceHeader.Length==0)throw new InvalidOperationException("The video encoder did not provide H.264 configuration.");
                if(encoded.Length>MaximumFrameBytes-sequenceHeader.Length)throw new InvalidOperationException("The encoded video frame exceeds its transfer limit.");
                var combined=new byte[sequenceHeader.Length+encoded.Length];sequenceHeader.CopyTo(combined,0);encoded.CopyTo(combined,sequenceHeader.Length);encoded=combined;
            }
            var timestamp=returned.GetSampleTime(out var pts)>=0?pts:fallbackTimestamp;
            emittedFrames++;return new(encoded,key,timestamp);
        }finally{
            if(output[0].Events!=IntPtr.Zero)Marshal.Release(output[0].Events);
            if(output[0].Sample!=IntPtr.Zero&&output[0].Sample!=suppliedPointer)Marshal.Release(output[0].Sample);
            if(suppliedPointer!=IntPtr.Zero)Marshal.Release(suppliedPointer);
            MfNative.Release(contiguous);
            // supplied and returned may be the same RCW; release it exactly once.
            if(returned is not null&&!ReferenceEquals(returned,supplied))MfNative.Release(returned);
            MfNative.Release(buffer);MfNative.Release(supplied);
        }
    }

    private void ConfigureType(MfNative.IMFMediaType type,Guid subtype)
    {
        Set(type,MfNative.MajorType,MfNative.Video);Set(type,MfNative.Subtype,subtype);
        Set(type,MfNative.FrameSize,((long)width<<32)|(uint)height);Set(type,MfNative.FrameRate,((long)rate<<32)|1);
        Set(type,MfNative.PixelAspectRatio,1L<<32|1);Set(type,MfNative.InterlaceMode,2);
        Set(type,new Guid("3e23d450-2c75-4d25-a00e-b91670d12327"),1); // BT.709 matrix.
        Set(type,new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11"),2); // Limited-range NV12.
    }
    private void RefreshSequenceHeader()
    {
        MfNative.IMFMediaType? type=null;
        try{
            MfNative.Check(transform.GetOutputCurrentType(0,out type));
            if(type.GetBlobSize(ref MfNative.SequenceHeader,out var size)<0)return;
            if(size is <4 or >65536)throw new InvalidOperationException("The encoder configuration is outside its size limit.");
            var bytes=new byte[size];MfNative.Check(type.GetBlob(ref MfNative.SequenceHeader,bytes,bytes.Length,out var actual));
            if(actual!=size)throw new InvalidOperationException("The encoder configuration is invalid.");
            var normalized=NormalizeSequenceHeader(bytes);sequenceHeader=normalized.Bytes;nalLengthSize=normalized.LengthSize;
        }finally{MfNative.Release(type);}
    }
    private static void Set(MfNative.IMFAttributes value,Guid key,Guid data)=>MfNative.Check(value.SetGUID(ref key,ref data));
    private static void Set(MfNative.IMFAttributes value,Guid key,int data)=>MfNative.Check(value.SetUINT32(ref key,data));
    private static void Set(MfNative.IMFAttributes value,Guid key,long data)=>MfNative.Check(value.SetUINT64(ref key,data));
    private static void SetCodec(MfNative.IMFTransform value,Guid key,object data)
    {
        if(value is not MfNative.ICodecAPI codec)return;
        var result=codec.SetValue(ref key,ref data);
        // Optional codec controls differ across installed Windows encoders; negotiated media types remain authoritative.
        if(result<0&&result!=unchecked((int)0x80004001)&&result!=unchecked((int)0x80070057)&&result!=unchecked((int)0x80070490))MfNative.Check(result);
    }
    internal static bool ContainsStartCode(ReadOnlySpan<byte> bytes)=>bytes.Length>=4&&bytes[0]==0&&bytes[1]==0&&(bytes[2]==1||(bytes[2]==0&&bytes[3]==1));
    internal static byte[] NormalizeAccessUnit(byte[] bytes,int lengthSize)
    {
        if(bytes.Length is <2 or >MaximumFrameBytes)throw new InvalidOperationException("The encoder returned an invalid access unit size.");
        if(ContainsStartCode(bytes))return bytes;
        if(lengthSize is <1 or >4)throw new InvalidOperationException("The encoder returned an invalid NAL length size.");
        using var result=new MemoryStream();var at=0;var nals=0;
        while(at<bytes.Length){
            if(bytes.Length-at<lengthSize||++nals>1024)throw new InvalidOperationException("The encoder returned a malformed NAL sequence.");
            uint length=0;for(var i=0;i<lengthSize;i++)length=(length<<8)|bytes[at++];
            if(length==0||length>bytes.Length-at||(long)result.Length+4+length>MaximumFrameBytes)throw new InvalidOperationException("The encoder returned an invalid NAL length.");
            result.Write(new byte[]{0,0,0,1});result.Write(bytes,at,(int)length);at+=(int)length;
        }
        return result.ToArray();
    }
    internal static (byte[] Bytes,int LengthSize) NormalizeSequenceHeader(byte[] bytes)
    {
        if(bytes.Length is <4 or >65536)throw new InvalidOperationException("The encoder configuration is outside its size limit.");
        if(ContainsStartCode(bytes)&&ContainsNal(bytes,7)&&ContainsNal(bytes,8))return(bytes,4);
        if(bytes.Length<7||bytes[0]!=1)throw new InvalidOperationException("The encoder configuration is unsupported.");
        var lengthSize=(bytes[4]&3)+1;var at=6;var sps=bytes[5]&31;
        if(sps==0)throw new InvalidOperationException("The encoder configuration has no SPS.");
        using var result=new MemoryStream();
        void Append(int count,int kind){for(var index=0;index<count;index++){
            if(bytes.Length-at<2)throw new InvalidOperationException("The encoder configuration is truncated.");
            var length=(bytes[at]<<8)|bytes[at+1];at+=2;
            if(length==0||length>bytes.Length-at||(bytes[at]&31)!=kind||result.Length+4+length>65536)throw new InvalidOperationException("The encoder configuration has an invalid parameter set.");
            result.Write(new byte[]{0,0,0,1});result.Write(bytes,at,length);at+=length;
        }}
        Append(sps,7);if(at>=bytes.Length)throw new InvalidOperationException("The encoder configuration has no PPS.");
        var pps=bytes[at++];if(pps==0)throw new InvalidOperationException("The encoder configuration has no PPS.");Append(pps,8);
        return(result.ToArray(),lengthSize);
    }
    internal static bool ContainsNal(ReadOnlySpan<byte> bytes,int kind)
    {
        for(var i=0;i+3<bytes.Length;i++){
            if(bytes[i]!=0||bytes[i+1]!=0)continue;
            var offset=bytes[i+2]==1?3:bytes[i+2]==0&&bytes[i+3]==1?4:0;
            if(offset>0&&i+offset<bytes.Length&&(bytes[i+offset]&31)==kind)return true;
        }
        return false;
    }

    internal static void BgraToNv12(byte[] bgra,int stride,int width,int height,byte[] destination)
    {
        if(width<2||height<2||(width&1)!=0||(height&1)!=0||stride<width*4||(long)stride*height>bgra.Length||destination.Length!=(long)width*height*3/2)
            throw new ArgumentException("The captured video frame has invalid pixel dimensions.");
        var uvStart=width*height;
        // Each iteration owns disjoint two-row luma and chroma spans. No per-frame pixel allocations.
        Parallel.For(0,height/2,new ParallelOptions{MaxDegreeOfParallelism=Math.Min(Environment.ProcessorCount,4)},row=>{
            var source=row*2*stride;var y=row*2*width;var uv=uvStart+row*width;
            for(var column=0;column<width;column+=2){
                var top=source+column*4;var bottom=top+stride;
                var b0=bgra[top];var g0=bgra[top+1];var r0=bgra[top+2];var b1=bgra[top+4];var g1=bgra[top+5];var r1=bgra[top+6];
                var b2=bgra[bottom];var g2=bgra[bottom+1];var r2=bgra[bottom+2];var b3=bgra[bottom+4];var g3=bgra[bottom+5];var r3=bgra[bottom+6];
                destination[y+column]=Luma(r0,g0,b0);destination[y+column+1]=Luma(r1,g1,b1);destination[y+width+column]=Luma(r2,g2,b2);destination[y+width+column+1]=Luma(r3,g3,b3);
                var r=(r0+r1+r2+r3+2)/4;var g=(g0+g1+g2+g3+2)/4;var b=(b0+b1+b2+b3+2)/4;
                destination[uv+column]=(byte)Math.Clamp(((-26*r-86*g+112*b+128)>>8)+128,16,240);
                destination[uv+column+1]=(byte)Math.Clamp(((112*r-102*g-10*b+128)>>8)+128,16,240);
            }
        });
    }
    private static byte Luma(int r,int g,int b)=>(byte)Math.Clamp(((47*r+157*g+16*b+128)>>8)+16,16,235);
    public void Dispose()
    {
        if(disposed)return;
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("The video encoder must be disposed on its owning stream worker.");
        disposed=true;
        try{transform.ProcessMessage(MfNative.CommandFlush,IntPtr.Zero);}
        finally{
            try{ReleaseTransform();}
            finally{MfNative.MFShutdown();if(ownsComApartment)MfNative.CoUninitialize();Array.Clear(nv12);sequenceHeader=[];}
        }
    }

    private void ReleaseTransform()
    {
        events=null;inputCredits=outputCredits=0;
        try{if(activation is not null)activation.ShutdownObject();else if(transform is MfNative.IMFShutdown shutdown)shutdown.Shutdown();}
        finally{MfNative.Release(transform);transform=null!;MfNative.Release(activation);activation=null;}
    }

    internal static void ReleaseActivationArray(IntPtr array,int count)
    {
        if(array==IntPtr.Zero)return;
        try{for(var index=0;index<Math.Max(0,count);index++){var pointer=Marshal.ReadIntPtr(array+(nint)index*IntPtr.Size);if(pointer!=IntPtr.Zero)Marshal.Release(pointer);}}
        finally{Marshal.FreeCoTaskMem(array);}
    }
}

// A failed format is skipped for five minutes on a fresh session. This never switches a
// published stream's codec mid-session and never grows with arbitrary remote inputs.
internal sealed class HardwareEncoderRetryWindow
{
    private readonly object gate=new();
    private readonly Dictionary<string,long> failures=new(StringComparer.Ordinal);
    internal static long RetryTicks=>5*60*Stopwatch.Frequency;
    internal int Count{get{lock(gate)return failures.Count;}}
    internal bool Blocked(string key,long now){lock(gate){return failures.TryGetValue(key,out var failed)&&now-failed>=0&&now-failed<RetryTicks;}}
    internal void Failed(string key,long now)
    {
        if(key.Length is <1 or >256)throw new ArgumentException("Invalid encoder identity.");
        lock(gate){
            foreach(var expired in failures.Where(entry=>now-entry.Value>=RetryTicks||now<entry.Value).Select(entry=>entry.Key).ToArray())failures.Remove(expired);
            if(failures.Count>=64&&!failures.ContainsKey(key))failures.Remove(failures.MinBy(entry=>entry.Value).Key);
            failures[key]=now;
        }
    }
}
