package app.tabletdeck;

/** Estimates cadence from render timestamps, not the potentially batched callback delivery times. */
final class RenderedFrameMetrics {
    private final long[] timestamps=new long[512];
    private int first,count;
    private long lastObservedNanos;
    synchronized boolean record(long renderedNanos,long observedNanos){
        if(renderedNanos<=0||renderedNanos>observedNanos+1_000_000_000L)return false;
        if(observedNanos<=0||count>0&&observedNanos<lastObservedNanos)return false;
        if(count>0&&renderedNanos<=timestamps[(first+count-1)%timestamps.length])return false;
        while(count>0&&(count==timestamps.length||renderedNanos-timestamps[first]>2_000_000_000L)){first=(first+1)%timestamps.length;count--;}
        timestamps[(first+count)%timestamps.length]=renderedNanos;count++;lastObservedNanos=observedNanos;return true;
    }
    synchronized double framesPerSecond(long now){
        if(count<2)return 0;
        long latest=timestamps[(first+count-1)%timestamps.length],span=latest-timestamps[first];
        // MediaCodec may deliver callbacks late or in batches. Compare local callback arrival
        // with the local observation clock; use render timestamps only for frame intervals.
        long age=now-lastObservedNanos;
        if(age<0||age>3_000_000_000L||span<100_000_000L)return 0;
        return (count-1)*1_000_000_000.0/span;
    }
    synchronized double renderClockOffsetMillis(){return count==0?Double.NaN:(lastObservedNanos-timestamps[(first+count-1)%timestamps.length])/1_000_000.0;}
    synchronized double callbackAgeMillis(long now){return count==0?Double.NaN:(now-lastObservedNanos)/1_000_000.0;}
    synchronized double renderWindowMillis(){return count<2?0:(timestamps[(first+count-1)%timestamps.length]-timestamps[first])/1_000_000.0;}
}
