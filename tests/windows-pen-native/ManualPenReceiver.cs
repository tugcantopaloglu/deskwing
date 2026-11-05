using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using TabletDeck;

// This mode receives Windows pen messages only. It never creates or injects a synthetic pointer.
internal sealed class ManualPenReceiver : Form
{
    private const int PointerUpdate=0x245,PointerDown=0x246,PointerUp=0x247,PointerLeave=0x24A;
    private const int MaximumSegments=20000,MaximumBitmapPixels=8000000;
    private readonly List<Segment> segments=[];
    private Bitmap? drawing;
    private bool rebuild=true;
    private PointF? previous;
    private uint? activePointer;
    private string reading="Waiting for your tablet pen.";
    private int strokes,strokeSamples;
    private uint minimumPressure=1024,maximumPressure;
    private static readonly Color InkColor=Color.FromArgb(118,215,228);

    internal ManualPenReceiver()
    {
        Text="DeskWing pen test canvas";
        ClientSize=new Size(1000,680);
        MinimumSize=new Size(480,320);
        FormBorderStyle=FormBorderStyle.Sizable;
        StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(16,19,24);
        ForeColor=Color.FromArgb(230,237,243);
        Font=new Font("Segoe UI",11);
        DoubleBuffered=true;
        ResizeRedraw=true;
        KeyPreview=true;
        var menu=new ContextMenuStrip();
        var clear=new ToolStripMenuItem("Clear canvas",null,(_,_)=>ClearCanvas()){ShortcutKeys=Keys.Control|Keys.Back};
        menu.Items.Add(clear);
        ContextMenuStrip=menu;
        KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.Back){ClearCanvas();e.Handled=true;e.SuppressKeyPress=true;}};
        Shown+=(_,_)=>Console.WriteLine("Manual pen canvas ready. No synthetic input is generated. Entire client area maps to the tablet, including all four edges. Select 'DeskWing pen test canvas' on the PC, focus this window, then Start pen session on the tablet. Right-click or Ctrl+Backspace clears.");
        FormClosed+=(_,_)=>Console.WriteLine($"Manual pen canvas closed after {strokes} completed strokes. Drawing was not saved.");
    }
    private void ClearCanvas()
    {
        segments.Clear();previous=null;activePointer=null;rebuild=true;Invalidate();
    }
    protected override void OnResize(EventArgs e)
    {
        rebuild=true;
        base.OnResize(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureDrawing();
        if(drawing is not null)e.Graphics.DrawImage(drawing,ClientRectangle);
        // This HUD is an overlay only: it does not change, clip or intercept the ink surface.
        var hud=new Rectangle(10,10,Math.Min(ClientSize.Width-20,630),54);
        using(var background=new SolidBrush(Color.FromArgb(205,16,19,24)))e.Graphics.FillRectangle(background,hud);
        TextRenderer.DrawText(e.Graphics,reading,Font,new Rectangle(hud.X+8,hud.Y+4,hud.Width-16,24),InkColor,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        TextRenderer.DrawText(e.Graphics,"Tablet: Match PC proportions · Right-click / Ctrl+Backspace clears",Font,new Rectangle(hud.X+8,hud.Y+27,hud.Width-16,22),Color.LightGray,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
    }
    private void EnsureDrawing()
    {
        if(!rebuild&&drawing is not null)return;
        var width=Math.Max(1,ClientSize.Width);var height=Math.Max(1,ClientSize.Height);
        var scale=Math.Min(1,Math.Sqrt(MaximumBitmapPixels/((double)width*height)));
        var size=new Size(Math.Max(1,(int)(width*scale)),Math.Max(1,(int)(height*scale)));
        if(drawing is null||drawing.Size!=size){drawing?.Dispose();drawing=new Bitmap(size.Width,size.Height);}
        using var graphics=Graphics.FromImage(drawing);
        graphics.Clear(BackColor);
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        foreach(var segment in segments)DrawSegment(graphics,segment,drawing.Size);
        rebuild=false;
    }
    private void AppendSegment(Segment segment)
    {
        if(segments.Count>=MaximumSegments){segments.RemoveRange(0,5000);rebuild=true;}
        segments.Add(segment);
        if(rebuild||drawing is null){EnsureDrawing();return;}
        using var graphics=Graphics.FromImage(drawing);
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        DrawSegment(graphics,segment,drawing.Size);
    }
    private void DrawSegment(Graphics graphics,Segment segment,Size size)
    {
        var from=ManualInkGeometry.Expand(segment.From,size);var to=ManualInkGeometry.Expand(segment.To,size);
        var width=Math.Max(0.5f,segment.Width*Math.Min(size.Width,size.Height));
        var color=segment.Eraser?BackColor:InkColor;
        if(from==to){using var brush=new SolidBrush(color);graphics.FillEllipse(brush,from.X-width/2,from.Y-width/2,width,width);return;}
        using var pen=new Pen(color,width){StartCap=LineCap.Round,EndCap=LineCap.Round};
        graphics.DrawLine(pen,from,to);
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg is PointerUpdate or PointerDown or PointerUp or PointerLeave) {
            var id=(uint)(message.WParam.ToInt64()&0xffff);
            if(GetPointerPenInfo(id,out var info)) {
                var point=ManualInkGeometry.Normalize(PointToClient(new Point(info.Pointer.Pixel.X,info.Pointer.Pixel.Y)),ClientSize);
                var touching=(info.Pointer.Flags&4)!=0;
                if(message.Msg==PointerDown&&activePointer is null) {
                    activePointer=id;previous=point;strokeSamples=0;minimumPressure=1024;maximumPressure=0;
                }
                if(activePointer==id&&touching) {
                    strokeSamples++;minimumPressure=Math.Min(minimumPressure,info.Pressure);maximumPressure=Math.Max(maximumPressure,info.Pressure);
                    var eraser=(info.Flags&4)!=0;
                    var width=(eraser?30:1+18*Math.Clamp(info.Pressure/1024f,0,1))/Math.Max(1,Math.Min(ClientSize.Width,ClientSize.Height));
                    AppendSegment(new(previous??point,point,width,eraser));
                    previous=point;
                }
                reading=$"Pressure {info.Pressure}/1024 · Tilt X {info.TiltX}° / Y {info.TiltY}° · Strokes {strokes}";
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
    protected override void Dispose(bool disposing)
    {
        if(disposing){drawing?.Dispose();ContextMenuStrip?.Dispose();}
        base.Dispose(disposing);
    }
    // Positions and width are proportional to the full client area, so resize remaps old and new ink consistently.
    private sealed record Segment(PointF From,PointF To,float Width,bool Eraser);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetPointerPenInfo(uint pointer,out NativeWindowsPenPlatform.Native.PointerPenInfo info);
}

internal static class ManualInkGeometry
{
    internal static PointF Normalize(Point point,Size size)=>new(Math.Clamp(point.X/(float)Math.Max(1,size.Width-1),0,1),Math.Clamp(point.Y/(float)Math.Max(1,size.Height-1),0,1));
    internal static PointF Expand(PointF point,Size size)=>new(point.X*Math.Max(1,size.Width-1),point.Y*Math.Max(1,size.Height-1));
    internal static void Verify()
    {
        var original=new Size(1000,680);var resized=new Size(730,940);
        var edge=Normalize(new Point(999,679),original);
        if(edge!=new PointF(1,1)||Expand(edge,resized)!=new PointF(729,939))throw new InvalidOperationException("The bottom-right edge must stay at the bottom-right edge after resize.");
        if(Normalize(Point.Empty,original)!=PointF.Empty||Expand(PointF.Empty,resized)!=PointF.Empty)throw new InvalidOperationException("The top-left edge must have no canvas inset.");
        var center=new PointF(.5f,.5f);
        if(Expand(center,resized)!=new PointF(364.5f,469.5f))throw new InvalidOperationException("Normalized ink must stay centered after aspect changes.");
        if(Normalize(new Point(-5,800),original)!=new PointF(0,1))throw new InvalidOperationException("Captured samples outside the client must clamp to its edges.");
        Console.WriteLine("PASS manual canvas full-client edges, normalized resize, center and outside-client clamp.");
    }
}
