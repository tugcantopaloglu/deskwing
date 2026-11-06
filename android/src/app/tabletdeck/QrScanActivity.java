package app.tabletdeck;

import android.app.Activity;
import android.Manifest;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
import java.util.Collections;
import java.util.Comparator;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/** Camera is opened only by the user's Scan pairing QR action. Frames never leave this Activity. */
public final class QrScanActivity extends Activity implements SurfaceHolder.Callback {
    private SurfaceView preview;private TextView status;private Camera camera;
    private volatile boolean active;private boolean surfaceReady;
    private long lastFrame;private final AtomicBoolean decoding=new AtomicBoolean();
    private final ExecutorService decoder=Executors.newSingleThreadExecutor();
    @Override public void onCreate(Bundle state){super.onCreate(state);getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON|WindowManager.LayoutParams.FLAG_SECURE);
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setPadding(20,20,20,20);root.setBackgroundColor(Color.rgb(16,19,24));
        status=new TextView(this);status.setText("Point the camera at the pairing QR in PC setup. Scanning fills the form; you still choose Pair and connect.");status.setTextColor(Color.WHITE);status.setTextSize(18);root.addView(status);
        preview=new SurfaceView(this);root.addView(preview,new LinearLayout.LayoutParams(-1,0,1));preview.getHolder().addCallback(this);
        Button cancel=new Button(this);cancel.setText("Cancel scan");cancel.setOnClickListener(v->finish());root.addView(cancel);setContentView(root);
        if(checkSelfPermission(Manifest.permission.CAMERA)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.CAMERA},41);
    }
    @Override protected void onResume(){super.onResume();active=true;openCamera();}
    @Override protected void onPause(){active=false;closeCamera();super.onPause();}
    @Override protected void onDestroy(){decoder.shutdownNow();super.onDestroy();}
    @Override public void onRequestPermissionsResult(int code,String[] permissions,int[] results){super.onRequestPermissionsResult(code,permissions,results);if(code==41){if(results.length>0&&results[0]==PackageManager.PERMISSION_GRANTED)openCamera();else{setResult(RESULT_CANCELED,new Intent().putExtra("error","Camera permission was declined. Use manual pairing instead."));finish();}}}
    public void surfaceCreated(SurfaceHolder holder){surfaceReady=true;openCamera();}
    public void surfaceChanged(SurfaceHolder holder,int format,int width,int height){}
    public void surfaceDestroyed(SurfaceHolder holder){surfaceReady=false;closeCamera();}
    private void openCamera(){
        if(!active||!surfaceReady||camera!=null||checkSelfPermission(Manifest.permission.CAMERA)!=PackageManager.PERMISSION_GRANTED)return;
        try{int id=0;Camera.CameraInfo info=new Camera.CameraInfo();for(int i=0;i<Camera.getNumberOfCameras();i++){Camera.getCameraInfo(i,info);if(info.facing==Camera.CameraInfo.CAMERA_FACING_BACK){id=i;break;}}
            camera=Camera.open(id);Camera.getCameraInfo(id,info);Camera.Parameters p=camera.getParameters();
            Camera.Size size=Collections.min(p.getSupportedPreviewSizes(),Comparator.comparingInt(s->Math.abs(s.width*s.height-1280*720)));p.setPreviewSize(size.width,size.height);
            if(p.getSupportedFocusModes()!=null&&p.getSupportedFocusModes().contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE))p.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            camera.setParameters(p);int rotation=getWindowManager().getDefaultDisplay().getRotation()*90;camera.setDisplayOrientation((info.orientation-rotation+360)%360);camera.setPreviewDisplay(preview.getHolder());
            camera.setPreviewCallback((bytes,source)->{if(!active||System.currentTimeMillis()-lastFrame<300||!decoding.compareAndSet(false,true))return;lastFrame=System.currentTimeMillis();final byte[] copy=bytes.clone();decoder.execute(()->{try{PlanarYUVLuminanceSource pixels=new PlanarYUVLuminanceSource(copy,size.width,size.height,0,0,size.width,size.height,false);MultiFormatReader reader=new MultiFormatReader();reader.setHints(Collections.singletonMap(DecodeHintType.POSSIBLE_FORMATS,Collections.singletonList(BarcodeFormat.QR_CODE)));Result result=reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(pixels)));String text=result.getText();PairingPayload.parse(text);runOnUiThread(()->{if(active&&!isFinishing()){setResult(RESULT_OK,new Intent().putExtra("pairing",text));finish();}});}catch(NotFoundException ignored){}catch(Exception ex){runOnUiThread(()->{if(active)status.setText("Scan a current DeskWing pairing QR, or Cancel and pair manually.");});}finally{decoding.set(false);}});});camera.startPreview();
        }catch(Exception ex){closeCamera();status.setText("Camera unavailable. Cancel and use manual pairing, or try scanning again.");}
    }
    private void closeCamera(){if(camera!=null){camera.setPreviewCallback(null);camera.stopPreview();camera.release();camera=null;}}
}
