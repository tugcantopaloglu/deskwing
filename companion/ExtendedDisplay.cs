using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TabletDeck;

public sealed record ExtendedDisplayStart(int Width=1280,int Height=800,bool ShareWithTablet=false,bool AcceptInstalledDriver=false,int RefreshRate=60);
public sealed record ExtendedDisplayFrameRequest(int Width=960);
public sealed record ExtendedDisplayFrame(long Sequence,int Width,int Height,string Image,DateTimeOffset CapturedAt,double AgeMs);
public sealed record ExtendedDisplayDriver(string Name,string Version,bool CatalogTrusted,string? Signer,string? Thumbprint,string Detail);
public sealed record ExtendedDesktop(string SourceName,string MonitorName,int X,int Y,int Width,int Height,bool SharesDesktopSource,bool IsOwned);
public sealed record ExtendedDisplayStatus(bool Available,bool Active,bool SharedWithTablet,bool IndependentDesktop,string? SourceName,int Width,int Height,ExtendedDisplayDriver? Driver,string Detail,ExtendedDesktop[] Displays,double RefreshRate=0);

internal sealed record DisplayVideoTarget(Guid Monitor,long Generation,ulong Adapter,uint TargetId,string SourceKey,string SourceName,int X,int Y,int Width,int Height,double RefreshRate,CancellationToken Invalidated);

// Original user-mode interop for the installed SudoVDA 0.2 protocol, not a bundled driver.
// Protocol reference/license and operational limits: docs/EXTENDED-DISPLAY.md.
// Construction and Status are read-only. Only the local-admin route may call Start/Stop/SetSharing.
public sealed class ExtendedDisplay : IDisposable
{
    internal interface IPlatform
    {
        Native.Installed? FindDriver();
        Native.DesktopPath[] ReadPaths();
        SafeFileHandle Open(string path);
        byte[] Ioctl(SafeFileHandle handle,uint code,byte[]? input,int outputSize);
    }
    private sealed class WindowsPlatform : IPlatform
    {
        public Native.Installed? FindDriver()=>Native.FindDriver();
        public Native.DesktopPath[] ReadPaths()=>Native.ReadPaths();
        public SafeFileHandle Open(string path)=>Native.Open(path);
        public byte[] Ioctl(SafeFileHandle handle,uint code,byte[]? input,int outputSize)=>Native.Ioctl(handle,code,input,outputSize);
    }
    private readonly IPlatform platform;
    public ExtendedDisplay():this(new WindowsPlatform()){}
    internal ExtendedDisplay(IPlatform platform){this.platform=platform;}
    private readonly object sync=new();
    private readonly object diagnosticGate=new();
    private Native.Installed? diagnosticDriver;
    private long diagnosticAt;
    private bool diagnosticRead;
    private readonly SemaphoreSlim captureGate=new(1,1);
    private SafeFileHandle? session;
    private System.Threading.Timer? heartbeat;
    private Guid? monitorGuid;
    private ulong adapterLuid;
    private uint targetId;
    private bool shared,disposed;
    private string? sessionError;
    private ExtendedDisplayDriver? sessionDriver;
    private long sequence,attemptTicks,captureTicks;
    private ExtendedDisplayFrame? cached;
    private int requestedWidth;
    private long videoGeneration;
    private CancellationTokenSource videoCancellation=new();
    private const int MaxJpegBytes=384*1024;
    private const uint AddMonitor=0x222000,RemoveMonitor=0x222004,Ping=0x222220,Protocol=0x2223fc;

    public ExtendedDisplayStatus Status()
    {
        Native.Installed? installed;
        // Driver catalog verification is a diagnostic, not part of frame authorization. Never
        // hold the capture/ownership lock while Windows verifies catalog signatures.
        try{installed=DriverDiagnostics();}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {lock(sync)return new(false,session is {IsInvalid:false,IsClosed:false},false,false,null,0,0,sessionDriver,"Display diagnostics unavailable: "+ex.Message,[]);}
        lock(sync)
        {
            try
            {
                var paths=platform.ReadPaths();
                var driver=sessionDriver??installed?.Info;
                var own=monitorGuid is null?null:paths.FirstOrDefault(p=>p.Adapter==adapterLuid&&p.TargetId==targetId);
                var independent=own is not null&&IsIndependent(own,paths);
                var active=session is {IsInvalid:false,IsClosed:false}&&monitorGuid is not null;
                var detail=sessionError??(active?(independent?(shared?"Independent tablet desktop is shared. Only this session's display can be captured.":"Independent desktop created. Enable tablet sharing on this PC to send frames."):"Monitor created; Windows has not exposed an independent desktop. In Windows Display settings choose Extend for TabletDeck. Physical displays are unchanged."):(installed?.Info.Detail??"Install a compatible, reviewed virtual display driver using its publisher's administrator setup. TouchBar controls work without it."));
                return new(installed?.Usable==true,active,active&&shared,active&&independent,own?.SourceName,own?.Width??0,own?.Height??0,driver,detail,
                    paths.Select(p=>new ExtendedDesktop(p.SourceName,p.Name,p.X,p.Y,p.Width,p.Height,p.IsClone,own==p)).ToArray(),own?.RefreshRate??0);
            }
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {
                return new(false,session is {IsInvalid:false,IsClosed:false},false,false,null,0,0,sessionDriver,"Display diagnostics unavailable: "+ex.Message,[]);
            }
        }
    }

    private Native.Installed? DriverDiagnostics()
    {
        lock(diagnosticGate)
        {
            if(diagnosticRead&&Stopwatch.GetElapsedTime(diagnosticAt)<TimeSpan.FromSeconds(30))return diagnosticDriver;
            diagnosticDriver=platform.FindDriver();diagnosticAt=Stopwatch.GetTimestamp();diagnosticRead=true;
            return diagnosticDriver;
        }
    }

    public async Task<ExtendedDisplayStatus> StartAsync(ExtendedDisplayStart request,CancellationToken ct=default)
    {
        Validate(request);
        Guid startedGuid;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(monitorGuid is not null)throw new InvalidOperationException("A TabletDeck display already exists. Stop it before creating another.");
            ct.ThrowIfCancellationRequested();
            var driver=platform.FindDriver()??throw new InvalidOperationException("No installed SudoVDA device interface was found. No driver was installed or changed.");
            if(!driver.Usable)throw new InvalidOperationException(driver.Info.Detail);
            if(platform.ReadPaths().Any(p=>Native.SameDevice(p.AdapterPath,driver.Path)))throw new InvalidOperationException("SudoVDA already has an active display owned by another application. Stop that session before creating a TabletDeck display.");
            var handle=platform.Open(driver.Path);
            Guid created=Guid.NewGuid();
            var addAttempted=false;
            try
            {
                var version=platform.Ioctl(handle,Protocol,null,4);
                if(version.Length!=4||version[0]!=0||version[1]!=2)throw new InvalidOperationException("Installed SudoVDA protocol is unsupported. Expected version 0.2; no display was created.");
                var input=new byte[56];
                BitConverter.GetBytes(request.Width).CopyTo(input,0);
                BitConverter.GetBytes(request.Height).CopyTo(input,4);
                BitConverter.GetBytes(request.RefreshRate).CopyTo(input,8);
                created.ToByteArray().CopyTo(input,12);
                Encoding.ASCII.GetBytes("TabletDeck").CopyTo(input,28);
                Encoding.ASCII.GetBytes(created.ToString("N")[..12]).CopyTo(input,42);
                addAttempted=true;
                var output=platform.Ioctl(handle,AddMonitor,input,12);
                if(output.Length!=12)throw new InvalidOperationException("SudoVDA returned an invalid monitor identity.");
                adapterLuid=BitConverter.ToUInt64(output,0);targetId=BitConverter.ToUInt32(output,8);
                session=handle;monitorGuid=created;shared=request.ShareWithTablet;sessionDriver=driver.Info;sessionError=null;
                InvalidateVideo();
                startedGuid=created;
                cached=null;attemptTicks=0;captureTicks=0;
                heartbeat=new System.Threading.Timer(_=>Heartbeat(),null,TimeSpan.FromMilliseconds(400),TimeSpan.FromMilliseconds(400));
            }
            catch
            {
                // Only our unpredictable GUID is eligible for cleanup, even if the add response was malformed.
                if(addAttempted)try{platform.Ioctl(handle,RemoveMonitor,created.ToByteArray(),0);}catch(Exception ex) when(ex is not OutOfMemoryException){}
                handle.Dispose();throw;
            }
        }
        // Windows activation is asynchronous. We never globally extend/clone or change existing monitor positions.
        try
        {
            for(var attempt=0;attempt<20;attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var state=Status();if(state.IndependentDesktop||!state.Active)return state;
                await Task.Delay(100,ct);
            }
            return Status();
        }
        catch(OperationCanceledException)
        {
            lock(sync){if(monitorGuid==startedGuid)Stop();}
            throw;
        }
    }

    public static void Validate(ExtendedDisplayStart request)
    {
        if((request.Width,request.Height) is not ((1280,800) or (1600,900) or (1920,1080)))throw new ArgumentException("Choose 1280×800, 1600×900, or 1920×1080.");
        if(request.RefreshRate is <60 or >240)throw new ArgumentException("Choose a display refresh rate from 60 to 240 Hz.");
        if(!request.AcceptInstalledDriver)throw new ArgumentException("Review and explicitly accept the installed driver on this PC before creating a display.");
    }

    public ExtendedDisplayStatus SetSharing(bool enabled)
    {
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(monitorGuid is null)throw new InvalidOperationException("Create a TabletDeck display on this PC first.");
            shared=enabled;cached=null;captureTicks=0;InvalidateVideo();
            return Status();
        }
    }

    public ExtendedDisplayStatus Stop()
    {
        lock(sync)
        {
            shared=false;cached=null;InvalidateVideo();
            if(session is not null)
            {
                try{if(monitorGuid is Guid guid&&!session.IsInvalid&&!session.IsClosed)platform.Ioctl(session,RemoveMonitor,guid.ToByteArray(),0);}
                catch(Exception ex) when(ex is not OutOfMemoryException)
                {
                    // Retain ownership so the PC can retry removing this exact GUID; never report a failed removal as stopped.
                    sessionError="Removing the TabletDeck monitor failed. Sharing is off; retry Stop display: "+ex.Message;
                    return Status();
                }
                heartbeat?.Dispose();heartbeat=null;session.Dispose();session=null;monitorGuid=null;sessionDriver=null;sessionError=null;
            }
            return Status();
        }
    }

    private void Heartbeat()
    {
        lock(sync)
        {
            if(disposed||session is null||monitorGuid is null)return;
            try{platform.Ioctl(session,Ping,null,0);}
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {
                shared=false;cached=null;InvalidateVideo();sessionError="Virtual display connection failed: "+ex.Message;
                heartbeat?.Dispose();heartbeat=null;session.Dispose();session=null;monitorGuid=null;
            }
        }
    }

    public async Task<ExtendedDisplayFrame> FrameAsync(ExtendedDisplayFrameRequest request,CancellationToken ct=default)
    {
        if(request.Width is not (640 or 960 or 1280))throw new ArgumentException("Choose a supported desktop preview width.");
        if(!await captureGate.WaitAsync(0,ct))throw new InvalidOperationException("Desktop capture is busy; the next frame will retry without a queue.");
        try
        {
            lock(sync)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                if(!shared||session is null||monitorGuid is null)throw new InvalidOperationException("Desktop sharing is off. Enable sharing on the PC after creating a display.");
                var paths=platform.ReadPaths();
                var own=paths.FirstOrDefault(p=>p.Adapter==adapterLuid&&p.TargetId==targetId);
                if(own is null||!IsIndependent(own,paths))throw new InvalidOperationException("The TabletDeck monitor is not an independent extended desktop. Select Extend in Windows Display settings.");
                if(own.Width>3840||own.Height>2160)throw new InvalidOperationException("The virtual desktop exceeds the bounded capture size.");
                var age=captureTicks==0?double.MaxValue:Stopwatch.GetElapsedTime(captureTicks).TotalMilliseconds;
                if(cached is not null&&requestedWidth==request.Width&&age<250)return cached with{AgeMs=age};
                if(attemptTicks!=0&&Stopwatch.GetElapsedTime(attemptTicks).TotalMilliseconds<250)throw new InvalidOperationException("Desktop capture is rate limited. Retry shortly.");
                attemptTicks=Stopwatch.GetTimestamp();ct.ThrowIfCancellationRequested();
                var width=Math.Min(request.Width,own.Width);var height=Math.Max(8,(int)Math.Round((double)width*own.Height/own.Width));
                if(height>1280){height=1280;width=Math.Max(8,(int)Math.Round((double)height*own.Width/own.Height));}
                // Scope DPI awareness to this capture thread, restoring it before leaving the method.
                using var dpi=new CaptureDpiScope();
                using var full=new Bitmap(own.Width,own.Height,PixelFormat.Format24bppRgb);
                using(var graphics=Graphics.FromImage(full))graphics.CopyFromScreen(own.X,own.Y,0,0,full.Size,CopyPixelOperation.SourceCopy);
                using var resized=new Bitmap(width,height,PixelFormat.Format24bppRgb);
                using(var graphics=Graphics.FromImage(resized)){graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;graphics.DrawImage(full,0,0,width,height);}
                // Verify ownership/topology again after capture. A topology change discards the bitmap.
                var afterPaths=platform.ReadPaths();var after=afterPaths.FirstOrDefault(p=>p.Adapter==adapterLuid&&p.TargetId==targetId);
                if(after is null||!IsIndependent(after,afterPaths)||after.SourceKey!=own.SourceKey||after.X!=own.X||after.Y!=own.Y||after.Width!=own.Width||after.Height!=own.Height)throw new InvalidOperationException("Display topology changed during capture; the frame was discarded.");
                using var bytes=new MemoryStream();using var options=new EncoderParameters(1);options.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,55L);
                resized.Save(bytes,ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid),options);
                if(bytes.Length>MaxJpegBytes)throw new InvalidOperationException("Desktop frame exceeds the transfer limit. Choose a smaller preview width.");
                ct.ThrowIfCancellationRequested();requestedWidth=request.Width;captureTicks=Stopwatch.GetTimestamp();
                return cached=new(++sequence,width,height,"data:image/jpeg;base64,"+Convert.ToBase64String(bytes.GetBuffer(),0,(int)bytes.Length),DateTimeOffset.UtcNow,0);
            }
        }
        finally{captureGate.Release();}
    }

    // Only the monitor this process created may enter the video encoder. Topology inspection is
    // independent of expensive driver/catalog diagnostics and capture never holds this lock.
    internal DisplayVideoTarget CaptureVideoTarget()
    {
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!shared||session is not {IsInvalid:false,IsClosed:false}||monitorGuid is not Guid owned)
                throw new InvalidOperationException("Create a Second display and enable sharing on the PC first.");
            var paths=platform.ReadPaths();
            var own=paths.FirstOrDefault(p=>p.Adapter==adapterLuid&&p.TargetId==targetId);
            if(own is null||!IsIndependent(own,paths))throw new InvalidOperationException("The DeskWing display must be an independent extended desktop.");
            if((own.Width,own.Height) is not ((1280,800) or (1600,900) or (1920,1080)))
                throw new InvalidOperationException("Choose a supported Second display size on the PC before starting video.");
            if(!double.IsFinite(own.RefreshRate)||own.RefreshRate is <1 or >1000)
                throw new InvalidOperationException("Windows did not report a valid refresh rate for the Second display.");
            return new(owned,videoGeneration,adapterLuid,targetId,own.SourceKey,own.SourceName,own.X,own.Y,own.Width,own.Height,own.RefreshRate,videoCancellation.Token);
        }
    }

    internal void ValidateVideoTarget(DisplayVideoTarget target)
    {
        lock(sync)
        {
            if(target.Invalidated.IsCancellationRequested)throw new InvalidOperationException("Second display sharing changed. Open the display again when ready.");
            var current=CaptureVideoTarget();
            if(current!=target)throw new InvalidOperationException("The Second display moved, changed refresh rate, or was replaced. Open it again after arranging the PC desktop.");
        }
    }

    private void InvalidateVideo()
    {
        videoGeneration++;
        var old=videoCancellation;videoCancellation=new();
        old.Cancel();old.Dispose();
    }

    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;
            try{Stop();}
            finally{disposed=true;shared=false;cached=null;InvalidateVideo();videoCancellation.Dispose();heartbeat?.Dispose();heartbeat=null;session?.Dispose();session=null;}
        }
    }

    private static bool IsIndependent(Native.DesktopPath own,Native.DesktopPath[] paths)
    {
        if(own.IsClone||own.Width<8||own.Height<8||!paths.Any(p=>p.SourceKey!=own.SourceKey))return false;
        var bounds=new Rectangle(own.X,own.Y,own.Width,own.Height);
        return !paths.Any(p=>p.SourceKey!=own.SourceKey&&bounds.IntersectsWith(new Rectangle(p.X,p.Y,p.Width,p.Height)));
    }
    private sealed class CaptureDpiScope : IDisposable
    {
        [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        private readonly IntPtr previous;
        public CaptureDpiScope(){previous=SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot establish physical-pixel display capture coordinates.");}
        public void Dispose(){if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
    }

    internal static class Native
    {
        private static readonly Guid InterfaceGuid=new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
        internal sealed record Installed(string Path,ExtendedDisplayDriver Info,bool Usable);
        internal sealed record DesktopPath(ulong Adapter,uint TargetId,string SourceKey,string SourceName,string Name,string AdapterPath,int X,int Y,int Width,int Height,bool IsClone,double RefreshRate=60);
        [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo{public uint Size;public Guid Class;public uint DevInst;public IntPtr Reserved;}
        [StructLayout(LayoutKind.Sequential)] private struct InterfaceInfo{public uint Size;public Guid Class;public uint Flags;public IntPtr Reserved;}
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid,IntPtr enumerator,IntPtr window,uint flags);
        [DllImport("setupapi.dll",SetLastError=true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices,IntPtr info,ref Guid guid,uint index,ref InterfaceInfo data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices,ref InterfaceInfo data,IntPtr detail,uint length,out uint required,ref DeviceInfo info);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devices,ref DeviceInfo info,uint property,out uint type,byte[] buffer,uint size,out uint needed);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SetupGetInfDriverStoreLocation(string file,IntPtr platform,IntPtr locale,StringBuilder result,uint size,out uint required);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle device,uint control,byte[]? input,uint inputSize,byte[] output,uint outputSize,out uint returned,IntPtr overlapped);

        internal static SafeFileHandle Open(string path)
        {
            var handle=CreateFile(path,0xc0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
            if(handle.IsInvalid){var error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error,error==5?"Windows denied access to the installed SudoVDA interface. Review its device permissions with its publisher; TabletDeck will not elevate or change permissions.":"Cannot open the installed SudoVDA device interface.");}
            return handle;
        }
        internal static byte[] Ioctl(SafeFileHandle handle,uint code,byte[]? input,int outputSize)
        {
            var output=new byte[outputSize];
            if(!DeviceIoControl(handle,code,input,(uint)(input?.Length??0),output,(uint)outputSize,out var returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"The installed SudoVDA driver rejected the requested operation.");
            if(returned>output.Length)throw new InvalidOperationException("Driver returned an invalid response size.");
            return output[..(int)returned];
        }
        private static string Property(IntPtr devices,ref DeviceInfo info,uint property)
        {
            var data=new byte[8192];
            return SetupDiGetDeviceRegistryProperty(devices,ref info,property,out _,data,(uint)data.Length,out var needed)?Encoding.Unicode.GetString(data,0,(int)Math.Min(needed,(uint)data.Length)).TrimEnd('\0'):"";
        }
        internal static bool SameDevice(string a,string b)=>a.LastIndexOf('#')>0&&b.LastIndexOf('#')>0&&string.Equals(a[..a.LastIndexOf('#')],b[..b.LastIndexOf('#')],StringComparison.OrdinalIgnoreCase);
        internal static Installed? FindDriver()
        {
            var guid=InterfaceGuid;var devices=SetupDiGetClassDevs(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
            if(devices==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for(uint index=0;index<16;index++)
                {
                    var iface=new InterfaceInfo{Size=(uint)Marshal.SizeOf<InterfaceInfo>()};
                    if(!SetupDiEnumDeviceInterfaces(devices,IntPtr.Zero,ref guid,index,ref iface)){var error=Marshal.GetLastWin32Error();if(error==259)return null;throw new Win32Exception(error);}
                    var info=new DeviceInfo{Size=(uint)Marshal.SizeOf<DeviceInfo>()};
                    SetupDiGetDeviceInterfaceDetail(devices,ref iface,IntPtr.Zero,0,out var length,ref info);
                    if(length<8||length>32768)throw new InvalidOperationException("Invalid display interface path length.");
                    var buffer=Marshal.AllocHGlobal((int)length);
                    try
                    {
                        Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                        if(!SetupDiGetDeviceInterfaceDetail(devices,ref iface,buffer,length,out _,ref info))throw new Win32Exception(Marshal.GetLastWin32Error());
                        var path=Marshal.PtrToStringUni(IntPtr.Add(buffer,4))??"";
                        var hardware=Property(devices,ref info,1);
                        if(!hardware.Split('\0').Any(id=>id.Equals("root\\sudomaker\\sudovda",StringComparison.OrdinalIgnoreCase)||id.Equals("SudoVDA",StringComparison.OrdinalIgnoreCase)))continue;
                        var name=Property(devices,ref info,12);if(name.Length==0)name=Property(devices,ref info,0);
                        var key=Property(devices,ref info,9);
                        using var registry=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+key);
                        var inf=registry?.GetValue("InfPath") as string;
                        if(string.IsNullOrEmpty(inf)||Path.GetFileName(inf)!=inf)return new(path,new(name,"",false,null,null,"The installed driver's INF identity could not be verified."),false);
                        var store=new StringBuilder(32768);
                        if(!SetupGetInfDriverStoreLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF",inf),IntPtr.Zero,IntPtr.Zero,store,(uint)store.Capacity,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
                        var folder=Path.GetDirectoryName(store.ToString())!;
                        var catalog=Path.Combine(folder,"sudovda.cat");var binary=Path.Combine(folder,"SudoVDA.dll");
                        var version=FileVersionInfo.GetVersionInfo(binary).FileVersion??registry?.GetValue("DriverVersion") as string??"unknown";
                        var trusted=VerifyCatalog(catalog,store.ToString())&&VerifyCatalog(catalog,binary);
                        string? signer=null,thumbprint=null;
                        if(File.Exists(catalog))
                        {
#pragma warning disable SYSLIB0057
                            using var cert=new X509Certificate2(X509Certificate.CreateFromSignedFile(catalog));
#pragma warning restore SYSLIB0057
                            signer=cert.Subject;thumbprint=cert.Thumbprint;
                        }
                        if(!trusted)return new(path,new(name,version,false,signer,thumbprint,"Installed SudoVDA catalog trust or package membership verification failed. No monitor will be created."),false);
                        try{using var access=Open(path);}
                        catch(Win32Exception ex){return new(path,new(name,version,true,signer,thumbprint,ex.Message),false);}
                        return new(path,new(name,version,true,signer,thumbprint,"Installed SudoVDA package verifies against this PC's trust store. Publisher: "+signer+". This is not a WHQL certification claim. Explicit acceptance on this PC is required."),true);
                    }
                    finally{Marshal.FreeHGlobal(buffer);}
                }
                return null;
            }
            finally{SetupDiDestroyDeviceInfoList(devices);}
        }

        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct CatalogInfo
        {
            public uint Size,Version;
            [MarshalAs(UnmanagedType.LPWStr)]public string CatalogPath;
            [MarshalAs(UnmanagedType.LPWStr)]public string MemberTag;
            [MarshalAs(UnmanagedType.LPWStr)]public string MemberPath;
            public IntPtr MemberFile,Hash;public uint HashSize;public IntPtr CatalogContext,Admin;
        }
        [StructLayout(LayoutKind.Sequential)] private struct TrustData
        {
            public uint Size;public IntPtr Policy,Sip;public uint UI,Revocation,Choice;public IntPtr Info;public uint Action;public IntPtr State,Url;public uint Flags,Context;public IntPtr Signature;
        }
        [DllImport("wintrust.dll",CharSet=CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
        [DllImport("wintrust.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin,IntPtr subsystem,string algorithm,IntPtr policy,uint flags);
        [DllImport("wintrust.dll",SetLastError=true)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin,SafeFileHandle file,ref uint size,byte[]? hash,uint flags);
        [DllImport("wintrust.dll")] private static extern bool CryptCATAdminReleaseContext(IntPtr admin,uint flags);
        private static bool VerifyCatalog(string catalog,string member)
        {
            if(!File.Exists(catalog)||!File.Exists(member))return false;
            if(!CryptCATAdminAcquireContext2(out var admin,IntPtr.Zero,"SHA256",IntPtr.Zero,0))return false;
            IntPtr hashPtr=IntPtr.Zero,infoPtr=IntPtr.Zero;
            try
            {
                using var file=File.OpenHandle(member,FileMode.Open,FileAccess.Read,FileShare.Read);
                uint size=0;CryptCATAdminCalcHashFromFileHandle2(admin,file,ref size,null,0);
                if(size is <1 or >128)return false;
                var hash=new byte[size];if(!CryptCATAdminCalcHashFromFileHandle2(admin,file,ref size,hash,0))return false;
                hashPtr=Marshal.AllocHGlobal((int)size);Marshal.Copy(hash,0,hashPtr,(int)size);
                var info=new CatalogInfo{Size=(uint)Marshal.SizeOf<CatalogInfo>(),CatalogPath=catalog,MemberTag=Convert.ToHexString(hash),MemberPath=member,MemberFile=file.DangerousGetHandle(),Hash=hashPtr,HashSize=size,Admin=admin};
                infoPtr=Marshal.AllocHGlobal(Marshal.SizeOf<CatalogInfo>());Marshal.StructureToPtr(info,infoPtr,false);
                var data=new TrustData{Size=(uint)Marshal.SizeOf<TrustData>(),UI=2,Choice=2,Info=infoPtr,Action=1,Flags=0x1000};
                var action=new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
                try{return WinVerifyTrust(IntPtr.Zero,ref action,ref data)==0;}
                finally{data.Action=2;WinVerifyTrust(IntPtr.Zero,ref action,ref data);}
            }
            finally
            {
                if(infoPtr!=IntPtr.Zero){Marshal.DestroyStructure<CatalogInfo>(infoPtr);Marshal.FreeHGlobal(infoPtr);}
                if(hashPtr!=IntPtr.Zero)Marshal.FreeHGlobal(hashPtr);
                CryptCATAdminReleaseContext(admin,0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;public readonly ulong Value=>((ulong)(uint)High<<32)|Low;}
        [StructLayout(LayoutKind.Sequential)]private struct Header{public uint Type,Size;public Luid Adapter;public uint Id;}
        [StructLayout(LayoutKind.Sequential)]private struct Source{public Luid Adapter;public uint Id,Mode,Status;}
        [StructLayout(LayoutKind.Sequential)]private struct Target{public Luid Adapter;public uint Id,Mode,Technology,Rotation,Scaling,Numerator,Denominator,Scanline;public int Available;public uint Status;}
        [StructLayout(LayoutKind.Sequential)]private struct CcdPath{public Source Source;public Target Target;public uint Flags;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct SourceName{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct TargetName{public Header Header;public uint Flags,Technology;public ushort Manufacturer,Product;public uint Connector;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct AdapterName{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path;}
        [DllImport("user32.dll")]private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")]private static extern int QueryDisplayConfig(uint flags,ref uint paths,IntPtr pathData,ref uint modes,IntPtr modeData,IntPtr topology);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]private static extern int DeviceName(ref SourceName value);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]private static extern int DeviceName(ref TargetName value);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]private static extern int DeviceName(ref AdapterName value);
        internal static DesktopPath[] ReadPaths()
        {
            for(var attempt=0;attempt<4;attempt++)
            {
                var result=GetDisplayConfigBufferSizes(2,out var pathCount,out var modeCount);if(result!=0)throw new Win32Exception(result,"Cannot inspect the local Windows desktop topology.");
                if(pathCount>1024||modeCount>8192)throw new InvalidOperationException("Unsupported desktop topology size.");
                var paths=Marshal.AllocHGlobal(Math.Max(1,checked((int)pathCount*72)));var modes=IntPtr.Zero;
                try
                {
                    modes=Marshal.AllocHGlobal(Math.Max(1,checked((int)modeCount*64)));
                    result=QueryDisplayConfig(2,ref pathCount,paths,ref modeCount,modes,IntPtr.Zero);if(result==122)continue;if(result!=0)throw new Win32Exception(result,"Cannot inspect the local Windows desktop topology.");
                    var list=new List<DesktopPath>();
                    for(var i=0;i<pathCount;i++)
                    {
                        var p=Marshal.PtrToStructure<CcdPath>(IntPtr.Add(paths,i*72));
                        var source=new SourceName{Header=new(){Type=1,Size=(uint)Marshal.SizeOf<SourceName>(),Adapter=p.Source.Adapter,Id=p.Source.Id}};
                        var target=new TargetName{Header=new(){Type=2,Size=(uint)Marshal.SizeOf<TargetName>(),Adapter=p.Target.Adapter,Id=p.Target.Id}};
                        var adapter=new AdapterName{Header=new(){Type=4,Size=(uint)Marshal.SizeOf<AdapterName>(),Adapter=p.Source.Adapter}};
                        if(DeviceName(ref source)!=0||DeviceName(ref target)!=0||DeviceName(ref adapter)!=0)throw new InvalidOperationException("Windows display identity changed during enumeration.");
                        int x=0,y=0,w=0,h=0;
                        if(p.Source.Mode<modeCount){var mode=IntPtr.Add(modes,checked((int)p.Source.Mode*64));if(Marshal.ReadInt32(mode)==1){w=Marshal.ReadInt32(mode,16);h=Marshal.ReadInt32(mode,20);x=Marshal.ReadInt32(mode,28);y=Marshal.ReadInt32(mode,32);}}
                        list.Add(new(p.Target.Adapter.Value,p.Target.Id,p.Source.Adapter.Value+":"+p.Source.Id,source.Name,target.Name,adapter.Path,x,y,w,h,false,p.Target.Denominator==0?0:(double)p.Target.Numerator/p.Target.Denominator));
                    }
                    return list.Select(p=>p with{IsClone=list.Count(other=>other.SourceKey==p.SourceKey)>1}).ToArray();
                }
                finally{if(modes!=IntPtr.Zero)Marshal.FreeHGlobal(modes);Marshal.FreeHGlobal(paths);}
            }
            throw new InvalidOperationException("Windows display topology is changing. Retry shortly.");
        }
    }
}
