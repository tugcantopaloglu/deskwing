import app.tabletdeck.ControlRequestPolicy;

public final class ControlRequestPolicyHarness {
    private static int passed;
    private static final long NOW = 10_000_000_000L;
    private static final String[] CONTROLS = {
        "/api/action", "/api/audio", "/api/mute", "/api/apps", "/api/hold",
        "/api/soundpad/play", "/api/computer/action", "/api/computer/media/action",
        "/api/computer/preset", "/api/computer/follow", "/api/layout", "/api/profile",
        "/api/soundpad/favourites", "/api/soundpad/recent/clear"
    };
    private static void check(boolean value, String label) {
        if (!value) throw new AssertionError(label);
        passed++;
        System.out.println("PASS " + label);
    }
    private static void validate(String path, String method, String operation, boolean paused,
                                 long submitted, long now, long generation, long submittedGeneration) {
        ControlRequestPolicy.validate(path, method, operation, paused, submitted, now, generation, submittedGeneration);
    }
    private static void reject(String path, String operation, boolean paused, long submitted,
                               long now, long generation, long submittedGeneration, String label) {
        try {
            validate(path, "POST", operation, paused, submitted, now, generation, submittedGeneration);
            throw new AssertionError(label);
        } catch (IllegalStateException expected) {
            check(expected.getMessage().contains("try again"), label);
        }
    }
    public static void main(String[] args) {
        for (String path : CONTROLS) {
            check(ControlRequestPolicy.isInteractiveWrite(path, "POST"), "recognizes interactive write " + path);
            validate(path, "POST", "begin", false, NOW, NOW, 7, 7);
            check(true, "fresh foreground write allowed " + path);
            reject(path, "begin", true, NOW, NOW, 7, 7, "background write rejected " + path);
            reject(path, "begin", false, NOW, NOW, 8, 7, "pre-background queued write rejected after resume " + path);
            reject(path, "begin", false, NOW, NOW + ControlRequestPolicy.MAX_QUEUE_AGE_NANOS + 1, 7, 7,
                "stale queued write rejected " + path);
            validate(path, "GET", "", true, 0, NOW, 8, 7);
            check(!ControlRequestPolicy.isInteractiveWrite(path, "GET"), "GET is not treated as an interactive write " + path);
        }

        validate("/api/action", "POST", "", false, NOW, NOW + ControlRequestPolicy.MAX_QUEUE_AGE_NANOS, 7, 7);
        check(true, "exact queue deadline remains valid");
        reject("/api/action", "", false, NOW + 1, NOW, 7, 7, "future timestamp is not silently accepted");
        validate("/api/action", "POST", "", false, Long.MAX_VALUE - 20, Long.MIN_VALUE + 20, 7, 7);
        check(true, "nanoTime rollover uses elapsed subtraction");

        validate("/api/hold", "POST", "release", true, 0, NOW, 8, 7);
        check(true, "hold release survives pause, stale queue and generation change");
        validate("/api/cancel", "POST", "", true, 0, NOW, 8, 7);
        check(!ControlRequestPolicy.isInteractiveWrite("/api/cancel", "POST"), "cancellation stays available for safety");
        reject("/api/hold", "renew", true, NOW, NOW, 7, 7, "hold renewal cannot keep background keys alive");
        reject("/api/hold", "Release", true, NOW, NOW, 7, 7, "release exemption requires exact operation");
        reject("/api/action", "release", true, NOW, NOW, 7, 7, "other actions cannot claim hold-release exemption");

        for (String path : new String[]{"/api/ping", "/api/soundpad/library", "/api/monitor/frame", "/api/computer/display/frame"}) {
            validate(path, "POST", "", true, 0, NOW, 8, 7);
            check(!ControlRequestPolicy.isInteractiveWrite(path, "POST"), "read-like POST has no interactive write policy " + path);
        }
        check(!ControlRequestPolicy.isInteractiveWrite("/api/action", "post"), "method matching is exact");
        check(!ControlRequestPolicy.isInteractiveWrite("/api/action?extra=1", "POST"), "path matching is exact");
        check(!ControlRequestPolicy.isInteractiveWrite(null, "POST"), "null path is not a control route");
        check(!ControlRequestPolicy.isInteractiveWrite("/api/action", null), "null method is not a control write");
        System.out.println(passed + " native control policy checks passed; no network, Android app or key injection used.");
    }
}
