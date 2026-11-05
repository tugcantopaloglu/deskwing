package app.tabletdeck;

import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** Reserves bounded cleanup capacity before a network session can open. Never runs cleanup inline. */
final class NetworkCloseQueue implements AutoCloseable {
    private final Object gate=new Object();
    // The extra queue entry covers a finished task releasing its reservation just before its
    // worker returns to dequeue the next task. At most two unclosed sessions are admitted.
    private final ThreadPoolExecutor executor=new ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(2),action->{Thread thread=new Thread(action,"DeskWing video TLS cleanup");thread.setDaemon(true);return thread;},new ThreadPoolExecutor.AbortPolicy());
    private boolean closed;
    private int reserved;
    Reservation reserve(){synchronized(gate){if(closed||reserved>=2)throw new IllegalStateException("The previous display connections are still closing. Try again shortly.");reserved++;return new Reservation();}}
    @Override public void close(){synchronized(gate){closed=true;if(reserved==0)executor.shutdown();}}
    final class Reservation {
        private boolean submitted;
        void submit(Runnable action){
            synchronized(gate){
                if(submitted)return;
                submitted=true;
                if(action==null){release();return;}
                // The reservation count prevents overflow, and shutdown waits for all reservations.
                executor.execute(()->{try{action.run();}finally{release();}});
            }
        }
        private void release(){synchronized(gate){reserved--;if(closed&&reserved==0)executor.shutdown();}}
    }
}
