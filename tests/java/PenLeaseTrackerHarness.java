import app.tabletdeck.PenLeaseTracker;

public final class PenLeaseTrackerHarness {
    private static int passed;
    private static void check(boolean result,String label){if(!result)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    public static void main(String[] args){
        PenLeaseTracker<String> tracker=new PenLeaseTracker<>();
        check(PenLeaseTracker.timelyBegin(100,100),"fresh begin response meets the native deadline");
        check(PenLeaseTracker.timelyBegin(100,100+PenLeaseTracker.BEGIN_DEADLINE_NANOS-1),"response just before deadline remains usable");
        check(!PenLeaseTracker.timelyBegin(100,100+PenLeaseTracker.BEGIN_DEADLINE_NANOS),"begin at the frontend timeout is rejected");
        check(!PenLeaseTracker.timelyBegin(100,99),"future begin timestamp is rejected");
        check(PenLeaseTracker.timelyBegin(Long.MAX_VALUE-20,Long.MIN_VALUE+20),"begin deadline handles nanoTime wraparound");
        check(tracker.accepted("current","PC-A",1,1,true)==null,"foreground begin activates without cancellation");
        check(tracker.accepted("current","PC-A",1,1,true)==null,"duplicate begin response keeps the same lease");
        PenLeaseTracker.Lease<String> old=tracker.accepted("replacement","PC-A",1,1,true);
        check(old!=null&&old.value.equals("current")&&old.target.equals("PC-A"),"replacement cancels only the old exact lease");
        PenLeaseTracker.Lease<String> late=tracker.accepted("late","PC-B",1,2,true);
        check(late!=null&&late.value.equals("late")&&late.target.equals("PC-B"),"late begin cancels against its original PC target");
        tracker.ended("late");
        check(tracker.take().value.equals("replacement"),"late response and old end cannot clear the current lease");
        check(tracker.take()==null,"pause cancellation detaches once");
        check(tracker.accepted("background","PC-A",2,2,false).value.equals("background"),"background begin response is cancelled immediately");
        check(tracker.take()==null,"background response never becomes active");
        tracker.accepted("next","PC-A",3,3,true);tracker.ended("next");check(tracker.take()==null,"acknowledged end clears its exact lease");
        tracker.accepted("http","HTTPS",4,4,true);
        check(tracker.takeIf(target->target.equals("TLS"))==null,"unrelated TLS disconnect does not detach an HTTPS pen session");
        check(tracker.take("old")==null,"late exact cancellation cannot detach a replacement lease");
        check(tracker.take("http").target.equals("HTTPS"),"exact cancellation retains the originating transport target");
        tracker.accepted("fast","TLS",4,4,true);
        check(tracker.takeIf(target->target.equals("TLS")).value.equals("fast"),"TLS disconnect can detach its own active lease");
        for(String invalid:new String[]{null,"",new String(new char[129]).replace('\0','x')}){
            try{tracker.accepted(invalid,"PC-A",3,3,true);throw new AssertionError("invalid lease accepted");}catch(IllegalArgumentException expected){passed++;}
        }
        check(tracker.take()==null,"invalid responses do not create an active lease");
        System.out.println(passed+" native pen lease checks passed; no PC input was sent.");
    }
}
