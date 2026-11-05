using System.Diagnostics;
using System.Runtime.InteropServices;
using TabletDeck;

// Only this disposable test form is captured. No display capture or synthetic input is used.
internal sealed class PreviewReceiver : Form
{
    private readonly NativePenPreviewPlatform platform=new();
    private Overlay? overlay;
    private bool alternateStroke;
    internal int Result {get;private set;}=1;
    private static readonly Color Background=Color.FromArgb(20,40,60);
    private static readonly Color TopEdge=Color.FromArgb(240,40,20);
    private static readonly Color RightEdge=Color.FromArgb(20,210,50);
    private static readonly Color BottomEdge=Color.FromArgb(40,70,230);
    private static readonly Color LeftEdge=Color.FromArgb(220,190,20);
    private Color Stroke=>alternateStroke?Color.FromArgb(230,140,30):Color.FromArgb(30,210,210);

    internal PreviewReceiver()
    {
        Text="DeskWing isolated preview verification";
        ClientSize=new Size(640,400);
        StartPosition=FormStartPosition.CenterScreen;
        BackColor=Background;
        FormBorderStyle=FormBorderStyle.Sizable;
        DoubleBuffered=true;
        ResizeRedraw=true;
        Shown+=async (_,_)=>await Verify();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Background);
        using(var brush=new SolidBrush(TopEdge))e.Graphics.FillRectangle(brush,0,0,ClientSize.Width,24);
        using(var brush=new SolidBrush(BottomEdge))e.Graphics.FillRectangle(brush,0,ClientSize.Height-24,ClientSize.Width,24);
        using(var brush=new SolidBrush(LeftEdge))e.Graphics.FillRectangle(brush,0,0,24,ClientSize.Height);
        using(var brush=new SolidBrush(RightEdge))e.Graphics.FillRectangle(brush,ClientSize.Width-24,0,24,ClientSize.Height);
        using var stroke=new Pen(Stroke,22);
        e.Graphics.DrawLine(stroke,ClientSize.Width/4,ClientSize.Height/2,ClientSize.Width*3/4,ClientSize.Height/2);
    }
    private async Task Verify()
    {
        try {
            await WaitForFocus("initial window");
            Require(GetForegroundWindow()==Handle,"The isolated preview window must own foreground.");
            Invalidate();Update();await Task.Delay(100);
            await CaptureAndCheck(320,"640×400 client → 320×200 JPEG");
            alternateStroke=true;Invalidate();Update();
            await CaptureAndCheck(320,"changed stroke appears in the next frame");
            ClientSize=new Size(900,600);Invalidate();Update();
            await CaptureAndCheck(450,"900×600 resized client → 450×300 JPEG with exact edges");
            overlay=new Overlay();
            PositionOverlay();
            overlay.Show();
            await Task.Delay(100);
            await WaitForFocus("covered window");
            Require(GetForegroundWindow()==Handle,"The test overlay must not activate.");
            Require(WindowFromPoint(new(PointToScreen(new Point(ClientSize.Width/2,ClientSize.Height/2))))==overlay.Handle,"The separate test overlay must actually cover the stroke.");
            await CaptureAndCheck(450,"covered client keeps its own stroke and excludes overlay pixels");
            Result=0;
            Console.WriteLine("Native preview verification passed: actual JPEG, full client edges, two sizes, live drawing update, and no covered-window pixels.");
        }catch(Exception error){Console.Error.WriteLine("Native preview verification failed: "+error.Message);}
        finally{overlay?.Close();overlay?.Dispose();Close();}
    }
    private async Task WaitForFocus(string phase)
    {
        Console.WriteLine($"Waiting up to 30 seconds for the isolated preview test window to receive focus ({phase}); no capture occurs before that.");
        for(var attempt=0;attempt<150&&GetForegroundWindow()!=Handle;attempt++)await Task.Delay(200);
    }
    private void PositionOverlay()
    {
        if(overlay is null)return;
        overlay.Bounds=new Rectangle(PointToScreen(new Point(ClientSize.Width/2-80,ClientSize.Height/2-60)),new Size(160,120));
    }
    private async Task CaptureAndCheck(int requestedWidth,string label)
    {
        var origin=PointToScreen(Point.Empty);
        var bounds=new PenRectangle(origin.X,origin.Y,ClientSize.Width,ClientSize.Height);
        var target=new PenDestination(new("own-preview-test","Private preview receiver","window",bounds.Width,bounds.Height),bounds,Handle.ToInt64(),(uint)Environment.ProcessId);
        var expectedHeight=(int)Math.Round((double)requestedWidth*ClientSize.Height/ClientSize.Width);
        var expectedStroke=Stroke;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started=Stopwatch.GetTimestamp();
        var frame=await Task.Run(()=>platform.Capture(target,requestedWidth,cancellation.Token),cancellation.Token);
        Require(frame.Width==requestedWidth&&frame.Height==expectedHeight,"Encoded dimensions must match the client aspect ratio.");
        Require(frame.Jpeg.Length>4&&frame.Jpeg[0]==255&&frame.Jpeg[1]==216&&frame.Jpeg[^2]==255&&frame.Jpeg[^1]==217,"Capture must be a bounded JPEG.");
        using var bytes=new MemoryStream(frame.Jpeg);
        using var image=new Bitmap(bytes);
        Require(image.Width==requestedWidth&&image.Height==expectedHeight,"Decoded JPEG dimensions must match the envelope.");
        CheckColor(image,image.Width/2,4,TopEdge,"top client edge");
        CheckColor(image,image.Width-5,image.Height/2,RightEdge,"right client edge");
        CheckColor(image,image.Width/2,image.Height-5,BottomEdge,"bottom client edge");
        CheckColor(image,4,image.Height/2,LeftEdge,"left client edge");
        CheckColor(image,image.Width/2,image.Height/2,expectedStroke,"known center stroke, not covering window");
        CheckColor(image,image.Width/2,image.Height/4,Background,"known target background");
        Console.WriteLine($"PASS {label}: {frame.Width}×{frame.Height}, {frame.Jpeg.Length} JPEG bytes, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
    }
    private static void CheckColor(Bitmap image,int x,int y,Color expected,string label)
    {
        var actual=image.GetPixel(x,y);
        Require(Math.Abs(actual.R-expected.R)<=24&&Math.Abs(actual.G-expected.G)<=24&&Math.Abs(actual.B-expected.B)<=24,$"{label}: expected RGB({expected.R},{expected.G},{expected.B}), got RGB({actual.R},{actual.G},{actual.B}).");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private sealed class Overlay : Form
    {
        internal Overlay(){BackColor=Color.Magenta;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;Enabled=false;StartPosition=FormStartPosition.Manual;}
        protected override bool ShowWithoutActivation=>true;
        protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle|=0x08000000|0x00000080;return value;}}
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative{public int X,Y;public PointNative(Point point){X=point.X;Y=point.Y;}}
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(PointNative point);
}
