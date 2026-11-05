using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using TabletDeck;

// This mode receives Windows pen messages only. It never creates or injects a synthetic pointer.
internal sealed class ManualPenReceiver : Form
{
    private const int PointerUpdate=0x245,PointerDown=0x246,PointerUp=0x247,PointerLeave=0x24A;
    private readonly List<Segment> segments=[];
    private readonly Button clear;
    private PointF? previous;
    private uint? activePointer;
    private string reading="Waiting for your tablet pen. Mouse and touch do not draw.";
    private int strokes,strokeSamples;
    private uint minimumPressure=1024,maximumPressure;

    internal ManualPenReceiver()
    {
        Text="DeskWing pen test canvas";
        ClientSize=new Size(1000,680);
        MinimumSize=new Size(680,450);
        StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(16,19,24);
        ForeColor=Color.FromArgb(230,237,243);
        Font=new Font("Segoe UI",12);
        DoubleBuffered=true;
        clear=new Button{Text="Clear canvas",Size=new Size(136,44),Anchor=AnchorStyles.Top|AnchorStyles.Right,Location=new Point(ClientSize.Width-160,20)};
        clear.Click+=(_,_)=>{segments.Clear();previous=null;activePointer=null;Invalidate();};
        Controls.Add(clear);
        Shown+=(_,_)=>Console.WriteLine("Manual pen canvas ready. No synthetic input is generated. Select 'DeskWing pen test canvas' on the PC, focus this window, then Start pen session on the tablet.");
        FormClosed+=(_,_)=>Console.WriteLine($"Manual pen canvas closed after {strokes} completed strokes. Drawing was not saved.");
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        e.Graphics.DrawString("DeskWing pen test",Font,Brushes.White,new PointF(24,20));
        e.Graphics.DrawString("Rest your palm, vary pressure and tilt, then try fullscreen on the tablet.",Font,Brushes.LightGray,new PointF(24,56));
        e.Graphics.DrawString(reading,Font,Brushes.Cyan,new PointF(24,92));
        var area=new Rectangle(18,134,ClientSize.Width-36,ClientSize.Height-152);
        using(var border=new Pen(Color.FromArgb(64,79,92),1))e.Graphics.DrawRectangle(border,area);
        e.Graphics.SetClip(area);
        foreach(var segment in segments) {
            using var pen=new Pen(segment.Eraser?BackColor:Color.FromArgb(118,215,228),segment.Width){StartCap=LineCap.Round,EndCap=LineCap.Round};
            e.Graphics.DrawLine(pen,segment.From,segment.To);
        }
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg is PointerUpdate or PointerDown or PointerUp or PointerLeave) {
            var id=(uint)(message.WParam.ToInt64()&0xffff);
            if(GetPointerPenInfo(id,out var info)) {
                var point=PointToClient(new Point(info.Pointer.Pixel.X,info.Pointer.Pixel.Y));
                var touching=(info.Pointer.Flags&4)!=0;
                if(message.Msg==PointerDown&&activePointer is null) {
                    activePointer=id;previous=point;strokeSamples=0;minimumPressure=1024;maximumPressure=0;
                }
                if(activePointer==id&&touching) {
                    strokeSamples++;minimumPressure=Math.Min(minimumPressure,info.Pressure);maximumPressure=Math.Max(maximumPressure,info.Pressure);
                    var start=previous??point;
                    var end=new PointF(point.X,point.Y);
                    if(start==end)end.X+=0.01f;
                    if(segments.Count>=20000)segments.RemoveRange(0,5000);
                    segments.Add(new(start,end,(info.Flags&4)!=0?30:1+18*Math.Clamp(info.Pressure/1024f,0,1),(info.Flags&4)!=0));
                    previous=point;
                }
                reading=$"Pressure {info.Pressure}/1024    Tilt X {info.TiltX}° · Y {info.TiltY}°    Strokes {strokes}";
                if(activePointer==id&&(message.Msg is PointerUp or PointerLeave||!touching)) {
                    if(strokeSamples>0) {
                        strokes++;
                        Console.WriteLine($"PEN stroke {strokes}: {strokeSamples} samples, pressure {minimumPressure}–{maximumPressure}/1024, final tilt ({info.TiltX}, {info.TiltY}), released.");
                    }
                    previous=null;activePointer=null;strokeSamples=0;
                }
                Invalidate();
                message.Result=IntPtr.Zero;
                return;
            }
        }
        base.WndProc(ref message);
    }
    private sealed record Segment(PointF From,PointF To,float Width,bool Eraser);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetPointerPenInfo(uint pointer,out NativeWindowsPenPlatform.Native.PointerPenInfo info);
}
