package app.tabletdeck;

import java.util.HashMap;
import java.util.Map;

/** Gives each bridge operation an app-lifetime ID, independent of a page's reused JavaScript IDs. */
final class BridgeRequestTickets {
    static final int MAX_PENDING=256;
    static final class Ticket {
        final int nativeId,originalId;
        final long document;
        Ticket(int nativeId,int originalId,long document){this.nativeId=nativeId;this.originalId=originalId;this.document=document;}
    }
    /** A duplicate has no independent reply address; replying would consume the original request. */
    static final class DuplicateRequestException extends IllegalStateException {
        DuplicateRequestException(){super("This tablet request is already pending.");}
    }
    private final Map<Integer,Ticket> pending=new HashMap<>();
    private int lastNativeId;

    BridgeRequestTickets(){this(0);}
    /** Counter injection is package-private so exhaustion can be tested without billions of requests. */
    BridgeRequestTickets(int initialCounter){
        if(initialCounter<0)throw new IllegalArgumentException("The bridge counter cannot be negative.");
        lastNativeId=initialCounter;
    }
    synchronized Ticket register(int originalId,long document){
        if(originalId<=0)throw new IllegalArgumentException("The tablet request ID must be positive.");
        for(Ticket ticket:pending.values())if(ticket.document==document&&ticket.originalId==originalId)throw new DuplicateRequestException();
        if(pending.size()>=MAX_PENDING)throw new IllegalStateException("Too many tablet requests are pending. Wait for a response before trying again.");
        if(lastNativeId==Integer.MAX_VALUE)throw new IllegalStateException("The tablet request counter is exhausted. Reopen DeskWing to continue.");
        Ticket ticket=new Ticket(++lastNativeId,originalId,document);
        pending.put(ticket.nativeId,ticket);
        return ticket;
    }
    synchronized Ticket complete(int nativeId){return pending.remove(nativeId);}
    synchronized void reset(){pending.clear();}
}
