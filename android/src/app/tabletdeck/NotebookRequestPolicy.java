package app.tabletdeck;

import java.util.regex.Pattern;

/** Exact notebook HTTP routes; local file handles and native display input use other policies. */
public final class NotebookRequestPolicy {
    public static final int CONTROL_BYTES=16*1024;
    public static final int WRITE_BYTES=4*1024*1024;
    public static final int RESPONSE_BYTES=8*1024*1024;
    private static final Pattern PAGE=Pattern.compile("/api/notebooks/[0-9A-Fa-f]{32}/pages/[0-9A-Fa-f]{32}");
    private static final Pattern REVISION=Pattern.compile("revision=[0-9A-Fa-f]{64}");
    private static final Pattern CONTEXT=Pattern.compile("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}");
    private static final int NONE=0,CATALOG=1,PAGE_ROUTE=2,HISTORY=3,RESTORE=4,REVISION_PAGE=5;

    private NotebookRequestPolicy() { }

    private static int route(String path) {
        if(path==null||path.length()>256)return NONE;
        if(path.equals("/api/notebooks"))return CATALOG;
        int query=path.indexOf('?');
        if(query>=0){
            return PAGE.matcher(path.substring(0,query)).matches()
                &&REVISION.matcher(path.substring(query+1)).matches()?REVISION_PAGE:NONE;
        }
        if(PAGE.matcher(path).matches())return PAGE_ROUTE;
        String history="/history",restore="/history/restore";
        if(path.endsWith(restore)&&PAGE.matcher(path.substring(0,path.length()-restore.length())).matches())return RESTORE;
        if(path.endsWith(history)&&PAGE.matcher(path.substring(0,path.length()-history.length())).matches())return HISTORY;
        return NONE;
    }

    public static boolean allowed(String method,String path) {
        int kind=route(path);
        return "GET".equals(method)&&(kind==CATALOG||kind==PAGE_ROUTE||kind==HISTORY||kind==REVISION_PAGE)
            ||"POST".equals(method)&&(kind==CATALOG||kind==PAGE_ROUTE||kind==RESTORE);
    }

    /** Body limit, not authorization. Unsupported variants retain the small control limit. */
    public static int requestLimit(String method,String path) {
        int kind=route(path);
        return "POST".equals(method)&&(kind==CATALOG||kind==PAGE_ROUTE)?WRITE_BYTES:CONTROL_BYTES;
    }

    /** A saved page includes its existing background as well as its bounded ink document. */
    public static int responseLimit(String method,String path) {
        return allowed(method,path)?RESPONSE_BYTES:CONTROL_BYTES;
    }

    /** Every private document, including the original shared scratchpad, is intended-PC bound. */
    public static boolean requiresContext(String method,String path) {
        return allowed(method,path)||"POST".equals(method)&&"/api/ink".equals(path)
            ||BridgeRequestLimits.isInkRead(path,method);
    }

    /** The caller must retain the intent's original public pairing ID, never refresh it at send time. */
    public static void requireContext(String expected,String current) {
        if(expected==null||current==null||!CONTEXT.matcher(expected).matches()||!CONTEXT.matcher(current).matches()||!expected.equals(current)){
            throw new IllegalStateException("The notebook's connected PC changed. Reopen its library before continuing.");
        }
    }
}
