package app.tabletdeck;

public final class RenderedFrameMetricsHarness {
    private static int checks;
    private static void check(boolean value,String message){checks++;if(!value)throw new AssertionError(message);}
    public static void main(String[] args){
        RenderedFrameMetrics metrics=new RenderedFrameMetrics();long origin=1_000_000_000L;
        // All callbacks arrive together one second later, while their render timestamps retain 60 Hz.
        for(int frame=0;frame<60;frame++)check(metrics.record(origin+frame*1_000_000_000L/60,origin+2_000_000_000L),"delayed frame accepted");
        double rate=metrics.framesPerSecond(origin+2_000_000_000L);check(Math.abs(rate-60)<0.001,"batched delivery does not create a false 120 Hz burst");
        check(!metrics.record(origin+1_000_000_000L/60,origin+2_000_000_000L),"old callback does not reorder cadence");
        check(!metrics.record(0,origin),"invalid render timestamp refused");
        check(!metrics.record(origin+10_000_000_000L,origin),"future render timestamp refused");
        check(metrics.framesPerSecond(origin+6_000_000_000L)==0,"stale callbacks are not presented as live cadence");
        RenderedFrameMetrics high=new RenderedFrameMetrics();for(int frame=0;frame<1000;frame++)high.record(origin+frame*1_000_000_000L/240,origin+frame*1_000_000_000L/240);
        check(Math.abs(high.framesPerSecond(origin+999*1_000_000_000L/240)-240)<0.001,"bounded rolling window retains 240 Hz cadence");
        check(new RenderedFrameMetrics().framesPerSecond(origin)==0,"empty measurement is not a claimed frame rate");
        System.out.println("Rendered frame metrics: "+checks+" checks passed.");
    }
}
