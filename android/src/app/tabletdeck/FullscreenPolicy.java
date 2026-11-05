package app.tabletdeck;

/** Validates immersive-mode requests before touching the Activity window. */
public final class FullscreenPolicy {
    private FullscreenPolicy() {}

    public static boolean validateRequest(Object value, boolean closed, boolean paused,
                                          boolean nativeToolOpen, long requestedGeneration,
                                          long currentGeneration) {
        if (!(value instanceof Boolean)) throw new IllegalArgumentException("Choose whether fullscreen is enabled.");
        if (closed) throw new IllegalStateException("The tablet workspace is closing.");
        boolean enabled = (Boolean)value;
        if (enabled && (paused || nativeToolOpen || requestedGeneration != currentGeneration)) {
            throw new IllegalStateException("Return to the tablet workspace before entering fullscreen.");
        }
        return enabled;
    }
}
