package app.tabletdeck;

import java.util.Base64;

/** Bounded sequential bytes for a user-selected PDF export. No paths or platform APIs. */
final class NotebookPdfTransfer {
    static final int CHUNK_BYTES=8192,MAX_PDF_BYTES=32*1024*1024;
    private final int expected;
    private final byte[] prefix=new byte[5];
    private int received;
    NotebookPdfTransfer(int expected){if(expected<5||expected>MAX_PDF_BYTES)throw new IllegalArgumentException("Export a PDF between 5 bytes and 32 MB.");this.expected=expected;}
    synchronized byte[] accept(int offset,String encoded){
        if(offset!=received||encoded==null||encoded.length()<4||encoded.length()>((CHUNK_BYTES+2)/3)*4)throw new IllegalArgumentException("Write the next PDF chunk in order.");
        final byte[] bytes;
        try{bytes=Base64.getDecoder().decode(encoded);}catch(IllegalArgumentException ex){throw new IllegalArgumentException("The PDF chunk is not valid base64.");}
        if(bytes.length<1||bytes.length>CHUNK_BYTES||bytes.length>expected-received)throw new IllegalArgumentException("The PDF chunk exceeds the export size.");
        for(int index=0;index<bytes.length&&received+index<prefix.length;index++)prefix[received+index]=bytes[index];
        received+=bytes.length;return bytes;
    }
    synchronized int received(){return received;}
    synchronized void finish(){
        if(received!=expected||prefix[0]!='%'||prefix[1]!='P'||prefix[2]!='D'||prefix[3]!='F'||prefix[4]!='-')throw new IllegalArgumentException("The PDF export is incomplete or invalid.");
    }
}
