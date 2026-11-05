package app.tabletdeck;

/** Estimates cadence from render timestamps, not the potentially batched callback delivery times. */
final class RenderedFrameMetrics {
    private final long[] timestamps=new long[512];
    private int first,count;
    synchronized boolean record(long renderedNanos,long observedNanos){
        if(renderedNanos<=0||renderedNanos>observedNanos+1_000_000_000L)return false;
        if(count>0&&renderedNanos<=timestamps[(first+count-1)%timestamps.length])return false;
        while(count>0&&(count==timestamps.length||renderedNanos-timestamps[first]>2_000_000_000L)){first=(first+1)%timestamps.length;count--;}
        timestamps[(first+count)%timestamps.length]=renderedNanos;count++;return true;
    }
    synchronized double framesPerSecond(long now){
        if(count<2)return 0;
        long latest=timestamps[(first+count-1)%timestamps.length],span=latest-timestamps[first];
        if(now-latest>3_000_000_000L||span<100_000_000L)return 0;
        return (count-1)*1_000_000_000.0/span;
    }
}
