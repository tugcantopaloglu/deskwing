package app.tabletdeck;

import android.app.Activity;
import android.graphics.Color;
import android.hardware.display.DisplayManager;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.Log;
import android.view.Display;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Iterator;
import java.util.Arrays;
import java.util.Locale;
import java.util.TreeSet;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.BooleanSupplier;
import java.util.function.LongSupplier;
import javax.net.ssl.HttpsURLConnection;
import org.json.JSONArray;
import org.json.JSONObject;

/** Native, pinned H.264 playback. Video bytes never pass through the JavaScript bridge. */
final class NativeDisplayVideo {
    interface Connection {HttpsURLConnection open(String path) throws Exception;}
    interface Connections {Connection create() throws Exception;}
    interface Reply {void complete(JSONObject value);}
    interface Events {void stopped(String session,String reason);}
    private static final String START="/api/computer/display/stream/start",STREAM="/api/computer/display/stream",STOP="/api/computer/display/stream/stop";
    private static final String MIME="application/vnd.deskwing.h264";
    private final Activity activity;
    private final FrameLayout root;
    private final Connections connections;
    private final BooleanSupplier foreground;
    private final LongSupplier lifecycle;
    private final Events events;
    private final Runnable enterFullscreen,exitFullscreen;
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final ExecutorService worker=new ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,new SynchronousQueue<Runnable>(),new ThreadPoolExecutor.AbortPolicy());
    private final AtomicBoolean workerBusy=new AtomicBoolean();
    private volatile Session active;
    private boolean closed;

    NativeDisplayVideo(Activity activity,FrameLayout root,Connections connections,BooleanSupplier foreground,LongSupplier lifecycle,Events events,Runnable enterFullscreen,Runnable exitFullscreen){
        this.activity=activity;this.root=root;this.connections=connections;this.foreground=foreground;this.lifecycle=lifecycle;this.events=events;this.enterFullscreen=enterFullscreen;this.exitFullscreen=exitFullscreen;
    }
    static boolean allowedPath(String path){return START.equals(path)||STREAM.equals(path)||STOP.equals(path);}
    boolean isOpen(){return active!=null;}
    JSONObject capabilities() throws Exception {
        Display display=display();float current=display==null?0:display.getRefreshRate();TreeSet<Integer> rates=new TreeSet<>();
        if(display!=null){Display.Mode currentMode=display.getMode();for(Display.Mode mode:display.getSupportedModes())if(mode.getPhysicalWidth()==currentMode.getPhysicalWidth()&&mode.getPhysicalHeight()==currentMode.getPhysicalHeight())rates.add(Math.round(mode.getRefreshRate()));}
        boolean decoder=false;for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos())if(hardwareAvc(info)){decoder=true;break;}
        JSONArray supported=new JSONArray();for(int rate:rates)supported.put(rate);
        return new JSONObject().put("available",decoder&&current>=59).put("currentRefreshRate",current).put("maxRefreshRate",rates.isEmpty()?current:rates.last()).put("supportedRefreshRates",supported).put("detail",decoder?"Hardware H.264 video. Actual playback rate is measured while viewing.":"This tablet does not expose a hardware H.264 decoder.");
    }
    void start(JSONObject body,Reply reply){
        Session created=null;
        try{
            exact(body,"session","refreshMode");String id=DisplayVideoProtocol.session(body.opt("session"));String mode=body.optString("refreshMode","");
            if(closed||!foreground.getAsBoolean())throw new IllegalStateException("Open DeskWing before starting the display.");
            if(active!=null||workerBusy.get())throw new IllegalStateException("The previous display is still open or closing. Close it before starting again.");
            Display display=display();float current=display==null?0:display.getRefreshRate();int target=DisplayVideoProtocol.targetRate(mode,current);
            long epoch=lifecycle.getAsLong();Connection connection=connections.create();if(epoch!=lifecycle.getAsLong())throw new IllegalStateException("The PC connection changed. Start the display again.");
            Session session=new Session(id,mode,target,current,connection,reply,epoch);created=session;active=session;
            attach(session);enterFullscreen.run();
        }catch(Exception ex){if(created!=null&&active==created){created.complete(error(ex));stop("The display could not open.");}else reply.complete(error(ex));}
    }
    JSONObject stop(JSONObject body) throws Exception {
        exact(body,"session");String id=DisplayVideoProtocol.session(body.opt("session"));
        Session session=active;boolean stopped=session!=null&&session.id.equals(id);
        if(stopped)stop("closed");
        return new JSONObject().put("stopped",stopped).put("session",id);
    }
    void stop(String reason){
        Session session=active;if(session==null)return;
        active=null;session.cancelled=true;
        HttpsURLConnection pending=session.pending;if(pending!=null)pending.disconnect();
        if(session.view!=null&&Build.VERSION.SDK_INT>=30){Surface surface=session.view.getHolder().getSurface();if(surface!=null&&surface.isValid())try{surface.setFrameRate(0,Surface.FRAME_RATE_COMPATIBILITY_DEFAULT);}catch(RuntimeException ignored){}}
        ui.removeCallbacks(session.statistics);if(session.manager!=null&&session.listener!=null)session.manager.unregisterDisplayListener(session.listener);
        root.removeView(session.overlay);exitFullscreen.run();
        session.complete(error(new IOException("The display stopped before it opened.")));
        events.stopped(session.id,reason);
    }
    void close(){closed=true;stop("DeskWing closed.");worker.shutdownNow();}
    void stopInvalid(String reason){Session session=active;if(session!=null&&!valid(session))stop(reason);}
    private Display display(){return Build.VERSION.SDK_INT>=30?activity.getDisplay():activity.getWindowManager().getDefaultDisplay();}
    private int dp(int value){return Math.round(value*activity.getResources().getDisplayMetrics().density);}
    private void attach(Session session){
        FrameLayout overlay=new FrameLayout(activity);overlay.setBackgroundColor(Color.BLACK);session.overlay=overlay;
        SurfaceView surface=new SurfaceView(activity);surface.setSecure(true);session.view=surface;overlay.addView(surface,new FrameLayout.LayoutParams(-1,-1,Gravity.CENTER));
        LinearLayout controls=new LinearLayout(activity);controls.setGravity(Gravity.CENTER_VERTICAL);controls.setPadding(dp(12),dp(4),dp(4),dp(4));controls.setBackgroundColor(0xdd101318);
        TextView statistics=new TextView(activity);statistics.setTextColor(Color.WHITE);statistics.setTextSize(13);statistics.setText("Connecting · target "+session.requestedRate+" Hz");statistics.setMaxLines(2);session.status=statistics;
        LinearLayout.LayoutParams textLayout=new LinearLayout.LayoutParams(0,-2,1);textLayout.setMarginEnd(dp(12));controls.addView(statistics,textLayout);
        Button exit=new Button(activity);exit.setText("Exit display");exit.setMinHeight(dp(48));exit.setMinWidth(dp(112));exit.setOnClickListener(view->stop("closed"));controls.addView(exit,new LinearLayout.LayoutParams(-2,dp(48)));
        FrameLayout.LayoutParams controlLayout=new FrameLayout.LayoutParams(-1,-2,Gravity.TOP);controlLayout.setMargins(dp(12),dp(12),dp(12),0);overlay.addView(controls,controlLayout);
        overlay.setOnApplyWindowInsetsListener((view,insets)->{int left=0,right=0,top=0;if(Build.VERSION.SDK_INT>=28&&insets.getDisplayCutout()!=null){left=insets.getDisplayCutout().getSafeInsetLeft();right=insets.getDisplayCutout().getSafeInsetRight();top=insets.getDisplayCutout().getSafeInsetTop();}FrameLayout.LayoutParams layout=(FrameLayout.LayoutParams)controls.getLayoutParams();layout.setMargins(Math.max(dp(12),left),Math.max(dp(12),top),Math.max(dp(12),right),0);controls.setLayoutParams(layout);return insets;});
        overlay.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->fit(session));
        surface.getHolder().addCallback(new SurfaceHolder.Callback(){
            public void surfaceCreated(SurfaceHolder holder){
                if(!valid(session)||!session.started.compareAndSet(false,true))return;
                if(!workerBusy.compareAndSet(false,true)){fail(session,new IOException("The previous display is still closing. Start again shortly."));return;}
                try{worker.execute(()->run(session,holder.getSurface()));}catch(RuntimeException ex){workerBusy.set(false);fail(session,ex);}
            }
            public void surfaceChanged(SurfaceHolder holder,int format,int width,int height){}
            public void surfaceDestroyed(SurfaceHolder holder){if(active==session)stop("The display surface closed. Start again when ready.");}
        });
        root.addView(overlay,new FrameLayout.LayoutParams(-1,-1));overlay.bringToFront();
        session.statistics=()->statistics(session);ui.postDelayed(session.statistics,1000);
        session.manager=(DisplayManager)activity.getSystemService(Activity.DISPLAY_SERVICE);
        session.listener=new DisplayManager.DisplayListener(){public void onDisplayAdded(int id){}public void onDisplayRemoved(int id){}public void onDisplayChanged(int id){if(!valid(session)||!session.mode.equals("match")||session.width==0)return;Display current=display();if(current!=null&&id==current.getDisplayId()&&Math.abs(current.getRefreshRate()-session.initialRefresh)>1.5f)stop("The tablet refresh rate changed. Start again to match its current rate.");}};
        if(session.manager!=null)session.manager.registerDisplayListener(session.listener,ui);
    }
    private void fit(Session session){
        if(active!=session||session.width<=0||session.overlay==null||session.view==null)return;
        int availableWidth=session.overlay.getWidth(),availableHeight=session.overlay.getHeight();if(availableWidth<1||availableHeight<1)return;
        double scale=Math.min((double)availableWidth/session.width,(double)availableHeight/session.height);
        FrameLayout.LayoutParams layout=(FrameLayout.LayoutParams)session.view.getLayoutParams();int width=Math.max(1,(int)Math.round(session.width*scale)),height=Math.max(1,(int)Math.round(session.height*scale));
        if(layout.width!=width||layout.height!=height){layout.width=width;layout.height=height;layout.gravity=Gravity.CENTER;session.view.setLayoutParams(layout);}
    }
    private void run(Session session,Surface surface){
        MediaCodec codec=null;Thread output=null;HandlerThread callbacks=null;
        try{
            JSONObject started=json(session,START,new JSONObject().put("refreshRate",session.requestedRate),false);
            String lease=started.optString("lease","");if(!lease.matches("[A-F0-9]{48}"))throw new IOException("The PC did not provide a valid display session.");session.lease=lease;
            guard(session);
            int width=started.getInt("width"),height=started.getInt("height"),rate=started.getInt("refreshRate");
            if(!(width==1280&&height==800||width==1600&&height==900||width==1920&&height==1080)||rate!=session.requestedRate||!"h264".equals(started.optString("codec")))throw new IOException("The PC could not provide the requested display settings.");
            double sourceRate=started.optDouble("sourceRefreshRate",0);if(!Double.isFinite(sourceRate)||sourceRate+1<rate)throw new IOException("Set the PC's DeskWing display to at least "+rate+" Hz, then start again.");
            session.width=width;session.height=height;
            HttpsURLConnection connection=session.connection.open(STREAM);session.pending=connection;configure(connection,5000);connection.setReadTimeout(3000);connection.setRequestProperty("Accept",MIME);
            write(connection,new JSONObject().put("lease",lease),()->guard(session));
            if(connection.getResponseCode()!=200)throw responseError(connection);
            String contentType=connection.getContentType();if(contentType==null||!contentType.split(";",2)[0].trim().equalsIgnoreCase(MIME))throw new IOException("The PC did not return a supported video stream.");
            try(DataInputStream input=new DataInputStream(new BufferedInputStream(connection.getInputStream(),65536))){
                DisplayVideoProtocol.header(input,width,height,rate);
                DisplayVideoProtocol.Unit first=DisplayVideoProtocol.unit(input);if(first.end()||!first.key())throw new IOException("The PC video did not begin with a complete picture.");
                byte[][] configuration=DisplayVideoProtocol.configuration(first.bytes);
                DisplayVideoProtocol.validateConfiguration(configuration,width,height);
                DisplayVideoProtocol.configurationOnly(first,true);
                MediaFormat format=MediaFormat.createVideoFormat("video/avc",width,height);format.setByteBuffer("csd-0",ByteBuffer.wrap(configuration[0]));format.setByteBuffer("csd-1",ByteBuffer.wrap(configuration[1]));
                format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,DisplayVideoProtocol.MAX_ACCESS_UNIT);format.setInteger(MediaFormat.KEY_FRAME_RATE,rate);format.setInteger(MediaFormat.KEY_PRIORITY,0);format.setFloat(MediaFormat.KEY_OPERATING_RATE,rate);if(Build.VERSION.SDK_INT>=30)format.setInteger(MediaFormat.KEY_LOW_LATENCY,1);
                String decoder=findDecoder(width,height,rate);codec=MediaCodec.createByCodecName(decoder);
                callbacks=new HandlerThread("DeskWing-display-rendered");callbacks.start();
                codec.setOnFrameRenderedListener((ignored,presentation,nanoTime)->{if(valid(session)&&session.renderMetrics.record(nanoTime,System.nanoTime()))session.rendered.incrementAndGet();},new Handler(callbacks.getLooper()));
                codec.configure(format,surface,null,0);codec.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);codec.start();
                guard(session);if(Build.VERSION.SDK_INT>=31)surface.setFrameRate(rate,Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE,Surface.CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS);else if(Build.VERSION.SDK_INT>=30)surface.setFrameRate(rate,Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
                ui.post(()->{if(valid(session)){fit(session);try{Display current=display();session.complete(new JSONObject().put("started",true).put("targetRefreshRate",rate).put("currentRefreshRate",current==null?session.initialRefresh:current.getRefreshRate()).put("session",session.id));}catch(Exception ex){fail(session,ex);}}});
                final MediaCodec decoderInstance=codec;output=new Thread(()->drain(session,decoderInstance),"DeskWing-display-output");output.start();
                DisplayVideoProtocol.Unit unit=first;long previous=-1;boolean waitingForKey=false;
                while(valid(session)){
                    guard(session);if(session.decoderError!=null)throw session.decoderError;
                    if(unit.end())break;
                    byte[][] nextConfiguration=DisplayVideoProtocol.configurationIfPresent(unit.bytes);
                    if(unit.key()&&nextConfiguration==null)throw new IOException("The PC key frame is missing its video configuration.");
                    if(nextConfiguration!=null){DisplayVideoProtocol.validateConfiguration(nextConfiguration,width,height);if(!Arrays.equals(configuration[0],nextConfiguration[0])||!Arrays.equals(configuration[1],nextConfiguration[1]))throw new IOException("The PC video configuration changed. Start the display again.");}
                    boolean configOnly=DisplayVideoProtocol.configurationOnly(unit,nextConfiguration!=null);
                    if(!configOnly){if(unit.presentationUs<=previous)throw new IOException("The PC returned an out-of-order video frame.");previous=unit.presentationUs;session.received.incrementAndGet();}
                    if(!waitingForKey||unit.key()){
                        int index=codec.dequeueInputBuffer(10000);
                        if(index>=0){ByteBuffer buffer=codec.getInputBuffer(index);if(buffer==null||buffer.capacity()<unit.bytes.length)throw new IOException("The tablet decoder cannot accept this video frame.");buffer.clear();buffer.put(unit.bytes);codec.queueInputBuffer(index,0,unit.bytes.length,unit.presentationUs,configOnly?MediaCodec.BUFFER_FLAG_CODEC_CONFIG:0);waitingForKey=false;}
                        else{waitingForKey=true;session.dropped.incrementAndGet();}
                    }else session.dropped.incrementAndGet();
                    unit=DisplayVideoProtocol.unit(input);
                }
                if(valid(session))throw new IOException("The PC stopped sharing this display.");
            }finally{connection.disconnect();if(session.pending==connection)session.pending=null;}
        }catch(Exception ex){if(!session.cancelled)fail(session,ex);}
        finally{
            session.cancelled=true;
            if(output!=null){output.interrupt();try{output.join(200);}catch(InterruptedException ex){Thread.currentThread().interrupt();}}
            if(codec!=null){try{codec.stop();}catch(RuntimeException ignored){}try{codec.release();}catch(RuntimeException ignored){}}
            if(callbacks!=null)callbacks.quitSafely();
            HttpsURLConnection pending=session.pending;if(pending!=null)pending.disconnect();
            if(!session.lease.isEmpty())try{json(session,STOP,new JSONObject().put("lease",session.lease),true);}catch(Exception ignored){}
            workerBusy.set(false);
        }
    }
    private void drain(Session session,MediaCodec codec){
        MediaCodec.BufferInfo info=new MediaCodec.BufferInfo();
        try{while(valid(session)&&!Thread.currentThread().isInterrupted()){
            int index=codec.dequeueOutputBuffer(info,10000);
            if(index>=0){codec.releaseOutputBuffer(index,valid(session));if((info.flags&MediaCodec.BUFFER_FLAG_END_OF_STREAM)!=0)return;}
            else if(index==MediaCodec.INFO_OUTPUT_FORMAT_CHANGED){MediaFormat format=codec.getOutputFormat();if(format.getInteger(MediaFormat.KEY_WIDTH)>1920||format.getInteger(MediaFormat.KEY_HEIGHT)>1088)throw new IOException("The PC video exceeds the supported display size.");}
        }}catch(Exception ex){if(valid(session))session.decoderError=ex;}
    }
    private void statistics(Session session){
        if(!valid(session))return;
        long now=System.nanoTime(),received=session.received.get(),rendered=session.rendered.get();double seconds=(now-session.statAt)/1e9;
        double incoming=(received-session.lastReceived)/seconds,shown=session.renderMetrics.framesPerSecond(now);
        session.statAt=now;session.lastReceived=received;session.lastRendered=rendered;
        Display display=display();float current=display==null?0:display.getRefreshRate();
        String below=rendered>0&&shown+2<session.requestedRate?" · below target":"";
        session.status.setText(rendered==0?"Waiting for video · target "+session.requestedRate+" Hz":String.format(Locale.ROOT,"≈%.0f fps shown · %.0f fps received\nTarget %d Hz · tablet %.0f Hz%s",shown,incoming,session.requestedRate,current,below));
        if(++session.statisticsTicks%2==0)Log.i("DeskWingDisplay",String.format(Locale.ROOT,"requested_hz=%d screen_hz=%.2f received_count=%d rendered_count=%d sample_duration_ms=%.0f receive_fps=%.2f render_estimate_fps=%.2f dropped_count=%d",session.requestedRate,current,received,rendered,seconds*1000,incoming,shown,session.dropped.get()));
        ui.postDelayed(session.statistics,1000);
    }
    private static boolean hardwareAvc(MediaCodecInfo info){
        if(info.isEncoder())return false;
        if(Build.VERSION.SDK_INT>=29){if(!info.isHardwareAccelerated()||info.isSoftwareOnly())return false;}
        else{String name=info.getName().toLowerCase(Locale.ROOT);if(name.startsWith("omx.google.")||name.startsWith("c2.android.")||name.contains(".sw."))return false;}
        for(String type:info.getSupportedTypes())if(type.equalsIgnoreCase("video/avc"))return true;
        return false;
    }
    private static String findDecoder(int width,int height,int rate) throws IOException {
        for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos())if(hardwareAvc(info))try{if(info.getCapabilitiesForType("video/avc").getVideoCapabilities().areSizeAndRateSupported(width,height,rate))return info.getName();}catch(IllegalArgumentException ignored){}
        throw new IOException("Hardware decoding cannot support "+width+" × "+height+" at "+rate+" Hz. Choose a smaller PC display or 60 Hz.");
    }
    private JSONObject json(Session session,String path,JSONObject body,boolean cleanup) throws Exception {
        HttpsURLConnection connection=session.connection.open(path);if(!cleanup)session.pending=connection;configure(connection,4000);
        try{write(connection,body,()->{if(!cleanup)guard(session);});int code=connection.getResponseCode();if(code<200||code>=300)throw responseError(connection);try(InputStream input=connection.getInputStream()){return new JSONObject(read(input));}}
        finally{connection.disconnect();if(session.pending==connection)session.pending=null;}
    }
    private static void configure(HttpsURLConnection connection,int timeout) throws Exception {connection.setConnectTimeout(timeout);connection.setReadTimeout(timeout);connection.setInstanceFollowRedirects(false);connection.setRequestMethod("POST");connection.setRequestProperty("Accept","application/json");connection.setRequestProperty("Cache-Control","no-store");connection.setUseCaches(false);}
    private static void write(HttpsURLConnection connection,JSONObject body,Runnable before) throws Exception {byte[] bytes=body.toString().getBytes(StandardCharsets.UTF_8);if(bytes.length>1024)throw new IOException("The display request is too large.");connection.setRequestProperty("Content-Type","application/json");connection.setDoOutput(true);connection.setFixedLengthStreamingMode(bytes.length);before.run();connection.connect();before.run();try(OutputStream output=connection.getOutputStream()){before.run();output.write(bytes);}}
    private static String read(InputStream input) throws IOException {if(input==null)throw new IOException("The PC did not return a display response.");ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];for(int count;(count=input.read(buffer))!=-1;){if(count>65536-bytes.size())throw new IOException("The PC display response is too large.");bytes.write(buffer,0,count);}return bytes.toString(StandardCharsets.UTF_8.name());}
    private static IOException responseError(HttpsURLConnection connection){try(InputStream input=connection.getErrorStream()){String message=new JSONObject(read(input)).optString("error","The PC refused the display stream.");return new IOException(message.length()>400?"The PC refused the display stream.":message);}catch(Exception ex){return new IOException("The PC refused the display stream.");}}
    private boolean valid(Session session){return !session.cancelled&&active==session&&!closed&&foreground.getAsBoolean()&&session.epoch==lifecycle.getAsLong();}
    private void guard(Session session){if(!valid(session))throw new IllegalStateException("The display session is no longer active.");}
    private void fail(Session session,Exception error){ui.post(()->{if(active!=session)return;session.complete(error(error));stop(error.getMessage()==null?"The display connection failed.":error.getMessage());});}
    private static JSONObject error(Exception ex){JSONObject result=new JSONObject();try{result.put("error",ex.getMessage()==null?"The display connection failed.":ex.getMessage());}catch(Exception ignored){}return result;}
    private static void exact(JSONObject object,String... names){if(object==null)throw new IllegalArgumentException("The display request is empty.");for(Iterator<String> keys=object.keys();keys.hasNext();){String key=keys.next();boolean valid=false;for(String name:names)if(name.equals(key))valid=true;if(!valid)throw new IllegalArgumentException("Unsupported display option.");}}
    private final class Session {
        final String id,mode;final int requestedRate;final float initialRefresh;final Connection connection;final Reply reply;final long epoch;
        final AtomicBoolean started=new AtomicBoolean(),replied=new AtomicBoolean();final AtomicLong received=new AtomicLong(),rendered=new AtomicLong(),dropped=new AtomicLong();final RenderedFrameMetrics renderMetrics=new RenderedFrameMetrics();
        volatile boolean cancelled;volatile HttpsURLConnection pending;volatile String lease="";volatile int width,height;volatile Exception decoderError;
        FrameLayout overlay;SurfaceView view;TextView status;Runnable statistics;DisplayManager manager;DisplayManager.DisplayListener listener;
        long statAt=System.nanoTime(),lastReceived,lastRendered;int statisticsTicks;
        Session(String id,String mode,int rate,float refresh,Connection connection,Reply reply,long epoch){this.id=id;this.mode=mode;requestedRate=rate;initialRefresh=refresh;this.connection=connection;this.reply=reply;this.epoch=epoch;}
        void complete(JSONObject result){if(replied.compareAndSet(false,true))reply.complete(result);}
    }
}
