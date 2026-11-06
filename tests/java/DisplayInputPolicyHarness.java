package app.tabletdeck;

/** Exercises the production admission, coordinate, queue and flush policies without Android or input injection. */
public final class DisplayInputPolicyHarness {
    private static int passed;
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    private static void rejects(Runnable action,String label){try{action.run();throw new AssertionError(label+" was accepted");}catch(IllegalArgumentException|IllegalStateException expected){check(true,label);}}
    private static DisplayInputPolicy.Sample event(DisplayInputPolicy policy,String phase,int id,String tool,int pointers,double x,double y){return policy.event(phase,id,tool,pointers,x,y,1280,800,0.7,14,-22,true);}
    public static void main(String[] args){
        DisplayInputPolicy policy=new DisplayInputPolicy();
        check(event(policy,"down",1,"pen",1,50,60)==null,"view mode cannot admit native input");
        rejects(()->policy.mode("mouse"),"unknown input mode is refused");
        policy.mode("pen");
        check(event(policy,"down",2,"finger",1,50,60)==null,"a palm or finger cannot start a pen stroke");
        check(event(policy,"move",1,"pen",1,50,60)==null,"a move cannot fabricate a down event");
        DisplayInputPolicy.Sample first=event(policy,"down",1,"pen",1,0,0);
        check(first!=null&&first.phase.equals("down")&&first.x==0&&first.y==0,"top-left contact maps to the target origin");
        check(first.pressure==0.7&&first.tiltX==14&&first.tiltY==-22&&first.barrel&&!first.eraser,"pen pressure, tilt and barrel are retained");
        check(event(policy,"down",2,"finger",1,20,20)==null,"palm contact does not interrupt an active pen stroke");
        check(event(policy,"move",99,"pen",1,20,20)==null,"a different pointer cannot take over contact");
        DisplayInputPolicy.Sample edge=event(policy,"move",1,"pen",1,1279.7,799.8);
        check(edge!=null&&edge.x==1&&edge.y==1,"fractional right and bottom edges map to the last target pixel");
        DisplayInputPolicy.Sample outside=event(policy,"move",1,"pen",1,-1,400);
        check(outside!=null&&outside.phase.equals("leave")&&outside.pressure==0,"leaving the video rectangle releases contact");
        check(event(policy,"move",1,"pen",1,50,60)==null,"re-entering after leaving does not resume a stroke");
        check(event(policy,"down",1,"pen",1,1280,400)==null,"letterbox/outside down is ignored rather than clamped onto the desktop");
        DisplayInputPolicy.Sample hover=event(policy,"hover",1,"eraser",1,100,100);
        check(hover!=null&&hover.pressure==0&&hover.eraser,"eraser hover keeps identity without contact pressure");
        check(policy.cancel()!=null,"hover exit removes the in-range synthetic pointer");
        check(policy.cancel()==null,"repeated cancellation cannot duplicate a release");
        rejects(()->policy.event("down",1,"pen",1,Double.NaN,0,1280,800,0.5,0,0,false),"non-finite coordinates are refused");
        rejects(()->policy.event("down",1,"pen",1,0,0,0,800,0.5,0,0,false),"unavailable target dimensions are refused");
        rejects(()->event(policy,"replay",1,"pen",1,1,1),"unknown phases are refused");
        DisplayInputPolicy.Sample eraser=policy.event("down",3,"eraser",1,100,100,1280,800,2,100,-100,true);
        check(eraser!=null&&eraser.pressure==1&&eraser.tiltX==90&&eraser.tiltY==-90&&eraser.eraser,"hardware extremes are bounded to protocol values");
        policy.mode("touch");
        check(event(policy,"down",4,"pen",1,50,60)==null,"touch mode does not mix a stylus into finger input");
        DisplayInputPolicy.Sample finger=event(policy,"down",4,"finger",1,50,60);
        check(finger!=null&&finger.pressure==0.5&&finger.tiltX==0&&finger.tiltY==0&&!finger.barrel&&!finger.eraser,"single-finger contact uses the explicit touch pen profile");
        DisplayInputPolicy.Sample second=event(policy,"down",5,"finger",2,60,60);
        check(second!=null&&second.phase.equals("leave"),"a second finger cancels the current contact");
        check(event(policy,"move",4,"finger",1,70,70)==null,"lifting one of two fingers cannot resume cancelled contact");
        policy.remaining(1);
        check(event(policy,"down",5,"finger",1,70,70)==null,"multi-touch remains blocked until all fingers lift");
        policy.remaining(0);
        check(event(policy,"down",6,"finger",1,70,70)!=null,"a fresh gesture is allowed after every finger lifts");
        DisplayInputPolicy.Sample up=event(policy,"up",6,"finger",1,80,80);
        check(up!=null&&up.pressure==0,"finger up releases pressure");
        check(policy.cancel()!=null,"touch up can be followed by an out-of-range release");
        policy.mode("pen");
        check(event(policy,"up",6,"pen",1,80,80)==null,"changing mode cannot continue the prior gesture");

        DisplayInputPolicy.Samples samples=new DisplayInputPolicy.Samples();samples.add(first);samples.add(edge);samples.add(outside);
        DisplayInputPolicy.Sample[] batch=samples.drain();
        check(batch.length==3&&batch[0]==first&&batch[1]==edge&&batch[2]==outside,"bounded batching preserves down, motion and release order");
        check(samples.size()==0&&samples.drain().length==0,"a transmitted batch cannot be replayed from the pending queue");
        samples.add(null);check(samples.size()==0,"ignored palm events consume no queue capacity");
        for(int i=0;i<DisplayInputPolicy.MAX_BATCH;i++)samples.add(edge);
        rejects(()->samples.add(edge),"a stalled transport cannot accumulate an unbounded event backlog");
        check(samples.size()==32,"overflow does not mutate the admitted queue");samples.clear();check(samples.size()==0,"stopping clears every unsent sample");

        DisplayInputPolicy.FlushSchedule flush=new DisplayInputPolicy.FlushSchedule();
        check(flush.schedule(0,8),"the first event schedules a bounded eight-millisecond flush");
        for(int tick=1;tick<8;tick++)check(!flush.schedule(tick,8),"continuous sub-eight-millisecond motion cannot postpone flush at tick "+tick);
        check(flush.delay(8)==0,"continuous high-rate input is due at the original deadline");
        flush.fired();check(flush.schedule(8,40),"an idle renewal can schedule a later check");
        check(flush.schedule(10,8)&&flush.delay(10)==8,"new motion advances an idle renewal to a low-latency flush");
        check(!flush.schedule(11,40)&&flush.delay(18)==0,"a later idle check cannot delay an admitted motion batch");
        flush.fired();check(flush.schedule(20,8),"a completed flush admits the next bounded batch");
        System.out.println(passed+" display input policy checks passed; no network, UI or Windows input was used.");
    }
}
