package app.tabletdeck;

import java.io.IOException;

/** Retains the initial key frame while a newly started decoder prepares its input buffers. */
final class DisplayDecoderStartup {
    interface InputSource {int dequeue(long timeoutUs) throws Exception;}
    static int firstInputBuffer(InputSource input,DisplayReadDeadline deadline,Runnable guard) throws Exception {
        if(input==null||deadline==null||guard==null)throw new IllegalArgumentException("Invalid decoder startup.");
        for(;;){
            check(deadline,guard);
            int index=input.dequeue(10000);
            check(deadline,guard);
            if(index>=0)return index;
        }
    }
    private static void check(DisplayReadDeadline deadline,Runnable guard) throws IOException {
        guard.run();
        if(deadline.expired())throw new IOException("The tablet decoder did not show its first picture in time. Start the display again.");
        if(deadline.cancelled())throw new IOException("The display startup was cancelled.");
    }
}
