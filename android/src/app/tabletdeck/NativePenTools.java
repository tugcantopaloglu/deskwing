package app.tabletdeck;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.Build;
import android.text.InputFilter;
import android.text.InputType;
import android.util.Base64;
import android.view.Gravity;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.OutputStream;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import org.json.JSONObject;

/** Local-only pen tools. Recognition belongs to the installed Android keyboard. */
final class NativePenTools {
    static final int MAX_TEXT=8000,MAX_PNG_BYTES=8*1024*1024,EXPORT_REQUEST=43;
    interface Result {void send(int id,JSONObject result);}
    private final Activity activity;
    private final Result result;
    private AlertDialog handwriting;
    private int handwritingId,exportId;
    private String originalText="";
    private byte[] exportBytes;
    private boolean exportSaving;
    private boolean closed;
    private final ExecutorService files=new ThreadPoolExecutor(1,1,0L,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(1),new ThreadPoolExecutor.AbortPolicy());

    NativePenTools(Activity activity,Result result){this.activity=activity;this.result=result;}

    void request(int id,String path,JSONObject body){
        try {
            if(closed||activity.isFinishing())throw new IllegalStateException("The tablet workspace is closing.");
            if(path.equals("/local/handwriting"))openHandwriting(id,body.optString("text",""));
            else if(path.equals("/local/export-image"))exportImage(id,body);
            else throw new IllegalArgumentException("This local pen request is not supported.");
        }catch(Exception ex){
            if(handwritingId==id){handwritingId=0;originalText="";AlertDialog failed=handwriting;handwriting=null;if(failed!=null)failed.dismiss();}
            error(id,ex.getMessage());
        }
    }

    private void openHandwriting(int id,String text) throws Exception {
        if(handwriting!=null)throw new IllegalStateException("Finish or cancel the open handwriting field first.");
        if(text.length()>MAX_TEXT)throw new IllegalArgumentException("Handwriting fields support up to 8,000 characters.");
        InputMethodManager input=(InputMethodManager)activity.getSystemService(Activity.INPUT_METHOD_SERVICE);
        boolean penAvailable=false;
        if(Build.VERSION.SDK_INT>=34&&input!=null)try{penAvailable=input.isStylusHandwritingAvailable();}catch(RuntimeException ignored){}
        final boolean supportsPen=penAvailable;
        LinearLayout content=new LinearLayout(activity);content.setOrientation(LinearLayout.VERTICAL);
        int padding=Math.round(20*activity.getResources().getDisplayMetrics().density);content.setPadding(padding,padding,padding,padding);
        TextView help=new TextView(activity);
        help.setText(supportsPen?"Write with your pen inside the text field. Your Android keyboard converts the strokes to text. You can also use the keyboard.":"Your current Android keyboard does not report stylus handwriting support. Type or paste here, or choose a handwriting-capable keyboard in Android settings.");
        content.addView(help,new LinearLayout.LayoutParams(-1,-2));
        EditText field=new EditText(activity);field.setContentDescription("Handwriting text");field.setHint("Write or type your note");
        field.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_FLAG_MULTI_LINE|InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        field.setFilters(new InputFilter[]{new InputFilter.LengthFilter(MAX_TEXT)});field.setMinLines(5);field.setMaxLines(12);field.setGravity(Gravity.TOP|Gravity.START);
        field.setText(text);field.setSelection(field.length());field.setShowSoftInputOnFocus(!supportsPen);
        if(Build.VERSION.SDK_INT>=34)field.setAutoHandwritingEnabled(true);
        content.addView(field,new LinearLayout.LayoutParams(-1,-2));
        originalText=text;handwritingId=id;
        handwriting=new AlertDialog.Builder(activity).setTitle("Handwriting").setView(content)
            .setPositiveButton("Use text",(dialog,which)->completeHandwriting(field.getText().toString(),false))
            .setNegativeButton("Cancel",(dialog,which)->completeHandwriting(originalText,true))
            .setNeutralButton("Use keyboard",null).create();
        handwriting.setOnCancelListener(dialog->completeHandwriting(originalText,true));
        handwriting.setOnDismissListener(dialog->{if(handwritingId!=0)completeHandwriting(originalText,true);handwriting=null;});
        handwriting.setOnShowListener(dialog->{
            field.requestFocus();
            handwriting.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(view->{field.setShowSoftInputOnFocus(true);field.requestFocus();if(input!=null)input.showSoftInput(field,InputMethodManager.SHOW_IMPLICIT);});
        });
        handwriting.show();
    }

    private void completeHandwriting(String text,boolean cancelled){
        int id=handwritingId;if(id==0)return;handwritingId=0;originalText="";
        try{result.send(id,new JSONObject().put("text",text).put("cancelled",cancelled));}catch(Exception ignored){}
    }

    void pause(){if(handwriting!=null){completeHandwriting(originalText,true);handwriting.dismiss();handwriting=null;}}

    private void exportImage(int id,JSONObject body) throws Exception {
        if(exportId!=0||exportSaving)throw new IllegalStateException("Finish or cancel the open image export first.");
        String data=body.optString("dataUrl","");String prefix="data:image/png;base64,";
        if(!data.startsWith(prefix)||data.length()>((MAX_PNG_BYTES+2)/3)*4+prefix.length())throw new IllegalArgumentException("Export a PNG image smaller than 8 MB.");
        byte[] bytes=Base64.decode(data.substring(prefix.length()),Base64.NO_WRAP);
        byte[] signature={(byte)137,80,78,71,13,10,26,10};
        if(bytes.length<33||bytes.length>MAX_PNG_BYTES)throw new IllegalArgumentException("The PNG export is empty or too large.");
        for(int i=0;i<signature.length;i++)if(bytes[i]!=signature[i])throw new IllegalArgumentException("Only PNG images can be exported.");
        BitmapFactory.Options dimensions=new BitmapFactory.Options();dimensions.inJustDecodeBounds=true;BitmapFactory.decodeByteArray(bytes,0,bytes.length,dimensions);
        if(dimensions.outWidth<1||dimensions.outHeight<1||dimensions.outWidth>4096||dimensions.outHeight>4096||!"image/png".equals(dimensions.outMimeType))throw new IllegalArgumentException("Export a valid PNG up to 4096 × 4096 pixels.");
        String name=body.optString("name","DeskWing-sketch.png");
        if(!name.matches("[A-Za-z0-9 _-]{1,80}\\.png"))name="DeskWing-sketch.png";
        Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("image/png").putExtra(Intent.EXTRA_TITLE,name);
        exportId=id;exportBytes=bytes;
        try{activity.startActivityForResult(intent,EXPORT_REQUEST);}catch(Exception ex){exportId=0;exportBytes=null;throw new IllegalStateException("No file picker is available to save the drawing.",ex);}
    }

    boolean activityResult(int request,int status,Intent data){
        if(request!=EXPORT_REQUEST)return false;
        int id=exportId;byte[] bytes=exportBytes;exportId=0;exportBytes=null;if(id==0)return true;
        try {
            if(status!=Activity.RESULT_OK||data==null||data.getData()==null){result.send(id,new JSONObject().put("saved",false).put("cancelled",true));return true;}
            Uri uri=data.getData();if(!"content".equals(uri.getScheme())||bytes==null)throw new IllegalStateException("Choose a writable document destination.");
            exportSaving=true;
            files.execute(()->{
                boolean saved=false;
                try(OutputStream output=activity.getContentResolver().openOutputStream(uri,"w")){if(output==null)throw new IllegalStateException("The selected destination could not be opened.");output.write(bytes);output.flush();saved=true;}catch(Exception ignored){}
                final boolean complete=saved;
                activity.runOnUiThread(()->{exportSaving=false;if(closed)return;try{if(complete)result.send(id,new JSONObject().put("saved",true).put("cancelled",false));else error(id,"The drawing could not be saved. Choose another destination and try again.");}catch(Exception ignored){}});
            });
        }catch(Exception ex){exportSaving=false;error(id,"The drawing could not be saved. Choose another destination and try again.");}
        return true;
    }

    private void error(int id,String message){try{result.send(id,new JSONObject().put("error",message==null?"The pen tool could not open. Try again.":message));}catch(Exception ignored){}}
    void close(){pause();closed=true;exportId=0;exportBytes=null;files.shutdownNow();}
}
