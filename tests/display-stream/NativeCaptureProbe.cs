using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace TabletDeck;

// Explicit opt-in only. Creates/removes this process's virtual monitor; it never adopts a physical
// or another application's source. Encoded frames are counted and immediately discarded.
internal static class NativeCaptureProbe
{
    internal static async Task Run(string[] args)
    {
        if(args.Length!=3||args[0]!="--native-capture"||args[1]!="--motion-helper"||!Path.IsPathFullyQualified(args[2])||!File.Exists(args[2]))
            throw new ArgumentException("Native capture is opt-in: --native-capture --motion-helper <absolute diagnostic executable>.");
        using var display=new ExtendedDisplay();
        var before=display.Status();
        if(before.Active||before.Displays.Any(item=>item.IsOwned))throw new InvalidOperationException("The probe requires no existing owned display.");
        Process? motion=null;var output=new List<object>();
        try
        {
            var created=await display.StartAsync(new(1280,800,true,true,165));
            if(!created.Active||!created.IndependentDesktop||!created.SharedWithTablet||created.RefreshRate<164)
                throw new InvalidOperationException("Windows did not expose the probe's independent 165 Hz display.");
            var own=created.Displays.Single(item=>item.IsOwned);
            Console.WriteLine($"Probe target {own.SourceName} at {own.X},{own.Y}, {own.Width}×{own.Height}, actual {created.RefreshRate:0.##} Hz.");
            var start=new ProcessStartInfo(args[2]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"--source",own.SourceName,"--bounds",$"{own.X},{own.Y},{own.Width},{own.Height}","--hz","120","--seconds","25"})start.ArgumentList.Add(arg);
            motion=Process.Start(start)??throw new InvalidOperationException("The motion fixture did not start.");
            var first=await motion.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if(first is null||motion.HasExited)throw new InvalidOperationException("The motion fixture exited before capture: "+await motion.StandardError.ReadToEndAsync());
            Console.WriteLine(first);
            await Task.Delay(500);
            using var video=new DisplayStream(display);
            foreach(var requested in new[]{60,120})
            {
                var lease=video.Start(new(requested),"native-capture-probe");
                using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var sink=new Counter();
                try{await video.StreamAsync(lease.Lease,"native-capture-probe",sink,()=>true,cancellation.Token);}
                catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
                var sample=sink.Result(requested,created.RefreshRate);output.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
            }
        }
        finally
        {
            var stopped=display.Stop();
            if(stopped.Active)throw new InvalidOperationException("Probe display removal failed; retry removing only this owned session.");
            if(motion is not null)
            {
                try{await motion.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));}catch(TimeoutException){}
                // The fixture exits automatically on removal of its own target, or at its bounded
                // duration. Do not kill unrelated processes or use global window operations.
                if(motion.HasExited){var logs=await motion.StandardOutput.ReadToEndAsync();if(logs.Length>0)Console.Write(logs);}
                motion.Dispose();
            }
            var after=display.Status();
            var unchanged=before.Displays.OrderBy(item=>item.SourceName).SequenceEqual(after.Displays.OrderBy(item=>item.SourceName));
            Console.WriteLine(JsonSerializer.Serialize(new{physicalTopologyUnchanged=unchanged,remainingOwnedDisplays=after.Displays.Count(item=>item.IsOwned),samples=output}));
            if(!unchanged)throw new InvalidOperationException("The display topology differs from the pre-probe snapshot.");
        }
    }

    private sealed class Counter : Stream
    {
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly List<double> intervals=[];
        private int state,expectedPayload,frames;
        private long totalBytes;
        private double first,last;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes=buffer.Span;totalBytes+=bytes.Length;
            if(state==0){if(bytes.Length!=32||!bytes[..4].SequenceEqual("DWV1"u8))throw new InvalidOperationException("Missing video header.");state=1;}
            else if(state==1){if(bytes.Length!=16)throw new InvalidOperationException("Missing frame header.");expectedPayload=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes));state=2;}
            else
            {
                if(bytes.Length!=expectedPayload)throw new InvalidOperationException("Partial encoded frame.");
                var time=clock.Elapsed.TotalSeconds;if(frames==0)first=time;else intervals.Add((time-last)*1000);
                last=time;frames++;state=1;
            }
            return ValueTask.CompletedTask;
        }
        internal object Result(int requested,double source)
        {
            intervals.Sort();var elapsed=last-first;
            return new{requestedRefreshRate=requested,sourceRefreshRate=source,frames,elapsedSeconds=Math.Round(elapsed,3),encodedAndFramedFps=Math.Round(elapsed>0?(frames-1)/elapsed:0,2),p95FrameGapMs=Math.Round(intervals.Count>0?intervals[(int)((intervals.Count-1)*.95)]:0,2),maximumFrameGapMs=Math.Round(intervals.Count>0?intervals[^1]:0,2),megabitsPerSecond=Math.Round(elapsed>0?totalBytes*8/elapsed/1000000:0,2),bytesRetained=0};
        }
        public override Task FlushAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
        public override void Flush(){}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).GetAwaiter().GetResult();
    }
}
