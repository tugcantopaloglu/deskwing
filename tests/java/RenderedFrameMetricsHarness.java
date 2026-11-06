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
        RenderedFrameMetrics offset=new RenderedFrameMetrics();long observedOrigin=900_000_000_000L;
        for(int frame=0;frame<120;frame++)offset.record(origin+frame*1_000_000_000L/60,observedOrigin+frame*1_000_000_000L/60);
        long latestObserved=observedOrigin+119*1_000_000_000L/60;
        check(Math.abs(offset.framesPerSecond(latestObserved)-60)<0.001,"accepted render cadence stays live despite a large observation-clock offset");
        check(Math.abs(offset.renderClockOffsetMillis()-899000)<0.001,"clock-offset diagnostic preserves the actual timestamp difference");
        check(Math.abs(offset.renderWindowMillis()-1983.333333)<0.001,"render-window diagnostic exposes the interval span used for cadence");
        check(Math.abs(offset.callbackAgeMillis(latestObserved+250_000_000L)-250)<0.001,"callback-age diagnostic uses observation time");
        check(offset.framesPerSecond(latestObserved-1)==0,"a query before the latest observation cannot claim live cadence");
        check(offset.framesPerSecond(latestObserved+3_000_000_001L)==0,"clock-offset cadence expires when callbacks stop");
        check(!offset.record(origin+119*1_000_000_000L/60,latestObserved+4_000_000_000L),"duplicate frame does not refresh callback activity");
        check(offset.framesPerSecond(latestObserved+4_000_000_000L)==0,"duplicate old callback cannot revive an inactive estimate");
        check(!offset.record(origin+2_000_000_000L,latestObserved-1),"regressing observation time cannot corrupt freshness");
        RenderedFrameMetrics delayed=new RenderedFrameMetrics();
        for(int frame=0;frame<120;frame++)delayed.record(origin+frame*1_000_000_000L/120,observedOrigin);
        check(Math.abs(delayed.framesPerSecond(observedOrigin)-120)<0.001,"a late callback batch retains render cadence instead of its arrival burst rate");
        check(delayed.framesPerSecond(observedOrigin+3_000_000_001L)==0,"late batches still expire after local callback inactivity");
        System.out.println("Rendered frame metrics: "+checks+" checks passed.");
    }
}
