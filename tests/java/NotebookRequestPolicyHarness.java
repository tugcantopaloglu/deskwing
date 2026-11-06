package app.tabletdeck;

import java.util.Arrays;

public final class NotebookRequestPolicyHarness {
    private static int passed;
    private static void check(boolean value,String label){if(!value)throw new AssertionError(label);passed++;}
    private static String repeated(char character,int count){char[] chars=new char[count];Arrays.fill(chars,character);return new String(chars);}
    private static void reject(String path,String method,String message,String body,String label){
        try{BridgeRequestLimits.validate(path,method,message,body);throw new AssertionError(label);}
        catch(IllegalArgumentException expected){check(true,label);}
    }
    private static void rejectContext(String expected,String current,String label){
        int[] httpUploads={0};
        try{
            NotebookRequestPolicy.requireContext(expected,current);
            httpUploads[0]++;
            throw new AssertionError(label);
        }catch(IllegalStateException rejected){check(httpUploads[0]==0,label);}
    }
    private static void attemptedUpload(String method,String path,String expected,String current,int[] uploads){
        if(NotebookRequestPolicy.requiresContext(method,path))NotebookRequestPolicy.requireContext(expected,current);
        uploads[0]++;
    }
    public static void main(String[] args){
        String page="/api/notebooks/"+repeated('a',32)+"/pages/"+repeated('B',32),revision=repeated('f',64);
        for(String path:new String[]{"/api/notebooks",page,page+"?revision="+revision,page+"/history"}){
            check(NotebookRequestPolicy.allowed("GET",path),"Canonical GET "+path);
            check(NotebookRequestPolicy.requestLimit("GET",path)==16*1024,"GET keeps small request envelope");
            check(NotebookRequestPolicy.responseLimit("GET",path)==8*1024*1024,"GET response fits image plus ink");
        }
        for(String path:new String[]{"/api/notebooks",page,page+"/history/restore"}){
            check(NotebookRequestPolicy.allowed("POST",path),"Canonical POST "+path);
            check(!NotebookRequestPolicy.allowed("DELETE",path),"DELETE is not admitted");
            check(!NotebookRequestPolicy.allowed("post",path),"Method is case-sensitive");
        }
        check(!NotebookRequestPolicy.allowed("POST",page+"?revision="+revision),"Revision poll is read-only");
        check(!NotebookRequestPolicy.allowed("POST",page+"/history"),"History listing is read-only");
        check(!NotebookRequestPolicy.allowed("GET",page+"/history/restore"),"Restore is write-only");
        String[] denied={
            "/api/notebooks/","/api/notebooks?revision="+revision,"/api/notebooks?x=1",
            page+"/",page+"?revision=",page+"?revision="+repeated('a',63),page+"?revision="+repeated('a',65),
            page+"?revision="+repeated('g',64),page+"?Revision="+revision,page+"?revision="+revision+"&x=1",
            page+"?revision="+revision+"&revision="+revision,page+"?revision="+revision+"#fragment",
            page+"?revision=%41"+repeated('a',63),page+"?revision="+revision+"?",page+"/history?revision="+revision,
            page+"/history/restore?x=1",page+"/history/restore/",page+"/history/restore/extra",
            "/api/notebooks/"+repeated('a',31)+"/pages/"+repeated('a',32),
            "/api/notebooks/"+repeated('a',33)+"/pages/"+repeated('a',32),
            "/api/notebooks/"+repeated('z',32)+"/pages/"+repeated('a',32),
            "/api/notebooks/"+repeated('a',32)+"/pages/"+repeated('a',31),
            "/api/notebooks/"+repeated('a',32)+"/pages/"+repeated('a',33),
            page.replace("/pages/","//pages/"),page.replace("/pages/","/../pages/"),page.replace("/pages/","/%70ages/"),
            page+"#x",page+"\n",page+"\r",page+" ",page.replace("/","\\"),
            "https://app.tabletdeck.local"+page,"/api/admin/config","/api/computer/display/input",null
        };
        for(String path:denied){
            for(String method:new String[]{"GET","POST"}){
                check(!NotebookRequestPolicy.allowed(method,path),"Reject route variant");
                check(NotebookRequestPolicy.requestLimit(method,path)==16*1024,"Rejected route cannot borrow write size");
                check(NotebookRequestPolicy.responseLimit(method,path)==16*1024,"Rejected route has no expanded response");
            }
        }
        check(!NotebookRequestPolicy.allowed(null,page),"Null method refused");
        int maximum=4*1024*1024;String body=repeated('x',maximum);
        for(String path:new String[]{"/api/notebooks",page}){
            check(NotebookRequestPolicy.requestLimit("POST",path)==maximum,"Notebook write body cap");
            BridgeRequestLimits.validate(path,"POST",body+repeated(' ',2048),body);check(true,"Exact body plus bounded envelope accepted");
            reject(path,"POST",body+"x",body+"x","One byte body overflow rejected");
            reject(path,"POST",body+repeated(' ',2049),body,"Envelope overhead overflow rejected");
            reject(path,"POST","{}",null,"Missing serialized body rejected");
            reject(path,"GET",repeated('x',16385),"{}","Read cannot borrow expanded write envelope");
            String multibyte=repeated('\u7b46',maximum/3+1);
            reject(path,"POST",multibyte,multibyte,"UTF-8 body overflow rejected");
        }
        check(NotebookRequestPolicy.requestLimit("POST",page+"/history/restore")==16384,"Restore stays a control request");
        reject(page+"/history/restore","POST",repeated('x',16385),"{}","Restore control envelope overflow rejected");
        reject("/local/notebook/export-pdf/write","POST",repeated('x',16385),"{}","File chunks retain control limit");
        reject(page+"?revision="+revision,"POST",repeated('x',16385),"{}","Invalid queried write retains control limit");
        String pcA="11111111-2222-4333-8444-555555555555",pcB="aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        NotebookRequestPolicy.requireContext(pcA,pcA);check(true,"Original captured PC context is accepted");
        rejectContext(pcA,pcB,"PC changed before JavaScript event: old notebook never reaches HTTP upload");
        rejectContext(pcA,null,"Forget invalidates original notebook before upload");
        rejectContext(null,pcA,"Missing captured context cannot bind to the latest PC");
        rejectContext("",pcA,"Empty legacy context is not silently rebound");
        rejectContext(pcA,"","An unpaired PC cannot receive notebook content");
        rejectContext("1-2-3-4-5",pcA,"Noncanonical UUID shorthand is rejected");
        rejectContext(pcA+" ",pcA,"Whitespace is not trimmed into a matching context");
        rejectContext(pcA+"\n",pcA,"Context framing characters are rejected");
        rejectContext(pcB.toUpperCase(),pcB,"Context equality is exact");
        NotebookRequestPolicy.requireContext(pcA,pcA);
        rejectContext(pcA,pcB,"Context is checked again immediately before HTTP commit");
        for(String path:new String[]{"/api/ink","/api/ink?revision="+revision}){
            check(NotebookRequestPolicy.requiresContext("GET",path),"Legacy scratchpad reads retain the selected PC");
            int[] uploads={0};
            try{attemptedUpload("GET",path,pcA,pcB,uploads);throw new AssertionError("Stale legacy ink read reached the new PC");}
            catch(IllegalStateException expected){check(uploads[0]==0,"Stale legacy ink read sends no HTTP request");}
            reject(path,"GET",repeated('x',16385),"{}","Legacy scratchpad GET keeps 16 KiB request envelope");
        }
        check(NotebookRequestPolicy.requiresContext("POST","/api/ink"),"Legacy scratchpad writes require original PC context");
        int[] legacyUploads={0};
        try{attemptedUpload("POST","/api/ink",pcA,pcB,legacyUploads);throw new AssertionError("Old scratchpad leaked to replacement PC");}
        catch(IllegalStateException expected){check(legacyUploads[0]==0,"Replacement PC receives zero old scratchpad uploads before CAS");}
        int[] originalUploads={0};attemptedUpload("POST","/api/ink",pcA,pcA,originalUploads);
        check(originalUploads[0]==1,"Original scratchpad can still save to its intended PC");
        check(!NotebookRequestPolicy.requiresContext("POST","/api/action"),"Ordinary controls keep their existing protocol");
        check(!NotebookRequestPolicy.requiresContext("GET","/api/ink?extra=1"),"Malformed ink poll is not admitted as private content");
        check(!NotebookRequestPolicy.allowed("POST","/api/ink"),"Context predicate does not expand Notebook route authorization");
        System.out.println(passed+" notebook route and byte-boundary checks passed; no network or app operations.");
    }
}
