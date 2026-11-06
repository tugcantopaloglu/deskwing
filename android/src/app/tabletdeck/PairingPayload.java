package app.tabletdeck;

import java.net.URI;
import java.util.HashMap;
import java.util.Map;

/** Strict local pairing data. Decoding a QR never opens a URL or pairs automatically. */
public final class PairingPayload {
    public final String host,mode,certificate,code;
    public final int port,controlPort;
    private PairingPayload(Map<String,String> data){host=data.get("host");mode=data.get("mode");certificate=data.get("certificate");code=data.get("code");port=Integer.parseInt(data.get("port"));controlPort=Integer.parseInt(data.get("controlPort"));}
    public static PairingPayload parse(String text) throws Exception {
        if(text==null||text.length()>512)throw new Exception("This is not a DeskWing pairing code.");
        URI uri=new URI(text);
        if(!"tabletdeck".equals(uri.getScheme())||!"pair".equals(uri.getHost())||uri.getRawUserInfo()!=null||uri.getPort()!=-1||uri.getRawFragment()!=null||uri.getRawPath()==null||!uri.getRawPath().isEmpty()||uri.getRawQuery()==null)throw new Exception("This is not a DeskWing pairing code.");
        Map<String,String> data=new HashMap<>();
        for(String part:uri.getRawQuery().split("&",-1)){String[] pair=part.split("=",-1);if(pair.length!=2||data.put(pair[0],pair[1])!=null)throw new Exception("Invalid pairing fields.");}
        if(data.size()!=7||!"1".equals(data.get("v"))||!data.containsKey("host")||!data.containsKey("mode")||!data.containsKey("port")||!data.containsKey("controlPort")||!data.containsKey("certificate")||!data.containsKey("code"))throw new Exception("Unsupported pairing code.");
        return validate(data);
    }
    private static PairingPayload validate(Map<String,String> data) throws Exception {
        if(!("wifi".equals(data.get("mode"))||"usb".equals(data.get("mode")))||!data.get("certificate").matches("[A-F0-9]{24}")||!data.get("code").matches("[0-9]{6}")||!data.get("port").matches("[0-9]{1,5}")||!data.get("controlPort").matches("[0-9]{1,5}"))throw new Exception("Invalid pairing code.");
        String host=data.get("host");if(!host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}"))throw new Exception("Use a private IPv4 PC address.");String[] parts=host.split("\\.");int[] a=new int[4];for(int i=0;i<4;i++){a[i]=Integer.parseInt(parts[i]);if(a[i]>255)throw new Exception("Invalid PC address.");}
        if(!(a[0]==10||a[0]==192&&a[1]==168||a[0]==172&&a[1]>=16&&a[1]<=31||a[0]==127||a[0]==169&&a[1]==254||a[0]==100&&a[1]>=64&&a[1]<=127))throw new Exception("Use a private PC address.");
        PairingPayload value=new PairingPayload(data);if(value.port<1||value.port>65535||value.controlPort<1||value.controlPort>65535||("usb".equals(value.mode)&&!value.host.equals("127.0.0.1")))throw new Exception("Invalid pairing ports or USB address.");return value;
    }
}
