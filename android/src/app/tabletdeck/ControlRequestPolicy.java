package app.tabletdeck;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Set;

/** Rejects delayed tablet writes before they reach either transport. */
public final class ControlRequestPolicy {
    public static final long MAX_QUEUE_AGE_NANOS = 2_000_000_000L;
    private static final Set<String> INTERACTIVE_WRITES = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
        "/api/action", "/api/audio", "/api/mute", "/api/apps", "/api/hold",
        "/api/soundpad/play", "/api/pen", "/api/pen/frame", "/api/computer/action", "/api/computer/media/action",
        "/api/computer/preset", "/api/computer/follow", "/api/layout", "/api/profile",
        "/api/soundpad/favourites", "/api/soundpad/recent/clear"
    )));

    private ControlRequestPolicy() {}

    public static boolean isInteractiveWrite(String path, String method) {
        return "POST".equals(method) && INTERACTIVE_WRITES.contains(path);
    }

    /**
     * Call on the worker immediately before dispatch. Times come from System.nanoTime();
     * the lifecycle generation must advance whenever the activity leaves the foreground.
     * A cancel or hold release remains available even after the originating view disappears.
     */
    public static void validate(String path, String method, String operation, boolean paused,
                                long submittedNanos, long currentNanos,
                                long lifecycleGeneration, long submittedGeneration) {
        if (!isInteractiveWrite(path, method)) return;
        if ("/api/hold".equals(path) && "release".equals(operation)) return;
        if ("/api/pen".equals(path) && ("end".equals(operation) || "cancel".equals(operation))) return;
        if (paused || lifecycleGeneration != submittedGeneration) {
            throw new IllegalStateException("Controls are paused while the tablet app is in the background. Return to the app and try again.");
        }
        long elapsed = currentNanos - submittedNanos;
        if (elapsed < 0 || elapsed > MAX_QUEUE_AGE_NANOS) {
            throw new IllegalStateException("This control request waited too long and was not sent. Check the connection and try again.");
        }
    }
}
