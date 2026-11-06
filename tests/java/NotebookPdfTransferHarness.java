package app.tabletdeck;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;

public final class NotebookPdfTransferHarness {
    private static int passed;
    private static void check(boolean value,String message){if(!value)throw new AssertionError(message);passed++;System.out.println("PASS "+message);}
    private static void reject(Runnable work,String message){try{work.run();throw new AssertionError("Accepted "+message);}catch(IllegalArgumentException expected){check(true,message);}}
    public static void main(String[] args){
        byte[] content=new byte[17000];Arrays.fill(content,(byte)'x');System.arraycopy("%PDF-1.7\n".getBytes(StandardCharsets.US_ASCII),0,content,0,9);
        NotebookPdfTransfer transfer=new NotebookPdfTransfer(content.length);
        int at=0;while(at<content.length){byte[] expected=Arrays.copyOfRange(content,at,Math.min(content.length,at+8192));byte[] actual=transfer.accept(at,Base64.getEncoder().encodeToString(expected));check(Arrays.equals(actual,expected),"Chunk bytes remain exact at "+at);at+=actual.length;}
        transfer.finish();check(transfer.received()==content.length,"A complete sequential PDF is accepted");
        reject(()->new NotebookPdfTransfer(4),"Empty or undersized exports are rejected");
        reject(()->new NotebookPdfTransfer(NotebookPdfTransfer.MAX_PDF_BYTES+1),"Exports above 32 MiB are rejected before writing");
        NotebookPdfTransfer ordered=new NotebookPdfTransfer(10);
        reject(()->ordered.accept(1,"JVBERi0="),"A skipped first chunk is rejected");
        check(ordered.received()==0,"Rejected offsets do not advance bytes");
        ordered.accept(0,"JVBERi0=");
        reject(()->ordered.accept(0,"eHh4eHg="),"A replayed chunk cannot overwrite prior bytes");
        reject(ordered::finish,"A truncated PDF cannot finish");
        ordered.accept(5,"eHh4eHg=");ordered.finish();check(ordered.received()==10,"A valid continuation completes the exact size");
        reject(()->ordered.accept(10,"eA=="),"Bytes beyond the declared length are rejected");
        NotebookPdfTransfer invalid=new NotebookPdfTransfer(5);invalid.accept(0,"aGVsbG8=");reject(invalid::finish,"Non-PDF content cannot be committed");
        NotebookPdfTransfer bounded=new NotebookPdfTransfer(20000);
        reject(()->bounded.accept(0,Base64.getEncoder().encodeToString(new byte[8193])),"Each decoded bridge chunk is bounded at 8 KiB");
        reject(()->bounded.accept(0,"!!!!"),"Malformed base64 cannot reach the destination");
        reject(()->bounded.accept(0,""),"Zero-progress chunks are rejected");
        check(bounded.received()==0,"Rejected encodings leave export offset intact");
        int[] square=NotebookRasterPolicy.size(4096,4096,2048);check(square[0]==2000&&square[1]==2000,"Square PDF stays within the exact four-million-pixel budget");
        int[] landscape=NotebookRasterPolicy.size(4000,2000,2048);check(landscape[0]==2048&&landscape[1]==1024,"Landscape geometry keeps its aspect and side bounds");
        int[] small=NotebookRasterPolicy.size(600,800,2048);check(small[0]==1536&&small[1]==2048,"Vector page units render at a readable bounded pixel resolution");
        int[] extreme=NotebookRasterPolicy.size(Integer.MAX_VALUE,1,2048);check(extreme[0]==2048&&extreme[1]==1,"Extreme legal aspect ratios cannot overflow or allocate huge bitmaps");
        reject(()->NotebookRasterPolicy.size(0,10,2048),"Zero-width pages are rejected");
        reject(()->NotebookRasterPolicy.size(10,10,2049),"An oversized raster request is rejected");
        System.out.println(passed+" notebook PDF transfer checks passed.");
    }
}
