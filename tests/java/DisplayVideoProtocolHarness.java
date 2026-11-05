package app.tabletdeck;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.Arrays;
import java.util.Base64;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/** No Android classes, network, decoder, or live display is used by this protocol fixture. */
public final class DisplayVideoProtocolHarness {
    private static int passed;
    private interface Checked {void run() throws Exception;}
    private static void check(boolean result,String label){if(!result)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    private static void rejects(Checked action,Class<? extends Exception> expected,String label) throws Exception {
        try{action.run();throw new AssertionError(label+" was accepted");}
        catch(Exception error){if(!expected.isInstance(error))throw new AssertionError(label+" threw "+error.getClass().getSimpleName(),error);passed++;System.out.println("PASS "+label);}
    }
    private static DataInputStream input(byte[] data){return new DataInputStream(new ByteArrayInputStream(data));}
    private static byte[] header(int magic,int version,int size,int width,int height,int rate,int denominator,int limit,int flags) throws IOException {
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();DataOutputStream out=new DataOutputStream(bytes);
        out.writeInt(magic);out.writeShort(version);out.writeShort(size);out.writeInt(width);out.writeInt(height);out.writeInt(rate);out.writeInt(denominator);out.writeInt(limit);out.writeInt(flags);return bytes.toByteArray();
    }
    private static byte[] header(int width,int height,int rate) throws IOException {return header(0x44575631,1,32,width,height,rate,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0);}
    private static byte[] packet(int length,int flags,long pts,byte[] payload) throws IOException {
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();DataOutputStream out=new DataOutputStream(bytes);
        out.writeInt(length);out.writeInt(flags);out.writeLong(pts);out.write(payload);return bytes.toByteArray();
    }
    private static byte[] join(byte[]... pieces){int length=0;for(byte[] piece:pieces)length+=piece.length;byte[] result=new byte[length];int at=0;for(byte[] piece:pieces){System.arraycopy(piece,0,result,at,piece.length);at+=piece.length;}return result;}
    private static final byte[] SPS={0,0,0,1,0x67,0x42,0x01,0x1e};
    private static final byte[] PPS={0,0,1,0x68,0x09,0x03};
    private static final byte[] IDR={0,0,0,1,0x65,0x11,0x22};
    private static final class Writer {
        private final ByteArrayOutputStream bytes=new ByteArrayOutputStream();private int pending,count;
        void bits(long value,int width){for(int index=width-1;index>=0;index--){pending=(pending<<1)|(int)((value>>>index)&1);if(++count==8){bytes.write(pending);pending=count=0;}}}
        void ue(long value){long code=value+1;int width=64-Long.numberOfLeadingZeros(code);bits(0,width-1);bits(code,width);}
        void se(int value){ue(value<=0?-2L*value:2L*value-1);}
        byte[] nal(int type,boolean trailing){
            if(trailing)bits(1,1);if(count>0)bits(0,8-count);
            ByteArrayOutputStream escaped=new ByteArrayOutputStream();escaped.write(0);escaped.write(0);escaped.write(0);escaped.write(1);escaped.write(0x60|type);
            int zeros=0;for(byte value:bytes.toByteArray()){int octet=value&255;if(zeros>=2&&octet<=3){escaped.write(3);zeros=0;}escaped.write(octet);zeros=octet==0?zeros+1:0;}return escaped.toByteArray();
        }
    }
    private static final class Sps {
        int profile=66,constraints=0,level=40,id=0,chroma=1,depth=0,scaling=0,frameLog=0,order=0,orderLog=0,cycle=0,references=1,widthMb=80,heightMb=50,progressive=1,left=0,right=0,top=0,bottom=0,buffer=4,reorder=0,hrdCount=0;
        boolean vui=false,hrd=false,trailing=true;
        Sps(int width,int height){widthMb=(width+15)/16;heightMb=(height+15)/16;bottom=(heightMb*16-height)/2;}
        byte[] data(){
            Writer out=new Writer();out.bits(profile,8);out.bits(constraints,8);out.bits(level,8);out.ue(id);
            if(profile==100){
                out.ue(chroma);out.ue(depth);out.ue(depth);out.bits(0,1);out.bits(scaling==0?0:1,1);
                if(scaling!=0)for(int list=0;list<8;list++){out.bits(1,1);if(scaling==2)out.se(-8);else{out.se(scaling==3?128:0);for(int item=1;item<(list<6?16:64);item++)out.se(0);}}
            }
            out.ue(frameLog);out.ue(order);
            if(order==0)out.ue(orderLog);
            else if(order==1){out.bits(0,1);out.se(-1);out.se(2);out.ue(cycle);for(int index=0;index<cycle;index++)out.se(index%3-1);}
            out.ue(references);out.bits(0,1);out.ue(widthMb-1);out.ue(heightMb-1);out.bits(progressive,1);if(progressive==0)out.bits(0,1);out.bits(1,1);
            boolean cropped=left!=0||right!=0||top!=0||bottom!=0;out.bits(cropped?1:0,1);if(cropped){out.ue(left);out.ue(right);out.ue(top);out.ue(bottom);}
            out.bits(vui?1:0,1);
            if(vui){
                out.bits(1,1);out.bits(255,8);out.bits(1,16);out.bits(1,16); // extended SAR
                out.bits(1,1);out.bits(0,1); // overscan
                out.bits(1,1);out.bits(5,3);out.bits(1,1);out.bits(1,1);out.bits(1,8);out.bits(1,8);out.bits(1,8);
                out.bits(1,1);out.ue(0);out.ue(0);
                out.bits(1,1);out.bits(1000,32);out.bits(120000,32);out.bits(1,1);
                out.bits(hrd?1:0,1);if(hrd){out.ue(hrdCount);out.bits(0,4);out.bits(0,4);for(int index=0;index<=hrdCount;index++){out.ue(1023);out.ue(2047);out.bits(1,1);}for(int index=0;index<4;index++)out.bits(23,5);}
                out.bits(0,1);if(hrd)out.bits(1,1);out.bits(0,1); // no VCL HRD, low delay, no pic struct
                out.bits(1,1);out.bits(1,1);out.ue(2);out.ue(1);out.ue(16);out.ue(16);out.ue(reorder);out.ue(buffer);
            }
            return out.nal(7,trailing);
        }
    }
    private static byte[] pps(int sps){Writer out=new Writer();out.ue(0);out.ue(sps);return out.nal(8,true);}
    private static void validates(Sps sps,int width,int height,String label) throws Exception {DisplayVideoProtocol.validateConfiguration(new byte[][]{sps.data(),pps(sps.id)},width,height);check(true,label);}
    private static void rejectsSps(Sps sps,int width,int height,String label) throws Exception {rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{sps.data(),pps(sps.id)},width,height),IOException.class,label);}
    private static void configurationGuards() throws Exception {
        // Actual Windows Media Foundation output from our synthetic-frame encoder probe;
        // no desktop pixels or application content were captured for these parameter sets.
        String[] recorded={"Z0LAKJWgFAGWwEQAAAMABAAAAwHgNoIhGoA=","Z0LAKpWgGQHP58BEAAADAAQAAAMB4DaCIRqA","Z0LAKpWgHgCJ+XARAAADAAEAAAMAeA2giEag"};
        int[][] recordedSizes={{1280,800},{1600,900},{1920,1080}};
        for(int index=0;index<recorded.length;index++){
            byte[] sps=join(new byte[]{0,0,0,1},Base64.getDecoder().decode(recorded[index]));
            byte[] pps=join(new byte[]{0,0,0,1},Base64.getDecoder().decode("aM48gA=="));
            DisplayVideoProtocol.validateConfiguration(new byte[][]{sps,pps},recordedSizes[index][0],recordedSizes[index][1]);
            check(true,"actual Windows AVC SPS/PPS validated for "+recordedSizes[index][0]+"x"+recordedSizes[index][1]);
        }
        for(int[] dimensions:new int[][]{{1280,800},{1600,900},{1920,1080}})for(int profile:new int[]{66,77,100}){
            Sps sps=new Sps(dimensions[0],dimensions[1]);sps.profile=profile;
            validates(sps,dimensions[0],dimensions[1],"AVC profile "+profile+" matches exact "+dimensions[0]+"x"+dimensions[1]+" including macroblock crop");
        }
        Sps highScaling=new Sps(1920,1080);highScaling.profile=100;highScaling.scaling=1;validates(highScaling,1920,1080,"all high-profile scaling lists parse within fixed bounds");
        highScaling.scaling=2;validates(highScaling,1920,1080,"default scaling-list shortcut consumes no missing values");
        highScaling.scaling=3;rejectsSps(highScaling,1920,1080,"out-of-range scaling delta rejected");
        Sps vui=new Sps(1280,800);vui.vui=true;validates(vui,1280,800,"VUI timing, chroma, SAR and buffering fields parse through emulation-prevention bytes");
        vui.hrd=true;vui.hrdCount=31;validates(vui,1280,800,"maximum bounded HRD schedule parses");
        vui.hrdCount=32;rejectsSps(vui,1280,800,"oversized HRD schedule rejected before iteration");vui.hrd=false;
        vui.buffer=17;rejectsSps(vui,1280,800,"VUI cannot request unbounded decoder frame buffers");vui.buffer=1;vui.reorder=2;
        rejectsSps(vui,1280,800,"VUI reorder count cannot exceed decoder buffer count");vui.reorder=0;vui.buffer=0;
        rejectsSps(vui,1280,800,"VUI buffers cannot be below declared reference-frame count");
        Sps order=new Sps(1280,800);order.order=1;order.cycle=255;validates(order,1280,800,"bounded picture-order cycle and signed offsets parse");
        order.cycle=256;rejectsSps(order,1280,800,"oversized picture-order cycle rejected before iteration");order.order=2;validates(order,1280,800,"picture-order type two parses without cycle fields");
        order.order=3;rejectsSps(order,1280,800,"unknown picture-order type rejected");
        Sps invalid=new Sps(1280,800);invalid.profile=110;rejectsSps(invalid,1280,800,"unsupported high-bit-depth profile rejected");
        invalid=new Sps(1280,800);invalid.profile=100;invalid.chroma=3;rejectsSps(invalid,1280,800,"non-420 chroma rejected before decoder allocation");
        invalid=new Sps(1280,800);invalid.profile=100;invalid.depth=1;rejectsSps(invalid,1280,800,"ten-bit samples rejected before decoder allocation");
        invalid=new Sps(1280,800);invalid.constraints=1;rejectsSps(invalid,1280,800,"reserved SPS constraint bits rejected");
        invalid=new Sps(1280,800);invalid.level=0;rejectsSps(invalid,1280,800,"invalid SPS level rejected");
        invalid=new Sps(1280,800);invalid.level=63;rejectsSps(invalid,1280,800,"unsupported SPS level rejected");
        invalid=new Sps(1280,800);invalid.id=32;rejectsSps(invalid,1280,800,"SPS identifier bound enforced");
        invalid=new Sps(1280,800);invalid.frameLog=13;rejectsSps(invalid,1280,800,"frame-number bit width bounded");
        invalid=new Sps(1280,800);invalid.orderLog=13;rejectsSps(invalid,1280,800,"picture-order bit width bounded");
        invalid=new Sps(1280,800);invalid.references=17;rejectsSps(invalid,1280,800,"reference frame count bounded");
        invalid=new Sps(1280,800);invalid.progressive=0;rejectsSps(invalid,1280,800,"interlaced SPS rejected for progressive desktop stream");
        invalid=new Sps(1280,800);invalid.widthMb=121;rejectsSps(invalid,1280,800,"coded width above 1920 rejected before decoder allocation");
        invalid=new Sps(1280,800);invalid.heightMb=69;rejectsSps(invalid,1280,800,"coded height above 1088 rejected before decoder allocation");
        invalid=new Sps(1280,800);invalid.widthMb=120;invalid.right=320;rejectsSps(invalid,1280,800,"cropping cannot disguise a larger coded allocation");
        invalid=new Sps(1280,800);invalid.right=640;rejectsSps(invalid,1280,800,"crop cannot remove the whole coded picture");
        invalid=new Sps(1280,800);invalid.left=961;rejectsSps(invalid,1280,800,"crop Exp-Golomb values are bounded");
        invalid=new Sps(1920,1080);invalid.bottom=0;rejectsSps(invalid,1920,1080,"1088 coded lines require the negotiated eight-line crop");
        invalid=new Sps(1280,800);invalid.trailing=false;rejectsSps(invalid,1280,800,"missing RBSP trailing bit rejected");
        byte[] safe=new Sps(1280,800).data();
        for(int cut=0;cut<safe.length;cut++){final byte[] truncated=Arrays.copyOf(safe,cut);rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{truncated,pps(0)},1280,800),IOException.class,"truncated SPS at "+cut+" bytes rejected");}
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{safe,pps(1)},1280,800),IOException.class,"PPS must reference the validated SPS");
        rejects(()->DisplayVideoProtocol.validateConfiguration(null,1280,800),IOException.class,"missing codec-specific data rejected");
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{safe,pps(0)},4096,2160),IOException.class,"unsupported negotiated dimensions rejected by allocation guard");
        byte[] forbidden=safe.clone();forbidden[4]|=(byte)0x80;
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{forbidden,pps(0)},1280,800),IOException.class,"forbidden SPS NAL header bit rejected");
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{join(safe,new byte[]{0,0,3,4}),pps(0)},1280,800),IOException.class,"invalid emulation-prevention sequence rejected");
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{join(safe,new byte[]{0,0,2}),pps(0)},1280,800),IOException.class,"unescaped reserved byte sequence rejected");
        Writer longCode=new Writer();longCode.bits(66,8);longCode.bits(0,8);longCode.bits(40,8);longCode.bits(0,31);longCode.bits(1,1);
        byte[] oversizedGolomb=longCode.nal(7,true);
        rejects(()->DisplayVideoProtocol.validateConfiguration(new byte[][]{oversizedGolomb,pps(0)},1280,800),IOException.class,"oversized Exp-Golomb prefix rejected without shift overflow");
        check(DisplayVideoProtocol.configurationIfPresent(IDR)==null,"ordinary IDR without parameters has no hidden configuration");
        byte[] pframe={0,0,0,1,0x41,0x11,0x22};
        check(DisplayVideoProtocol.configurationIfPresent(pframe)==null,"ordinary non-IDR has no hidden configuration");
        byte[] pair=join(safe,pps(0));
        check(DisplayVideoProtocol.configurationIfPresent(join(pair,safe,pps(0),IDR)).length==2,"byte-identical repeated parameters remain usable");
        byte[] changed=safe.clone();changed[6]^=4;
        rejects(()->DisplayVideoProtocol.configurationIfPresent(join(pair,changed,IDR)),IOException.class,"different SPS inside one access unit cannot overwrite the validated SPS");
        rejects(()->DisplayVideoProtocol.configurationIfPresent(join(pair,pps(1),IDR)),IOException.class,"different PPS inside one access unit rejected");
        rejects(()->DisplayVideoProtocol.configurationIfPresent(join(safe,IDR)),IOException.class,"unflagged partial parameter update rejected");
        rejects(()->DisplayVideoProtocol.configurationIfPresent(new byte[]{0,0,0,1,0x6f,0x11}),IOException.class,"subset SPS cannot bypass ordinary SPS allocation guard");
        check(DisplayVideoProtocol.videoKind(pair)==0,"parameter-only access unit has no VCL regardless of flags");
        check(DisplayVideoProtocol.videoKind(join(pair,IDR))==5,"IDR type derived from bitstream");
        check(DisplayVideoProtocol.videoKind(pframe)==1,"non-IDR type derived from bitstream");
        check(DisplayVideoProtocol.videoKind(join(IDR,IDR))==5,"multiple IDR slices retain one picture kind");
        rejects(()->DisplayVideoProtocol.videoKind(join(IDR,pframe)),IOException.class,"mixed IDR and non-IDR slices rejected");
        rejects(()->DisplayVideoProtocol.videoKind(new byte[]{0,0,0,1,0x74,0x11}),IOException.class,"extension VCL cannot bypass ordinary picture-kind check");
        for(int partition=2;partition<=4;partition++){final byte kind=(byte)(0x60|partition);rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(2,0,new byte[]{0,0,0,1,kind,0x11}),false),IOException.class,"partitioned VCL "+partition+" cannot bypass picture timestamp validation");}
        rejects(()->DisplayVideoProtocol.videoKind(new byte[]{0,0,0,1}),IOException.class,"empty NAL cannot masquerade as configuration-only packet");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(2,0,pframe),false),IOException.class,"configuration flag cannot hide a non-IDR video picture from timestamp checks");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(0,0,join(pair,IDR)),true),IOException.class,"unflagged IDR picture rejected");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(1,0,pframe),false),IOException.class,"key flag cannot disguise a non-IDR picture");
        check(!DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(3,0,join(pair,IDR)),true),"key-plus-configuration IDR remains a timestamped video picture");
        check(!DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(1,0,join(pair,IDR)),true),"key frame with included validated configuration accepted");
        check(DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(2,0,pair),true),"flagged complete parameter-only packet accepted");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(0,0,pair),true),IOException.class,"unflagged parameter-only packet rejected");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(2,0,pair),false),IOException.class,"configuration-only packet requires both validated parameter sets");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(1,0,IDR),false),IOException.class,"IDR missing its validated configuration rejected");
        check(!DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(0,0,pframe),false),"ordinary non-IDR remains a timestamped video picture");
        rejects(()->DisplayVideoProtocol.configurationOnly(new DisplayVideoProtocol.Unit(4,0,new byte[0]),false),IOException.class,"end packet is not interpreted as codec configuration");
    }
    private static void heartbeatGuards() throws Exception {
        DisplayVideoProtocol.Unit heartbeat=DisplayVideoProtocol.unit(input(packet(0,8,500_000,new byte[0])));
        check(heartbeat.heartbeat()&&!heartbeat.key()&&!heartbeat.end()&&heartbeat.bytes.length==0,"empty standalone heartbeat is distinct from AVC and end packets");
        rejects(()->DisplayVideoProtocol.unit(input(packet(1,8,0,new byte[]{0}))),IOException.class,"heartbeat payload is refused before decoding");
        for(int flags=9;flags<=15;flags++){final int mixed=flags;rejects(()->DisplayVideoProtocol.unit(input(packet(0,mixed,0,new byte[0]))),IOException.class,"heartbeat cannot combine with video or end flags "+flags);}
        rejects(()->DisplayVideoProtocol.unit(input(packet(0,8,-1,new byte[0]))),IOException.class,"negative heartbeat timestamp rejected");
        rejects(()->DisplayVideoProtocol.configurationOnly(heartbeat,false),IOException.class,"heartbeat cannot enter the AVC decoder guard");
        DisplayVideoProtocol.StreamState state=new DisplayVideoProtocol.StreamState(0);
        check(!state.accept(heartbeat,false,500_000_000L)&&state.pictures()==0,"startup heartbeat does not increment received-video count");
        check(!state.stillImage(600_000_000L),"heartbeats without an initial image cannot claim a still image");
        state.requireFirstPictureWithin(4_999_999_999L);check(true,"first-picture deadline has a bounded startup allowance");
        state.accept(new DisplayVideoProtocol.Unit(8,4_000_000,new byte[0]),false,4_000_000_000L);
        rejects(()->state.requireFirstPictureWithin(5_000_000_000L),IOException.class,"heartbeats cannot extend the first-picture deadline");
        check(state.accept(new DisplayVideoProtocol.Unit(1,1_000_000,IDR),false,5_000_000_000L),"video timestamps are independent of the later heartbeat clock");
        check(state.pictures()==1,"one actual video picture increments the counter once");
        check(!state.accept(new DisplayVideoProtocol.Unit(8,5_000_000,new byte[0]),false,5_500_000_000L)&&state.pictures()==1,"post-start heartbeat never increments video or decoder count");
        check(state.stillImage(6_000_000_000L),"recent heartbeat identifies an unchanged already-rendered picture");
        check(!state.stillImage(7_000_000_000L),"expired heartbeat cannot claim the image is still shared");
        rejects(()->state.accept(new DisplayVideoProtocol.Unit(8,5_000_000,new byte[0]),false,6_000_000_000L),IOException.class,"replayed heartbeat timestamp rejected");
        rejects(()->state.accept(new DisplayVideoProtocol.Unit(8,4_500_000,new byte[0]),false,6_000_000_000L),IOException.class,"decreasing heartbeat timestamp rejected");
        check(state.accept(new DisplayVideoProtocol.Unit(0,1_100_000,new byte[]{0,0,0,1,0x61,1}),false,7_100_000_000L)&&!state.stillImage(7_200_000_000L),"new motion leaves the still-image status immediately");
        rejects(()->state.accept(new DisplayVideoProtocol.Unit(0,1_050_000,new byte[]{0,0,0,1,0x61,1}),false,7_200_000_000L),IOException.class,"heartbeats do not weaken video timestamp ordering");
        check(!state.accept(new DisplayVideoProtocol.Unit(2,0,join(SPS,PPS)),true,7_300_000_000L)&&state.pictures()==2,"configuration-only units remain outside received-video count");
    }
    private static final class TrickleInput extends InputStream {
        final CountDownLatch blocking=new CountDownLatch(1);final byte[] prefix;final int fastBytes;private int offset;private boolean closed;
        TrickleInput(byte[] prefix,int fastBytes){this.prefix=prefix;this.fastBytes=fastBytes;}
        @Override public synchronized int read() throws IOException {
            if(closed)throw new IOException("Test stream disconnected.");
            if(offset>=fastBytes){blocking.countDown();try{wait(250);}catch(InterruptedException ex){Thread.currentThread().interrupt();throw new IOException("Test read interrupted.");}if(closed)throw new IOException("Test stream disconnected.");}
            return offset<prefix.length?prefix[offset++]&255:0;
        }
        @Override public synchronized void close(){closed=true;notifyAll();}
    }
    private static void deadlineGuards() throws Exception {
        ScheduledExecutorService deadlines=Executors.newSingleThreadScheduledExecutor();ExecutorService readers=Executors.newSingleThreadExecutor();
        try{
            for(boolean initialHeader:new boolean[]{true,false}){
                TrickleInput trickle=new TrickleInput(initialHeader?header(1280,800,60):packet(DisplayVideoProtocol.MAX_ACCESS_UNIT,1,0,new byte[0]),initialHeader?0:16);
                Future<Boolean> read=readers.submit(()->{try{if(initialHeader)DisplayVideoProtocol.header(new DataInputStream(trickle),1280,800,60);else DisplayVideoProtocol.unit(new DataInputStream(trickle));return false;}catch(IOException expected){return true;}});
                try{
                    check(trickle.blocking.await(2,TimeUnit.SECONDS),"trickled "+(initialHeader?"header":"access unit")+" enters a blocking read");
                    try(DisplayReadDeadline deadline=new DisplayReadDeadline(deadlines,50,trickle::close)){
                        check(read.get(3,TimeUnit.SECONDS)&&deadline.expired(),"independent deadline interrupts partial "+(initialHeader?"header":"2 MiB access unit")+" despite continued trickling");
                        check(!deadline.acceptPicture(),"expired read cannot revive a late picture");
                    }
                }finally{trickle.close();read.cancel(true);}
            }
            CountDownLatch acceptedAbort=new CountDownLatch(1);
            try(DisplayReadDeadline accepted=new DisplayReadDeadline(deadlines,500,acceptedAbort::countDown)){
                check(accepted.acceptPicture()&&accepted.acceptPicture(),"first validated picture wins the deadline exactly once");
                check(!acceptedAbort.await(650,TimeUnit.MILLISECONDS)&&!accepted.expired(),"accepted picture cancels the pending disconnect");
            }
            CountDownLatch oldAbort=new CountDownLatch(1),newAbort=new CountDownLatch(1);
            try(DisplayReadDeadline old=new DisplayReadDeadline(deadlines,50,oldAbort::countDown);DisplayReadDeadline next=new DisplayReadDeadline(deadlines,100,newAbort::countDown)){
                old.close();check(!old.acceptPicture(),"stopped session cannot accept a late first picture");
                check(newAbort.await(2,TimeUnit.SECONDS)&&oldAbort.getCount()==1,"stopped deadline cannot disconnect a replacement session");
            }
        }finally{readers.shutdownNow();deadlines.shutdownNow();}
    }
    public static void main(String[] args) throws Exception {
        for(int[] dimensions:new int[][]{{1280,800},{1600,900},{1920,1080}}){
            for(int rate:new int[]{60,90,120,144,165,240}){
                DisplayVideoProtocol.Header value=DisplayVideoProtocol.header(input(header(dimensions[0],dimensions[1],rate)),dimensions[0],dimensions[1],rate);
                check(value.width==dimensions[0]&&value.height==dimensions[1]&&value.refreshRate==rate,"exact "+dimensions[0]+"x"+dimensions[1]+" at "+rate+" Hz accepted");
            }
        }
        byte[] validHeader=header(1280,800,60);
        for(int cut=0;cut<32;cut++){
            final byte[] truncated=Arrays.copyOf(validHeader,cut);
            rejects(()->DisplayVideoProtocol.header(input(truncated),1280,800,60),IOException.class,"truncated stream header at "+cut+" bytes rejected");
        }
        int[][] invalidHeaders={
            {0x44575632,1,32,1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,2,32,1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,31,1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1920,1200,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1280,800,59,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1280,800,241,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1280,800,60,0,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1280,800,60,2,DisplayVideoProtocol.MAX_ACCESS_UNIT,0},
            {0x44575631,1,32,1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT+1,0},
            {0x44575631,1,32,1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,1},
            {0x44575631,1,32,-1280,800,60,1,DisplayVideoProtocol.MAX_ACCESS_UNIT,0}
        };
        for(int index=0;index<invalidHeaders.length;index++){
            final int[] h=invalidHeaders[index];
            rejects(()->DisplayVideoProtocol.header(input(header(h[0],h[1],h[2],h[3],h[4],h[5],h[6],h[7],h[8])),1280,800,60),IOException.class,"invalid protocol header variant "+index+" rejected");
        }
        rejects(()->DisplayVideoProtocol.header(input(header(1280,800,60)),1600,900,60),IOException.class,"negotiated dimensions cannot change in the stream");
        rejects(()->DisplayVideoProtocol.header(input(header(1280,800,120)),1280,800,60),IOException.class,"negotiated refresh rate cannot change in the stream");

        byte[] accessUnit=join(SPS,PPS,IDR);
        DisplayVideoProtocol.Unit key=DisplayVideoProtocol.unit(input(packet(accessUnit.length,DisplayVideoProtocol.KEY_FRAME,16667,accessUnit)));
        check(key.key()&&!key.end()&&key.presentationUs==16667&&Arrays.equals(accessUnit,key.bytes),"complete key access unit preserves bytes and timestamp");
        DisplayVideoProtocol.Unit config=DisplayVideoProtocol.unit(input(packet(accessUnit.length,DisplayVideoProtocol.CODEC_CONFIG,0,accessUnit)));
        check(!config.key()&&!config.end()&&config.flags==2,"codec configuration flag is distinct from a key frame");
        DisplayVideoProtocol.Unit combined=DisplayVideoProtocol.unit(input(packet(accessUnit.length,3,0,accessUnit)));
        check(combined.key()&&!combined.end(),"key and configuration flags can coexist");
        DisplayVideoProtocol.Unit end=DisplayVideoProtocol.unit(input(packet(0,DisplayVideoProtocol.END,999,new byte[0])));
        check(end.end()&&!end.key()&&end.bytes.length==0,"zero-length explicit end packet accepted");
        byte[] unitHeader=packet(IDR.length,1,0,new byte[0]);
        for(int cut=0;cut<16;cut++){
            final byte[] truncated=Arrays.copyOf(unitHeader,cut);
            rejects(()->DisplayVideoProtocol.unit(input(truncated)),IOException.class,"truncated access-unit header at "+cut+" bytes rejected");
        }
        for(int length:new int[]{Integer.MIN_VALUE,-1,0,1,4,DisplayVideoProtocol.MAX_ACCESS_UNIT+1,Integer.MAX_VALUE}){
            rejects(()->DisplayVideoProtocol.unit(input(packet(length,1,0,new byte[0]))),IOException.class,"invalid access-unit length "+length+" rejected before payload");
        }
        for(int flags:new int[]{-1,8,16,Integer.MAX_VALUE})rejects(()->DisplayVideoProtocol.unit(input(packet(IDR.length,flags,0,IDR))),IOException.class,"unknown access-unit flags "+flags+" rejected");
        rejects(()->DisplayVideoProtocol.unit(input(packet(IDR.length,1,-1,IDR))),IOException.class,"negative presentation timestamp rejected");
        for(int flags:new int[]{4,5,6,7})rejects(()->DisplayVideoProtocol.unit(input(packet(IDR.length,flags,0,IDR))),IOException.class,"end packet with payload or extra flags "+flags+" rejected");
        rejects(()->DisplayVideoProtocol.unit(input(packet(0,5,0,new byte[0]))),IOException.class,"empty end packet with key flag rejected");
        for(int cut=0;cut<IDR.length;cut++){
            final byte[] truncated=Arrays.copyOf(IDR,cut);
            rejects(()->DisplayVideoProtocol.unit(input(packet(IDR.length,1,0,truncated))),IOException.class,"truncated access-unit payload at "+cut+" bytes rejected");
        }
        rejects(()->DisplayVideoProtocol.unit(input(packet(6,0,0,new byte[]{1,2,3,4,5,6}))),IOException.class,"non-Annex-B payload rejected");
        DisplayVideoProtocol.Unit threeByte=DisplayVideoProtocol.unit(input(packet(PPS.length,2,0,PPS)));
        check(Arrays.equals(threeByte.bytes,PPS),"three-byte Annex-B prefix accepted");
        byte[] maximum=new byte[DisplayVideoProtocol.MAX_ACCESS_UNIT];maximum[3]=1;maximum[4]=0x65;
        check(DisplayVideoProtocol.unit(input(packet(maximum.length,1,0,maximum))).bytes.length==maximum.length,"exact access-unit bound accepted");
        byte[][] csd=DisplayVideoProtocol.configuration(accessUnit);
        check(Arrays.equals(csd[0],SPS),"SPS configuration preserves its Annex-B data");
        check(Arrays.equals(csd[1],new byte[]{0,0,0,1,0x68,0x09,0x03}),"PPS three-byte prefix is normalized to MediaCodec four-byte prefix");
        rejects(()->DisplayVideoProtocol.configuration(IDR),IOException.class,"missing SPS and PPS rejected");
        rejects(()->DisplayVideoProtocol.configuration(join(SPS,IDR)),IOException.class,"missing PPS rejected");
        rejects(()->DisplayVideoProtocol.configuration(join(PPS,IDR)),IOException.class,"missing SPS rejected");
        rejects(()->DisplayVideoProtocol.configuration(new byte[0]),IOException.class,"empty AVC configuration rejected");
        rejects(()->DisplayVideoProtocol.configuration(new byte[]{0,0,0,1}),IOException.class,"start code without a NAL rejected");
        rejects(()->DisplayVideoProtocol.configuration(join(new byte[]{3,4},SPS,PPS)),IOException.class,"junk before AVC configuration rejected");
        byte[] largeSps=new byte[65537];Arrays.fill(largeSps,(byte)0x55);largeSps[0]=largeSps[1]=largeSps[2]=0;largeSps[3]=1;largeSps[4]=0x67;
        rejects(()->DisplayVideoProtocol.configuration(join(largeSps,PPS)),IOException.class,"oversized SPS rejected");
        byte[] largePps=Arrays.copyOf(largeSps,largeSps.length);largePps[4]=0x68;
        rejects(()->DisplayVideoProtocol.configuration(join(SPS,largePps)),IOException.class,"oversized PPS rejected");
        byte[] exactSps=Arrays.copyOf(largeSps,65536);
        check(DisplayVideoProtocol.configuration(join(exactSps,PPS))[0].length==65536,"exact codec-specific-data bound accepted");

        String uuid="123e4567-e89b-42d3-a456-426614174000";
        check(DisplayVideoProtocol.session(uuid).equals(uuid),"canonical session UUID accepted");
        check(DisplayVideoProtocol.session(uuid.toUpperCase()).equals(uuid.toUpperCase()),"UUID case does not change session spelling");
        for(Object invalid:new Object[]{null,1,"",uuid.substring(1),"1-1-1-1-1"," "+uuid,uuid+" ",uuid.replace('e','z')})rejects(()->DisplayVideoProtocol.session(invalid),IllegalArgumentException.class,"invalid session representation rejected");
        check(DisplayVideoProtocol.targetRate("60",Float.NaN)==60,"fixed 60 Hz does not depend on refresh-rate discovery");
        check(DisplayVideoProtocol.targetRate("match",120.00001f)==120,"actual 120.00001 Hz tablet rate rounds to 120");
        check(DisplayVideoProtocol.targetRate("match",59.94f)==60,"59.94 Hz active rate negotiates integer 60");
        check(DisplayVideoProtocol.targetRate("match",165f)==165,"165 Hz is retained when it is the active rate");
        check(DisplayVideoProtocol.targetRate("match",240f)==240,"240 Hz supported ceiling accepted");
        for(float invalid:new float[]{Float.NaN,Float.POSITIVE_INFINITY,Float.NEGATIVE_INFINITY,0,58.99f,240.51f})rejects(()->DisplayVideoProtocol.targetRate("match",invalid),IllegalArgumentException.class,"invalid active refresh rate rejected");
        for(String invalid:new String[]{null,"","120","MATCH","auto"})rejects(()->DisplayVideoProtocol.targetRate(invalid,120),IllegalArgumentException.class,"unsupported refresh mode rejected");
        configurationGuards();
        heartbeatGuards();
        deadlineGuards();
        System.out.println(passed+" display-video protocol checks passed; no native decoder, capture, or network was used.");
    }
}
