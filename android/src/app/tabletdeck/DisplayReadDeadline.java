package app.tabletdeck;

import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.LongSupplier;

/** A first-picture deadline that can interrupt a blocking read, including a trickled payload. */
final class DisplayReadDeadline implements AutoCloseable {
    private static final int WAITING=0,ACCEPTED=1,EXPIRED=2,CLOSED=3;
    private final AtomicInteger state=new AtomicInteger(WAITING);
    private final ScheduledFuture<?> task;
    private final LongSupplier clock;
    private final long started,budget;
    private final Runnable abort;
    DisplayReadDeadline(ScheduledExecutorService scheduler,long milliseconds,Runnable abort){
        this(scheduler,milliseconds,abort,System::nanoTime);
    }
    DisplayReadDeadline(ScheduledExecutorService scheduler,long milliseconds,Runnable abort,LongSupplier clock){
        if(scheduler==null||abort==null||clock==null||milliseconds<1||milliseconds>30000)throw new IllegalArgumentException("Invalid display read deadline.");
        this.clock=clock;this.abort=abort;started=clock.getAsLong();budget=TimeUnit.MILLISECONDS.toNanos(milliseconds);
        task=scheduler.schedule(this::expire,milliseconds,TimeUnit.MILLISECONDS);
    }
    boolean acceptPicture(){
        expireIfLate();
        if(state.compareAndSet(WAITING,ACCEPTED)){task.cancel(false);return true;}
        return state.get()==ACCEPTED;
    }
    boolean expired(){expireIfLate();return state.get()==EXPIRED;}
    boolean cancelled(){return state.get()==CLOSED;}
    private void expireIfLate(){if(state.get()==WAITING&&clock.getAsLong()-started>=budget)expire();}
    private void expire(){if(state.compareAndSet(WAITING,EXPIRED))try{abort.run();}catch(RuntimeException ignored){/* Keep expiry final even if the TLS provider's cleanup fails. */}}
    @Override public void close(){state.compareAndSet(WAITING,CLOSED);task.cancel(false);}
}
