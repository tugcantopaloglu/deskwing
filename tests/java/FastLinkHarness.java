import app.tabletdeck.FastLink;
import app.tabletdeck.PinnedTrustManager;
import javax.net.ssl.*;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

public final class FastLinkHarness {
    private static int passed;
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    public static void main(String[] args) throws Exception {
        final String pin=System.getenv("DECK_TEST_PIN"),token=System.getenv("DECK_TEST_TOKEN"),action=System.getenv("DECK_TEST_ACTION");
        final int port=Integer.parseInt(System.getenv("DECK_TEST_PORT"));
        SSLContext context=SSLContext.getInstance("TLS");
        context.init(null,new TrustManager[]{new PinnedTrustManager(pin,null)},null);
        try(SSLSocket probe=(SSLSocket)context.getSocketFactory().createSocket("127.0.0.1",port)){
            probe.setSoTimeout(3000);probe.startHandshake();
            X509Certificate peer=(X509Certificate)probe.getSession().getPeerCertificates()[0];
            final String[] found={null};new PinnedTrustManager(pin.substring(0,24),fingerprint->found[0]=fingerprint).checkServerTrusted(new X509Certificate[]{peer},"RSA");
            check(pin.equals(found[0]),"pairing code verifies the full self-signed PC certificate through platform trust validation");
            try{new PinnedTrustManager(new String(new char[64]).replace('\0','0'),value->{throw new AssertionError("Wrong pin callback");}).checkServerTrusted(new X509Certificate[]{peer},"RSA");throw new AssertionError("Wrong pin accepted");}catch(java.security.cert.CertificateException expected){check(true,"wrong certificate pin is rejected before trust-anchor creation");}
            try{new PinnedTrustManager(pin,null).checkServerTrusted(new X509Certificate[0],"RSA");throw new AssertionError("Empty chain accepted");}catch(java.security.cert.CertificateException expected){check(true,"empty certificate chain is rejected");}
        }
        CountDownLatch ready=new CountDownLatch(1),result=new CountDownLatch(1),state=new CountDownLatch(1),reconnected=new CountDownLatch(2),pong=new CountDownLatch(1);
        FastLink link=new FastLink(new FastLink.Listener(){
            public FastLink.Settings settings(){return new FastLink.Settings("127.0.0.1",port,token,pin,context.getSocketFactory());}
            public boolean packet(String json){
                if(json.matches("(?s).*\"type\"\\s*:\\s*\"ready\".*")){ready.countDown();reconnected.countDown();return true;}
                if(json.matches("(?s).*\"id\"\\s*:\\s*9[,\\s].*")&&json.matches("(?s).*\"ok\"\\s*:\\s*true.*"))result.countDown();
                if(json.matches("(?s).*\"type\"\\s*:\\s*\"state\".*"))state.countDown();
                if(json.matches("(?s).*\"id\"\\s*:\\s*11[,\\s].*"))pong.countDown();return false;
            }
            public void disconnected(){}
        });
        try {
            link.start();check(ready.await(4,TimeUnit.SECONDS),"native Java TLS handshake and authenticated ready");
            for(int i=0;i<100&&!link.connected();i++)Thread.sleep(5);
            check(link.send("{\"id\":9,\"path\":\"/api/action\",\"body\":{\"id\":\""+action+"\"}}"),"native Java sends a configured action");
            check(result.await(3,TimeUnit.SECONDS),"native Java receives action acknowledgement");
            check(state.await(3,TimeUnit.SECONDS),"native Java receives pushed state");
            java.lang.reflect.Field gateField=FastLink.class.getDeclaredField("writeGate");gateField.setAccessible(true);
            AtomicBoolean expired=new AtomicBoolean(false),rejected=new AtomicBoolean(false);CountDownLatch queued=new CountDownLatch(1);
            Thread delayedSend=new Thread(()->{queued.countDown();try{link.send("{\"id\":99,\"path\":\"/api/action\",\"body\":{\"id\":\""+action+"\"}}",()->{if(expired.get())throw new IllegalStateException("Expired fixture request");});}catch(IllegalStateException expected){rejected.set(true);}catch(Exception ex){throw new RuntimeException(ex);}});
            synchronized(gateField.get(link)){delayedSend.start();check(queued.await(1,TimeUnit.SECONDS),"delayed-send fixture starts");for(int i=0;i<100&&delayedSend.getState()!=Thread.State.BLOCKED;i++)Thread.sleep(5);check(delayedSend.getState()==Thread.State.BLOCKED,"queued fast send waits for the transport write gate");expired.set(true);}
            delayedSend.join(1000);check(!delayedSend.isAlive()&&rejected.get(),"fast send rechecks expiry after acquiring the gate and does not write a stale action");
            link.pause();check(!link.connected(),"native pause closes the fast link");
            for(int i=0;i<100&&!link.statistics().contains("\"disconnects\":1");i++)Thread.sleep(5);
            check(link.statistics().contains("\"disconnects\":1"),"pause/reset counts the established disconnect exactly once after ready is cleared");
            check(!link.send("{\"id\":10,\"path\":\"/api/ping\",\"body\":{}}"),"native disconnected send is not queued");
            link.resume();check(reconnected.await(4,TimeUnit.SECONDS),"native resume reconnects without replaying controls");
            for(int i=0;i<100&&!link.connected();i++)Thread.sleep(5);
            check(link.send("{\"id\":11,\"path\":\"/api/ping\",\"body\":{}}")&&pong.await(3,TimeUnit.SECONDS),"native reconnected link exchanges new requests");
            check(link.statistics().contains("\"connects\":2")&&link.statistics().contains("\"disconnects\":1"),"reconnection preserves accurate established-session counters");
        }finally{link.close();}
        AtomicBoolean accepted=new AtomicBoolean(false);
        FastLink wrong=new FastLink(new FastLink.Listener(){public FastLink.Settings settings(){return new FastLink.Settings("127.0.0.1",port,token,new String(new char[64]).replace('\0','0'),context.getSocketFactory());}public boolean packet(String json){accepted.set(true);return true;}public void disconnected(){}});
        try{wrong.start();Thread.sleep(1200);check(!accepted.get()&&!wrong.connected()&&!wrong.send("{}"),"native extra certificate-pin check rejects mismatch");}finally{wrong.close();}
        System.out.println(passed+" native Java fast-link checks passed; no Android app or GUI opened.");
    }
}
