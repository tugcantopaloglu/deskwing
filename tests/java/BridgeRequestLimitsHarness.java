import app.tabletdeck.BridgeRequestLimits;
import java.util.Arrays;

public final class BridgeRequestLimitsHarness {
    private static int passed;
    private static void check(boolean value, String label) {
        if (!value) throw new AssertionError(label);
        passed++;
        System.out.println("PASS " + label);
    }
    private static String repeated(char value, int count) {
        char[] data = new char[count];
        Arrays.fill(data, value);
        return new String(data);
    }
    private static void accept(String path, String method, String message, String body, String label) {
        BridgeRequestLimits.validate(path, method, message, body);
        check(true, label);
    }
    private static void reject(String path, String method, String message, String body, String label) {
        try {
            BridgeRequestLimits.validate(path, method, message, body);
            throw new AssertionError(label);
        } catch (IllegalArgumentException expected) {
            check(expected.getMessage() != null && !expected.getMessage().isEmpty(), label);
        }
    }
    public static void main(String[] args) {
        String revision = repeated('a', 64);
        check(BridgeRequestLimits.isInkRead("/api/ink", "GET"), "plain ink GET is recognized");
        check(BridgeRequestLimits.isInkRead("/api/ink?revision=" + revision, "GET"), "exact revision ink GET is recognized");
        check(BridgeRequestLimits.isInkRead("/api/ink?revision=" + repeated('F', 64), "GET"), "uppercase hex revision is recognized");
        for (String path : new String[]{"/api/ink?revision=", "/api/ink?revision=" + repeated('a', 63),
                "/api/ink?revision=" + repeated('a', 65), "/api/ink?revision=" + repeated('g', 64),
                "/api/ink?revision=" + revision + "&extra=1", "/api/ink?revision=" + revision + "&revision=" + revision,
                "/api/ink?revision=" + revision + "#fragment", "/api/ink?revision=" + repeated('a', 62) + "%61",
                "/api/ink?Revision=" + revision, "/api/ink/?revision=" + revision, "/api/ink?x=1", null}) {
            check(!BridgeRequestLimits.isInkRead(path, "GET"), "ink polling rejects noncanonical path " + path);
        }
        check(!BridgeRequestLimits.isInkRead("/api/ink?revision=" + revision, "POST"), "revision path cannot authorize a write");
        check(!BridgeRequestLimits.isInkRead("/api/ink", "get"), "ink polling method remains case-sensitive");
        check(!BridgeRequestLimits.isInkRead("/api/ink", null), "ink polling rejects null method");
        int control = BridgeRequestLimits.MAX_CONTROL_BYTES;
        String exactControl = repeated('a', control);
        accept("/api/pen", "POST", exactControl, "{}", "control accepts exact 16 KiB UTF-8 envelope");
        reject("/api/pen", "POST", exactControl + "a", "{}", "control rejects one-byte envelope overflow");
        accept("/api/pen", "POST", repeated('\u00e9', control / 2), "{}", "two-byte UTF-8 exact boundary accepted");
        reject("/api/pen", "POST", repeated('\u00e9', control / 2 + 1), "{}", "control limit counts UTF-8 bytes rather than UTF-16 characters");
        accept("/api/pen", "POST", repeated('\u7b46', control / 3) + "a", "{}", "three-byte handwriting characters fit exact byte boundary");
        reject("/api/pen", "POST", repeated('\u7b46', control / 3 + 1), "{}", "CJK envelope overflow rejected");
        StringBuilder emoji = new StringBuilder();
        for (int i = 0; i < control / 4; i++) emoji.append("\ud83d\udd8a");
        accept("/api/pen", "POST", emoji.toString(), "{}", "supplementary character envelope exact boundary accepted");
        reject("/api/pen", "POST", emoji.toString() + "a", "{}", "supplementary character byte overflow rejected");

        String exactInk = repeated('a', BridgeRequestLimits.MAX_INK_BYTES);
        accept("/api/ink", "POST", exactInk + repeated(' ', 2048), exactInk, "ink allows 4 MiB body and bounded 2048-byte envelope overhead");
        reject("/api/ink", "POST", exactInk + "a", exactInk + "a", "ink body cannot spend envelope overhead as drawing data");
        reject("/api/ink", "POST", exactInk + repeated(' ', 2049), exactInk, "ink rejects excessive envelope overhead");
        String multibyteInk = repeated('\u7b46', BridgeRequestLimits.MAX_INK_BYTES / 3 + 1);
        reject("/api/ink", "POST", multibyteInk, multibyteInk, "ink body validates actual UTF-8 size");
        reject("/api/ink", "POST", "{}", null, "ink requires a serialized body");
        accept("/api/ink", "GET", "{}", null, "ink reads keep ordinary small-request behavior");
        reject("/api/ink", "GET", exactControl + "a", "{}", "ink GET does not obtain expanded write limit");
        reject("/api/ink?extra=1", "POST", exactControl + "a", "{}", "ink query variant cannot obtain expanded limit");
        reject("/api/ink", "post", exactControl + "a", "{}", "ink method matching is exact");
        reject("/api/action", "POST", exactControl + "a", exactInk, "large body does not grant another route ink privileges");

        String exactExport = repeated('a', BridgeRequestLimits.MAX_EXPORT_ENVELOPE_BYTES);
        accept("/local/export-image", "POST", exactExport, "{}", "PNG export retains bounded base64 envelope capacity");
        reject("/local/export-image", "POST", exactExport + "a", "{}", "PNG export envelope cap enforced");
        reject("/local/export-image", "GET", exactControl + "a", "{}", "export GET cannot obtain expanded limit");
        reject("/local/export-image?x=1", "POST", exactControl + "a", "{}", "export route matching is exact");
        reject(null, "POST", exactControl + "a", "{}", "null route does not obtain an expanded limit");
        reject("/api/ink", null, exactControl + "a", "{}", "null method does not obtain an expanded limit");
        reject("/api/action", "POST", null, "{}", "null bridge envelope rejected");
        System.out.println(passed + " native bridge byte-limit checks passed; no network, Android app or document access used.");
    }
}
