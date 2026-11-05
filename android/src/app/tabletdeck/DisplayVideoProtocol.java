package app.tabletdeck;

import java.io.DataInputStream;
import java.io.IOException;
import java.util.Arrays;
import java.util.UUID;

/** Bounded DWV1 framing, independent of Android and suitable for hostile-input tests. */
final class DisplayVideoProtocol {
    static final int MAX_ACCESS_UNIT=2*1024*1024;
    static final int KEY_FRAME=1,CODEC_CONFIG=2,END=4,HEARTBEAT=8;

    static final class Header {
        final int width,height,refreshRate;
        Header(int width,int height,int refreshRate){this.width=width;this.height=height;this.refreshRate=refreshRate;}
    }
    static final class Unit {
        final int flags;final long presentationUs;final byte[] bytes;
        Unit(int flags,long presentationUs,byte[] bytes){this.flags=flags;this.presentationUs=presentationUs;this.bytes=bytes;}
        boolean key(){return (flags&KEY_FRAME)!=0;}
        boolean end(){return flags==END;}
        boolean heartbeat(){return flags==HEARTBEAT;}
    }
    static Header header(DataInputStream input,int expectedWidth,int expectedHeight,int expectedRate) throws IOException {
        if(input.readInt()!=0x44575631||input.readUnsignedShort()!=1||input.readUnsignedShort()!=32)throw new IOException("The PC video protocol is unsupported.");
        int width=input.readInt(),height=input.readInt(),rate=input.readInt(),denominator=input.readInt(),limit=input.readInt(),flags=input.readInt();
        boolean supported=width==1280&&height==800||width==1600&&height==900||width==1920&&height==1080;
        if(!supported||width!=expectedWidth||height!=expectedHeight||rate!=expectedRate||rate<60||rate>240||denominator!=1||limit!=MAX_ACCESS_UNIT||flags!=0)throw new IOException("The PC video settings changed. Start the display again.");
        return new Header(width,height,rate);
    }
    static Unit unit(DataInputStream input) throws IOException {
        int length=input.readInt(),flags=input.readInt();long presentation=input.readLong();
        if(length<0||length>MAX_ACCESS_UNIT||flags<0||(flags&~15)!=0||presentation<0)throw new IOException("The PC returned an invalid video frame.");
        if((flags&HEARTBEAT)!=0){if(flags!=HEARTBEAT||length!=0)throw new IOException("The PC returned an invalid video heartbeat.");return new Unit(flags,presentation,new byte[0]);}
        if((flags&END)!=0){if(flags!=END||length!=0)throw new IOException("The PC returned an invalid video ending.");return new Unit(flags,presentation,new byte[0]);}
        if(length<5)throw new IOException("The PC returned an empty video frame.");
        byte[] bytes=new byte[length];input.readFully(bytes);
        if(prefix(bytes,0)==0)throw new IOException("The PC returned unsupported video data.");
        return new Unit(flags,presentation,bytes);
    }
    /** Heartbeats prove an unchanged image is still shared; they are never video frames. */
    static final class StreamState {
        private final long startedAt;
        private long heartbeatPts=-1,videoPts=-1,lastHeartbeatAt,lastVideoAt,pictures;
        StreamState(long now){startedAt=now;}
        synchronized void requireFirstPictureWithin(long now) throws IOException {
            if(pictures==0&&now-startedAt>=5_000_000_000L)throw new IOException("The PC did not provide the first display picture in time. Start again.");
        }
        synchronized boolean accept(Unit unit,boolean configurationOnly,long now) throws IOException {
            if(unit.heartbeat()){
                if(unit.presentationUs<=heartbeatPts)throw new IOException("The PC returned an out-of-order display heartbeat.");
                heartbeatPts=unit.presentationUs;lastHeartbeatAt=now;return false;
            }
            if(unit.end()||configurationOnly)return false;
            if(unit.presentationUs<=videoPts)throw new IOException("The PC returned an out-of-order video frame.");
            videoPts=unit.presentationUs;lastVideoAt=now;pictures++;return true;
        }
        synchronized boolean stillImage(long now){return pictures>0&&heartbeatPts>=0&&lastHeartbeatAt>=lastVideoAt&&now-lastVideoAt>=1_000_000_000L&&now-lastHeartbeatAt<1_500_000_000L;}
        synchronized long pictures(){return pictures;}
    }
    /** SPS/PPS include the Annex-B start code required by MediaCodec's AVC codec-specific data. */
    static byte[][] configuration(byte[] data) throws IOException {
        byte[][] value=configurationIfPresent(data);
        if(value==null)throw new IOException("The PC did not provide its AVC configuration.");
        return value;
    }
    /** Check every access unit, including unflagged units, before submitting it to MediaCodec. */
    static byte[][] configurationIfPresent(byte[] data) throws IOException {
        if(data==null||data.length<4||data.length>MAX_ACCESS_UNIT)throw new IOException("Invalid AVC configuration.");
        byte[] sps=null,pps=null;
        for(int start=0;start<data.length;){
            int prefix=prefix(data,start);if(prefix==0)throw new IOException("Invalid AVC configuration.");
            int nal=start+prefix,end=nal+1;
            if(nal>=data.length)throw new IOException("Invalid AVC configuration.");
            while(end<data.length&&prefix(data,end)==0)end++;
            int type=data[nal]&31;
            // DWV1 transports ordinary AVC only. Extensions/subset SPS can change decoder
            // allocations without passing the ordinary SPS dimension guard.
            if((data[nal]&0x80)!=0||!(type==1||type==5||type==6||type==7||type==8||type==9||type==10||type==11||type==12))throw new IOException("The PC returned an unsupported AVC unit.");
            if(type==7||type==8){
                if(end-nal>65532)throw new IOException("The PC video configuration is too large.");
                byte[] value=new byte[end-nal+4];value[3]=1;System.arraycopy(data,nal,value,4,end-nal);
                if(type==7){if(sps!=null&&!Arrays.equals(sps,value))throw new IOException("The PC changed AVC configuration within one frame.");sps=value;}
                else{if(pps!=null&&!Arrays.equals(pps,value))throw new IOException("The PC changed AVC configuration within one frame.");pps=value;}
            }
            start=end;
        }
        if(sps==null&&pps==null)return null;
        if(sps==null||pps==null)throw new IOException("The PC did not provide its complete AVC configuration.");
        return new byte[][]{sps,pps};
    }
    static int videoKind(byte[] data) throws IOException {
        if(data==null||data.length<4||data.length>MAX_ACCESS_UNIT)throw invalidConfiguration();
        int kind=0;
        for(int start=0;start<data.length;){
            int code=prefix(data,start);if(code==0||start+code>=data.length)throw invalidConfiguration();
            int nal=start+code,type=data[nal]&31;
            if((data[nal]&0x80)!=0||!(type==1||type==5||type==6||type==7||type==8||type==9||type==10||type==11||type==12))throw invalidConfiguration();
            if(type==1||type==5){if(kind!=0&&kind!=type)throw new IOException("The PC mixed different AVC picture types in one frame.");kind=type;}
            int end=nal+1;while(end<data.length&&prefix(data,end)==0)end++;start=end;
        }
        return kind;
    }
    static boolean configurationOnly(Unit unit,boolean hasConfiguration) throws IOException {
        if(unit==null||unit.flags<0||(unit.flags&~3)!=0)throw new IOException("The PC returned invalid AVC frame flags.");
        int kind=videoKind(unit.bytes);
        if(unit.key()!=(kind==5))throw new IOException("The AVC picture type does not match its frame flags.");
        if(kind==0){
            if(unit.flags!=CODEC_CONFIG||!hasConfiguration)throw new IOException("The PC returned an incomplete AVC configuration packet.");
            return true;
        }
        if(kind==5&&!hasConfiguration)throw new IOException("An AVC key frame must include its validated configuration.");
        if(kind==1&&(unit.flags&CODEC_CONFIG)!=0)throw new IOException("An AVC video picture cannot masquerade as configuration.");
        return false;
    }
    /** Original bounded parser of H.264 7.3.2.1.1 / Annex E; this is an allocation guard,
     * not a substitute for the platform decoder's complete bitstream validation. */
    static void validateConfiguration(byte[][] csd,int expectedWidth,int expectedHeight) throws IOException {
        if(csd==null||csd.length!=2||!(expectedWidth==1280&&expectedHeight==800||expectedWidth==1600&&expectedHeight==900||expectedWidth==1920&&expectedHeight==1080))throw invalidConfiguration();
        Bits sps=new Bits(rbsp(csd[0],7));
        int profile=sps.read(8),constraints=sps.read(8),level=sps.read(8);
        if(!(profile==66||profile==77||profile==100)||(constraints&3)!=0||level==0||level>62)throw invalidConfiguration();
        int spsId=sps.ue(31);
        if(profile==100){
            if(sps.ue(3)!=1||sps.ue(6)!=0||sps.ue(6)!=0)throw invalidConfiguration(); // 8-bit 4:2:0 only
            sps.read(1); // qpprime_y_zero_transform_bypass_flag
            if(sps.read(1)!=0)for(int list=0;list<8;list++)if(sps.read(1)!=0)skipScalingList(sps,list<6?16:64);
        }
        sps.ue(12); // log2_max_frame_num_minus4
        int order=sps.ue(2);
        if(order==0)sps.ue(12);
        else if(order==1){
            sps.read(1);sps.se();sps.se();
            int cycle=sps.ue(255);for(int index=0;index<cycle;index++)sps.se();
        }
        int references=sps.ue(16);sps.read(1);
        int codedWidth=(sps.ue(119)+1)*16,codedHeight=(sps.ue(67)+1)*16;
        if(sps.read(1)!=1)throw invalidConfiguration(); // only progressive desktop video
        sps.read(1); // direct_8x8_inference_flag
        int left=0,right=0,top=0,bottom=0;
        if(sps.read(1)!=0){left=sps.ue(960);right=sps.ue(960);top=sps.ue(544);bottom=sps.ue(544);}
        int width=codedWidth-2*(left+right),height=codedHeight-2*(top+bottom);
        // Cropping must not disguise a much larger coded allocation.
        if(codedWidth!=((expectedWidth+15)/16)*16||codedHeight!=((expectedHeight+15)/16)*16||width!=expectedWidth||height!=expectedHeight)throw new IOException("The AVC picture size does not match the negotiated PC display.");
        if(sps.read(1)!=0)skipVui(sps,references);
        sps.trailing();
        Bits pps=new Bits(rbsp(csd[1],8));pps.ue(255);
        if(pps.ue(31)!=spsId)throw invalidConfiguration();
    }
    private static void skipScalingList(Bits bits,int size) throws IOException {
        int last=8,next=8;
        for(int index=0;index<size;index++){
            if(next!=0){int delta=bits.se();if(delta< -128||delta>127)throw invalidConfiguration();next=(last+delta+256)&255;}
            if(next!=0)last=next;
        }
    }
    private static void skipVui(Bits bits,int references) throws IOException {
        if(bits.read(1)!=0){int aspect=bits.read(8);if(aspect==255){if(bits.read(16)==0||bits.read(16)==0)throw invalidConfiguration();}}
        if(bits.read(1)!=0)bits.read(1);
        if(bits.read(1)!=0){bits.read(3);bits.read(1);if(bits.read(1)!=0){bits.read(8);bits.read(8);bits.read(8);}}
        if(bits.read(1)!=0){bits.ue(5);bits.ue(5);}
        if(bits.read(1)!=0){if(bits.readLong(32)==0||bits.readLong(32)==0)throw invalidConfiguration();bits.read(1);}
        boolean nalHrd=bits.read(1)!=0;if(nalHrd)skipHrd(bits);
        boolean vclHrd=bits.read(1)!=0;if(vclHrd)skipHrd(bits);
        if(nalHrd||vclHrd)bits.read(1);
        bits.read(1);
        if(bits.read(1)!=0){
            bits.read(1);bits.ue(16);bits.ue(16);bits.ue(16);bits.ue(16);
            int reorder=bits.ue(16),buffer=bits.ue(16);
            if(reorder>buffer||buffer<references)throw invalidConfiguration();
        }
    }
    private static void skipHrd(Bits bits) throws IOException {
        int count=bits.ue(31)+1;bits.read(4);bits.read(4);
        for(int index=0;index<count;index++){bits.ue(Integer.MAX_VALUE);bits.ue(Integer.MAX_VALUE);bits.read(1);}
        for(int index=0;index<4;index++)bits.read(5);
    }
    private static byte[] rbsp(byte[] nal,int expectedType) throws IOException {
        if(nal==null||nal.length<6||nal.length>65536||prefix(nal,0)!=4||(nal[4]&0x80)!=0||(nal[4]&0x60)==0||(nal[4]&31)!=expectedType)throw invalidConfiguration();
        int end=nal.length;while(end>5&&nal[end-1]==0)end--; // Annex-B trailing_zero_8bits
        byte[] result=new byte[end-5];int length=0,zeros=0;
        for(int index=5;index<end;index++){
            int value=nal[index]&255;
            if(zeros>=2){
                if(value==3){if(index+1>=end||(nal[index+1]&255)>3)throw invalidConfiguration();zeros=0;continue;}
                if(value<=2)throw invalidConfiguration();
            }
            result[length++]=(byte)value;zeros=value==0?zeros+1:0;
        }
        return Arrays.copyOf(result,length);
    }
    private static IOException invalidConfiguration(){return new IOException("The PC returned unsupported or invalid AVC configuration.");}
    private static final class Bits {
        private final byte[] bytes;private int position;
        Bits(byte[] bytes){this.bytes=bytes;}
        int read(int count) throws IOException {return (int)readLong(count);}
        long readLong(int count) throws IOException {
            if(count<0||count>32||count>bytes.length*8-position)throw invalidConfiguration();
            long value=0;for(int bit=0;bit<count;bit++,position++)value=(value<<1)|((bytes[position/8]>>(7-position%8))&1);return value;
        }
        int ue(int maximum) throws IOException {
            int zeros=0;while(read(1)==0){if(++zeros>30)throw invalidConfiguration();}
            long value=((1L<<zeros)-1)+readLong(zeros);
            if(value>maximum)throw invalidConfiguration();return (int)value;
        }
        int se() throws IOException {int code=ue(Integer.MAX_VALUE);return (code&1)==0?-(code/2):(code/2)+1;}
        void trailing() throws IOException {if(read(1)!=1)throw invalidConfiguration();while(position<bytes.length*8)if(read(1)!=0)throw invalidConfiguration();}
    }
    private static int prefix(byte[] bytes,int offset){
        if(offset+3<=bytes.length&&bytes[offset]==0&&bytes[offset+1]==0){
            if(bytes[offset+2]==1)return 3;
            if(offset+4<=bytes.length&&bytes[offset+2]==0&&bytes[offset+3]==1)return 4;
        }
        return 0;
    }
    static String session(Object value){
        if(!(value instanceof String))throw new IllegalArgumentException("Start the display again.");
        String text=(String)value;
        try{if(text.length()!=36||!UUID.fromString(text).toString().equalsIgnoreCase(text))throw new IllegalArgumentException();}
        catch(IllegalArgumentException ex){throw new IllegalArgumentException("Start the display again.");}
        return text;
    }
    static int targetRate(String mode,float current){
        if("60".equals(mode))return 60;
        if(!"match".equals(mode))throw new IllegalArgumentException("Choose 60 Hz or Match tablet refresh rate.");
        if(!Float.isFinite(current)||current<59||current>240.5f)throw new IllegalArgumentException("The current tablet refresh rate is unavailable. Choose 60 Hz.");
        return Math.max(60,Math.min(240,Math.round(current)));
    }
    private DisplayVideoProtocol(){}
}
