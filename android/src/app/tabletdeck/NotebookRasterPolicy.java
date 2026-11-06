package app.tabletdeck;

/** Pixel limits shared by native PDF rasterization and its platform-free regression harness. */
final class NotebookRasterPolicy {
    static final int MAX_PIXELS=4_000_000,MAX_SIDE=2048;
    private NotebookRasterPolicy(){}
    static int[] size(int width,int height,int side){
        if(width<1||height<1||side<1||side>MAX_SIDE)throw new IllegalArgumentException("The PDF page dimensions are invalid.");
        // PDF dimensions are vector page units, so render small pages at a readable pixel
        // resolution too; the side and area budgets still cap every bitmap allocation.
        double scale=Math.min((double)side/Math.max(width,height),Math.sqrt((double)MAX_PIXELS/((double)width*height)));
        int w=Math.max(1,(int)Math.floor(width*scale)),h=Math.max(1,(int)Math.floor(height*scale));
        if(w>MAX_SIDE||h>MAX_SIDE||(long)w*h>MAX_PIXELS)throw new IllegalArgumentException("The PDF page exceeds the pixel limit.");
        return new int[]{w,h};
    }
}
