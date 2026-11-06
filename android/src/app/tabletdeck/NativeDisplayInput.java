package app.tabletdeck;

import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.SurfaceView;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.function.BooleanSupplier;
import java.util.function.IntSupplier;
import java.util.function.Supplier;
import javax.net.ssl.HttpsURLConnection;
import org.json.JSONArray;
import org.json.JSONObject;

/** Explicit per-viewer input; all pinned HTTP and cleanup stay on its bounded worker. */
final class NativeDisplayInput implements AutoCloseable {
    static final String PATH="/api/computer/display/input";
    // Shared process-wide bounds survive activity/viewer replacement. An uncooperative TLS
    // provider can stop new input admission, but cannot create one abandoned worker per viewer.
    private static final ThreadPoolExecutor NETWORK=new ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(2),action->{Thread thread=new Thread(action,"DeskWing display input");thread.setDaemon(true);return thread;},new ThreadPoolExecutor.AbortPolicy());
    private static final ScheduledThreadPoolExecutor DEADLINES=new ScheduledThreadPoolExecutor(1,action->{Thread thread=new Thread(action,"DeskWing input deadlines");thread.setDaemon(true);return thread;});
    private static final NetworkCloseQueue CLEANUP=new NetworkCloseQueue();
    static {DEADLINES.setRemoveOnCancelPolicy(true);}
    private final NativeDisplayVideo.Connection connection;
    private final BooleanSupplier valid;
    private final Supplier<String> videoLease;
    private final IntSupplier width,height;
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final DisplayInputPolicy policy=new DisplayInputPolicy();
    private final DisplayInputPolicy.Samples queued=new DisplayInputPolicy.Samples();
    private final DisplayInputPolicy.FlushSchedule schedule=new DisplayInputPolicy.FlushSchedule();
    private final RadioGroup modes;
    private final RadioButton view,pen,touch;
    private final TextView message;
    private final Runnable pump=this::pump;
    private volatile InputSession active;
    private volatile boolean closed;
    private boolean ready,changing,busy;

    NativeDisplayInput(SurfaceView surface,LinearLayout controls,NativeDisplayVideo.Connection connection,BooleanSupplier valid,Supplier<String> videoLease,IntSupplier width,IntSupplier height){
        this.connection=connection;this.valid=valid;this.videoLease=videoLease;this.width=width;this.height=height;
        modes=new RadioGroup(surface.getContext());modes.setOrientation(LinearLayout.HORIZONTAL);
        view=choice(surface,"View");pen=choice(surface,"Pen");touch=choice(surface,"Touch");modes.addView(view);modes.addView(pen);modes.addView(touch);view.setChecked(true);pen.setEnabled(false);touch.setEnabled(false);
        message=new TextView(surface.getContext());message.setTextColor(Color.LTGRAY);message.setTextSize(12);message.setText("View only · PC input permission is separate");
        controls.addView(modes,new LinearLayout.LayoutParams(-2,-2));controls.addView(message,new LinearLayout.LayoutParams(-1,-2));
        modes.setOnCheckedChangeListener((group,id)->{if(changing)return;select(id==pen.getId()?"pen":id==touch.getId()?"touch":"view");});
        surface.setOnTouchListener((ignored,event)->touch(event,surface));
        surface.setOnHoverListener((ignored,event)->hover(event,surface));
        surface.addOnLayoutChangeListener((changed,left,top,right,bottom,oldLeft,oldTop,oldRight,oldBottom)->{if(active!=null&&oldRight>oldLeft&&oldBottom>oldTop&&(right-left!=oldRight-oldLeft||bottom-top!=oldBottom-oldTop))failure("The tablet display area changed. Choose Pen or Touch again.");});
    }
    private static RadioButton choice(SurfaceView surface,String label){RadioButton button=new RadioButton(surface.getContext());button.setId(android.view.View.generateViewId());button.setText(label);button.setTextColor(Color.WHITE);button.setMinHeight(Math.round(48*surface.getResources().getDisplayMetrics().density));return button;}
    void ready(){if(closed)return;ready=true;pen.setEnabled(true);touch.setEnabled(true);}
    private void select(String mode){
        stopInput();if(mode.equals("view"))return;
        if(!ready||closed||!valid.getAsBoolean()||!videoLease.get().matches("[A-F0-9]{48}")){failure("Start the display before enabling input.");return;}
        changing=true;(mode.equals("pen")?pen:touch).setChecked(true);changing=false;
        InputSession session=new InputSession(mode,videoLease.get());active=session;busy=true;pen.setEnabled(false);touch.setEnabled(false);message.setText("Requesting PC input permission…");
        try{NETWORK.execute(()->{
            try{
                session.started=SystemClock.elapsedRealtime();
                JSONObject body=new JSONObject().put("operation","begin").put("videoLease",session.videoLease).put("session",UUID.randomUUID().toString()).put("mode",mode);
                JSONObject reply=request(session,body,false);String lease=reply.optString("lease","");if(!lease.matches("[A-F0-9]{48}"))throw new IOException("The PC did not provide a valid input session.");session.lease=lease;
                JSONObject target=reply.getJSONObject("target");if(target.getInt("width")!=width.getAsInt()||target.getInt("height")!=height.getAsInt())throw new IOException("The PC input geometry changed. Restart the display.");
                if(!current(session)){end(session);return;}
                ui.post(()->{if(!current(session))return;busy=false;policy.mode(mode);pen.setEnabled(true);touch.setEnabled(true);message.setText(mode.equals("pen")?"Pen input · fingers ignored":"Touch input · one finger");schedulePump(8);});
            }catch(Exception error){end(session);ui.post(()->{if(active==session)failure(friendly(error));});}
        });}catch(RuntimeException error){failure("The previous input session is still closing. Choose the mode again shortly.");}
    }
    private boolean current(InputSession session){return !closed&&active==session&&!session.cancelled&&valid.getAsBoolean();}
    private boolean touch(MotionEvent event,SurfaceView surface){
        InputSession session=active;if(session==null||session.lease.length()==0||!current(session))return false;
        int action=event.getActionMasked(),index=event.getActionIndex();
        try{
            if(action==MotionEvent.ACTION_CANCEL){enqueue(policy.cancel());policy.remaining(0);return true;}
            if(action==MotionEvent.ACTION_DOWN||action==MotionEvent.ACTION_POINTER_DOWN){append(event,index,"down",surface);}
            else if(action==MotionEvent.ACTION_MOVE){for(int i=0;i<event.getPointerCount();i++)append(event,i,"move",surface);}
            else if(action==MotionEvent.ACTION_UP||action==MotionEvent.ACTION_POINTER_UP){append(event,index,"up",surface);if(session.mode.equals("touch"))enqueue(policy.cancel());policy.remaining(eligible(event,index));}
            else return false;
            return true;
        }catch(RuntimeException error){failure(friendly(error));return true;}
    }
    private boolean hover(MotionEvent event,SurfaceView surface){
        InputSession session=active;if(session==null||!session.mode.equals("pen")||session.lease.length()==0||!current(session))return false;
        try{int action=event.getActionMasked();if(action==MotionEvent.ACTION_HOVER_EXIT)enqueue(policy.cancel());else if(action==MotionEvent.ACTION_HOVER_ENTER||action==MotionEvent.ACTION_HOVER_MOVE)append(event,0,"hover",surface);else return false;return true;}catch(RuntimeException error){failure(friendly(error));return true;}
    }
    private String tool(MotionEvent event,int index){int type=event.getToolType(index);return type==MotionEvent.TOOL_TYPE_STYLUS?"pen":type==MotionEvent.TOOL_TYPE_ERASER?"eraser":type==MotionEvent.TOOL_TYPE_FINGER?"finger":"other";}
    private int eligible(MotionEvent event,int exclude){int count=0;for(int i=0;i<event.getPointerCount();i++)if(i!=exclude&&policy.admits(tool(event,i)))count++;return count;}
    private void append(MotionEvent event,int index,String phase,SurfaceView surface){
        String tool=tool(event,index);if(!policy.admits(tool))return;
        double tilt=Math.toDegrees(event.getAxisValue(MotionEvent.AXIS_TILT,index)),orientation=event.getOrientation(index);
        enqueue(policy.event(phase,event.getPointerId(index),tool,eligible(event,-1),event.getX(index),event.getY(index),surface.getWidth(),surface.getHeight(),event.getPressure(index),tilt*Math.sin(orientation),tilt*Math.cos(orientation),(event.getButtonState()&(MotionEvent.BUTTON_STYLUS_PRIMARY|MotionEvent.BUTTON_STYLUS_SECONDARY))!=0));
    }
    private void schedulePump(long delay){long now=SystemClock.elapsedRealtime();if(schedule.schedule(now,delay)){ui.removeCallbacks(pump);ui.postDelayed(pump,schedule.delay(now));}}
    private void enqueue(DisplayInputPolicy.Sample sample){queued.add(sample);if(sample!=null)schedulePump(8);}
    private void pump(){
        schedule.fired();
        InputSession session=active;if(session==null||session.lease.length()==0||!current(session))return;
        if(busy){schedulePump(8);return;}
        long now=SystemClock.elapsedRealtime();if(queued.size()==0&&now-session.lastSent<350){schedulePump(40);return;}
        DisplayInputPolicy.Sample[] samples=queued.drain();long sequence=++session.sequence;session.lastSent=now;busy=true;
        try{NETWORK.execute(()->{try{
            JSONArray values=new JSONArray();for(DisplayInputPolicy.Sample sample:samples)values.put(new JSONObject().put("phase",sample.phase).put("x",sample.x).put("y",sample.y).put("pressure",sample.pressure).put("tiltX",sample.tiltX).put("tiltY",sample.tiltY).put("barrel",sample.barrel).put("eraser",sample.eraser));
            JSONObject body=new JSONObject().put("operation",samples.length==0?"renew":"samples").put("videoLease",session.videoLease).put("lease",session.lease).put("sequence",sequence).put("elapsedMs",SystemClock.elapsedRealtime()-session.started).put("samples",values);
            request(session,body,false);ui.post(()->{if(current(session)){busy=false;schedulePump(queued.size()>0?8:40);}});
        }catch(Exception error){ui.post(()->{if(active==session)failure(friendly(error));});}});}catch(RuntimeException error){failure("Input is closing. Choose Pen or Touch again.");}
    }
    private JSONObject request(InputSession session,JSONObject body,boolean cleanup) throws Exception {
        if(!cleanup&&!current(session))throw new IOException("This input session ended.");
        byte[] bytes=body.toString().getBytes(StandardCharsets.UTF_8);if(bytes.length>16*1024)throw new IOException("The input packet is too large.");
        NetworkCloseQueue.Reservation retirement=CLEANUP.reserve();HttpsURLConnection opened=null;DisplayReadDeadline deadline=null;
        try{
            final HttpsURLConnection request=connection.open(PATH);opened=request;request.setConnectTimeout(1200);request.setReadTimeout(1500);request.setInstanceFollowRedirects(false);request.setRequestMethod("POST");request.setDoOutput(true);request.setRequestProperty("Content-Type","application/json");request.setRequestProperty("Accept","application/json");request.setFixedLengthStreamingMode(bytes.length);
            deadline=new DisplayReadDeadline(DEADLINES,1600,()->{if(!cleanup)ui.post(()->{if(active==session)failure("The PC input request timed out. Choose Pen or Touch again.");});retirement.submit(()->{try{request.disconnect();}catch(RuntimeException ignored){}});});
            if(!cleanup&&!current(session))throw new IOException("This input session ended.");
            try(OutputStream output=request.getOutputStream()){if(!cleanup&&!current(session))throw new IOException("This input session ended.");output.write(bytes);}
            int status=request.getResponseCode();InputStream stream=status==200?request.getInputStream():request.getErrorStream();ByteArrayOutputStream result=new ByteArrayOutputStream();
            if(stream!=null)try(InputStream input=stream){byte[] buffer=new byte[2048];int read;while((read=input.read(buffer))!=-1){if(result.size()+read>64*1024)throw new IOException("The input reply exceeded its limit.");result.write(buffer,0,read);}}
            if(!deadline.acceptPicture())throw new IOException("The PC input request timed out.");
            JSONObject reply=result.size()==0?new JSONObject():new JSONObject(new String(result.toByteArray(),StandardCharsets.UTF_8));if(status!=200)throw new IOException(reply.optString("error","Enable tablet input for this display in the PC settings, then choose Pen or Touch again."));return reply;
        }finally{if(deadline!=null)deadline.close();final HttpsURLConnection retired=opened;retirement.submit(retired==null?null:()->{try{retired.disconnect();}catch(RuntimeException ignored){}});}
    }
    private void end(InputSession session){if(session.lease.length()==0)return;try{request(session,new JSONObject().put("operation","end").put("videoLease",session.videoLease).put("lease",session.lease),true);}catch(Exception ignored){/* The server's 1.2-second lease releases a disconnected tablet. */}}
    private void stopInput(){
        InputSession session=active;active=null;if(session!=null){session.cancelled=true;try{NETWORK.execute(()->end(session));}catch(RuntimeException ignored){}}
        busy=false;queued.clear();policy.mode("view");schedule.fired();ui.removeCallbacks(pump);changing=true;view.setChecked(true);changing=false;pen.setEnabled(ready&&!closed);touch.setEnabled(ready&&!closed);message.setText("View only · PC input permission is separate");
    }
    private void failure(String detail){stopInput();message.setText(detail);}
    private static String friendly(Exception error){String value=error.getMessage();return value==null?"Input stopped. Choose Pen or Touch again.":value.length()>300?value.substring(0,300):value;}
    @Override public void close(){if(closed)return;closed=true;stopInput();}
    private static final class InputSession {final String mode,videoLease;volatile long started;volatile String lease="";volatile boolean cancelled;long sequence,lastSent;InputSession(String mode,String videoLease){this.mode=mode;this.videoLease=videoLease;}}
}
