import app.tabletdeck.FastLink;
import app.tabletdeck.PinnedTrustManager;
import javax.net.ssl.*;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.io.InputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.Socket;
import java.net.SocketAddress;
import java.net.InetAddress;

public final class FastLinkHarness {
    private static int passed;
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);passed++;System.out.println("PASS "+label);}
    public static void main(String[] args) throws Exception {
        final String pin=System.getenv("DECK_TEST_PIN"),token=System.getenv("DECK_TEST_TOKEN"),action=System.getenv("DECK_TEST_ACTION");
        final int port=Integer.parseInt(System.getenv("DECK_TEST_PORT"));
        SSLContext context=SSLContext.getInstance("TLS");
        context.init(null,new TrustManager[]{new PinnedTrustManager(pin,null)},null);
        final X509Certificate fixturePeer;
        try(SSLSocket probe=(SSLSocket)context.getSocketFactory().createSocket("127.0.0.1",port)){
            probe.setSoTimeout(3000);probe.startHandshake();
            X509Certificate peer=(X509Certificate)probe.getSession().getPeerCertificates()[0];
            fixturePeer=peer;
            final String[] found={null};new PinnedTrustManager(pin.substring(0,24),fingerprint->found[0]=fingerprint).checkServerTrusted(new X509Certificate[]{peer},"RSA");
            check(pin.equals(found[0]),"pairing code verifies the full self-signed PC certificate through platform trust validation");
            try{new PinnedTrustManager(new String(new char[64]).replace('\0','0'),value->{throw new AssertionError("Wrong pin callback");}).checkServerTrusted(new X509Certificate[]{peer},"RSA");throw new AssertionError("Wrong pin accepted");}catch(java.security.cert.CertificateException expected){check(true,"wrong certificate pin is rejected before trust-anchor creation");}
            try{new PinnedTrustManager(pin,null).checkServerTrusted(new X509Certificate[0],"RSA");throw new AssertionError("Empty chain accepted");}catch(java.security.cert.CertificateException expected){check(true,"empty certificate chain is rejected");}
        }
        cleanupChecks(fixturePeer,pin,token);
        stalePacketChecks(fixturePeer,pin,token);
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
    private static void cleanupChecks(X509Certificate certificate,String pin,String token) throws Exception {
        final Thread caller=Thread.currentThread();final BlockingSocket first=new BlockingSocket(certificate,caller,true),second=new BlockingSocket(certificate,caller,false);
        final AtomicInteger created=new AtomicInteger(),settingsRead=new AtomicInteger();final CountDownLatch ready=new CountDownLatch(2);
        final AtomicLong firstGeneration=new AtomicLong(),secondGeneration=new AtomicLong(),closedGeneration=new AtomicLong();final java.util.concurrent.ConcurrentLinkedQueue<Object> contexts=new java.util.concurrent.ConcurrentLinkedQueue<>();
        SSLSocketFactory factory=new SSLSocketFactory(){
            public String[] getDefaultCipherSuites(){return new String[0];}public String[] getSupportedCipherSuites(){return new String[0];}
            @Override public Socket createSocket() throws IOException {int count=created.incrementAndGet();if(count==1)return first;if(count==2)return second;throw new IOException("No additional fixture sockets.");}
            public Socket createSocket(Socket socket,String host,int port,boolean close) throws IOException {throw new IOException("Unused fixture overload.");}
            public Socket createSocket(String host,int port) throws IOException {throw new IOException("Unused fixture overload.");}
            public Socket createSocket(String host,int port,InetAddress local,int localPort) throws IOException {throw new IOException("Unused fixture overload.");}
            public Socket createSocket(InetAddress host,int port) throws IOException {throw new IOException("Unused fixture overload.");}
            public Socket createSocket(InetAddress host,int port,InetAddress local,int localPort) throws IOException {throw new IOException("Unused fixture overload.");}
        };
        FastLink link=new FastLink(new FastLink.Listener(){
            public FastLink.Settings settings(){return new FastLink.Settings("127.0.0.1",1,token,pin,factory,"scope-"+settingsRead.incrementAndGet());}
            public boolean packet(String json,FastLink.Settings settings,long generation){contexts.add(settings.context);if(!firstGeneration.compareAndSet(0,generation))secondGeneration.set(generation);ready.countDown();return true;}
            public void disconnected(FastLink.Settings settings,long generation){closedGeneration.set(generation);}
        });
        try{
            link.start();for(int count=0;count<200&&!link.connected();count++)Thread.sleep(5);
            check(link.connected(),"cleanup fixture reaches an authenticated ready state");
            check(firstGeneration.get()>0&&link.isCurrentGeneration(firstGeneration.get())&&"scope-1".equals(contexts.poll()),"packet callback retains the originating settings scope and unique connection generation");
            long began=System.nanoTime();link.reset();long elapsed=System.nanoTime()-began;
            check(elapsed<TimeUnit.MILLISECONDS.toNanos(250)&&!link.connected(),"reset immediately invalidates readiness without waiting for TLS close");
            check(!link.isCurrentGeneration(firstGeneration.get()),"reset immediately invalidates a previously queued state callback generation");
            check(first.closeStarted.await(1,TimeUnit.SECONDS)&&!first.closedOnCaller.get(),"TLS close runs on a background cleanup thread");
            check(first.closed.getCount()==1,"fixture holds the TLS close in progress");
            began=System.nanoTime();for(int count=0;count<1000;count++)link.reset();link.pause();elapsed=System.nanoTime()-began;
            check(elapsed<TimeUnit.MILLISECONDS.toNanos(250),"repeated reset and pause return while TLS close is blocked");
            check(first.closeCalls.get()==1&&created.get()==1,"repeated reset deduplicates one exact close and cannot open replacement sockets early");
            check(!link.send("{}"),"reset commands are rejected instead of queued for the next connection");
            first.allowClose.countDown();check(first.closed.await(1,TimeUnit.SECONDS),"background cleanup physically closes the old socket");
            link.resume();check(ready.await(3,TimeUnit.SECONDS),"replacement connection starts after the old socket closes");
            for(int count=0;count<200&&!link.connected();count++)Thread.sleep(5);
            check(link.connected()&&second.closeCalls.get()==0&&first.closeCalls.get()==1,"old cleanup cannot close the new connection");
            check(secondGeneration.get()>firstGeneration.get()&&link.isCurrentGeneration(secondGeneration.get())&&"scope-2".equals(contexts.poll()),"replacement connection carries its own settings scope and a newer generation");
            check(closedGeneration.get()==firstGeneration.get()&&!link.isCurrentGeneration(closedGeneration.get()),"queued old-disconnect callback cannot invalidate the replacement connection");
            int bytesBefore=second.sent.size();boolean invalidated=false;
            try{link.send("{}",link::reset);}catch(IOException expected){invalidated=true;}
            check(invalidated&&second.sent.size()==bytesBefore,"reset during beforeCommit rejects the request without writing to the invalidated socket");
            began=System.nanoTime();link.close();elapsed=System.nanoTime()-began;
            check(elapsed<TimeUnit.MILLISECONDS.toNanos(250)&&!link.connected(),"close invalidates immediately without caller-thread TLS I/O");
            check(!link.isCurrentGeneration(secondGeneration.get()),"close invalidates queued callbacks from the final connection");
            check(second.closed.await(1,TimeUnit.SECONDS)&&!second.closedOnCaller.get()&&second.closeCalls.get()==1,"final exact socket is closed once on the cleanup worker");
            java.lang.reflect.Field cleanupField=FastLink.class.getDeclaredField("cleanup");cleanupField.setAccessible(true);
            java.util.concurrent.ThreadPoolExecutor cleanup=(java.util.concurrent.ThreadPoolExecutor)cleanupField.get(link);
            check(cleanup.awaitTermination(3,TimeUnit.SECONDS),"cleanup worker terminates after transport shutdown");
            check(cleanup.getLargestPoolSize()==1&&cleanup.getTaskCount()==2,"cleanup uses one worker and one task per socket, without a reset backlog");
        }finally{first.allowClose.countDown();second.allowClose.countDown();link.close();}
    }
    private static void stalePacketChecks(X509Certificate certificate,String pin,String token) throws Exception {
        final Thread caller=Thread.currentThread();final BlockingSocket socket=new BlockingSocket(certificate,caller,true,true);
        final AtomicInteger packets=new AtomicInteger();final CountDownLatch disconnected=new CountDownLatch(1);final Object scope=new Object();
        SSLSocketFactory factory=new SingleSocketFactory(socket);
        FastLink link=new FastLink(new FastLink.Listener(){
            public FastLink.Settings settings(){return new FastLink.Settings("127.0.0.1",1,token,pin,factory,scope);}
            public boolean packet(String json,FastLink.Settings settings,long generation){packets.incrementAndGet();return true;}
            public void disconnected(FastLink.Settings settings,long generation){if(settings.context==scope)disconnected.countDown();}
        });
        try{
            link.start();check(socket.packetStarted.await(1,TimeUnit.SECONDS),"stale-packet fixture holds a response inside its blocking read");
            link.reset();check(socket.closeStarted.await(1,TimeUnit.SECONDS),"reset begins exact-socket cleanup while the old read is pending");
            socket.allowPacket.countDown();
            check(socket.packetFinished.await(1,TimeUnit.SECONDS),"old response finishes reading only after its socket generation was invalidated");
            java.lang.reflect.Field workerField=FastLink.class.getDeclaredField("worker");workerField.setAccessible(true);Thread worker=(Thread)workerField.get(link);
            for(int count=0;count<200&&worker.getState()!=Thread.State.WAITING;count++)Thread.sleep(5);
            check(worker.getState()==Thread.State.WAITING&&packets.get()==0&&!link.connected(),"packet returned by an invalidated socket never reaches the listener or restores readiness");
            link.close();socket.allowClose.countDown();check(disconnected.await(1,TimeUnit.SECONDS),"cleanup notification retains the original scope even after reset and close");
            worker.join(1000);check(!worker.isAlive()&&packets.get()==0,"blocked-read reset fixture shuts down without delivering stale packets or replaying controls");
        }finally{socket.allowPacket.countDown();socket.allowClose.countDown();link.close();}
        AtomicInteger idleDisconnects=new AtomicInteger();FastLink idle=new FastLink(new FastLink.Listener(){public FastLink.Settings settings(){return null;}public void disconnected(){idleDisconnects.incrementAndGet();}});
        idle.start();idle.pause();idle.resume();idle.close();
        java.lang.reflect.Field workerField=FastLink.class.getDeclaredField("worker");workerField.setAccessible(true);((Thread)workerField.get(idle)).join(1000);
        check(idleDisconnects.get()==0,"unpaired or paused retry cycles do not fabricate socket-disconnect callbacks");
    }
    private static final class SingleSocketFactory extends SSLSocketFactory {
        final SSLSocket socket;final AtomicBoolean given=new AtomicBoolean();SingleSocketFactory(SSLSocket socket){this.socket=socket;}
        public String[] getDefaultCipherSuites(){return new String[0];}public String[] getSupportedCipherSuites(){return new String[0];}
        @Override public Socket createSocket() throws IOException {if(!given.compareAndSet(false,true))throw new IOException("No replacement fixture socket.");return socket;}
        public Socket createSocket(Socket socket,String host,int port,boolean close) throws IOException {throw new IOException("Unused fixture overload.");}
        public Socket createSocket(String host,int port) throws IOException {throw new IOException("Unused fixture overload.");}
        public Socket createSocket(String host,int port,InetAddress local,int localPort) throws IOException {throw new IOException("Unused fixture overload.");}
        public Socket createSocket(InetAddress host,int port) throws IOException {throw new IOException("Unused fixture overload.");}
        public Socket createSocket(InetAddress host,int port,InetAddress local,int localPort) throws IOException {throw new IOException("Unused fixture overload.");}
    }
    /** Synthetic TLS socket: all I/O stays in memory and close can deliberately block. */
    private static final class BlockingSocket extends SSLSocket {
        final Thread caller;final X509Certificate certificate;final CountDownLatch allowClose,closeStarted=new CountDownLatch(1),closed=new CountDownLatch(1);
        final AtomicInteger closeCalls=new AtomicInteger();final AtomicBoolean closedOnCaller=new AtomicBoolean();final byte[] packet;final ByteArrayOutputStream sent=new ByteArrayOutputStream();final CountDownLatch packetStarted=new CountDownLatch(1),packetFinished=new CountDownLatch(1),allowPacket;
        BlockingSocket(X509Certificate certificate,Thread caller,boolean blocked) throws IOException {this(certificate,caller,blocked,false);}
        BlockingSocket(X509Certificate certificate,Thread caller,boolean blocked,boolean blockPacket) throws IOException {this.certificate=certificate;this.caller=caller;allowClose=new CountDownLatch(blocked?1:0);allowPacket=new CountDownLatch(blockPacket?1:0);ByteArrayOutputStream bytes=new ByteArrayOutputStream();DataOutputStream output=new DataOutputStream(bytes);byte[] json="{\"type\":\"ready\"}".getBytes(java.nio.charset.StandardCharsets.UTF_8);output.writeInt(json.length);output.write(json);packet=bytes.toByteArray();}
        @Override public void connect(SocketAddress address,int timeout){}@Override public void setTcpNoDelay(boolean value){}@Override public void setKeepAlive(boolean value){}@Override public void setSoTimeout(int value){}
        @Override public void startHandshake(){}
        @Override public java.io.OutputStream getOutputStream(){return sent;}
        @Override public InputStream getInputStream(){return new InputStream(){int offset;public int read() throws IOException {try{packetStarted.countDown();allowPacket.await();if(offset<packet.length){int value=packet[offset++]&255;if(offset==packet.length)packetFinished.countDown();return value;}closed.await();return -1;}catch(InterruptedException ex){Thread.currentThread().interrupt();throw new IOException("Fixture read interrupted.",ex);}}};}
        @Override public void close() throws IOException {closeCalls.incrementAndGet();if(Thread.currentThread()==caller){closedOnCaller.set(true);throw new IOException("TLS close attempted on the caller thread.");}closeStarted.countDown();try{allowClose.await();}catch(InterruptedException ex){Thread.currentThread().interrupt();throw new IOException("Fixture close interrupted.",ex);}finally{closed.countDown();}}
        @Override public SSLSession getSession(){return new SSLSession(){public byte[] getId(){return new byte[0];}public SSLSessionContext getSessionContext(){return null;}public long getCreationTime(){return 0;}public long getLastAccessedTime(){return 0;}public void invalidate(){}public boolean isValid(){return true;}public void putValue(String name,Object value){}public Object getValue(String name){return null;}public void removeValue(String name){}public String[] getValueNames(){return new String[0];}public java.security.cert.Certificate[] getPeerCertificates(){return new java.security.cert.Certificate[]{certificate};}public java.security.cert.Certificate[] getLocalCertificates(){return null;}public javax.security.cert.X509Certificate[] getPeerCertificateChain(){return null;}public java.security.Principal getPeerPrincipal(){return certificate.getSubjectX500Principal();}public java.security.Principal getLocalPrincipal(){return null;}public String getCipherSuite(){return "TLS_AES_128_GCM_SHA256";}public String getProtocol(){return "TLSv1.3";}public String getPeerHost(){return "127.0.0.1";}public int getPeerPort(){return 1;}public int getPacketBufferSize(){return 16384;}public int getApplicationBufferSize(){return 16384;}};}
        public String[] getSupportedCipherSuites(){return new String[0];}public String[] getEnabledCipherSuites(){return new String[0];}public void setEnabledCipherSuites(String[] suites){}public String[] getSupportedProtocols(){return new String[0];}public String[] getEnabledProtocols(){return new String[0];}public void setEnabledProtocols(String[] protocols){}public void addHandshakeCompletedListener(HandshakeCompletedListener listener){}public void removeHandshakeCompletedListener(HandshakeCompletedListener listener){}public void setUseClientMode(boolean mode){}public boolean getUseClientMode(){return true;}public void setNeedClientAuth(boolean need){}public boolean getNeedClientAuth(){return false;}public void setWantClientAuth(boolean want){}public boolean getWantClientAuth(){return false;}public void setEnableSessionCreation(boolean flag){}public boolean getEnableSessionCreation(){return true;}
    }
}
