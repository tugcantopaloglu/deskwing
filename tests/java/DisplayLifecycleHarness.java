package app.tabletdeck;

import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/** Real cleanup-worker handoffs and injected decoder waits; no Android, network, or display access. */
public final class DisplayLifecycleHarness {
    private static int passed;
    private interface Checked {void run() throws Exception;}
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    private static void rejects(Checked action,Class<? extends Exception> type,String label) throws Exception {
        try{action.run();throw new AssertionError(label+" was accepted");}
        catch(Exception error){if(!type.isInstance(error))throw new AssertionError(label+" threw "+error.getClass().getSimpleName(),error);check(true,label);}
    }
    private static void await(CountDownLatch latch,String label) throws Exception {if(!latch.await(3,TimeUnit.SECONDS))throw new AssertionError(label);}
    private static void cleanup() throws Exception {
        Thread caller=Thread.currentThread();CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1),finished=new CountDownLatch(1);
        AtomicReference<Thread> cleanupThread=new AtomicReference<>();AtomicInteger calls=new AtomicInteger();
        try(NetworkCloseQueue queue=new NetworkCloseQueue()){
            NetworkCloseQueue.Reservation first=queue.reserve(),second=queue.reserve();
            rejects(queue::reserve,IllegalStateException.class,"cleanup admits at most two unclosed sessions");
            first.submit(()->{cleanupThread.set(Thread.currentThread());calls.incrementAndGet();entered.countDown();try{release.await();}catch(InterruptedException error){Thread.currentThread().interrupt();}});
            await(entered,"blocked cleanup did not start");
            check(cleanupThread.get()!=caller,"TLS cleanup executes on its worker, never the caller");
            first.submit(()->calls.addAndGet(100));
            second.submit(()->{calls.incrementAndGet();finished.countDown();});
            check(finished.getCount()==1,"a blocked close retains the second task without running it inline");
            rejects(queue::reserve,IllegalStateException.class,"queued closure retains reserved capacity");
            queue.close();
            rejects(queue::reserve,IllegalStateException.class,"closed queue refuses new sessions");
            check(finished.getCount()==1,"shutdown does not block the caller waiting for TLS close");
            release.countDown();await(finished,"reserved cleanup was dropped during shutdown");
            check(calls.get()==2,"each reserved cleanup runs exactly once through shutdown");
        }finally{release.countDown();}
        CountDownLatch late=new CountDownLatch(1);
        NetworkCloseQueue deferred=new NetworkCloseQueue();NetworkCloseQueue.Reservation reserved=deferred.reserve();deferred.close();reserved.submit(late::countDown);await(late,"shutdown rejected an existing reservation");
        check(true,"shutdown accepts cleanup submitted by an already admitted session");
        CountDownLatch survived=new CountDownLatch(1);AtomicReference<Thread> throwingThread=new AtomicReference<>(),nextThread=new AtomicReference<>();
        try(NetworkCloseQueue queue=new NetworkCloseQueue()){
            NetworkCloseQueue.Reservation first=queue.reserve(),second=queue.reserve();
            first.submit(()->{throwingThread.set(Thread.currentThread());throw new IllegalStateException("fake TLS cleanup failure");});
            second.submit(()->{nextThread.set(Thread.currentThread());survived.countDown();});await(survived,"cleanup worker did not survive provider exception");
            check(throwingThread.get()==nextThread.get(),"a TLS provider exception is contained without killing the cleanup worker");
        }
        try(NetworkCloseQueue queue=new NetworkCloseQueue()){
            NetworkCloseQueue.Reservation first=queue.reserve();first.submit(null);first.submit(null);
            NetworkCloseQueue.Reservation second=queue.reserve(),third=queue.reserve();
            rejects(queue::reserve,IllegalStateException.class,"duplicate empty cleanup cannot release capacity twice");
            second.submit(null);third.submit(null);
        }
    }
    private static void startup() throws Exception {
        ScheduledExecutorService scheduler=Executors.newSingleThreadScheduledExecutor();
        CountDownLatch occupied=new CountDownLatch(1),unblock=new CountDownLatch(1);
        scheduler.execute(()->{occupied.countDown();try{unblock.await();}catch(InterruptedException error){Thread.currentThread().interrupt();}});await(occupied,"scheduler fixture did not block");
        AtomicLong clock=new AtomicLong(100);AtomicInteger aborts=new AtomicInteger(),attempts=new AtomicInteger(),guards=new AtomicInteger();
        try{
            try(DisplayReadDeadline deadline=new DisplayReadDeadline(scheduler,1000,aborts::incrementAndGet,clock::get)){
                int index=DisplayDecoderStartup.firstInputBuffer(timeout->{if(timeout!=10000)throw new AssertionError("unbounded decoder poll");return attempts.incrementAndGet()<4?-1:7;},deadline,guards::incrementAndGet);
                check(index==7&&attempts.get()==4,"initial key frame waits through unavailable decoder input buffers");
                check(guards.get()==8&&aborts.get()==0,"each bounded input wait rechecks its session before and after polling");
                check(deadline.acceptPicture(),"first actual rendered picture accepts the startup deadline");
                clock.addAndGet(TimeUnit.SECONDS.toNanos(2));
                check(!deadline.expired()&&deadline.acceptPicture()&&aborts.get()==0,"later frames cannot expire an already rendered startup");
            }
            try(DisplayReadDeadline late=new DisplayReadDeadline(scheduler,1000,aborts::incrementAndGet,clock::get)){
                clock.addAndGet(TimeUnit.SECONDS.toNanos(1));
                check(!late.acceptPicture()&&late.expired(),"late first frame is rejected even while the timer thread is blocked");
                check(aborts.get()==1,"monotonic expiry aborts exactly the expired connection once");
                late.expired();late.acceptPicture();check(aborts.get()==1,"repeated expiry checks do not repeat network cleanup");
            }
            try(DisplayReadDeadline deadline=new DisplayReadDeadline(scheduler,1000,aborts::incrementAndGet,clock::get)){
                rejects(()->DisplayDecoderStartup.firstInputBuffer(timeout->{clock.addAndGet(TimeUnit.SECONDS.toNanos(1));return 4;},deadline,()->{}),IOException.class,"a buffer returned after the deadline cannot queue the stale key frame");
            }
            int before=aborts.get();
            try(DisplayReadDeadline deadline=new DisplayReadDeadline(scheduler,1000,aborts::incrementAndGet,clock::get)){
                deadline.close();clock.addAndGet(TimeUnit.SECONDS.toNanos(2));
                check(!deadline.acceptPicture()&&!deadline.expired()&&aborts.get()==before,"closing a waiting deadline never aborts on the caller or accepts a picture");
                rejects(()->DisplayDecoderStartup.firstInputBuffer(timeout->{throw new AssertionError("cancelled decoder polled");},deadline,()->{}),IOException.class,"closed startup stops before another decoder poll");
            }
            try(DisplayReadDeadline deadline=new DisplayReadDeadline(scheduler,1000,aborts::incrementAndGet,clock::get)){
                rejects(()->DisplayDecoderStartup.firstInputBuffer(timeout->-1,deadline,()->{throw new IllegalStateException("replaced session");}),IllegalStateException.class,"session replacement cancels initial decoder waiting");
            }
            try(DisplayReadDeadline failing=new DisplayReadDeadline(scheduler,1000,()->{throw new IllegalStateException("fake disconnect failure");},clock::get)){
                clock.addAndGet(TimeUnit.SECONDS.toNanos(1));
                check(failing.expired()&&!failing.acceptPicture(),"disconnect exceptions leave a final expired startup state");
            }
        }finally{unblock.countDown();scheduler.shutdownNow();if(!scheduler.awaitTermination(3,TimeUnit.SECONDS))throw new AssertionError("deadline fixture worker did not stop");}
        ScheduledExecutorService live=Executors.newSingleThreadScheduledExecutor();CountDownLatch expired=new CountDownLatch(1);
        try(DisplayReadDeadline deadline=new DisplayReadDeadline(live,20,expired::countDown)){
            await(expired,"watchdog could not interrupt an idle startup");check(deadline.expired()&&!deadline.acceptPicture(),"scheduled watchdog expires without another decoder or network callback");
        }finally{live.shutdownNow();live.awaitTermination(3,TimeUnit.SECONDS);}
    }
    public static void main(String[] args) throws Exception {cleanup();startup();System.out.println(passed+" display cleanup and startup checks passed.");}
}
