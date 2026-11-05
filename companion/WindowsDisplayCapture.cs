using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TabletDeck;

/// <summary>Persistent physical-pixel capture storage for one authorized display stream.
/// The caller owns target authorization and must validate it before and after every capture.</summary>
internal sealed class WindowsDisplayCapture : IDisposable
{
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private readonly byte[] pixels;
    private IntPtr screenDc,memoryDc,bitmap,previousBitmap,bits;
    private bool disposed;
    internal int Width{get;}
    internal int Height{get;}
    internal int Stride{get;}

    internal WindowsDisplayCapture(int width,int height)
    {
        ValidateDimensions(width,height);
        Width=width;Height=height;Stride=checked(width*4);pixels=new byte[checked(Stride*height)];
        try{
            screenDc=Native.GetDC(IntPtr.Zero);
            if(screenDc==IntPtr.Zero)throw Failure("Cannot open the display capture context.");
            memoryDc=Native.CreateCompatibleDC(screenDc);
            if(memoryDc==IntPtr.Zero)throw Failure("Cannot create display capture storage.");
            var info=new Native.BitmapInfo{
                Header=new Native.BitmapInfoHeader{
                    Size=(uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),Width=width,Height=-height,
                    Planes=1,BitCount=32,Compression=0,SizeImage=(uint)pixels.Length
                }
            };
            // A negative height creates top-down BGRA rows, matching the encoder's positive stride.
            // No file mapping is supplied: this is private in-memory storage only.
            bitmap=Native.CreateDIBSection(screenDc,ref info,0,out bits,IntPtr.Zero,0);
            if(bitmap==IntPtr.Zero||bits==IntPtr.Zero)throw Failure("Cannot allocate the display capture bitmap.");
            previousBitmap=Native.SelectObject(memoryDc,bitmap);
            if(previousBitmap==IntPtr.Zero||previousBitmap==new IntPtr(-1)){
                previousBitmap=IntPtr.Zero;throw Failure("Cannot select the display capture bitmap.");
            }
        }catch{Dispose();throw;}
    }

    internal static void ValidateDimensions(int width,int height)
    {
        if(width is <16 or >4096||height is <16 or >2160||(width&1)!=0||(height&1)!=0||(long)width*height>8_847_360)
            throw new ArgumentException("Capture dimensions must be even and no larger than 4096 × 2160.");
    }

    // The returned array is reused. Encode or copy it before the next call; never retain it in a queue.
    internal byte[] Capture(int x,int y)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)throw new InvalidOperationException("Display capture must stay on its owning stream worker.");
        if((long)x+Width>int.MaxValue||(long)y+Height>int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(x),"Display coordinates exceed the native capture range.");
        // Coordinates are server-resolved, signed desktop pixels. Neither the tablet nor this class
        // chooses an arbitrary source rectangle; the display service checks its owned monitor.
        if(!Native.BitBlt(memoryDc,0,0,Width,Height,screenDc,x,y,0x00cc0020))throw Failure("Windows could not capture the selected display.");
        WindowsDisplayCursor.Draw(memoryDc,x,y,Width,Height);
        // Microsoft requires the GDI batch to finish before reading DIB memory directly.
        if(!Native.GdiFlush())throw Failure("Windows could not finish the display capture.");
        Marshal.Copy(bits,pixels,0,pixels.Length);
        return pixels;
    }

    private static Win32Exception Failure(string message)=>new(Marshal.GetLastWin32Error(),message);

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        Array.Clear(pixels);
        // The bitmap must be deselected before deletion. Deleting the memory DC first is the fallback
        // if restoring its stock bitmap fails; either path releases the selection before DeleteObject.
        if(memoryDc!=IntPtr.Zero&&previousBitmap!=IntPtr.Zero){Native.SelectObject(memoryDc,previousBitmap);previousBitmap=IntPtr.Zero;}
        if(memoryDc!=IntPtr.Zero){Native.DeleteDC(memoryDc);memoryDc=IntPtr.Zero;}
        if(bitmap!=IntPtr.Zero){Native.DeleteObject(bitmap);bitmap=IntPtr.Zero;bits=IntPtr.Zero;}
        if(screenDc!=IntPtr.Zero){Native.ReleaseDC(IntPtr.Zero,screenDc);screenDc=IntPtr.Zero;}
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
        {
            internal uint Size;internal int Width,Height;internal ushort Planes,BitCount;
            internal uint Compression,SizeImage;internal int XPelsPerMeter,YPelsPerMeter;internal uint ColorsUsed,ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo{internal BitmapInfoHeader Header;internal uint Color;}
        [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll",SetLastError=true)] internal static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll",SetLastError=true)] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(IntPtr destination,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,uint operation);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    }
}
