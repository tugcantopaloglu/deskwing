using System.Runtime.InteropServices;

namespace TabletDeck;

internal readonly record struct DisplayCursorPlacement(int X,int Y);
internal readonly record struct DisplayCursorSnapshot(IntPtr Handle,DisplayCursorPlacement Placement);

// Only the visible cursor's anchor inside the already-authorized monitor is eligible for drawing.
// No desktop pixels are read here; Windows supplies the cursor resource, and the DIB clips its edges.
internal static class WindowsDisplayCursor
{
    internal static void Draw(IntPtr destination,int sourceX,int sourceY,int width,int height)
    {
        if(destination==IntPtr.Zero)return;
        var visible=GetVisible(sourceX,sourceY,width,height);
        if(visible is { } cursor)DrawAt(destination,cursor);
    }

    internal static DisplayCursorSnapshot? GetVisible(int sourceX,int sourceY,int width,int height)
    {
        var cursor=new Native.CursorInfo{Size=Marshal.SizeOf<Native.CursorInfo>()};
        if(!Native.GetCursorInfo(ref cursor)||(cursor.Flags&1)==0||cursor.Cursor==IntPtr.Zero)return null;
        var placement=GetPlacement(cursor.Cursor,sourceX,sourceY,width,height,cursor.Position.X,cursor.Position.Y);
        return placement is { } point?new(cursor.Cursor,point):null;
    }

    internal static bool DrawAt(IntPtr destination,DisplayCursorSnapshot cursor)
    {
        if(destination==IntPtr.Zero||cursor.Handle==IntPtr.Zero)return false;
        // Natural resource size and DI_NORMAL preserve alpha/monochrome cursor rendering. The private
        // memory DC contains only the selected display-sized bitmap, so off-edge cursor pixels clip.
        return Native.DrawIconEx(destination,cursor.Placement.X,cursor.Placement.Y,cursor.Handle,0,0,0,IntPtr.Zero,3);
    }

    internal static DisplayCursorPlacement? GetPlacement(IntPtr cursor,int sourceX,int sourceY,int width,int height,int cursorX,int cursorY)
    {
        if(cursor==IntPtr.Zero||CalculatePlacement(sourceX,sourceY,width,height,cursorX,cursorY,0,0) is null)return null;
        var icon=new Native.IconInfo();
        try{
            if(!Native.GetIconInfo(cursor,out icon))return null;
            return CalculatePlacement(sourceX,sourceY,width,height,cursorX,cursorY,icon.HotspotX,icon.HotspotY);
        }finally{
            // GetIconInfo creates these two bitmaps; the shared HCURSOR itself is never destroyed.
            if(icon.Mask!=IntPtr.Zero)Native.DeleteObject(icon.Mask);
            if(icon.Color!=IntPtr.Zero&&icon.Color!=icon.Mask)Native.DeleteObject(icon.Color);
        }
    }

    internal static DisplayCursorPlacement? CalculatePlacement(int sourceX,int sourceY,int width,int height,int cursorX,int cursorY,uint hotspotX,uint hotspotY)
    {
        if(width is <1 or >4096||height is <1 or >2160||hotspotX>4096||hotspotY>4096)return null;
        var localX=(long)cursorX-sourceX;var localY=(long)cursorY-sourceY;
        if(localX<0||localY<0||localX>=width||localY>=height)return null;
        return new((int)(localX-hotspotX),(int)(localY-hotspotY));
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point{internal int X,Y;}
        [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo{internal int Size,Flags;internal IntPtr Cursor;internal Point Position;}
        [StructLayout(LayoutKind.Sequential)] internal struct IconInfo{[MarshalAs(UnmanagedType.Bool)] internal bool Icon;internal uint HotspotX,HotspotY;internal IntPtr Mask,Color;}
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorInfo(ref CursorInfo cursor);
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetIconInfo(IntPtr icon,out IconInfo info);
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DrawIconEx(IntPtr dc,int x,int y,IntPtr icon,int width,int height,uint frame,IntPtr brush,uint flags);
        [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr value);
    }
}
