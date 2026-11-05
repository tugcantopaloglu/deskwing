using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TabletDeck;

// Composites the Windows cursor into a freshly acquired authorized DXGI image. This class never
// reads a screen DC: its only image source is the caller's frame buffer and its only native storage
// is a private top-down DIB. The caller must supply clean pixels on every call, including mouse-only
// DXGI updates, so previously composited cursors cannot remain as trails.
internal sealed class WindowsDisplayCursorOverlay : IDisposable
{
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private readonly int width,height,stride;
    private IntPtr dc,bitmap,previous,bits;
    private bool disposed;

    internal WindowsDisplayCursorOverlay(int width,int height)
    {
        WindowsDisplayCapture.ValidateDimensions(width,height);
        this.width=width;this.height=height;stride=checked(width*4);
        try{
            dc=Native.CreateCompatibleDC(IntPtr.Zero);
            if(dc==IntPtr.Zero)throw Failure("Cannot create cursor composition storage.");
            var info=new Native.BitmapInfo{Header=new Native.BitmapHeader{Size=40,Width=width,Height=-height,Planes=1,BitCount=32,SizeImage=(uint)checked(stride*height)}};
            bitmap=Native.CreateDIBSection(dc,ref info,0,out bits,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero||bits==IntPtr.Zero)throw Failure("Cannot allocate cursor composition pixels.");
            previous=Native.SelectObject(dc,bitmap);
            if(previous==IntPtr.Zero||previous==new IntPtr(-1)){previous=IntPtr.Zero;throw Failure("Cannot select cursor composition pixels.");}
        }catch{Dispose();throw;}
    }

    internal bool Overlay(byte[] pixels,int sourceStride,int sourceX,int sourceY)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("Cursor composition must stay on its owning stream worker.");
        ValidateBuffer(pixels,sourceStride,width,height);
        var cursor=WindowsDisplayCursor.GetVisible(sourceX,sourceY,width,height);
        if(cursor is not { } visible)return false;
        CopyToDib(pixels,sourceStride);
        var drawn=WindowsDisplayCursor.DrawAt(dc,visible);
        if(!Native.GdiFlush())throw Failure("Windows could not finish cursor composition.");
        if(!drawn)return false;
        CopyFromDib(pixels,sourceStride);
        return true;
    }

    internal static void ValidateBuffer(byte[] pixels,int stride,int width,int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        WindowsDisplayCapture.ValidateDimensions(width,height);
        if(stride<checked(width*4)||(long)stride*height>pixels.Length)throw new ArgumentException("The cursor frame has invalid row storage.");
    }
    private void CopyToDib(byte[] source,int sourceStride)
    {
        if(sourceStride==stride){Marshal.Copy(source,0,bits,checked(stride*height));return;}
        for(var row=0;row<height;row++)Marshal.Copy(source,row*sourceStride,IntPtr.Add(bits,row*stride),stride);
    }
    private void CopyFromDib(byte[] destination,int destinationStride)
    {
        if(destinationStride==stride){Marshal.Copy(bits,destination,0,checked(stride*height));return;}
        for(var row=0;row<height;row++)Marshal.Copy(IntPtr.Add(bits,row*stride),destination,row*destinationStride,stride);
    }
    private static Win32Exception Failure(string message)=>new(Marshal.GetLastWin32Error(),message);
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(dc!=IntPtr.Zero&&previous!=IntPtr.Zero){Native.SelectObject(dc,previous);previous=IntPtr.Zero;}
        if(dc!=IntPtr.Zero){Native.DeleteDC(dc);dc=IntPtr.Zero;}
        if(bitmap!=IntPtr.Zero){Native.DeleteObject(bitmap);bitmap=IntPtr.Zero;bits=IntPtr.Zero;}
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapHeader{internal uint Size;internal int Width,Height;internal ushort Planes,BitCount;internal uint Compression,SizeImage;internal int XPelsPerMeter,YPelsPerMeter;internal uint ColorsUsed,ColorsImportant;}
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo{internal BitmapHeader Header;internal uint Color;}
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    }
}
