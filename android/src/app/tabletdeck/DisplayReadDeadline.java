package app.tabletdeck;

import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/** A first-picture deadline that can interrupt a blocking read, including a trickled payload. */
final class DisplayReadDeadline implements AutoCloseable {
    private static final int WAITING=0,ACCEPTED=1,EXPIRED=2,CLOSED=3;
    private final AtomicInteger state=new AtomicInteger(WAITING);
    private final ScheduledFuture<?> task;
    DisplayReadDeadline(ScheduledExecutorService scheduler,long milliseconds,Runnable abort){
        if(scheduler==null||abort==null||milliseconds<1||milliseconds>30000)throw new IllegalArgumentException("Invalid display read deadline.");
        task=scheduler.schedule(()->{if(state.compareAndSet(WAITING,EXPIRED))abort.run();},milliseconds,TimeUnit.MILLISECONDS);
    }
    boolean acceptPicture(){
        if(state.compareAndSet(WAITING,ACCEPTED)){task.cancel(false);return true;}
        return state.get()==ACCEPTED;
    }
    boolean expired(){return state.get()==EXPIRED;}
    @Override public void close(){state.compareAndSet(WAITING,CLOSED);task.cancel(false);}
}
