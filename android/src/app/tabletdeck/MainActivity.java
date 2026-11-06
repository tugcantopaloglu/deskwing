package app.tabletdeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.os.Build;
import android.os.Bundle;
import android.content.SharedPreferences;
import android.content.Intent;
import android.widget.FrameLayout;
import android.graphics.Color;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.view.WindowManager;
import org.json.JSONObject;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.X509TrustManager;
import javax.net.ssl.TrustManager;
import java.io.InputStream;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.net.URL;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.security.cert.CertificateException;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.atomic.AtomicLong;

public final class MainActivity extends Activity {
    private static final String UI_HOST="app.tabletdeck.local";
    private static final Set<String> GET_PATHS=new HashSet<>(Arrays.asList("/api/ink","/api/pen","/api/computer/media","/api/computer","/api/computer/display","/api/computer/display/input","/api/computer/display/stream/status","/api/ping","/api/mixer","/api/soundpad/recent","/api/profiles","/api/soundpad/favourites","/api/board","/api/state","/api/execution","/api/apps","/api/soundpad/library"));
    private static final Set<String> POST_PATHS=new HashSet<>(Arrays.asList("/api/ink","/api/pen","/api/pen/frame","/api/computer/follow","/api/computer/media/action","/api/computer/action","/api/computer/preset","/api/computer/display/frame","/api/monitor/frame","/api/ping","/api/profile-lock","/api/soundpad/recent/clear","/api/profile","/api/soundpad/favourites","/api/cancel","/api/hold","/api/action","/api/audio","/api/mute","/api/apps","/api/layout","/api/soundpad/play","/api/soundpad/library"));
    private static final Set<String> UI_ASSETS=new HashSet<>(Arrays.asList("/pen-tablet.js","/pen-tablet.css","/modes.js","/modes.css","/readiness.css","/editor.js","/editor.css","/macro-editor.js","/macro-editor.css","/pen.js","/pen.css","/tablet-workflows.css","/soundpad-board.js","/media-touchbar.js","/media-touchbar.css","/computer.js","/computer.css","/index.html","/app.js","/features.js","/boards.js","/desktop.js","/studio.js","/monitor.js","/hardening.js","/latency.js","/style.css","/fonts/Manrope.ttf","/vendor/purify.min.js"));
    static {UI_ASSETS.addAll(NotebookUiAssets.PATHS);}
    private WebView web;
    private ChatPane chatPane;
    private NativePenTools penTools;
    private NativeNotebookDocuments notebookDocuments;
    private NativeFullscreen fullscreen;
    private NativeDisplayVideo displayVideo;
    private boolean backPending;
    private long backSequence;
    private SharedPreferences preferences;
    private final Object connectionGate=new Object();
    private final ExecutorService network=boundedExecutor(2,32);
    private final ExecutorService controls=boundedExecutor(3,16);
    private final ExecutorService urgent=boundedExecutor(1,8);
    private final ExecutorService penControls=boundedExecutor(1,2);
    private final ExecutorService previews=boundedExecutor(1,2);
    // Pen frames never queue or share the input transport: a busy capture is dropped.
    private final ExecutorService penPreviews=new ThreadPoolExecutor(1,1,0L,TimeUnit.MILLISECONDS,new SynchronousQueue<Runnable>(),new ThreadPoolExecutor.AbortPolicy());
    private final ScheduledExecutorService previewDeadlines=Executors.newSingleThreadScheduledExecutor();
    private final AtomicLong lifecycleGeneration=new AtomicLong();
    private final AtomicLong webDocumentGeneration=new AtomicLong();
    private final BridgeRequestTickets bridgeTickets=new BridgeRequestTickets();
    private final AtomicLong penPreviewGeneration=new AtomicLong();
    private final ConcurrentHashMap<Integer,Boolean> fastPending=new ConcurrentHashMap<>();
    private final ConcurrentHashMap<Integer,PenRequest> fastPenPending=new ConcurrentHashMap<>();
    private final PenLeaseTracker<PenTarget> penLeases=new PenLeaseTracker<>();
    private FastLink fastLink;
    private int pendingScan;
    private SSLSocketFactory cachedFactory;
    private String cachedPin="";
    private volatile boolean paused=false;
    private WifiManager.WifiLock wifiLock;
    private volatile boolean closed=false;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        preferences=getSharedPreferences("connection",MODE_PRIVATE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON|WindowManager.LayoutParams.FLAG_FULLSCREEN|WindowManager.LayoutParams.FLAG_SECURE);
        web=new WebView(this);
        web.setBackgroundColor(Color.rgb(16,19,24));
        WebSettings settings=web.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        settings.setSupportMultipleWindows(false);
        settings.setMediaPlaybackRequiresUserGesture(true);
        settings.setSafeBrowsingEnabled(true);
        web.setWebViewClient(new WebViewClient(){
            @Override public void onPageStarted(WebView view,String url,android.graphics.Bitmap icon){resetWebDocument("The tablet page changed. Start the display again.");}
            @Override public boolean onRenderProcessGone(WebView view,android.webkit.RenderProcessGoneDetail detail){resetWebDocument("The tablet page restarted. Start the display again.");if(view.getParent() instanceof android.view.ViewGroup)((android.view.ViewGroup)view.getParent()).removeView(view);web=null;view.removeJavascriptInterface("DeckNative");view.destroy();recreate();return true;}
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request) {
                Uri url=request.getUrl();return !url.getScheme().equals("https")||!UI_HOST.equals(url.getHost());
            }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request) {
                Uri url=request.getUrl();
                if(!"https".equals(url.getScheme())||!UI_HOST.equals(url.getHost())||!"GET".equals(request.getMethod())) return blocked();
                String path=url.getPath();if(path==null||path.equals("/"))path="/index.html";
                if(!UI_ASSETS.contains(path))return blocked();
                try {
                    String mime=path.endsWith(".html")?"text/html":path.endsWith(".mjs")?"text/javascript":path.endsWith(".js")?"application/javascript":path.endsWith(".css")?"text/css":path.endsWith(".bcmap")?"application/octet-stream":path.endsWith(".pfb")?"application/x-font-type1":path.endsWith(".png")?"image/png":path.endsWith(".json")?"application/json":"font/ttf";
                    Map<String,String> headers=new HashMap<>();
                    headers.put("Content-Security-Policy","default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'");
                    headers.put("X-Content-Type-Options","nosniff");
                    return new WebResourceResponse(mime,"UTF-8",200,"OK",headers,getAssets().open("www"+path));
                }catch(Exception ex){return blocked();}
            }
        });
        web.addJavascriptInterface(new Bridge(),"DeckNative");
        FrameLayout root=new FrameLayout(this);root.addView(web,new FrameLayout.LayoutParams(-1,-1));setContentView(root);
        chatPane=new ChatPane(this,root,web,event->{if(!closed&&web!=null)web.evaluateJavascript("if(window.monitorChatEvent)window.monitorChatEvent("+event.toString()+");",null);});
        penTools=new NativePenTools(this,this::deliver);
        notebookDocuments=new NativeNotebookDocuments(this,this::deliver,webDocumentGeneration::get,()->!paused&&!closed);
        fullscreen=new NativeFullscreen(this,web,(enabled,reason)->{
            backSequence++;backPending=false;
            if(!closed&&web!=null)web.evaluateJavascript("document.dispatchEvent(new CustomEvent('deskwing-fullscreen',{bubbles:true,detail:{enabled:"+enabled+",reason:"+JSONObject.quote(reason)+"}}));",null);
        },this::handleBack);
        displayVideo=new NativeDisplayVideo(this,root,this::displayConnection,()->!closed&&!paused,lifecycleGeneration::get,
            (session,reason)->{if(!closed&&web!=null)web.evaluateJavascript("document.dispatchEvent(new CustomEvent('deskwing-display',{detail:{active:false,session:"+JSONObject.quote(session)+",reason:"+JSONObject.quote(reason)+"}}));",null);},
            ()->{cancelActivePen();fullscreen.setEnabled(true,"display");},()->fullscreen.setEnabled(false,"display"));
        web.setOnApplyWindowInsetsListener((view,insets)->{if(fullscreen.isEnabled())view.setPadding(0,0,0,0);else view.setPadding(insets.getSystemWindowInsetLeft(),0,insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets;});
        web.loadUrl("https://"+UI_HOST+"/index.html");
        fastLink=new FastLink(new FastLink.Listener(){
            public FastLink.Settings settings() throws Exception {
                synchronized(connectionGate){
                    String token=preferences.getString("token",""),pin=preferences.getString("fingerprint","");
                    if(token.isEmpty()||!pin.matches("[A-F0-9]{64}"))return null;
                    String host=preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":preferences.getString("host","");validateHost(host);
                    FastScope scope=new FastScope(webDocumentGeneration.get(),lifecycleGeneration.get());
                    return new FastLink.Settings(host,preferences.getInt("controlPort",8744),token,pin,pinnedFactory(pin,null),scope);
                }
            }
            public boolean packet(String json,FastLink.Settings settings,long generation) throws Exception {
                JSONObject message=new JSONObject(json);String type=message.optString("type");
                FastScope scope=(FastScope)settings.context;
                if(type.equals("ready"))return currentFastScope(scope,generation)&&message.optInt("protocol")==1;
                if(type.equals("state")){final String state=message.getJSONObject("state").toString();runOnUiThread(()->{synchronized(connectionGate){if(currentFastScope(scope,generation)&&web!=null)web.evaluateJavascript("if(window.deckPush)window.deckPush("+state+");",null);}});}
                else if(message.has("id")){int id=message.getInt("id");if(id==0){fastLink.pong();return false;}PenRequest penRequest=fastPenPending.remove(id);if(fastPending.remove(id)!=null){JSONObject result=message.has("error")?new JSONObject().put("error",message.getString("error")):message.getJSONObject("result");observePenResult(penRequest,result);deliverApi(id,result,scope.lifecycle);}}
                return false;
            }
            public void disconnected(FastLink.Settings settings,long generation){FastScope scope=(FastScope)settings.context;cancelPenLease(penLeases.takeIf(target->target.fast));runOnUiThread(()->{synchronized(connectionGate){if(currentFastScope(scope,generation)&&web!=null)web.evaluateJavascript("if(window.deckStreamClosed)window.deckStreamClosed();",null);}});for(Integer id:fastPending.keySet())if(fastPending.remove(id)!=null){fastPenPending.remove(id);try{deliverApi(id,new JSONObject().put("error","Fast link closed before confirmation. The action may have run; it was not repeated."),scope.lifecycle);}catch(Exception ignored){}}}
        });fastLink.start();
    }
    private static final class FastScope {
        final long document,lifecycle;
        FastScope(long document,long lifecycle){this.document=document;this.lifecycle=lifecycle;}
    }
    private boolean currentFastScope(FastScope scope,long generation){return !closed&&!paused&&scope.document==webDocumentGeneration.get()&&scope.lifecycle==lifecycleGeneration.get()&&fastLink!=null&&fastLink.isCurrentGeneration(generation);}
    private static WebResourceResponse blocked(){return new WebResourceResponse("text/plain","UTF-8",403,"Blocked",Collections.emptyMap(),new ByteArrayInputStream(new byte[0]));}
    private static ExecutorService boundedExecutor(int threads,int capacity){return new ThreadPoolExecutor(threads,threads,0L,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(capacity),new ThreadPoolExecutor.AbortPolicy());}
    private JSONObject connection() throws Exception {
        synchronized(connectionGate){
            String pairingId=preferences.getString("pairingId","");
            if(!preferences.getString("token","").isEmpty()&&pairingId.isEmpty()){
                pairingId=java.util.UUID.randomUUID().toString();
                if(!preferences.edit().putString("pairingId",pairingId).commit())throw new IllegalStateException("The PC context could not be saved. Open connection settings and try again.");
            }
            return new JSONObject().put("diagnostics",fastLink==null?new JSONObject():new JSONObject(fastLink.statistics())).put("roundTripMs",fastLink==null?-1:fastLink.roundTripMs()).put("fastConnected",fastLink!=null&&fastLink.connected()).put("paired",!preferences.getString("token","").isEmpty()).put("host",preferences.getString("host","")).put("mode",preferences.getString("mode","wifi")).put("pairingId",pairingId).put("port",preferences.getInt("port",8743)).put("controlPort",preferences.getInt("controlPort",8744));
        }
    }
    public final class Bridge {
        @JavascriptInterface public void dispatch(String message) {
            if(message==null||message.length()>BridgeRequestLimits.MAX_EXPORT_ENVELOPE_BYTES||closed)return;
            final String requestedPath;final JSONObject request;
            final long submittedDocument=webDocumentGeneration.get();int originalId=0,registeredId=0;
            try{request=new JSONObject(message);originalId=request.getInt("id");BridgeRequestTickets.Ticket ticket=bridgeTickets.register(originalId,submittedDocument);registeredId=ticket.nativeId;request.put("id",ticket.nativeId);if(submittedDocument!=webDocumentGeneration.get()){bridgeTickets.complete(ticket.nativeId);return;}requestedPath=request.getString("path");}
            catch(BridgeRequestTickets.DuplicateRequestException ignored){return;}
            catch(Exception ex){JSONObject error=newError("The request could not start. "+friendly(ex));if(registeredId>0)deliver(registeredId,error);else if(originalId>0)deliverRaw(originalId,error,submittedDocument);return;}
            try{BridgeRequestLimits.validate(requestedPath,request.optString("method","POST"),message,request.optJSONObject("body")==null?"{}":request.getJSONObject("body").toString());}catch(Exception ex){failRequest(request,friendly(ex));return;}
            if(requestedPath.equals("/local/display/capabilities")||requestedPath.equals("/local/display/start")||requestedPath.equals("/local/display/stop")){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Display requests require POST.");return;}
                final long requestedGeneration=lifecycleGeneration.get(),requestedDocument=submittedDocument;
                runOnUiThread(()->{try{
                    if(requestedDocument!=webDocumentGeneration.get())return;
                    int id=request.getInt("id");JSONObject body=request.optJSONObject("body");if(body==null)body=new JSONObject();
                    if(requestedPath.equals("/local/display/stop")){deliver(id,displayVideo.stop(body));return;}
                    if(closed||paused||requestedGeneration!=lifecycleGeneration.get())throw new IllegalStateException("Open DeskWing before starting the display.");
                    if(requestedPath.equals("/local/display/capabilities")){if(body.length()!=0)throw new IllegalArgumentException("Display capabilities do not accept options.");deliver(id,displayVideo.capabilities());return;}
                    if(penTools.isOpen()||notebookDocuments.isOpen())throw new IllegalStateException("Close the open pen or document tool before opening the display.");
                    displayVideo.start(body,result->deliverDisplay(id,result,requestedDocument));
                }catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(requestedPath.equals("/local/fullscreen")){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Fullscreen requires a POST request.");return;}
                final long requestedGeneration=lifecycleGeneration.get();
                runOnUiThread(()->{try{
                    if(submittedDocument!=webDocumentGeneration.get())return;
                    int id=request.getInt("id");
                    JSONObject body=request.optJSONObject("body");
                    boolean enabled=FullscreenPolicy.validateRequest(body==null?null:body.opt("enabled"),closed,paused,penTools.isOpen()||notebookDocuments.isOpen(),requestedGeneration,lifecycleGeneration.get());
                    fullscreen.setEnabled(enabled,"request");
                    deliver(id,new JSONObject().put("enabled",fullscreen.isEnabled()).put("supported",true));
                }catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(NativeNotebookDocuments.allowedPath(requestedPath)){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Notebook document requests require POST.");return;}
                final long requestedGeneration=lifecycleGeneration.get();
                runOnUiThread(()->{try{synchronized(connectionGate){if(submittedDocument!=webDocumentGeneration.get())return;if(paused||closed||requestedGeneration!=lifecycleGeneration.get())throw new IllegalStateException("The PC connection changed before this document request started.");if(requestedPath.equals("/local/notebook/import-pdf")||requestedPath.equals("/local/notebook/export-pdf/finish")){displayVideo.stop("The display stopped when a notebook document opened.");fullscreen.setEnabled(false,"document");}notebookDocuments.request(request.getInt("id"),requestedPath,request.optJSONObject("body")==null?new JSONObject():request.getJSONObject("body"));}}catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(requestedPath.equals("/local/handwriting")||requestedPath.equals("/local/export-image")){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Local pen tools require a POST request.");return;}
                runOnUiThread(()->{try{if(submittedDocument!=webDocumentGeneration.get())return;if(paused||closed)throw new IllegalStateException("Open the tablet workspace before using pen tools.");displayVideo.stop("The display stopped when a pen tool opened.");fullscreen.setEnabled(false,"tool");penTools.request(request.getInt("id"),requestedPath,request.optJSONObject("body")==null?new JSONObject():request.getJSONObject("body"));}catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(requestedPath.equals("/native/chat")){runOnUiThread(()->{try{if(submittedDocument!=webDocumentGeneration.get())return;displayVideo.stop("The display stopped when a chat window opened.");deliver(request.getInt("id"),chatPane.command(request.getJSONObject("body")));}catch(Exception ex){try{deliver(request.getInt("id"),new JSONObject().put("error",ex.getMessage()));}catch(Exception ignored){}}});return;}
            final long submittedNanos=System.nanoTime();
            final boolean writingRequest=NotebookRequestPolicy.requiresContext(request.optString("method","POST"),requestedPath);
            final String expectedPairingId=request.opt("expectedPairingId") instanceof String?request.optString("expectedPairingId",""):"";
            final PcConnection submittedConnection;
            try{synchronized(connectionGate){if(writingRequest)NotebookRequestPolicy.requireContext(expectedPairingId,preferences.getString("pairingId",""));submittedConnection=connectionSnapshot();}}catch(Exception ex){failRequest(request,friendly(ex));return;}
            final long submittedGeneration=submittedConnection.generation;
            JSONObject submittedBody=request.optJSONObject("body");
            String submittedOperation=submittedBody==null?"":submittedBody.optString("operation");
            if(requestedPath.equals("/api/pen")&&request.optString("method","POST").equals("POST")&&(submittedOperation.equals("begin")||submittedOperation.equals("end")||submittedOperation.equals("cancel")))penPreviewGeneration.incrementAndGet();
            final long submittedPenGeneration=penPreviewGeneration.get();
            final String previewLease=submittedBody==null?"":submittedBody.optString("lease");
            boolean safetyRequest=requestedPath.equals("/api/cancel")||requestedPath.equals("/api/hold")&&submittedOperation.equals("release")||requestedPath.equals("/api/pen")&&(submittedOperation.equals("end")||submittedOperation.equals("cancel"));
            ExecutorService executor=safetyRequest?urgent:requestedPath.equals("/api/pen")&&request.optString("method","POST").equals("POST")?penControls:requestedPath.equals("/api/pen/frame")?penPreviews:requestedPath.equals("/api/monitor/frame")||requestedPath.equals("/api/computer/display/frame")?previews:ControlRequestPolicy.isInteractiveWrite(requestedPath,request.optString("method","POST"))?controls:network;
            try{executor.execute(()->{
                int id=0;JSONObject result;
                try {
                    id=request.getInt("id");
                    if(submittedDocument!=webDocumentGeneration.get())throw new IllegalStateException("The tablet page changed before this request started.");
                    String path=request.getString("path"),method=request.optString("method","POST");
                    JSONObject body=request.optJSONObject("body");if(body==null)body=new JSONObject();
                    if(path.equals("/native/connection"))result=connection();
                    else if(path.equals("/native/scan")){startScan(id,submittedDocument);return;}
                    else if(path.equals("/native/diagnostics/reset")){fastLink.clearStatistics();result=connection();}
                    else if(path.equals("/native/forget")){synchronized(connectionGate){guardConnectionChange(submittedDocument,submittedGeneration);lifecycleGeneration.incrementAndGet();preferences.edit().clear().commit();fastLink.reset();notebookConnectionChangedLocked("The PC pairing was forgotten.");}cancelActivePen();updateWifiLock();result=connection();}
                    else if(path.equals("/native/transport")){
                        String mode=body.getString("mode");if(!mode.equals("usb")&&!mode.equals("wifi"))throw new Exception("Choose Wi-Fi or USB.");
                        String host=body.optString("host",submittedConnection.savedHost).trim();
                        if(mode.equals("wifi"))validateHost(host);
                        synchronized(connectionGate){guardConnectionChange(submittedDocument,submittedGeneration);lifecycleGeneration.incrementAndGet();preferences.edit().putString("pairingId",java.util.UUID.randomUUID().toString()).putString("mode",mode).putString("host",host).commit();fastLink.reset();notebookConnectionChangedLocked("The PC connection changed.");}cancelActivePen();updateWifiLock();fastLink.clearStatistics();result=connection();
                    }
                    else if(path.equals("/native/pair"))result=pair(body,submittedDocument,submittedGeneration);
                    else {
                        if(!(method.equals("GET")&&GET_PATHS.contains(path))&&!BridgeRequestLimits.isInkRead(path,method)&&!(method.equals("POST")&&POST_PATHS.contains(path))&&!NotebookRequestPolicy.allowed(method,path))throw new Exception("This request is not allowed from the tablet.");
                        if(submittedConnection.token.isEmpty())throw new Exception("Pair this tablet with your PC first.");
                        ControlRequestPolicy.validate(path,method,body.optString("operation"),paused,submittedNanos,System.nanoTime(),lifecycleGeneration.get(),submittedGeneration);
                        if(paused&&(path.equals("/api/monitor/frame")||path.equals("/api/computer/display/frame")))throw new Exception("Previews are paused while the tablet app is in the background.");
                        final String operation=body.optString("operation");
                        Runnable beforeCommit=()->{synchronized(connectionGate){if(closed||submittedDocument!=webDocumentGeneration.get())throw new IllegalStateException("The tablet page changed before this request started.");if(writingRequest)NotebookRequestPolicy.requireContext(expectedPairingId,preferences.getString("pairingId",""));if(!safetyRequest&&submittedGeneration!=lifecycleGeneration.get())throw new IllegalStateException("The PC connection changed before this request started.");ControlRequestPolicy.validate(path,method,operation,paused,submittedNanos,System.nanoTime(),lifecycleGeneration.get(),submittedGeneration);}};
                        PenRequest penRequest=path.equals("/api/pen")&&method.equals("POST")?new PenRequest(operation,body.optString("lease"),submittedGeneration,submittedNanos,submittedConnection.penTarget()):null;
                        if(method.equals("POST")&&fastPath(path)&&fastLink!=null&&fastLink.connected()&&submittedGeneration==lifecycleGeneration.get()){
                            final int fastId=id;
                            try{if(fastLink.send(request.toString(),()->{synchronized(connectionGate){beforeCommit.run();if(submittedGeneration!=lifecycleGeneration.get())throw new IllegalStateException("The PC connection changed before this request started.");fastPending.put(fastId,Boolean.TRUE);if(penRequest!=null)fastPenPending.put(fastId,penRequest.forFastLink());}}))return;fastPending.remove(id);fastPenPending.remove(id);}catch(IllegalStateException ex){fastPending.remove(id);fastPenPending.remove(id);throw ex;}catch(Exception ex){fastPending.remove(id);fastPenPending.remove(id);throw new Exception("Connection lost before confirmation. The action may have run; it was not repeated.",ex);}
                        }
                        result=sendTo(submittedConnection.host,path,method,body,submittedConnection.fingerprint,submittedConnection.token,null,submittedConnection.port,beforeCommit);
                        observePenResult(penRequest,result);
                    }
                }catch(Exception ex){result=new JSONObject();try{result.put("error",friendly(ex));}catch(Exception ignored){}}
                if(requestedPath.equals("/api/pen/frame"))deliverPenPreview(id,result,previewLease,submittedGeneration,submittedPenGeneration);
                else if(writingRequest)deliverWritingApi(id,result,submittedGeneration,expectedPairingId);
                else if(requestedPath.startsWith("/api/"))deliverApi(id,result,submittedGeneration);
                else deliver(id,result);
            });}catch(RejectedExecutionException ex){failRequest(request,"Too many pending tablet requests. Wait for the current action, then try again; this request was not queued.");}
        }
    }
    private void failRequest(JSONObject request,String message){try{deliver(request.getInt("id"),new JSONObject().put("error",message));}catch(Exception ignored){}}
    private static final class PcConnection {
        final String host,savedHost,fingerprint,token;final int port;final long generation;
        PcConnection(String host,String savedHost,int port,String fingerprint,String token,long generation){this.host=host;this.savedHost=savedHost;this.port=port;this.fingerprint=fingerprint;this.token=token;this.generation=generation;}
        PenTarget penTarget(){return new PenTarget(host,port,fingerprint,token,false);}
    }
    private PcConnection connectionSnapshot(){synchronized(connectionGate){String savedHost=preferences.getString("host","");return new PcConnection(preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":savedHost,savedHost,preferences.getInt("port",8743),preferences.getString("fingerprint",""),preferences.getString("token",""),lifecycleGeneration.get());}}
    private void guardConnectionChange(long document,long generation){if(closed||paused||document!=webDocumentGeneration.get()||generation!=lifecycleGeneration.get())throw new IllegalStateException("The tablet page or PC connection changed. Try the connection change again.");}
    private static final class PenTarget {
        final String host,fingerprint,token;final int port;final boolean fast;
        PenTarget(String host,int port,String fingerprint,String token,boolean fast){this.host=host;this.port=port;this.fingerprint=fingerprint;this.token=token;this.fast=fast;}
        PenTarget forFastLink(){return new PenTarget(host,port,fingerprint,token,true);}
    }
    private static final class PenRequest {
        final String operation,lease;final long generation,submittedNanos;final PenTarget target;
        PenRequest(String operation,String lease,long generation,long submittedNanos,PenTarget target){this.operation=operation;this.lease=lease;this.generation=generation;this.submittedNanos=submittedNanos;this.target=target;}
        PenRequest forFastLink(){return new PenRequest(operation,lease,generation,submittedNanos,target.forFastLink());}
    }
    private void observePenResult(PenRequest request,JSONObject result) throws Exception {
        if(request==null||result.has("error"))return;
        if(request.operation.equals("begin")){
            String lease=result.optString("lease","");
            boolean timely=PenLeaseTracker.timelyBegin(request.submittedNanos,System.nanoTime());
            cancelPenLease(penLeases.accepted(lease,request.target,request.generation,lifecycleGeneration.get(),!paused&&!closed&&timely));
            // A pause can race the argument snapshot above. Detach this exact response only;
            // a newly started replacement lease must never be cancelled by an older result.
            if(paused||closed||request.generation!=lifecycleGeneration.get()||!timely){cancelPenLease(penLeases.take(lease));result.put("error",timely?"The pen session was cancelled when the workspace changed. Start a new session.":"The pen session start arrived too late. Check the connection and start again.");}
        }else if(request.operation.equals("end")||request.operation.equals("cancel"))penLeases.ended(request.lease);
    }
    private void cancelActivePen(){cancelPenLease(penLeases.take());}
    private void cancelPenLease(PenLeaseTracker.Lease<PenTarget> lease){
        if(lease==null)return;
        try{urgent.execute(()->{try{PenTarget target=lease.target;sendTo(target.host,"/api/pen","POST",new JSONObject().put("operation","cancel").put("lease",lease.value),target.fingerprint,target.token,null,target.port);}catch(Exception ignored){/* The PC also releases exact fast-link leases and expires missing renewals. */}});}catch(RejectedExecutionException ignored){/* A bounded queue never retains pointer movements for replay. */}
    }
    private static boolean fastPath(String path){return path.equals("/api/pen")||path.equals("/api/ping")||path.equals("/api/cancel")||path.equals("/api/apps")||path.equals("/api/action")||path.equals("/api/audio")||path.equals("/api/mute")||path.equals("/api/hold");}
    private synchronized void startScan(int id,long document) throws Exception {if(pendingScan!=0)throw new Exception("A pairing scan is already open.");pendingScan=id;runOnUiThread(()->{if(document!=webDocumentGeneration.get()){if(pendingScan==id)pendingScan=0;return;}try{startActivityForResult(new Intent(this,QrScanActivity.class),42);}catch(Exception ex){int request=pendingScan;pendingScan=0;try{deliver(request,new JSONObject().put("error","Camera scanner unavailable. Use manual pairing."));}catch(Exception ignored){}}});}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(notebookDocuments!=null&&notebookDocuments.activityResult(request,result,data))return;if(penTools!=null&&penTools.activityResult(request,result,data))return;if(request!=42)return;int id=pendingScan;pendingScan=0;if(id==0)return;try{if(result!=RESULT_OK||data==null)throw new Exception(data==null?"Scan cancelled. You can pair manually.":data.getStringExtra("error")==null?"Scan cancelled.":data.getStringExtra("error"));PairingPayload p=PairingPayload.parse(data.getStringExtra("pairing"));deliver(id,new JSONObject().put("host",p.host).put("mode",p.mode).put("port",p.port).put("controlPort",p.controlPort).put("certificate",p.certificate).put("code",p.code));}catch(Exception ex){try{deliver(id,new JSONObject().put("error",ex.getMessage()));}catch(Exception ignored){}}}
    private void deliver(int id,JSONObject result){final String json=result.toString();runOnUiThread(()->{BridgeRequestTickets.Ticket ticket=bridgeTickets.complete(id);if(ticket!=null&&!closed&&web!=null&&ticket.document==webDocumentGeneration.get())web.evaluateJavascript("window.nativeResult("+ticket.originalId+","+json+");",null);});}
    private void deliverApi(int id,JSONObject result,long generation){final String json=result.toString();runOnUiThread(()->{synchronized(connectionGate){BridgeRequestTickets.Ticket ticket=bridgeTickets.complete(id);if(ticket==null||closed||web==null||ticket.document!=webDocumentGeneration.get())return;String delivered=generation==lifecycleGeneration.get()?json:newError("The PC connection or workspace changed while this request was pending. Its result was discarded; actions were not repeated.").toString();web.evaluateJavascript("window.nativeResult("+ticket.originalId+","+delivered+");",null);}});}
    private void deliverWritingApi(int id,JSONObject result,long generation,String expectedPairingId){final String json=result.toString();runOnUiThread(()->{synchronized(connectionGate){BridgeRequestTickets.Ticket ticket=bridgeTickets.complete(id);if(ticket==null||closed||web==null||ticket.document!=webDocumentGeneration.get())return;String delivered=json;try{NotebookRequestPolicy.requireContext(expectedPairingId,preferences.getString("pairingId",""));if(generation!=lifecycleGeneration.get())throw new IllegalStateException("The PC connection changed.");}catch(Exception changed){delivered=newError("The writing workspace's PC connection changed while this request was pending. Its result was discarded.").toString();}web.evaluateJavascript("window.nativeResult("+ticket.originalId+","+delivered+");",null);}});}
    private void deliverRaw(int id,JSONObject result,long document){final String json=result.toString();runOnUiThread(()->{if(!closed&&web!=null&&document==webDocumentGeneration.get())web.evaluateJavascript("window.nativeResult("+id+","+json+");",null);});}
    private void deliverDisplay(int id,JSONObject result,long document){deliver(id,result);}
    private static JSONObject newError(String message){JSONObject result=new JSONObject();try{result.put("error",message);}catch(Exception ignored){}return result;}
    private void resetWebDocument(String reason){synchronized(connectionGate){webDocumentGeneration.incrementAndGet();bridgeTickets.reset();lifecycleGeneration.incrementAndGet();if(fastLink!=null)fastLink.reset();}if(notebookDocuments!=null)notebookDocuments.reset(reason);if(displayVideo!=null)displayVideo.stop(reason);if(fullscreen!=null)fullscreen.setEnabled(false,"navigation");if(penTools!=null)penTools.pause();if(chatPane!=null)chatPane.hide();cancelActivePen();}
    // Called only by a winning preferences commit while connectionGate is held. A queued
    // notification cannot cancel a newer document or a later connection's file session.
    private void notebookConnectionChangedLocked(String reason){
        if(notebookDocuments!=null)notebookDocuments.reset(reason);
        final long generation=lifecycleGeneration.get(),document=webDocumentGeneration.get();
        runOnUiThread(()->{synchronized(connectionGate){if(closed||web==null||document!=webDocumentGeneration.get()||generation!=lifecycleGeneration.get())return;if(displayVideo!=null)displayVideo.stopInvalid(reason);web.evaluateJavascript("window.dispatchEvent(new CustomEvent('deskwing-connection-changed',{detail:{reason:"+JSONObject.quote(reason)+",generation:"+JSONObject.quote(Long.toString(generation))+"}}));",null);}});
    }
    private void deliverPenPreview(int id,JSONObject result,String lease,long submittedGeneration,long submittedPenGeneration){
        final boolean error=result.has("error");final String json=result.toString();
        runOnUiThread(()->{
            BridgeRequestTickets.Ticket ticket=bridgeTickets.complete(id);
            if(ticket==null||closed||web==null||ticket.document!=webDocumentGeneration.get())return;
            String delivered=json;
            if(!error&&!penLeases.canDeliverPreview(lease,submittedGeneration,lifecycleGeneration.get(),submittedPenGeneration,penPreviewGeneration.get(),!paused)){
                delivered="{\"error\":\"This preview belongs to a pen session that has ended or changed. Start the pen tablet again.\"}";
            }
            web.evaluateJavascript("window.nativeResult("+ticket.originalId+","+delivered+");",null);
        });
    }
    private JSONObject pair(JSONObject body,long document,long generation) throws Exception {
        synchronized(connectionGate){guardConnectionChange(document,generation);}
        String host=body.optString("host","").trim(),mode=body.optString("mode","wifi");
        if(!mode.equals("wifi")&&!mode.equals("usb"))throw new Exception("Choose Wi-Fi or USB.");
        if(!host.isEmpty())validateHost(host);
        if(mode.equals("wifi")&&host.isEmpty())throw new Exception("Enter the PC’s Wi-Fi address.");
        String certificate=body.getString("certificate").replaceAll("[- :\\s]","").toUpperCase(java.util.Locale.ROOT);
        if(!certificate.matches("[A-F0-9]{24}|[A-F0-9]{64}"))throw new Exception("Enter the 24-character certificate code shown on the PC.");
        String code=body.getString("code");if(!code.matches("[0-9]{6}"))throw new Exception("Enter the six-digit pairing PIN.");
        int port=body.optInt("port",8743),controlPort=body.optInt("controlPort",8744);if(port<1||port>65535||controlPort<1||controlPort>65535)throw new Exception("Invalid pairing ports.");
        String[] actualFingerprint=new String[1];
        JSONObject request=new JSONObject().put("code",code).put("name",Build.MANUFACTURER+" "+Build.MODEL);
        JSONObject response=sendTo(mode.equals("usb")?"127.0.0.1":host,"/api/pair","POST",request,certificate,"",actualFingerprint,port,()->{synchronized(connectionGate){guardConnectionChange(document,generation);}});
        if(actualFingerprint[0]==null||!actualFingerprint[0].equals(response.getString("fingerprint")))throw new Exception("The PC certificate changed. Generate a new pairing code.");
        String token=response.getString("token");if(!token.matches("[A-F0-9]{64}"))throw new Exception("The PC returned invalid pairing credentials.");
        synchronized(connectionGate){guardConnectionChange(document,generation);lifecycleGeneration.incrementAndGet();preferences.edit().putString("pairingId",java.util.UUID.randomUUID().toString()).putInt("port",port).putInt("controlPort",controlPort).putString("host",host).putString("mode",mode).putString("fingerprint",actualFingerprint[0]).putString("token",token).commit();fastLink.reset();notebookConnectionChangedLocked("The PC pairing changed.");}cancelActivePen();
        updateWifiLock();fastLink.clearStatistics();return connection();
    }
    private static void validateHost(String host) throws Exception {
        if(!host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}"))throw new Exception("Use the PC’s numeric IPv4 address shown in Pair tablet.");
        String[] parts=host.split("\\.");for(String p:parts)if(Integer.parseInt(p)>255)throw new Exception("Invalid PC address.");
        byte[] address=InetAddress.getByName(host).getAddress();int first=address[0]&255,second=address[1]&255;
        if(!(first==10||first==192&&second==168||first==172&&second>=16&&second<=31||first==127||first==169&&second==254||first==100&&second>=64&&second<=127))throw new Exception("Use a private local-network PC address.");
    }
    private static String hash(X509Certificate cert) throws Exception {
        byte[] digest=MessageDigest.getInstance("SHA-256").digest(cert.getEncoded());StringBuilder result=new StringBuilder();for(byte b:digest)result.append(String.format(java.util.Locale.ROOT,"%02X",b));return result.toString();
    }
    private JSONObject sendTo(String host,String path,String method,JSONObject body,String expected,String token,String[] actual,int port) throws Exception {
        return sendTo(host,path,method,body,expected,token,actual,port,()->{});
    }
    private JSONObject sendTo(String host,String path,String method,JSONObject body,String expected,String token,String[] actual,int port,Runnable beforeCommit) throws Exception {
        validateHost(host);
        if(!expected.matches("[A-F0-9]{24}|[A-F0-9]{64}"))throw new Exception("Pair this tablet with your PC first.");
        HttpsURLConnection conn=(HttpsURLConnection)new URL("https://"+host+":"+port+path).openConnection();
        conn.setSSLSocketFactory(pinnedFactory(expected,actual));
        conn.setHostnameVerifier((hostname,session)->{try{X509Certificate peer=(X509Certificate)session.getPeerCertificates()[0];peer.checkValidity();String found=hash(peer);return hostname.equals(host)&&MessageDigest.isEqual(found.substring(0,expected.length()).getBytes(StandardCharsets.US_ASCII),expected.getBytes(StandardCharsets.US_ASCII));}catch(Exception ex){return false;}});
        conn.setRequestProperty("Connection","keep-alive");
        boolean penPreview=path.equals("/api/pen/frame");
        conn.setConnectTimeout(penPreview?1500:6000);conn.setReadTimeout(penPreview?2000:60000);conn.setInstanceFollowRedirects(false);conn.setRequestMethod(method);conn.setRequestProperty("Accept","application/json");
        if(!token.isEmpty())conn.setRequestProperty("Authorization","Bearer "+token);
        ScheduledFuture<?> previewDeadline=penPreview?previewDeadlines.schedule(conn::disconnect,4,TimeUnit.SECONDS):null;
        boolean complete=false;
        try {
            beforeCommit.run();
            if(method.equals("POST")){byte[] requestBytes=body.toString().getBytes(StandardCharsets.UTF_8);conn.setDoOutput(true);conn.setFixedLengthStreamingMode(requestBytes.length);conn.setRequestProperty("Content-Type","application/json");conn.connect();beforeCommit.run();try(java.io.OutputStream out=conn.getOutputStream()){beforeCommit.run();out.write(requestBytes);}}
            int status=conn.getResponseCode();
            InputStream stream=status>=200&&status<300?conn.getInputStream():conn.getErrorStream();
            if(stream==null)throw new Exception("The PC refused the connection ("+status+").");
            ByteArrayOutputStream data=new ByteArrayOutputStream();
            int responseLimit=NotebookRequestPolicy.allowed(method,path)?NotebookRequestPolicy.responseLimit(method,path):path.equals("/api/ink")||BridgeRequestLimits.isInkRead(path,method)?BridgeRequestLimits.MAX_INK_ENVELOPE_BYTES:penPreview?6*1024*1024:8*1024*1024;
            try(InputStream input=stream){byte[] buffer=new byte[8192];int count;while((count=input.read(buffer))!=-1){data.write(buffer,0,count);if(data.size()>responseLimit)throw new Exception("PC response exceeds limit.");}}
            complete=true;JSONObject response=new JSONObject(data.toString("UTF-8"));
            if(status<200||status>=300)throw new Exception(response.optString("error","The PC refused this action ("+status+")."));
            return response;
        } finally {if(previewDeadline!=null)previewDeadline.cancel(false);if(!complete)conn.disconnect();}
    }
    private synchronized SSLSocketFactory pinnedFactory(String expected,String[] actual) throws Exception {
        if(actual==null&&cachedFactory!=null&&cachedPin.equals(expected))return cachedFactory;
        X509TrustManager trust=new PinnedTrustManager(expected,actual==null?null:fingerprint->actual[0]=fingerprint);
        SSLContext context=SSLContext.getInstance("TLS");context.init(null,new TrustManager[]{trust},new java.security.SecureRandom());
        SSLSocketFactory factory=context.getSocketFactory();if(actual==null){cachedFactory=factory;cachedPin=expected;}return factory;
    }
    private static String friendly(Exception ex) {
        if(ex instanceof javax.net.ssl.SSLException)return "Cannot verify the PC certificate. Check the certificate code in Pair tablet.";
        if(ex instanceof java.net.SocketTimeoutException||ex instanceof java.net.ConnectException)return "Cannot reach the PC. Keep the companion running. Check Wi-Fi / Windows Firewall, or run USB-Connect.cmd for USB.";
        return ex.getMessage()==null?"Connection failed. Try pairing again.":ex.getMessage();
    }
    private NativeDisplayVideo.Connection displayConnection() throws Exception {
        final String host,expected,token;final int port;
        synchronized(connectionGate){host=preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":preferences.getString("host","");expected=preferences.getString("fingerprint","");token=preferences.getString("token","");port=preferences.getInt("port",8743);}
        validateHost(host);if(!expected.matches("[A-F0-9]{64}")||token.isEmpty()||port<1||port>65535)throw new Exception("Pair this tablet with your PC first.");
        final SSLSocketFactory factory=pinnedFactory(expected,null);
        return path->{
            if(!NativeDisplayVideo.allowedPath(path))throw new IllegalArgumentException("Unsupported display request.");
            HttpsURLConnection connection=(HttpsURLConnection)new URL("https://"+host+":"+port+path).openConnection();connection.setSSLSocketFactory(factory);
            connection.setHostnameVerifier((hostname,session)->{try{X509Certificate peer=(X509Certificate)session.getPeerCertificates()[0];peer.checkValidity();return hostname.equals(host)&&MessageDigest.isEqual(hash(peer).getBytes(StandardCharsets.US_ASCII),expected.getBytes(StandardCharsets.US_ASCII));}catch(Exception ex){return false;}});
            connection.setRequestProperty("Authorization","Bearer "+token);return connection;
        };
    }
    private synchronized void updateWifiLock(){
        boolean wanted=!paused&&!closed&&!preferences.getString("token","").isEmpty()&&preferences.getString("mode","wifi").equals("wifi");
        try {
            if(wanted){if(wifiLock==null){WifiManager manager=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);if(manager==null)return;wifiLock=manager.createWifiLock(Build.VERSION.SDK_INT>=29?WifiManager.WIFI_MODE_FULL_LOW_LATENCY:WifiManager.WIFI_MODE_FULL_HIGH_PERF,"DeskWing:foreground-controls");wifiLock.setReferenceCounted(false);}if(!wifiLock.isHeld())wifiLock.acquire();}
            else if(wifiLock!=null&&wifiLock.isHeld())wifiLock.release();
        }catch(RuntimeException ignored){/* Unsupported Wi-Fi hardware keeps the normal transport. */}
    }
    private void handleBack(){
        if(closed||paused||web==null)return;
        if(displayVideo!=null&&displayVideo.isOpen()){displayVideo.stop("closed");return;}
        if(fullscreen!=null&&fullscreen.isEnabled()){fullscreen.setEnabled(false,"back");return;}
        if(backPending)return;
        backPending=true;final long sequence=++backSequence,generation=lifecycleGeneration.get();
        web.evaluateJavascript("(function(){var d=document.querySelector('#confirm-dialog');if(d&&d.open){d.close('cancel');return true;}var p=document.querySelector('#panel');if(p&&p.open){document.querySelector('#close-panel').click();return true;}return false;})()",result->{
            if(sequence!=backSequence)return;backPending=false;
            if(!closed&&!paused&&generation==lifecycleGeneration.get()&&!fullscreen.isEnabled()&&"false".equals(result))finish();
        });
    }
    @Override public void onBackPressed(){handleBack();}
    @Override public void onWindowFocusChanged(boolean hasFocus){super.onWindowFocusChanged(hasFocus);if(fullscreen!=null)fullscreen.windowFocusChanged(hasFocus);}
    @Override protected void onPause(){synchronized(connectionGate){paused=true;lifecycleGeneration.incrementAndGet();if(fastLink!=null)fastLink.pause();}backSequence++;backPending=false;if(displayVideo!=null)displayVideo.stop("DeskWing moved to the background. Start the display again when ready.");if(fullscreen!=null)fullscreen.setEnabled(false,"background");cancelActivePen();if(penTools!=null)penTools.pause();if(notebookDocuments!=null)notebookDocuments.pause();updateWifiLock();if(chatPane!=null)chatPane.pause();if(web!=null){web.evaluateJavascript("if(window.penTabletSuspend)window.penTabletSuspend();if(typeof releaseDeckHold===\'function\')releaseDeckHold();if(window.monitorSuspend)window.monitorSuspend();if(window.penSuspend)window.penSuspend();",null);web.onPause();}super.onPause();}
    @Override protected void onResume(){super.onResume();synchronized(connectionGate){paused=false;if(fastLink!=null)fastLink.resume();}updateWifiLock();if(chatPane!=null)chatPane.resume();if(web!=null){web.onResume();web.evaluateJavascript("if(window.monitorResume)window.monitorResume();if(window.penTabletResume)window.penTabletResume();",null);}}
    @Override protected void onDestroy(){synchronized(connectionGate){closed=true;lifecycleGeneration.incrementAndGet();if(fastLink!=null)fastLink.close();}if(displayVideo!=null)displayVideo.close();if(fullscreen!=null)fullscreen.close();cancelActivePen();if(penTools!=null)penTools.close();if(notebookDocuments!=null)notebookDocuments.close();updateWifiLock();if(chatPane!=null)chatPane.hide();network.shutdownNow();controls.shutdownNow();penControls.shutdownNow();urgent.shutdownNow();previews.shutdownNow();penPreviews.shutdownNow();previewDeadlines.shutdown();if(web!=null){web.removeJavascriptInterface("DeckNative");web.destroy();}super.onDestroy();}
}
