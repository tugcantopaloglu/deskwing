using System.Runtime.InteropServices;
using System.Text;

namespace TabletDeck;

// ABI declarations transcribed from the Windows SDK 10.0.26100.0 mfobjects.h,
// mftransform.h, mfapi.h, icodecapi.h and codecapi.h. HRESULTs remain explicit.
internal static class MfNative
{
    internal const int Version=0x00020070,NeedMoreInput=unchecked((int)0xc00d6d72),StreamChange=unchecked((int)0xc00d6d61),NotAccepting=unchecked((int)0xc00d36b0);
    internal const int OutputProvidesSamples=0x100,OutputCanProvideSamples=0x200;
    internal const int CommandFlush=0,CommandDrain=1,NotifyBeginStreaming=0x10000000,NotifyEndStreaming=0x10000001,NotifyEndOfStream=0x10000002,NotifyStartOfStream=0x10000003;
    internal const int NoEventsAvailable=unchecked((int)0xc00d3e80),EventNoWait=1,TransformNeedInput=601,TransformHaveOutput=602;
    internal static Guid H264Encoder=new("6ca50344-051a-4ded-9779-a43305165e35");
    internal static Guid Video=new("73646976-0000-0010-8000-00aa00389b71"),H264=new("34363248-0000-0010-8000-00aa00389b71"),NV12=new("3231564e-0000-0010-8000-00aa00389b71"),IYUV=new("56555949-0000-0010-8000-00aa00389b71");
    internal static Guid MajorType=new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"),Subtype=new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static Guid FrameSize=new("1652c33d-d6b2-4012-b834-72030849a37d"),FrameRate=new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),PixelAspectRatio=new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static Guid InterlaceMode=new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),AverageBitrate=new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"),Profile=new("ad76a80b-2d5c-4e0b-b375-64e520137036"),Level=new("96f66574-11c5-4015-8666-bff516436da7");
    internal static Guid SequenceHeader=new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3"),CleanPoint=new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    internal static Guid LowLatency=new("9c27891a-ed7a-40e1-88e8-b22727a024ee"),RateControlMode=new("1c0608e9-370c-4710-8a58-cb6181c42423"),QualityVsSpeed=new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    internal static Guid MeanBitrate=new("f7222374-2144-4815-b550-a37f8e12ee52"),GopSize=new("95f31b26-95a4-41aa-9303-246a7fc6eef1"),BPictureCount=new("8d390aac-dc5c-4200-b57f-814d04babab2"),ForceKeyFrame=new("398c1b98-8353-475a-9ef2-8f265d260345");
    internal static Guid VideoEncoderCategory=new("f79eac7d-e545-4387-bdee-d647d7bde42a"),TransformAsync=new("f81a699a-649a-497d-8c73-29f8fed6ad7a"),TransformAsyncUnlock=new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    internal static Guid FriendlyName=new("314ffbae-5b41-4c95-9c19-4e7d586face3"),TransformInterface=typeof(IMFTransform).GUID;

    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFStartup(int version,int flags);
    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFShutdown();
    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFCreateMediaType(out IMFMediaType mediaType);
    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFCreateMemoryBuffer(int maximumLength,out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll",ExactSpelling=true)] internal static extern int MFTEnumEx(Guid category,int flags,ref RegisterTypeInfo inputType,ref RegisterTypeInfo outputType,out IntPtr activations,out int count);
    [DllImport("ole32.dll",ExactSpelling=true)] internal static extern int CoInitializeEx(IntPtr reserved,int flags);
    [DllImport("ole32.dll",ExactSpelling=true)] internal static extern void CoUninitialize();

    internal static void Check(int result){if(result<0)Marshal.ThrowExceptionForHR(result);}
    internal static void Release(object? value){if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}

    [StructLayout(LayoutKind.Sequential)] internal struct InputStreamInfo{internal long MaximumLatency;internal int Flags,Size,MaximumLookahead,Alignment;}
    [StructLayout(LayoutKind.Sequential)] internal struct OutputStreamInfo{internal int Flags,Size,Alignment;}
    [StructLayout(LayoutKind.Sequential)] internal struct RegisterTypeInfo{internal Guid MajorType,Subtype;}
    // Raw pointers preserve the two distinct output ownership cases. The caller releases Events,
    // and releases Sample only when the transform supplied a new COM reference.
    [StructLayout(LayoutKind.Sequential)] internal struct OutputDataBuffer{internal int StreamId;internal IntPtr Sample;internal int Status;internal IntPtr Events;}

    [ComImport,Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key,IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key,out int type);
        [PreserveSig] int CompareItem(ref Guid key,IntPtr value,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IMFAttributes other,int matchType,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(ref Guid key,out int value);
        [PreserveSig] int GetUINT64(ref Guid key,out long value);
        [PreserveSig] int GetDouble(ref Guid key,out double value);
        [PreserveSig] int GetGUID(ref Guid key,out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key,out int length);
        [PreserveSig] int GetString(ref Guid key,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int size,out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key,out IntPtr value,out int length);
        [PreserveSig] int GetBlobSize(ref Guid key,out int size);
        [PreserveSig] int GetBlob(ref Guid key,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size,out int actual);
        [PreserveSig] int GetAllocatedBlob(ref Guid key,out IntPtr value,out int size);
        [PreserveSig] int GetUnknown(ref Guid key,ref Guid iid,out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key,IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key,int value);
        [PreserveSig] int SetUINT64(ref Guid key,long value);
        [PreserveSig] int SetDouble(ref Guid key,double value);
        [PreserveSig] int SetGUID(ref Guid key,ref Guid value);
        [PreserveSig] int SetString(ref Guid key,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size);
        [PreserveSig] int SetUnknown(ref Guid key,[MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index,out Guid key,IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport,Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFActivate : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key,out int type);
        [PreserveSig] new int CompareItem(ref Guid key,IntPtr value,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int Compare(IMFAttributes other,int matchType,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key,out int value);
        [PreserveSig] new int GetUINT64(ref Guid key,out long value);
        [PreserveSig] new int GetDouble(ref Guid key,out double value);
        [PreserveSig] new int GetGUID(ref Guid key,out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key,out int length);
        [PreserveSig] new int GetString(ref Guid key,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int size,out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,out IntPtr value,out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key,out int size);
        [PreserveSig] new int GetBlob(ref Guid key,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size,out int actual);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key,out IntPtr value,out int size);
        [PreserveSig] new int GetUnknown(ref Guid key,ref Guid iid,out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key,int value);
        [PreserveSig] new int SetUINT64(ref Guid key,long value);
        [PreserveSig] new int SetDouble(ref Guid key,double value);
        [PreserveSig] new int SetGUID(ref Guid key,ref Guid value);
        [PreserveSig] new int SetString(ref Guid key,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size);
        [PreserveSig] new int SetUnknown(ref Guid key,[MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index,out Guid key,IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int ActivateObject(ref Guid iid,out IntPtr value);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }
    [ComImport,Guid("df598932-f10c-4e39-bba2-c308f101daa3"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEvent : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key,out int type);
        [PreserveSig] new int CompareItem(ref Guid key,IntPtr value,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int Compare(IMFAttributes other,int matchType,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key,out int value);
        [PreserveSig] new int GetUINT64(ref Guid key,out long value);
        [PreserveSig] new int GetDouble(ref Guid key,out double value);
        [PreserveSig] new int GetGUID(ref Guid key,out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key,out int length);
        [PreserveSig] new int GetString(ref Guid key,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int size,out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,out IntPtr value,out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key,out int size);
        [PreserveSig] new int GetBlob(ref Guid key,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size,out int actual);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key,out IntPtr value,out int size);
        [PreserveSig] new int GetUnknown(ref Guid key,ref Guid iid,out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key,int value);
        [PreserveSig] new int SetUINT64(ref Guid key,long value);
        [PreserveSig] new int SetDouble(ref Guid key,double value);
        [PreserveSig] new int SetGUID(ref Guid key,ref Guid value);
        [PreserveSig] new int SetString(ref Guid key,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size);
        [PreserveSig] new int SetUnknown(ref Guid key,[MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index,out Guid key,IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetType(out int eventType);
        [PreserveSig] int GetExtendedType(out Guid extendedType);
        [PreserveSig] int GetStatus(out int status);
        [PreserveSig] int GetValue(IntPtr value);
    }
    [ComImport,Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(int flags,out IMFMediaEvent mediaEvent);
        [PreserveSig] int BeginGetEvent(IntPtr callback,IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result,out IMFMediaEvent mediaEvent);
        [PreserveSig] int QueueEvent(int eventType,ref Guid extendedType,int status,IntPtr value);
    }
    [ComImport,Guid("97ec2ea4-0e42-4937-97ac-9d6d328824e1"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFShutdown
    {
        [PreserveSig] int Shutdown();
        [PreserveSig] int GetShutdownStatus(out int status);
    }

    [ComImport,Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key,out int type);
        [PreserveSig] new int CompareItem(ref Guid key,IntPtr value,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int Compare(IMFAttributes other,int matchType,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key,out int value);
        [PreserveSig] new int GetUINT64(ref Guid key,out long value);
        [PreserveSig] new int GetDouble(ref Guid key,out double value);
        [PreserveSig] new int GetGUID(ref Guid key,out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key,out int length);
        [PreserveSig] new int GetString(ref Guid key,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int size,out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,out IntPtr value,out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key,out int size);
        [PreserveSig] new int GetBlob(ref Guid key,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size,out int actual);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key,out IntPtr value,out int size);
        [PreserveSig] new int GetUnknown(ref Guid key,ref Guid iid,out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key,int value);
        [PreserveSig] new int SetUINT64(ref Guid key,long value);
        [PreserveSig] new int SetDouble(ref Guid key,double value);
        [PreserveSig] new int SetGUID(ref Guid key,ref Guid value);
        [PreserveSig] new int SetString(ref Guid key,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size);
        [PreserveSig] new int SetUnknown(ref Guid key,[MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index,out Guid key,IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other,out int flags);
        [PreserveSig] int GetRepresentation(Guid representation,out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation,IntPtr value);
    }
    [ComImport,Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key,out int type);
        [PreserveSig] new int CompareItem(ref Guid key,IntPtr value,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int Compare(IMFAttributes other,int matchType,[MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key,out int value);
        [PreserveSig] new int GetUINT64(ref Guid key,out long value);
        [PreserveSig] new int GetDouble(ref Guid key,out double value);
        [PreserveSig] new int GetGUID(ref Guid key,out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key,out int length);
        [PreserveSig] new int GetString(ref Guid key,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,int size,out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key,out IntPtr value,out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key,out int size);
        [PreserveSig] new int GetBlob(ref Guid key,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size,out int actual);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key,out IntPtr value,out int size);
        [PreserveSig] new int GetUnknown(ref Guid key,ref Guid iid,out IntPtr value);
        [PreserveSig] new int SetItem(ref Guid key,IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key,int value);
        [PreserveSig] new int SetUINT64(ref Guid key,long value);
        [PreserveSig] new int SetDouble(ref Guid key,double value);
        [PreserveSig] new int SetGUID(ref Guid key,ref Guid value);
        [PreserveSig] new int SetString(ref Guid key,[MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] new int SetBlob(ref Guid key,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] byte[] value,int size);
        [PreserveSig] new int SetUnknown(ref Guid key,[MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index,out Guid key,IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index,out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }
    [ComImport,Guid("045fa593-8799-42b8-bc8d-8968c6453507"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer,out int maximumLength,out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }
    [ComImport,Guid("bf94c121-5b05-4e6f-8000-ba598961414d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out int inputMinimum,out int inputMaximum,out int outputMinimum,out int outputMaximum);
        [PreserveSig] int GetStreamCount(out int inputCount,out int outputCount);
        [PreserveSig] int GetStreamIDs(int inputSize,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)] int[] inputIds,int outputSize,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] int[] outputIds);
        [PreserveSig] int GetInputStreamInfo(int streamId,out InputStreamInfo info);
        [PreserveSig] int GetOutputStreamInfo(int streamId,out OutputStreamInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int GetInputStreamAttributes(int streamId,out IMFAttributes attributes);
        [PreserveSig] int GetOutputStreamAttributes(int streamId,out IMFAttributes attributes);
        [PreserveSig] int DeleteInputStream(int streamId);
        [PreserveSig] int AddInputStreams(int count,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)] int[] ids);
        [PreserveSig] int GetInputAvailableType(int streamId,int typeIndex,out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(int streamId,int typeIndex,out IMFMediaType type);
        [PreserveSig] int SetInputType(int streamId,IMFMediaType? type,int flags);
        [PreserveSig] int SetOutputType(int streamId,IMFMediaType? type,int flags);
        [PreserveSig] int GetInputCurrentType(int streamId,out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(int streamId,out IMFMediaType type);
        [PreserveSig] int GetInputStatus(int streamId,out int flags);
        [PreserveSig] int GetOutputStatus(out int flags);
        [PreserveSig] int SetOutputBounds(long lowerBound,long upperBound);
        [PreserveSig] int ProcessEvent(int streamId,IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(int message,IntPtr parameter);
        [PreserveSig] int ProcessInput(int streamId,IMFSample sample,int flags);
        [PreserveSig] int ProcessOutput(int flags,int outputCount,[In,Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)] OutputDataBuffer[] output,out int status);
    }
    [ComImport,Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api,[MarshalAs(UnmanagedType.Struct)] out object minimum,[MarshalAs(UnmanagedType.Struct)] out object maximum,[MarshalAs(UnmanagedType.Struct)] out object step);
        [PreserveSig] int GetParameterValues(ref Guid api,out IntPtr values,out int count);
        [PreserveSig] int GetDefaultValue(ref Guid api,[MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetValue(ref Guid api,[MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int SetValue(ref Guid api,[In,MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}

