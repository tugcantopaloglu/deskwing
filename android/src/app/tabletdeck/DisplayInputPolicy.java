package app.tabletdeck;

import java.util.ArrayList;

/** Android-free admission, geometry and bounded sample ordering for the native display surface. */
final class DisplayInputPolicy {
    static final int MAX_BATCH=32;
    static final class Sample {
        final String phase;final double x,y,pressure,tiltX,tiltY;final boolean barrel,eraser;
        Sample(String phase,double x,double y,double pressure,double tiltX,double tiltY,boolean barrel,boolean eraser){this.phase=phase;this.x=x;this.y=y;this.pressure=pressure;this.tiltX=tiltX;this.tiltY=tiltY;this.barrel=barrel;this.eraser=eraser;}
    }
    private String mode="view";
    private int pointer=-1;
    private boolean blocked,inRange;
    private double lastX,lastY;
    void mode(String value){if(!value.equals("view")&&!value.equals("pen")&&!value.equals("touch"))throw new IllegalArgumentException("Choose View, Pen or Touch.");mode=value;pointer=-1;blocked=false;inRange=false;}
    boolean admits(String tool){return mode.equals("pen")&&(tool.equals("pen")||tool.equals("eraser"))||mode.equals("touch")&&tool.equals("finger");}
    void remaining(int eligiblePointers){if(eligiblePointers==0){blocked=false;pointer=-1;}}
    Sample event(String phase,int id,String tool,int eligiblePointers,double x,double y,double width,double height,double pressure,double tiltX,double tiltY,boolean barrel){
        if(!admits(tool))return null;
        if(eligiblePointers>1){blocked=true;return cancel();}
        if(blocked)return null;
        if(!finite(x,y,width,height,pressure,tiltX,tiltY)||width<1||height<1)throw new IllegalArgumentException("The display input area is unavailable.");
        if(x<0||y<0||x>=width||y>=height)return pointer==id?cancel():null;
        if(phase.equals("down")){if(pointer!=-1)return null;pointer=id;}
        else if(phase.equals("move")||phase.equals("up")){if(pointer!=id)return null;}
        else if(phase.equals("hover")){if(pointer!=-1||!mode.equals("pen"))return null;}
        else if(phase.equals("leave"))return cancel();
        else throw new IllegalArgumentException("Unsupported display input phase.");
        lastX=clamp(x/Math.max(1,width-1),0,1);lastY=clamp(y/Math.max(1,height-1),0,1);inRange=true;
        boolean contact=phase.equals("down")||phase.equals("move"),touch=mode.equals("touch");
        Sample sample=new Sample(phase,lastX,lastY,contact?(touch?0.5:clamp(pressure,0,1)):0,touch?0:clamp(tiltX,-90,90),touch?0:clamp(tiltY,-90,90),!touch&&barrel,!touch&&tool.equals("eraser"));
        if(phase.equals("up"))pointer=-1;
        return sample;
    }
    Sample cancel(){pointer=-1;if(!inRange)return null;inRange=false;return new Sample("leave",lastX,lastY,0,0,0,false,false);}
    private static boolean finite(double... values){for(double value:values)if(!Double.isFinite(value))return false;return true;}
    private static double clamp(double value,double low,double high){return Math.max(low,Math.min(high,value));}
    static final class Samples {
        private final ArrayList<Sample> items=new ArrayList<>();
        void add(Sample sample){if(sample==null)return;if(items.size()>=MAX_BATCH)throw new IllegalStateException("Display input could not keep up. Choose Pen or Touch again.");items.add(sample);}
        Sample[] drain(){Sample[] result=items.toArray(new Sample[0]);items.clear();return result;}
        int size(){return items.size();}
        void clear(){items.clear();}
    }
    static final class FlushSchedule {
        private long due=Long.MAX_VALUE;
        boolean schedule(long now,long delay){long requested=now+delay;if(requested>=due)return false;due=requested;return true;}
        long delay(long now){return Math.max(0,due-now);}
        void fired(){due=Long.MAX_VALUE;}
    }
}
