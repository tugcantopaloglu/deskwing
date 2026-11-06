package app.tabletdeck;

import java.nio.charset.StandardCharsets;

/** Byte limits for the local JavaScript bridge; this does not authorize a route. */
public final class BridgeRequestLimits {
    public static final int MAX_CONTROL_BYTES = 16 * 1024;
    public static final int MAX_INK_BYTES = 4 * 1024 * 1024;
    public static final int MAX_PNG_BYTES = 8 * 1024 * 1024;
    public static final int MAX_INK_ENVELOPE_BYTES = MAX_INK_BYTES + 2048;
    public static final int MAX_EXPORT_ENVELOPE_BYTES = ((MAX_PNG_BYTES + 2) / 3) * 4 + 2048;

    private BridgeRequestLimits() {}

    public static boolean isInkRead(String path, String method) {
        return "GET".equals(method) && path != null &&
            ("/api/ink".equals(path) || path.matches("/api/ink\\?revision=[0-9A-Fa-f]{64}"));
    }

    /**
     * message is the full incoming bridge JSON; bodyJson is the serialized body which
     * will actually be sent to the PC. Route/method matching is deliberately exact.
     */
    public static void validate(String path, String method, String message, String bodyJson) {
        if (message == null) throw new IllegalArgumentException("The tablet request is empty.");
        int maximum = MAX_CONTROL_BYTES;
        boolean inkWrite = "/api/ink".equals(path) && "POST".equals(method);
        boolean notebookWrite = NotebookRequestPolicy.requestLimit(method,path) == NotebookRequestPolicy.WRITE_BYTES;
        String writeError = notebookWrite
            ? "This notebook request exceeds its 4 MB limit. Reduce the page and try again."
            : "This ink request exceeds the 4 MB drawing limit. Reduce the drawing and try again.";
        if (inkWrite) maximum = MAX_INK_ENVELOPE_BYTES;
        else if (notebookWrite) maximum = NotebookRequestPolicy.WRITE_BYTES + 2048;
        else if ("/local/export-image".equals(path) && "POST".equals(method)) maximum = MAX_EXPORT_ENVELOPE_BYTES;

        if (message.length() > maximum || message.getBytes(StandardCharsets.UTF_8).length > maximum) {
            throw new IllegalArgumentException(inkWrite || notebookWrite
                ? writeError
                : maximum == MAX_CONTROL_BYTES
                    ? "This request exceeds the tablet control limit."
                    : "Export a PNG image smaller than 8 MB.");
        }
        if ((inkWrite || notebookWrite) && (bodyJson == null || bodyJson.length() > MAX_INK_BYTES || bodyJson.getBytes(StandardCharsets.UTF_8).length > MAX_INK_BYTES)) {
            throw new IllegalArgumentException(writeError);
        }
    }
}
