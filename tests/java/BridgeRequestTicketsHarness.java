package app.tabletdeck;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

public final class BridgeRequestTicketsHarness {
    private static int checks;
    private static void check(boolean condition,String message){checks++;if(!condition)throw new AssertionError(message);}
    private static void refused(Runnable operation,Class<? extends RuntimeException> expected,String message){
        try{operation.run();throw new AssertionError(message);}
        catch(RuntimeException ex){check(expected.isInstance(ex),message+": wrong exception "+ex.getClass().getSimpleName());}
    }
    public static void main(String[] args) throws Exception {
        BridgeRequestTickets tickets=new BridgeRequestTickets();
        BridgeRequestTickets.Ticket first=tickets.register(7,10);
        check(first.nativeId==1&&first.originalId==7&&first.document==10,"registration retains original page identity");
        refused(()->tickets.register(7,10),BridgeRequestTickets.DuplicateRequestException.class,"duplicate original ID is distinguishable so it cannot settle the original request");
        BridgeRequestTickets.Ticket otherDocument=tickets.register(7,11);
        check(otherDocument.nativeId==2&&otherDocument.document==11,"a different document can reuse its JavaScript ID without native ID reuse");
        check(tickets.complete(first.nativeId)==first,"old completion resolves its own exact ticket");
        check(tickets.complete(first.nativeId)==null,"completion is idempotent");
        check(tickets.complete(otherDocument.nativeId)==otherDocument,"old completion cannot remove the newer document ticket");
        refused(()->tickets.register(0,12),IllegalArgumentException.class,"zero original ID is rejected");
        refused(()->tickets.register(-1,12),IllegalArgumentException.class,"negative original ID is rejected");
        refused(()->new BridgeRequestTickets(-1),IllegalArgumentException.class,"negative native counter is rejected");
        check(tickets.complete(0)==null&&tickets.complete(-1)==null&&tickets.complete(200)==null,"unknown IDs cannot complete a ticket");

        BridgeRequestTickets.Ticket beforeReset=tickets.register(1,12);
        tickets.reset();
        BridgeRequestTickets.Ticket afterReset=tickets.register(1,13);
        check(afterReset.nativeId>beforeReset.nativeId,"page reset never resets the app-lifetime native counter");
        check(tickets.complete(beforeReset.nativeId)==null,"old completion after reset is discarded");
        check(tickets.complete(afterReset.nativeId)==afterReset,"old completion does not consume the new same-JavaScript-ID ticket");
        tickets.reset();tickets.reset();
        BridgeRequestTickets.Ticket afterDoubleReset=tickets.register(1,13);
        check(afterDoubleReset.nativeId>afterReset.nativeId,"repeated reset cannot reuse an assigned native ID");
        check(tickets.complete(afterDoubleReset.nativeId)==afterDoubleReset,"same original ID can be used after its prior operation is complete");

        BridgeRequestTickets capped=new BridgeRequestTickets();List<BridgeRequestTickets.Ticket> active=new ArrayList<>();
        for(int index=1;index<=BridgeRequestTickets.MAX_PENDING;index++)active.add(capped.register(index,20));
        check(active.size()==256&&active.get(255).nativeId==256,"exactly 256 pending operations are admitted");
        refused(()->capped.register(257,20),IllegalStateException.class,"pending request cap is enforced");
        refused(()->capped.register(1,21),IllegalStateException.class,"pending cap applies across documents");
        check(capped.complete(active.get(0).nativeId)==active.get(0),"completing a request frees one slot");
        BridgeRequestTickets.Ticket replacement=capped.register(257,20);
        check(replacement.nativeId==257,"rejected registrations neither reuse nor consume an assigned ID");
        refused(()->capped.register(258,20),IllegalStateException.class,"replacement restores the pending cap");
        for(BridgeRequestTickets.Ticket ticket:active)if(ticket.nativeId!=1)check(capped.complete(ticket.nativeId)==ticket,"each pending completion stays isolated");
        check(capped.complete(replacement.nativeId)==replacement,"replacement resolves once");
        check(capped.complete(replacement.nativeId)==null,"replacement completion is idempotent");

        BridgeRequestTickets exhausted=new BridgeRequestTickets(Integer.MAX_VALUE-1);
        BridgeRequestTickets.Ticket last=exhausted.register(1,30);
        check(last.nativeId==Integer.MAX_VALUE,"last positive native ID can be assigned");
        refused(()->exhausted.register(2,30),IllegalStateException.class,"native ID counter cannot wrap");
        check(exhausted.complete(last.nativeId)==last,"exhausted registry can still complete its final operation");
        exhausted.reset();
        refused(()->exhausted.register(1,31),IllegalStateException.class,"reset cannot undo native counter exhaustion");
        refused(()->new BridgeRequestTickets(Integer.MAX_VALUE).register(1,1),IllegalStateException.class,"already-exhausted registry requires reopening the app");

        BridgeRequestTickets concurrent=new BridgeRequestTickets();ExecutorService workers=Executors.newFixedThreadPool(8);
        try{
            List<Future<BridgeRequestTickets.Ticket>> issued=new ArrayList<>();CountDownLatch ready=new CountDownLatch(1);
            for(int index=1;index<=256;index++){final int original=index;issued.add(workers.submit(()->{ready.await();return concurrent.register(original,40);}));}
            ready.countDown();Set<Integer> nativeIds=new HashSet<>();
            for(Future<BridgeRequestTickets.Ticket> operation:issued)nativeIds.add(operation.get(5,TimeUnit.SECONDS).nativeId);
            check(nativeIds.size()==256&&nativeIds.contains(1)&&nativeIds.contains(256),"concurrent registrations issue every native ID exactly once");
            List<Future<BridgeRequestTickets.Ticket>> completions=new ArrayList<>();for(int id:nativeIds)completions.add(workers.submit(()->concurrent.complete(id)));
            boolean complete=true;for(Future<BridgeRequestTickets.Ticket> completion:completions)complete&=completion.get(5,TimeUnit.SECONDS)!=null;
            check(complete,"concurrent completions each receive one ticket");
            concurrent.reset();CountDownLatch duplicateReady=new CountDownLatch(1);List<Future<Boolean>> duplicates=new ArrayList<>();
            for(int index=0;index<16;index++)duplicates.add(workers.submit(()->{duplicateReady.await();try{concurrent.register(1,41);return true;}catch(IllegalStateException ex){return false;}}));
            duplicateReady.countDown();int admitted=0;for(Future<Boolean> duplicate:duplicates)if(duplicate.get(5,TimeUnit.SECONDS))admitted++;
            check(admitted==1,"concurrent duplicate registrations admit only one operation");
        }finally{workers.shutdownNow();check(workers.awaitTermination(5,TimeUnit.SECONDS),"harness workers terminate");}
        System.out.println("Bridge request tickets: "+checks+" checks passed.");
    }
}
