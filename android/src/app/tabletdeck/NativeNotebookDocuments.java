package app.tabletdeck;

import android.app.Activity;
import android.content.Intent;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.pdf.PdfRenderer;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashSet;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;
import java.util.function.LongSupplier;
import org.json.JSONObject;

/** User-selected local PDFs only. Raster bytes and exports cross the bridge in bounded chunks. */
final class NativeNotebookDocuments implements AutoCloseable {
    static final int IMPORT_REQUEST=46,EXPORT_REQUEST=47,MAX_IMPORT_BYTES=20*1024*1024,MAX_PAGES=20,MAX_IMAGE_BYTES=1024*1024;
    interface Result {void send(int id,JSONObject value);}
    private final Activity activity;
    private final Result result;
    private final LongSupplier document;
    private final BooleanSupplier foreground;
    private final Object gate=new Object();
    private final ThreadPoolExecutor files=new ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,new ArrayBlockingQueue<Runnable>(1),action->{Thread thread=new Thread(action,"DeskWing notebook files");thread.setDaemon(true);return thread;},new ThreadPoolExecutor.AbortPolicy());
    private final NetworkCloseQueue cleanup=new NetworkCloseQueue();
    private final AtomicBoolean busy=new AtomicBoolean();
    private long generation;
    private boolean closed;
    private Pending picker;
    private Session imported,exported;

    NativeNotebookDocuments(Activity activity,Result result,LongSupplier document,BooleanSupplier foreground){this.activity=activity;this.result=result;this.document=document;this.foreground=foreground;}
    static boolean allowedPath(String path){return path!=null&&new HashSet<String>(Arrays.asList("/local/notebook/import-pdf","/local/notebook/render-pdf","/local/notebook/read-pdf-image","/local/notebook/close-pdf","/local/notebook/export-pdf/start","/local/notebook/export-pdf/write","/local/notebook/export-pdf/finish","/local/notebook/export-pdf/cancel")).contains(path);}
    boolean isOpen(){synchronized(gate){return picker!=null;}}
    void request(int requestId,String path,JSONObject body){
        try{
            synchronized(gate){if(closed||activity.isFinishing()||!foreground.getAsBoolean())throw new IllegalStateException("Open DeskWing before using notebook files.");}
            if(path.equals("/local/notebook/import-pdf")){fields(body);openImport(requestId);return;}
            if(path.equals("/local/notebook/export-pdf/start")){fields(body,"name","size");openExport(requestId,text(body,"name",100),number(body,"size",5,NotebookPdfTransfer.MAX_PDF_BYTES));return;}
            final Session session;
            if(path.equals("/local/notebook/read-pdf-image")){
                fields(body,"id","offset","length");String id=text(body,"id",40);int offset=number(body,"offset",0,MAX_IMAGE_BYTES),length=number(body,"length",1,NotebookPdfTransfer.CHUNK_BYTES);
                synchronized(gate){session=requireImportImage(id);byte[] image=session.image;if(offset>image.length)throw new IllegalArgumentException("The image offset is invalid.");int count=Math.min(length,image.length-offset);send(requestId,new JSONObject().put("dataBase64",Base64.getEncoder().encodeToString(Arrays.copyOfRange(image,offset,offset+count))).put("offset",offset).put("eof",offset+count==image.length));}return;
            }
            if(path.equals("/local/notebook/render-pdf")){fields(body,"id","page");synchronized(gate){session=requireImport(text(body,"id",40));session.image=null;session.imageId="";}int page=number(body,"page",0,MAX_PAGES-1);execute(requestId,session,()->render(session,page));return;}
            if(path.equals("/local/notebook/close-pdf")){fields(body,"id");synchronized(gate){session=requireImport(text(body,"id",40));imported=null;}retire(session);send(requestId,new JSONObject().put("closed",true));return;}
            if(path.equals("/local/notebook/export-pdf/write")){
                fields(body,"id","offset","dataBase64");synchronized(gate){session=requireExport(text(body,"id",40));}int offset=number(body,"offset",0,NotebookPdfTransfer.MAX_PDF_BYTES);String data=text(body,"dataBase64",((NotebookPdfTransfer.CHUNK_BYTES+2)/3)*4);
                execute(requestId,session,()->{byte[] bytes=session.transfer.accept(offset,data);session.output.write(bytes);guard(session);return new JSONObject().put("written",session.transfer.received());});return;
            }
            if(path.equals("/local/notebook/export-pdf/finish")){
                fields(body,"id");synchronized(gate){session=requireExport(text(body,"id",40));}
                execute(requestId,session,()->{session.transfer.finish();session.output.flush();session.output.close();session.output=null;guard(session);return new JSONObject();},false,true);return;
            }
            if(path.equals("/local/notebook/export-pdf/cancel")){fields(body,"id");synchronized(gate){session=requireExport(text(body,"id",40));exported=null;}retire(session);send(requestId,new JSONObject().put("cancelled",true));return;}
            throw new IllegalArgumentException("This notebook file request is unsupported.");
        }catch(Exception ex){error(requestId,message(ex));}
    }
    private void openImport(int id) throws Exception {
        Session session;
        synchronized(gate){if(picker!=null||imported!=null||busy.get())throw new IllegalStateException("Finish the open PDF import first.");session=new Session(document.getAsLong(),generation,cleanup.reserve());picker=new Pending(id,IMPORT_REQUEST,session);}
        Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/pdf");
        try{activity.startActivityForResult(intent,IMPORT_REQUEST);}catch(Exception ex){cancelPicker(session);throw new IllegalStateException("No PDF file picker is available.");}
    }
    private void openExport(int id,String name,int size) throws Exception {
        Session session;
        synchronized(gate){if(picker!=null||exported!=null||busy.get())throw new IllegalStateException("Finish the open PDF export first.");session=new Session(document.getAsLong(),generation,cleanup.reserve());session.transfer=new NotebookPdfTransfer(size);session.name=safeName(name);exported=session;}
        execute(id,session,()->{synchronized(session.lock){guard(session);session.temporary=File.createTempFile("deskwing-notebook-export-",".pdf",activity.getCacheDir());session.output=new FileOutputStream(session.temporary);}return new JSONObject().put("id",session.id);});
    }
    private void chooseExportDestination(int id,Session session) throws Exception {
        synchronized(gate){guard(session);if(picker!=null)throw new IllegalStateException("Close the open document picker first.");picker=new Pending(id,EXPORT_REQUEST,session);exported=null;}
        Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/pdf").putExtra(Intent.EXTRA_TITLE,session.name);
        try{activity.startActivityForResult(intent,EXPORT_REQUEST);}catch(Exception ex){cancelPicker(session);throw new IllegalStateException("No PDF export destination picker is available.");}
    }
    boolean activityResult(int requestCode,int status,Intent data){
        if(requestCode!=IMPORT_REQUEST&&requestCode!=EXPORT_REQUEST)return false;
        final Pending pending;
        synchronized(gate){pending=picker;if(pending==null||pending.requestCode!=requestCode)return true;picker=null;}
        Session session=pending.session;
        try{
            guard(session);
            if(status!=Activity.RESULT_OK||data==null||data.getData()==null){retire(session);send(pending.requestId,new JSONObject().put("cancelled",true));return true;}
            Uri uri=data.getData();if(!"content".equals(uri.getScheme()))throw new IllegalArgumentException("Choose a local document provider file.");
            synchronized(gate){if(requestCode==IMPORT_REQUEST)imported=session;else exported=session;}
            execute(pending.requestId,session,()->{
                if(requestCode==IMPORT_REQUEST)return importPdf(session,uri);
                session.transfer.finish();
                try(InputStream input=new java.io.FileInputStream(session.temporary);OutputStream opened=activity.getContentResolver().openOutputStream(uri,"w")){
                    if(opened==null)throw new IllegalStateException("The PDF destination could not be opened.");
                    synchronized(session.lock){guard(session);session.input=input;session.output=opened;}
                    byte[] buffer=new byte[8192];int count;long written=0;
                    while((count=input.read(buffer))!=-1){guard(session);opened.write(buffer,0,count);written+=count;if(written>NotebookPdfTransfer.MAX_PDF_BYTES)throw new IllegalStateException("The PDF export exceeds its limit.");}
                    if(written!=session.transfer.received())throw new IllegalStateException("The PDF export is incomplete.");opened.flush();
                }finally{session.input=null;session.output=null;}
                guard(session);return new JSONObject().put("saved",true).put("cancelled",false);
            },requestCode==EXPORT_REQUEST);
        }catch(Exception ex){retire(session);error(pending.requestId,message(ex));}
        return true;
    }
    private JSONObject importPdf(Session session,Uri uri) throws Exception {
        String name="Imported PDF";
        try(Cursor cursor=activity.getContentResolver().query(uri,new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE},null,null,null)){
            if(cursor!=null&&cursor.moveToFirst()){
                int sizeColumn=cursor.getColumnIndex(OpenableColumns.SIZE);if(sizeColumn>=0&&!cursor.isNull(sizeColumn)&&cursor.getLong(sizeColumn)>MAX_IMPORT_BYTES)throw new IllegalArgumentException("Import a PDF no larger than 20 MB.");
                int nameColumn=cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);if(nameColumn>=0&&!cursor.isNull(nameColumn))name=displayName(cursor.getString(nameColumn));
            }
        }
        final File owned;synchronized(session.lock){guard(session);owned=File.createTempFile("deskwing-notebook-",".pdf",activity.getCacheDir());session.temporary=owned;}
        try(InputStream input=activity.getContentResolver().openInputStream(uri)){
            if(input==null)throw new IllegalArgumentException("The PDF could not be read.");
            final OutputStream copy;
            synchronized(session.lock){guard(session);session.input=input;copy=new FileOutputStream(owned);session.output=copy;}
            try(OutputStream output=copy){
                byte[] buffer=new byte[8192],prefix=new byte[5];int total=0,count;
                while((count=input.read(buffer))!=-1){guard(session);if(count>MAX_IMPORT_BYTES-total)throw new IllegalArgumentException("Import a PDF no larger than 20 MB.");for(int index=0;index<count&&total+index<prefix.length;index++)prefix[total+index]=buffer[index];output.write(buffer,0,count);total+=count;}
                if(total<5||prefix[0]!='%'||prefix[1]!='P'||prefix[2]!='D'||prefix[3]!='F'||prefix[4]!='-')throw new IllegalArgumentException("Choose a valid PDF file.");
            }finally{session.output=null;}
        }finally{session.input=null;}
        guard(session);
        synchronized(session.lock){guard(session);session.descriptor=ParcelFileDescriptor.open(owned,ParcelFileDescriptor.MODE_READ_ONLY);session.pdf=new PdfRenderer(session.descriptor);if(session.pdf.getPageCount()<1||session.pdf.getPageCount()>MAX_PAGES)throw new IllegalArgumentException("Import a PDF with between 1 and 20 pages.");}
        guard(session);return new JSONObject().put("id",session.id).put("name",name).put("pageCount",session.pdf.getPageCount()).put("nativeRaster",true);
    }
    private JSONObject render(Session session,int number) throws Exception {
        final byte[] png;final int width,height;
        synchronized(session.lock){
            guard(session);if(session.pdf==null||number>=session.pdf.getPageCount())throw new IllegalArgumentException("Choose an existing PDF page.");
            try(PdfRenderer.Page page=session.pdf.openPage(number)){
                if(page.getWidth()<1||page.getHeight()<1)throw new IllegalArgumentException("The PDF page dimensions are invalid.");
                int side=2048;byte[] rendered=null;int renderedWidth=0,renderedHeight=0;
                while(side>=256){
                    int[] dimensions=NotebookRasterPolicy.size(page.getWidth(),page.getHeight(),side);int w=dimensions[0],h=dimensions[1];
                    Bitmap bitmap=Bitmap.createBitmap(w,h,Bitmap.Config.ARGB_8888);
                    try{bitmap.eraseColor(Color.WHITE);page.render(bitmap,null,null,PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY);guard(session);ByteArrayOutputStream bytes=new ByteArrayOutputStream();if(!bitmap.compress(Bitmap.CompressFormat.PNG,100,bytes))throw new IllegalStateException("The PDF page could not be rendered.");if(bytes.size()<=MAX_IMAGE_BYTES){rendered=bytes.toByteArray();renderedWidth=w;renderedHeight=h;break;}}finally{bitmap.recycle();}
                    side=side*3/4;
                }
                if(rendered==null)throw new IllegalArgumentException("This PDF page exceeds the image limit. Try a simpler PDF.");png=rendered;width=renderedWidth;height=renderedHeight;
            }
        }
        synchronized(gate){guard(session);session.image=png;session.imageId=UUID.randomUUID().toString().replace("-","");return new JSONObject().put("imageId",session.imageId).put("width",width).put("height",height).put("sourcePage",number+1).put("size",png.length).put("mime","image/png");}
    }
    private interface Work {JSONObject run() throws Exception;}
    private void execute(int id,Session session,Work work){execute(id,session,work,false);}
    private void execute(int id,Session session,Work work,boolean terminal){execute(id,session,work,terminal,false);}
    private void execute(int id,Session session,Work work,boolean terminal,boolean destinationPicker){
        if(!busy.compareAndSet(false,true)){error(id,"A notebook file operation is still running. Try again shortly.");return;}
        try{files.execute(()->{try{guard(session);JSONObject response=work.run();guard(session);activity.runOnUiThread(()->{if(valid(session)){try{if(destinationPicker){chooseExportDestination(id,session);return;}if(terminal){synchronized(gate){if(exported==session)exported=null;}}send(id,response);if(terminal)retire(session);}catch(Exception ex){retire(session);error(id,message(ex));}}});}catch(Exception ex){synchronized(gate){if(imported==session)imported=null;if(exported==session)exported=null;}retire(session);activity.runOnUiThread(()->error(id,message(ex)));}finally{if(session.retired.get())session.dispose();busy.set(false);}});}catch(RuntimeException ex){busy.set(false);retire(session);error(id,"The notebook file worker is unavailable.");}
    }
    void pause(){synchronized(gate){if(picker==null)generation++;Session oldImport=imported,oldExport=exported;imported=null;exported=null;if(oldImport!=null)retire(oldImport);if(oldExport!=null)retire(oldExport);}}
    void reset(String reason){Pending old; synchronized(gate){generation++;old=picker;picker=null;Session a=imported,b=exported;imported=null;exported=null;if(a!=null)retire(a);if(b!=null)retire(b);}if(old!=null){retire(old.session);error(old.requestId,"The notebook file operation was cancelled because the workspace changed.");}}
    @Override public void close(){synchronized(gate){if(closed)return;closed=true;}reset("closed");files.shutdownNow();cleanup.close();}
    private void cancelPicker(Session session){synchronized(gate){if(picker!=null&&picker.session==session)picker=null;}retire(session);}
    private Session requireImport(String id){Session value=imported;if(value==null||!value.id.equals(id)||!valid(value))throw new IllegalArgumentException("The PDF import session has ended. Choose the file again.");return value;}
    private Session requireImportImage(String id){Session value=imported;if(value==null||!value.imageId.equals(id)||value.image==null||!valid(value))throw new IllegalArgumentException("The PDF image is no longer available.");return value;}
    private Session requireExport(String id){Session value=exported;if(value==null||!value.id.equals(id)||!valid(value))throw new IllegalArgumentException("The PDF export session has ended. Choose a destination again.");return value;}
    private boolean valid(Session session){synchronized(gate){return !closed&&!session.retired.get()&&session.document==document.getAsLong()&&session.generation==generation;}}
    private void guard(Session session){if(!valid(session))throw new IllegalStateException("The notebook file operation was cancelled because the workspace changed.");}
    private void retire(Session session){if(session!=null&&session.retired.compareAndSet(false,true))session.cleanup.submit(session::dispose);}
    private void send(int id,JSONObject value){result.send(id,value);}
    private void error(int id,String message){try{send(id,new JSONObject().put("error",message));}catch(Exception ignored){}}
    private static String message(Exception ex){return ex instanceof SecurityException?"Permission to the selected document was denied. Choose it again.":ex.getMessage()==null?"The notebook file operation failed. Try again.":ex.getMessage();}
    private static void fields(JSONObject body,String... names){HashSet<String> expected=new HashSet<>(Arrays.asList(names));if(body==null||body.length()!=expected.size())throw new IllegalArgumentException("The notebook request has unsupported fields.");for(String name:names)if(!body.has(name))throw new IllegalArgumentException("The notebook request is missing a field.");}
    private static String text(JSONObject body,String name,int max) throws Exception {Object value=body.get(name);if(!(value instanceof String)||((String)value).length()>max)throw new IllegalArgumentException("The notebook "+name+" is invalid.");return (String)value;}
    private static int number(JSONObject body,String name,int min,int max) throws Exception {Object value=body.get(name);if(!(value instanceof Integer)&&!(value instanceof Long))throw new IllegalArgumentException("The notebook "+name+" must be a whole number.");long number=((Number)value).longValue();if(number<min||number>max)throw new IllegalArgumentException("The notebook "+name+" is outside its limit.");return (int)number;}
    private static String safeName(String name){return name.matches("[A-Za-z0-9 _-]{1,80}\\.pdf")?name:"DeskWing-notes.pdf";}
    private static String displayName(String name){if(name==null)return "Imported PDF";String value=name.replaceAll("[\\p{Cntrl}/\\\\]", " ").trim();return value.length()>120?value.substring(0,120):value.length()==0?"Imported PDF":value;}
    private static final class Pending {final int requestId,requestCode;final Session session;Pending(int id,int code,Session session){requestId=id;requestCode=code;this.session=session;}}
    private static final class Session {
        final String id=UUID.randomUUID().toString().replace("-","");final long document,generation;final NetworkCloseQueue.Reservation cleanup;final AtomicBoolean retired=new AtomicBoolean();final Object lock=new Object();
        volatile InputStream input;volatile OutputStream output;volatile File temporary;volatile ParcelFileDescriptor descriptor;volatile PdfRenderer pdf;volatile byte[] image;volatile String imageId="",name="DeskWing-notes.pdf";NotebookPdfTransfer transfer;
        Session(long document,long generation,NetworkCloseQueue.Reservation cleanup){this.document=document;this.generation=generation;this.cleanup=cleanup;}
        void dispose(){final InputStream in;final OutputStream out;final PdfRenderer renderer;final ParcelFileDescriptor file;final File owned;synchronized(lock){in=input;input=null;out=output;output=null;renderer=pdf;pdf=null;file=descriptor;descriptor=null;owned=temporary;temporary=null;image=null;imageId="";}try{if(in!=null)in.close();}catch(Exception ignored){}try{if(out!=null)out.close();}catch(Exception ignored){}try{if(renderer!=null)renderer.close();}catch(Exception ignored){}try{if(file!=null)file.close();}catch(Exception ignored){}if(owned!=null)owned.delete();}
    }
}
