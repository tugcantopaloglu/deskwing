using System.Runtime.InteropServices;
using TabletDeck;

internal static class DxgiCaptureChecks
{
    internal static void Run(Action<bool,string> check)
    {
        WindowsDxgiCapture.ValidateInteropLayouts();check(true,"DXGI x64 structs match Windows SDK layout");
        const int width=16,height=16,row=width*4,pitch=row+16;
        var native=Marshal.AllocHGlobal(pitch*height);
        try
        {
            var padded=new byte[pitch*height];Array.Fill(padded,(byte)0xee);
            for(var y=0;y<height;y++)for(var x=0;x<row;x++)padded[y*pitch+x]=(byte)(x+y);
            Marshal.Copy(padded,0,native,padded.Length);
            var pixels=new byte[row*height];
            WindowsDxgiCapture.CopyRows(native,pitch,width,height,pixels);
            check(Enumerable.Range(0,height).All(y=>pixels.AsSpan(y*row,row).SequenceEqual(padded.AsSpan(y*pitch,row))),"GPU padded rows preserve all BGRA pixels and exclude padding");
            var compact=Enumerable.Range(0,row*height).Select(index=>(byte)index).ToArray();Marshal.Copy(compact,0,native,compact.Length);
            WindowsDxgiCapture.CopyRows(native,row,width,height,pixels);
            check(pixels.SequenceEqual(compact),"GPU compact rows copy the exact bounded image");
            Reject(()=>WindowsDxgiCapture.CopyRows(IntPtr.Zero,pitch,width,height,pixels),check,"GPU null mapping rejected");
            Reject(()=>WindowsDxgiCapture.CopyRows(native,row-1,width,height,pixels),check,"GPU undersized pitch rejected");
            Reject(()=>WindowsDxgiCapture.CopyRows(native,65537,width,height,pixels),check,"GPU oversized pitch rejected");
            Reject(()=>WindowsDxgiCapture.CopyRows(native,pitch,width,height,new byte[pixels.Length-1]),check,"GPU destination mismatch rejected");
        }
        finally{Marshal.FreeHGlobal(native);}
    }
    private static void Reject(Action action,Action<bool,string> check,string description)
    {
        var rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}
        check(rejected,description);
    }
}
