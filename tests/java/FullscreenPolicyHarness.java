import app.tabletdeck.FullscreenPolicy;

public final class FullscreenPolicyHarness {
    private static int passed;
    private static void check(boolean value, String label) {
        if (!value) throw new AssertionError(label);
        passed++;
        System.out.println("PASS " + label);
    }
    private static void reject(Object value, boolean closed, boolean paused, boolean tool,
                               long requested, long current, Class<?> expected, String label) {
        try {
            FullscreenPolicy.validateRequest(value,closed,paused,tool,requested,current);
            throw new AssertionError(label);
        } catch (RuntimeException error) {
            check(expected.isInstance(error), label);
        }
    }
    public static void main(String[] args) {
        check(FullscreenPolicy.validateRequest(Boolean.TRUE,false,false,false,3,3), "foreground Boolean true enters fullscreen");
        check(!FullscreenPolicy.validateRequest(Boolean.FALSE,false,false,false,3,3), "Boolean false exits fullscreen");
        for (Object value : new Object[]{null,"true","false","",0,1,0L,1.0,new Object(),new boolean[]{true}}) {
            reject(value,false,false,false,3,3,IllegalArgumentException.class,"non-Boolean request rejected: " + (value==null?"null":value.getClass().getSimpleName()));
        }
        for (boolean closed : new boolean[]{false,true}) {
            for (boolean paused : new boolean[]{false,true}) {
                for (boolean tool : new boolean[]{false,true}) {
                    for (boolean stale : new boolean[]{false,true}) {
                        long requested = stale ? 2 : 3;
                        String state="closed="+closed+", paused="+paused+", tool="+tool+", stale="+stale;
                        if (closed || paused || tool || stale) {
                            reject(Boolean.TRUE,closed,paused,tool,requested,3,IllegalStateException.class,"enable rejected for "+state);
                        } else {
                            check(FullscreenPolicy.validateRequest(Boolean.TRUE,false,false,false,requested,3),"enable accepted for "+state);
                        }
                        if (closed) {
                            reject(Boolean.FALSE,true,paused,tool,requested,3,IllegalStateException.class,"closed workspace rejects disable for "+state);
                        } else {
                            check(!FullscreenPolicy.validateRequest(Boolean.FALSE,false,paused,tool,requested,3),"disable restores safely for "+state);
                        }
                    }
                }
            }
        }
        reject(Boolean.TRUE,false,false,false,Long.MAX_VALUE,Long.MIN_VALUE,IllegalStateException.class,"generation rollover still invalidates a pending enable");
        check(!FullscreenPolicy.validateRequest(Boolean.FALSE,false,true,true,Long.MAX_VALUE,Long.MIN_VALUE),"disable remains available after generation rollover and native tool launch");
        System.out.println(passed+" native fullscreen request checks passed; no Activity, window or system bars modified.");
    }
}
