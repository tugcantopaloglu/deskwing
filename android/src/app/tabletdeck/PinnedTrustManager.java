package app.tabletdeck;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Locale;
import java.util.function.Consumer;
import javax.net.ssl.TrustManager;
import javax.net.ssl.TrustManagerFactory;
import javax.net.ssl.X509TrustManager;

/** Trusts only the explicitly paired PC certificate, then applies platform TLS validation. */
public final class PinnedTrustManager implements X509TrustManager {
    private final String expected;
    private final Consumer<String> verified;
    private X509TrustManager platform;
    private String platformFingerprint="";

    public PinnedTrustManager(String expected,Consumer<String> verified){
        if(expected==null||!expected.matches("[A-F0-9]{24}|[A-F0-9]{64}"))throw new IllegalArgumentException("Enter the PC certificate code before connecting.");
        this.expected=expected;this.verified=verified;
    }

    @Override public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
    @Override public void checkClientTrusted(X509Certificate[] chain,String auth) throws CertificateException {throw new CertificateException("Client certificates are not supported.");}
    @Override public void checkServerTrusted(X509Certificate[] chain,String auth) throws CertificateException {
        try {
            if(chain==null||chain.length==0||chain.length>8||chain[0]==null)throw new CertificateException("No valid PC certificate chain.");
            if(auth==null||auth.isEmpty())throw new CertificateException("No TLS authentication type.");
            chain[0].checkValidity();
            String found=fingerprint(chain[0]);
            if(!MessageDigest.isEqual(found.substring(0,expected.length()).getBytes(StandardCharsets.US_ASCII),expected.getBytes(StandardCharsets.US_ASCII)))throw new CertificateException("PC certificate code does not match.");
            // The user-verified fingerprint establishes the local trust anchor. Never add
            // a certificate to the keystore before this comparison, or trust the peer's other CAs.
            X509TrustManager anchored=platformFor(chain[0],found);
            anchored.checkServerTrusted(chain,auth);
            if(verified!=null)verified.accept(found);
        }catch(CertificateException ex){throw ex;}catch(Exception ex){throw new CertificateException("Cannot verify this PC.",ex);}
    }

    private synchronized X509TrustManager platformFor(X509Certificate certificate,String fingerprint) throws Exception {
        if(platform!=null&&platformFingerprint.equals(fingerprint))return platform;
        KeyStore trusted=KeyStore.getInstance(KeyStore.getDefaultType());trusted.load(null,null);
        trusted.setCertificateEntry("paired-pc",certificate);
        TrustManagerFactory factory=TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());factory.init(trusted);
        for(TrustManager manager:factory.getTrustManagers())if(manager instanceof X509TrustManager){platform=(X509TrustManager)manager;platformFingerprint=fingerprint;return platform;}
        throw new CertificateException("Platform certificate validation is unavailable.");
    }

    public static String fingerprint(X509Certificate certificate) throws Exception {
        byte[] digest=MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded());StringBuilder result=new StringBuilder();
        for(byte value:digest)result.append(String.format(Locale.ROOT,"%02X",value));return result.toString();
    }
}
