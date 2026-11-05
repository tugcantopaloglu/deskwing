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
import java.util.concurrent.TimeUnit;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.atomic.AtomicLong;

public final class MainActivity extends Activity {
    private static final String UI_HOST="app.tabletdeck.local";
    private static final Set<String> GET_PATHS=new HashSet<>(Arrays.asList("/api/ink","/api/pen","/api/computer/media","/api/computer","/api/computer/display","/api/ping","/api/mixer","/api/soundpad/recent","/api/profiles","/api/soundpad/favourites","/api/board","/api/state","/api/execution","/api/apps","/api/soundpad/library"));
    private static final Set<String> POST_PATHS=new HashSet<>(Arrays.asList("/api/ink","/api/pen","/api/computer/follow","/api/computer/media/action","/api/computer/action","/api/computer/preset","/api/computer/display/frame","/api/monitor/frame","/api/ping","/api/profile-lock","/api/soundpad/recent/clear","/api/profile","/api/soundpad/favourites","/api/cancel","/api/hold","/api/action","/api/audio","/api/mute","/api/apps","/api/layout","/api/soundpad/play","/api/soundpad/library"));
    private static final Set<String> UI_ASSETS=new HashSet<>(Arrays.asList("/pen-tablet.js","/pen-tablet.css","/modes.js","/modes.css","/readiness.css","/editor.js","/editor.css","/macro-editor.js","/macro-editor.css","/pen.js","/pen.css","/tablet-workflows.css","/soundpad-board.js","/media-touchbar.js","/media-touchbar.css","/computer.js","/computer.css","/index.html","/app.js","/features.js","/boards.js","/desktop.js","/studio.js","/monitor.js","/hardening.js","/latency.js","/style.css","/fonts/Manrope.ttf","/vendor/purify.min.js"));
    private WebView web;
    private ChatPane chatPane;
    private NativePenTools penTools;
    private NativeFullscreen fullscreen;
    private boolean backPending;
    private long backSequence;
    private SharedPreferences preferences;
    private final ExecutorService network=boundedExecutor(2,32);
    private final ExecutorService controls=boundedExecutor(3,16);
    private final ExecutorService urgent=boundedExecutor(1,8);
    private final ExecutorService penControls=boundedExecutor(1,2);
    private final ExecutorService previews=boundedExecutor(1,2);
    private final AtomicLong lifecycleGeneration=new AtomicLong();
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
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request) {
                Uri url=request.getUrl();return !url.getScheme().equals("https")||!UI_HOST.equals(url.getHost());
            }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request) {
                Uri url=request.getUrl();
                if(!"https".equals(url.getScheme())||!UI_HOST.equals(url.getHost())||!"GET".equals(request.getMethod())) return blocked();
                String path=url.getPath();if(path==null||path.equals("/"))path="/index.html";
                if(!UI_ASSETS.contains(path))return blocked();
                try {
                    String mime=path.endsWith(".html")?"text/html":path.endsWith(".js")?"application/javascript":path.endsWith(".css")?"text/css":"font/ttf";
                    Map<String,String> headers=new HashMap<>();
                    headers.put("Content-Security-Policy","default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'");
                    headers.put("X-Content-Type-Options","nosniff");
                    return new WebResourceResponse(mime,"UTF-8",200,"OK",headers,getAssets().open("www"+path));
                }catch(Exception ex){return blocked();}
            }
        });
        web.addJavascriptInterface(new Bridge(),"DeckNative");
        FrameLayout root=new FrameLayout(this);root.addView(web,new FrameLayout.LayoutParams(-1,-1));setContentView(root);
        chatPane=new ChatPane(this,root,web,event->{if(!closed)web.evaluateJavascript("if(window.monitorChatEvent)window.monitorChatEvent("+event.toString()+");",null);});
        penTools=new NativePenTools(this,this::deliver);
        fullscreen=new NativeFullscreen(this,web,(enabled,reason)->{
            backSequence++;backPending=false;
            if(!closed)web.evaluateJavascript("document.dispatchEvent(new CustomEvent('deskwing-fullscreen',{bubbles:true,detail:{enabled:"+enabled+",reason:"+JSONObject.quote(reason)+"}}));",null);
        },this::handleBack);
        web.setOnApplyWindowInsetsListener((view,insets)->{if(fullscreen.isEnabled())view.setPadding(0,0,0,0);else view.setPadding(insets.getSystemWindowInsetLeft(),0,insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets;});
        web.loadUrl("https://"+UI_HOST+"/index.html");
        fastLink=new FastLink(new FastLink.Listener(){
            public FastLink.Settings settings() throws Exception {
                String token=preferences.getString("token",""),pin=preferences.getString("fingerprint","");
                if(token.isEmpty()||!pin.matches("[A-F0-9]{64}"))return null;
                String host=preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":preferences.getString("host","");validateHost(host);
                return new FastLink.Settings(host,preferences.getInt("controlPort",8744),token,pin,pinnedFactory(pin,null));
            }
            public boolean packet(String json) throws Exception {
                JSONObject message=new JSONObject(json);String type=message.optString("type");
                if(type.equals("ready"))return message.optInt("protocol")==1;
                if(type.equals("state")){final String state=message.getJSONObject("state").toString();runOnUiThread(()->{if(!closed)web.evaluateJavascript("if(window.deckPush)window.deckPush("+state+");",null);});}
                else if(message.has("id")){int id=message.getInt("id");if(id==0){fastLink.pong();return false;}PenRequest penRequest=fastPenPending.remove(id);if(fastPending.remove(id)!=null){JSONObject result=message.has("error")?new JSONObject().put("error",message.getString("error")):message.getJSONObject("result");observePenResult(penRequest,result);deliver(id,result);}}
                return false;
            }
            public void disconnected(){cancelPenLease(penLeases.takeIf(target->target.fast));runOnUiThread(()->{if(!closed)web.evaluateJavascript("if(window.deckStreamClosed)window.deckStreamClosed();",null);});for(Integer id:fastPending.keySet())if(fastPending.remove(id)!=null){fastPenPending.remove(id);try{deliver(id,new JSONObject().put("error","Fast link closed before confirmation. The action may have run; it was not repeated."));}catch(Exception ignored){}}}
        });fastLink.start();
    }
    private static WebResourceResponse blocked(){return new WebResourceResponse("text/plain","UTF-8",403,"Blocked",Collections.emptyMap(),new ByteArrayInputStream(new byte[0]));}
    private static ExecutorService boundedExecutor(int threads,int capacity){return new ThreadPoolExecutor(threads,threads,0L,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(capacity),new ThreadPoolExecutor.AbortPolicy());}
    private JSONObject connection() throws Exception {
        return new JSONObject().put("diagnostics",fastLink==null?new JSONObject():new JSONObject(fastLink.statistics())).put("roundTripMs",fastLink==null?-1:fastLink.roundTripMs()).put("fastConnected",fastLink!=null&&fastLink.connected()).put("paired",!preferences.getString("token","").isEmpty()).put("host",preferences.getString("host","")).put("mode",preferences.getString("mode","wifi")).put("pairingId",preferences.getString("pairingId","")).put("port",preferences.getInt("port",8743)).put("controlPort",preferences.getInt("controlPort",8744));
    }
    public final class Bridge {
        @JavascriptInterface public void dispatch(String message) {
            if(message==null||message.length()>BridgeRequestLimits.MAX_EXPORT_ENVELOPE_BYTES||closed)return;
            final String requestedPath;final JSONObject request;
            try{request=new JSONObject(message);requestedPath=request.getString("path");}catch(Exception ignored){return;}
            try{BridgeRequestLimits.validate(requestedPath,request.optString("method","POST"),message,request.optJSONObject("body")==null?"{}":request.getJSONObject("body").toString());}catch(Exception ex){failRequest(request,friendly(ex));return;}
            if(requestedPath.equals("/local/fullscreen")){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Fullscreen requires a POST request.");return;}
                final long requestedGeneration=lifecycleGeneration.get();
                runOnUiThread(()->{try{
                    int id=request.getInt("id");
                    JSONObject body=request.optJSONObject("body");
                    boolean enabled=FullscreenPolicy.validateRequest(body==null?null:body.opt("enabled"),closed,paused,penTools.isOpen(),requestedGeneration,lifecycleGeneration.get());
                    fullscreen.setEnabled(enabled,"request");
                    deliver(id,new JSONObject().put("enabled",fullscreen.isEnabled()).put("supported",true));
                }catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(requestedPath.equals("/local/handwriting")||requestedPath.equals("/local/export-image")){
                if(!request.optString("method","POST").equals("POST")){failRequest(request,"Local pen tools require a POST request.");return;}
                runOnUiThread(()->{try{if(paused||closed)throw new IllegalStateException("Open the tablet workspace before using pen tools.");fullscreen.setEnabled(false,"tool");penTools.request(request.getInt("id"),requestedPath,request.optJSONObject("body")==null?new JSONObject():request.getJSONObject("body"));}catch(Exception ex){failRequest(request,friendly(ex));}});return;
            }
            if(requestedPath.equals("/native/chat")){runOnUiThread(()->{try{deliver(request.getInt("id"),chatPane.command(request.getJSONObject("body")));}catch(Exception ex){try{deliver(request.getInt("id"),new JSONObject().put("error",ex.getMessage()));}catch(Exception ignored){}}});return;}
            final long submittedNanos=System.nanoTime(),submittedGeneration=lifecycleGeneration.get();
            JSONObject submittedBody=request.optJSONObject("body");
            String submittedOperation=submittedBody==null?"":submittedBody.optString("operation");
            boolean safetyRequest=requestedPath.equals("/api/cancel")||requestedPath.equals("/api/hold")&&submittedOperation.equals("release")||requestedPath.equals("/api/pen")&&(submittedOperation.equals("end")||submittedOperation.equals("cancel"));
            ExecutorService executor=safetyRequest?urgent:requestedPath.equals("/api/pen")&&request.optString("method","POST").equals("POST")?penControls:requestedPath.equals("/api/monitor/frame")||requestedPath.equals("/api/computer/display/frame")?previews:ControlRequestPolicy.isInteractiveWrite(requestedPath,request.optString("method","POST"))?controls:network;
            try{executor.execute(()->{
                int id=0;JSONObject result;
                try {
                    id=request.getInt("id");
                    String path=request.getString("path"),method=request.optString("method","POST");
                    JSONObject body=request.optJSONObject("body");if(body==null)body=new JSONObject();
                    if(path.equals("/native/connection"))result=connection();
                    else if(path.equals("/native/scan")){startScan(id);return;}
                    else if(path.equals("/native/diagnostics/reset")){fastLink.clearStatistics();result=connection();}
                    else if(path.equals("/native/forget")){lifecycleGeneration.incrementAndGet();cancelActivePen();preferences.edit().clear().commit();updateWifiLock();fastLink.reset();result=connection();}
                    else if(path.equals("/native/transport")){
                        String mode=body.getString("mode");if(!mode.equals("usb")&&!mode.equals("wifi"))throw new Exception("Choose Wi-Fi or USB.");
                        String host=body.optString("host",preferences.getString("host","")).trim();
                        if(mode.equals("wifi"))validateHost(host);
                        lifecycleGeneration.incrementAndGet();cancelActivePen();preferences.edit().putString("mode",mode).putString("host",host).commit();updateWifiLock();fastLink.clearStatistics();fastLink.reset();result=connection();
                    }
                    else if(path.equals("/native/pair"))result=pair(body);
                    else {
                        if(!(method.equals("GET")&&GET_PATHS.contains(path))&&!BridgeRequestLimits.isInkRead(path,method)&&!(method.equals("POST")&&POST_PATHS.contains(path)))throw new Exception("This request is not allowed from the tablet.");
                        String token=preferences.getString("token","");if(token.isEmpty())throw new Exception("Pair this tablet with your PC first.");
                        ControlRequestPolicy.validate(path,method,body.optString("operation"),paused,submittedNanos,System.nanoTime(),lifecycleGeneration.get(),submittedGeneration);
                        if(paused&&(path.equals("/api/monitor/frame")||path.equals("/api/computer/display/frame")))throw new Exception("Previews are paused while the tablet app is in the background.");
                        final String operation=body.optString("operation");
                        Runnable beforeCommit=()->ControlRequestPolicy.validate(path,method,operation,paused,submittedNanos,System.nanoTime(),lifecycleGeneration.get(),submittedGeneration);
                        PenRequest penRequest=path.equals("/api/pen")&&method.equals("POST")?new PenRequest(operation,body.optString("lease"),submittedGeneration,submittedNanos,penTarget()):null;
                        if(method.equals("POST")&&fastPath(path)&&fastLink!=null&&fastLink.connected()){
                            fastPending.put(id,Boolean.TRUE);
                            if(penRequest!=null)fastPenPending.put(id,penRequest.forFastLink());
                            try{if(fastLink.send(request.toString(),beforeCommit))return;fastPending.remove(id);fastPenPending.remove(id);}catch(IllegalStateException ex){fastPending.remove(id);fastPenPending.remove(id);throw ex;}catch(Exception ex){fastPending.remove(id);fastPenPending.remove(id);throw new Exception("Connection lost before confirmation. The action may have run; it was not repeated.",ex);}
                        }
                        String host=preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":preferences.getString("host","");
                        result=send(host,path,method,body,preferences.getString("fingerprint",""),token,null,beforeCommit);
                        observePenResult(penRequest,result);
                    }
                }catch(Exception ex){result=new JSONObject();try{result.put("error",friendly(ex));}catch(Exception ignored){}}
                deliver(id,result);
            });}catch(RejectedExecutionException ex){failRequest(request,"Too many pending tablet requests. Wait for the current action, then try again; this request was not queued.");}
        }
    }
    private void failRequest(JSONObject request,String message){try{deliver(request.getInt("id"),new JSONObject().put("error",message));}catch(Exception ignored){}}
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
    private PenTarget penTarget(){return new PenTarget(preferences.getString("mode","wifi").equals("usb")?"127.0.0.1":preferences.getString("host",""),preferences.getInt("port",8743),preferences.getString("fingerprint",""),preferences.getString("token",""),false);}
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
    private synchronized void startScan(int id) throws Exception {if(pendingScan!=0)throw new Exception("A pairing scan is already open.");pendingScan=id;runOnUiThread(()->{try{startActivityForResult(new Intent(this,QrScanActivity.class),42);}catch(Exception ex){int request=pendingScan;pendingScan=0;try{deliver(request,new JSONObject().put("error","Camera scanner unavailable. Use manual pairing."));}catch(Exception ignored){}}});}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(penTools!=null&&penTools.activityResult(request,result,data))return;if(request!=42)return;int id=pendingScan;pendingScan=0;if(id==0)return;try{if(result!=RESULT_OK||data==null)throw new Exception(data==null?"Scan cancelled. You can pair manually.":data.getStringExtra("error")==null?"Scan cancelled.":data.getStringExtra("error"));PairingPayload p=PairingPayload.parse(data.getStringExtra("pairing"));deliver(id,new JSONObject().put("host",p.host).put("mode",p.mode).put("port",p.port).put("controlPort",p.controlPort).put("certificate",p.certificate).put("code",p.code));}catch(Exception ex){try{deliver(id,new JSONObject().put("error",ex.getMessage()));}catch(Exception ignored){}}}
    private void deliver(int id,JSONObject result){final String json=result.toString();runOnUiThread(()->{if(!closed)web.evaluateJavascript("window.nativeResult("+id+","+json+");",null);});}
    private JSONObject pair(JSONObject body) throws Exception {
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
        JSONObject response=sendTo(mode.equals("usb")?"127.0.0.1":host,"/api/pair","POST",request,certificate,"",actualFingerprint,port);
        if(actualFingerprint[0]==null||!actualFingerprint[0].equals(response.getString("fingerprint")))throw new Exception("The PC certificate changed. Generate a new pairing code.");
        lifecycleGeneration.incrementAndGet();cancelActivePen();preferences.edit().putString("pairingId",java.util.UUID.randomUUID().toString()).putInt("port",port).putInt("controlPort",controlPort).putString("host",host).putString("mode",mode).putString("fingerprint",actualFingerprint[0]).putString("token",response.getString("token")).commit();
        updateWifiLock();fastLink.clearStatistics();fastLink.reset();return connection();
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
    private JSONObject send(String host,String path,String method,JSONObject body,String expected,String token,String[] actual,Runnable beforeCommit) throws Exception {
        return sendTo(host,path,method,body,expected,token,actual,preferences.getInt("port",8743),beforeCommit);
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
        conn.setConnectTimeout(6000);conn.setReadTimeout(60000);conn.setInstanceFollowRedirects(false);conn.setRequestMethod(method);conn.setRequestProperty("Accept","application/json");
        if(!token.isEmpty())conn.setRequestProperty("Authorization","Bearer "+token);
        boolean complete=false;
        try {
            if(method.equals("POST")){byte[] requestBytes=body.toString().getBytes(StandardCharsets.UTF_8);conn.setDoOutput(true);conn.setFixedLengthStreamingMode(requestBytes.length);conn.setRequestProperty("Content-Type","application/json");conn.connect();beforeCommit.run();try(java.io.OutputStream out=conn.getOutputStream()){beforeCommit.run();out.write(requestBytes);}}
            int status=conn.getResponseCode();
            InputStream stream=status>=200&&status<300?conn.getInputStream():conn.getErrorStream();
            if(stream==null)throw new Exception("The PC refused the connection ("+status+").");
            ByteArrayOutputStream data=new ByteArrayOutputStream();
            int responseLimit=path.equals("/api/ink")||BridgeRequestLimits.isInkRead(path,method)?BridgeRequestLimits.MAX_INK_ENVELOPE_BYTES:8*1024*1024;
            try(InputStream input=stream){byte[] buffer=new byte[8192];int count;while((count=input.read(buffer))!=-1){data.write(buffer,0,count);if(data.size()>responseLimit)throw new Exception("PC response exceeds limit.");}}
            complete=true;JSONObject response=new JSONObject(data.toString("UTF-8"));
            if(status<200||status>=300)throw new Exception(response.optString("error","The PC refused this action ("+status+")."));
            return response;
        } finally {if(!complete)conn.disconnect();}
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
    private synchronized void updateWifiLock(){
        boolean wanted=!paused&&!closed&&!preferences.getString("token","").isEmpty()&&preferences.getString("mode","wifi").equals("wifi");
        try {
            if(wanted){if(wifiLock==null){WifiManager manager=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);if(manager==null)return;wifiLock=manager.createWifiLock(Build.VERSION.SDK_INT>=29?WifiManager.WIFI_MODE_FULL_LOW_LATENCY:WifiManager.WIFI_MODE_FULL_HIGH_PERF,"TabletDeck:foreground-controls");wifiLock.setReferenceCounted(false);}if(!wifiLock.isHeld())wifiLock.acquire();}
            else if(wifiLock!=null&&wifiLock.isHeld())wifiLock.release();
        }catch(RuntimeException ignored){/* Unsupported Wi-Fi hardware keeps the normal transport. */}
    }
    private void handleBack(){
        if(closed||paused||web==null)return;
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
    @Override protected void onPause(){paused=true;backSequence++;backPending=false;lifecycleGeneration.incrementAndGet();if(fullscreen!=null)fullscreen.setEnabled(false,"background");cancelActivePen();if(penTools!=null)penTools.pause();updateWifiLock();if(chatPane!=null)chatPane.pause();if(fastLink!=null)fastLink.pause();if(web!=null){web.evaluateJavascript("if(window.penTabletSuspend)window.penTabletSuspend();if(typeof releaseDeckHold===\'function\')releaseDeckHold();if(window.monitorSuspend)window.monitorSuspend();if(window.penSuspend)window.penSuspend();",null);web.onPause();}super.onPause();}
    @Override protected void onResume(){super.onResume();paused=false;updateWifiLock();if(chatPane!=null)chatPane.resume();if(fastLink!=null)fastLink.resume();if(web!=null){web.onResume();web.evaluateJavascript("if(window.monitorResume)window.monitorResume();if(window.penTabletResume)window.penTabletResume();",null);}}
    @Override protected void onDestroy(){closed=true;lifecycleGeneration.incrementAndGet();if(fullscreen!=null)fullscreen.close();cancelActivePen();if(penTools!=null)penTools.close();updateWifiLock();if(chatPane!=null)chatPane.hide();if(fastLink!=null)fastLink.close();network.shutdownNow();controls.shutdownNow();penControls.shutdownNow();urgent.shutdownNow();previews.shutdownNow();web.removeJavascriptInterface("DeckNative");web.destroy();super.onDestroy();}
}
