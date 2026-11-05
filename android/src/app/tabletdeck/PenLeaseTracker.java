package app.tabletdeck;

/** Tracks the exact PC pen lease across asynchronous begin responses and activity changes. */
public final class PenLeaseTracker<T> {
    // Matches the live pen client's start deadline; a late result is cancelled, never activated.
    public static final long BEGIN_DEADLINE_NANOS=850_000_000L;
    public static boolean timelyBegin(long submittedNanos,long nowNanos){long elapsed=nowNanos-submittedNanos;return elapsed>=0&&elapsed<BEGIN_DEADLINE_NANOS;}
    public static final class Lease<T> {
        public final String value;
        public final T target;
        private Lease(String value,T target){this.value=value;this.target=target;}
    }
    private Lease<T> active;

    /** Returns an exact lease which must be cancelled, without disturbing a newer session. */
    public synchronized Lease<T> accepted(String value,T target,long submittedGeneration,long currentGeneration,boolean foreground){
        if(value==null||value.length()<1||value.length()>128)throw new IllegalArgumentException("The PC returned an invalid pen lease.");
        Lease<T> incoming=new Lease<>(value,target);
        if(!foreground||submittedGeneration!=currentGeneration)return incoming;
        Lease<T> previous=active;active=incoming;
        return previous!=null&&!previous.value.equals(value)?previous:null;
    }

    public synchronized void ended(String value){if(active!=null&&active.value.equals(value))active=null;}
    public synchronized Lease<T> take(){Lease<T> previous=active;active=null;return previous;}
    public synchronized Lease<T> take(String value){return active!=null&&active.value.equals(value)?take():null;}
    public synchronized Lease<T> takeIf(java.util.function.Predicate<T> predicate){return active!=null&&predicate.test(active.target)?take():null;}
}
