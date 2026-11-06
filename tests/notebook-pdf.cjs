'use strict';
// Real offline PDF rendering/export against isolated Chromium; native file pickers are mocked.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),web=path.join(root,'companion/wwwroot');
let checks=0,browser;
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
const served=[],external=[],errors=[],rejected=[];
async function main(){
 const manifest=JSON.parse(fs.readFileSync(path.join(root,'scripts/notebook-pdf-vendor.json'),'utf8').replace(/^\uFEFF/,''));
 const assets=new Map(),digest=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
 const remember=(pathname,body,contentType)=>{assert.ok(!assets.has(pathname)&&Buffer.isBuffer(body)&&body.length<=4*1024*1024,'bounded unique fixture asset');assets.set(pathname,Object.freeze({body,size:body.length,sha256:digest(body),contentType}));};
 let total=0;
 for(const entry of manifest.assets){
  assert.ok(typeof entry.path==='string'&&/^vendor\/(?:pdfjs|pdf-lib)\/[A-Za-z0-9_./-]+$/.test(entry.path)&&!entry.path.split('/').includes('..')&&Number.isInteger(entry.bytes)&&entry.bytes>=0&&entry.bytes<=4*1024*1024&&/^[a-f0-9]{64}$/.test(entry.sha256),'valid pinned asset descriptor');
  const file=path.resolve(web,entry.path);assert.ok(file.startsWith(web+path.sep)&&fs.statSync(file).isFile()&&fs.statSync(file).size===entry.bytes,'pinned asset bounds '+entry.path);
  const bytes=fs.readFileSync(file);assert.equal(digest(bytes),entry.sha256,'pinned asset hash '+entry.path);total+=bytes.length;assert.ok(total<=8*1024*1024,'bounded complete vendor payload');
  remember('/'+entry.path,bytes,/\.m?js$/.test(file)?'application/javascript':/\.ttf$/.test(file)?'font/ttf':'application/octet-stream');
 }
 check(manifest.assets.length>0,'all vendored PDF assets match their pinned hashes');
 const moduleFile=path.join(web,'notebook-pdf.js');assert.ok(fs.statSync(moduleFile).size<=128*1024,'bounded first-party PDF module');remember('/notebook-pdf.js',fs.readFileSync(moduleFile),'application/javascript');
 remember('/fixture',Buffer.from('<!doctype html><html><head><script src="/notebook-pdf.js" defer></script></head><body></body></html>'),'text/html');
 remember('/fixture-poisoned-script',Buffer.from('<!doctype html><html><head><script src="/fixture-poison.js"></script><script src="/notebook-pdf.js" defer></script></head><body></body></html>'),'text/html');
 remember('/fixture-poison.js',Buffer.from('Object.defineProperty(document,"currentScript",{configurable:true,get:function(){return{src:"https://invalid.example/injected-script.js"};}});'),'application/javascript');
 remember('/favicon.ico',Buffer.alloc(0),'image/x-icon');
 const origin='https://notebook-pdf.invalid';
 browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});const context=await browser.newContext({serviceWorkers:'block'});
 await context.route('**/*',async route=>{
  const request=route.request();let url;try{url=new URL(request.url());}catch{rejected.push('invalid URL');return route.abort('blockedbyclient');}
  if(url.origin!==origin){external.push(request.url());return route.abort('blockedbyclient');}
  if(request.method()!=='GET'||request.postDataBuffer()!==null||url.username||url.password||url.search||url.hash||request.url()!==origin+url.pathname||request.headers().authorization||request.headers().cookie){rejected.push(request.method()+' '+request.url());return route.abort('blockedbyclient');}
  const asset=assets.get(url.pathname);if(!asset){rejected.push(request.method()+' '+request.url());return route.abort('blockedbyclient');}
  assert.ok(asset.body.length===asset.size&&digest(asset.body)===asset.sha256,'preloaded fixture asset remains immutable');served.push(url.pathname);
  return route.fulfill({status:200,headers:{'Content-Type':asset.contentType,'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Content-Security-Policy':"default-src 'none'; script-src 'self'; worker-src 'self'; connect-src 'self'; img-src data:; font-src 'self'; object-src 'none'; base-uri 'none'"},body:asset.body});
 });
 const page=await context.newPage();
 page.on('request',request=>{if(!request.url().startsWith(origin+'/')&&!request.url().startsWith('data:'))external.push(request.url());});page.on('pageerror',error=>errors.push(error.message));
 await page.goto(origin+'/fixture');await page.waitForFunction(()=>!!window.DeskNotebookPdf);
 check(!served.some(p=>p.startsWith('/vendor/')),'PDF dependencies load lazily');
 const denied=await page.evaluate(async()=>{let denied=0;for(const [url,options] of [['/fixture?extra=1',{}],['/fixture',{method:'POST',body:'unexpected body'}]])try{await fetch(url,options);}catch{denied++;}return denied;});check(denied===2,'fixture rejects queries and mutation requests without network fallback');
 const result=await page.evaluate(async()=>{
  const results=[],ok=(condition,label)=>{if(!condition)throw Error(label);results.push(label);};
  const empty=()=>({version:1,strokes:[],notes:'',penOnly:true,updatedAt:'2026-11-06T00:00:00.000Z'});
  const api=window.DeskNotebookPdf;
  ok(typeof window.PDFLib==='undefined','export loader is exercised with PDF-lib initially absent');
  const first=empty();first.strokes=[{tool:'pen',color:'#0000ff',width:40,points:[[.2,.5,1],[.8,.5,1]]},{tool:'eraser',color:'#000000',width:40,points:[[.5,.5,1]]}];
  const exported=await api.exportDocument({name:'Initial notebook',pages:[{width:600,height:400,document:first},{width:400,height:600,document:empty()}]});
  ok(exported.type==='application/pdf'&&exported.size>100,'creates a real multi-page PDF');
  const bytes=new Uint8Array(await exported.arrayBuffer());ok(new TextDecoder().decode(bytes.slice(0,5))==='%PDF-','PDF export has a valid header');
  const original=await PDFLib.PDFDocument.create();const one=original.addPage([300,500]);one.drawRectangle({x:20,y:20,width:100,height:100,color:PDFLib.rgb(1,0,0)});one.drawText('DeskWing PDF proof',{x:20,y:450,size:18});const two=original.addPage([600,300]);two.drawRectangle({x:20,y:20,width:100,height:100,color:PDFLib.rgb(0,1,0)});original.addJavaScript('untrusted-source','fetch("https://invalid.example/never")');
  const originalBytes=await original.save();const imported=await api.importFile(new File([originalBytes],'Proof.pdf',{type:'application/pdf'}));
  ok(imported.name==='Proof'&&imported.pages.length===2,'imports every page of a real PDF');
  ok(imported.pages[0].height>imported.pages[0].width&&imported.pages[1].width>imported.pages[1].height,'preserves each page orientation');
  ok(imported.pages.every(p=>p.width<=2048&&p.height<=2048&&api.validateBackground(p.backgroundPng).bytes.length<=1024*1024),'imported pages stay within PNG and pixel budgets');
  const image=new Image();image.src=imported.pages[0].backgroundPng;await image.decode();const canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;const context=canvas.getContext('2d');context.drawImage(image,0,0);const red=context.getImageData(Math.floor(image.width*.2),Math.floor(image.height*.87),1,1).data;
  ok(red[0]>200&&red[1]<80&&red[2]<80,'PDF renderer produces actual source pixels');
  const annotated=empty();annotated.strokes=[{tool:'pen',color:'#0000ff',width:40,points:[[.2,.5,1],[.8,.5,1]]}];
  const final=await api.exportDocument({name:'Annotated proof',pages:imported.pages.map((p,i)=>({...p,document:i===0?annotated:empty()}))});
  const reopened=await PDFLib.PDFDocument.load(await final.arrayBuffer());ok(reopened.getPageCount()===2,'exported annotation PDF reloads with both pages');
  ok(!reopened.catalog.get(PDFLib.PDFName.of('Names')),'flattened export drops source JavaScript and actions');
  const roundTrip=await api.importFile(new File([await final.arrayBuffer()],'Annotated.pdf'));const roundImage=new Image();roundImage.src=roundTrip.pages[0].backgroundPng;await roundImage.decode();canvas.width=roundImage.width;canvas.height=roundImage.height;context.drawImage(roundImage,0,0);const blue=context.getImageData(Math.floor(canvas.width*.35),Math.floor(canvas.height*.5),1,1).data;ok(blue[2]>180&&blue[0]<100,'annotations survive PDF export and reimport');
  const raster=await api.exportPNG({width:600,height:400,document:first});const erased=new Image();erased.src=raster;await erased.decode();canvas.width=erased.width;canvas.height=erased.height;context.drawImage(erased,0,0);const erasePixel=context.getImageData(canvas.width/2,canvas.height/2,1,1).data;ok(erasePixel[0]>240&&erasePixel[1]>240&&erasePixel[2]>240,'erasers remove ink without deleting paper');
  const written=empty();written.notes='İstanbul, ğış çÖü — writing notes\n'+('This deliberately long note keeps its words and line breaks. '.repeat(90));
  const withNotes=await api.exportDocument({name:'Writing proof',pages:[{width:600,height:400,document:written}]});const notePDF=await PDFLib.PDFDocument.load(await withNotes.arrayBuffer());ok(notePDF.getPageCount()>2,'long Unicode text notes receive their own paginated PDF pages');
  const noteImport=await api.importFile(new File([await withNotes.arrayBuffer()],'Notes.pdf'));const noteImage=new Image();noteImage.src=noteImport.pages[1].backgroundPng;await noteImage.decode();canvas.width=noteImage.width;canvas.height=noteImage.height;context.drawImage(noteImage,0,0);const notePixels=context.getImageData(0,0,canvas.width,canvas.height).data;let dark=0;for(let i=0;i<notePixels.length;i+=4)if(notePixels[i]<100&&notePixels[i+1]<100&&notePixels[i+2]<100)dark++;ok(dark>1000,'typed notes survive export as real rendered content');
  const explicitImages=await api.exportDocument({name:'Explicit image only',pages:[{document:written}]},{includeNotes:false});ok((await PDFLib.PDFDocument.load(await explicitImages.arrayBuffer())).getPageCount()===1,'notes are omitted only by explicit export choice');
  const reject=async(fn,label)=>{let failed=false;try{await fn();}catch{failed=true;}ok(failed,label);};
  await reject(()=>api.importFile(new File(['not a PDF'],'bad.pdf')),'rejects invalid PDF data');
  await reject(()=>api.importFile(new File([new Uint8Array(20*1024*1024+1)],'large.pdf')),'rejects oversized files before parsing');
  const many=await PDFLib.PDFDocument.create();for(let i=0;i<21;i++)many.addPage([100,100]);await reject(async()=>api.importFile(new File([await many.save()],'many.pdf')),'rejects excessive PDF page counts');
  const aborted=new AbortController();aborted.abort();await reject(()=>api.importFile(new File([originalBytes],'cancel.pdf'),{signal:aborted.signal}),'honors cancellation before import');
  const readingAbort=new AbortController();const reading=api.importFile(new File([originalBytes],'cancel-during-read.pdf'),{signal:readingAbort.signal});readingAbort.abort();await reject(()=>reading,'cancels an in-flight browser file read');
  await reject(()=>api.exportDocument({name:'Broken',pages:[{document:{version:1,strokes:[{tool:'pen',color:'#000000',width:2,points:[[Infinity,0,1]]}]}}]}),'rejects nonfinite annotation points');
  const tooManyNotes=empty();tooManyNotes.notes='\n'.repeat(7999);await reject(()=>api.exportDocument({name:'Over budget',pages:[{document:tooManyNotes}]}),'bounds the total PDF page count including text notes');
  const png=api.validateBackground(imported.pages[0].backgroundPng).bytes,id='0123456789abcdef0123456789abcdef',imageId='abcdef0123456789abcdef0123456789',calls=[];
  window.nativeRequest=async(path,body)=>{calls.push({path,body});if(path.endsWith('/import-pdf'))return{id,name:'Native.pdf',pageCount:1,nativeRaster:true};if(path.endsWith('/render-pdf'))return{imageId,width:imported.pages[0].width,height:imported.pages[0].height,size:png.length,mime:'image/png',sourcePage:1};if(path.endsWith('/read-pdf-image')){const chunk=png.slice(body.offset,body.offset+body.length);return{dataBase64:btoa(String.fromCharCode(...chunk)),eof:body.offset+chunk.length===png.length};}if(path.endsWith('/close-pdf'))return{closed:true};throw Error('Unexpected native call');};
  const native=await api.importNative();ok(native.pages[0].backgroundPng===imported.pages[0].backgroundPng,'native PDF import reassembles exact bounded chunks');ok(calls.at(-1).path.endsWith('/close-pdf'),'native import closes its file session');ok(calls.every(c=>JSON.stringify(c.body).length<=16384),'native import retains bridge request bounds');
  const nativeRequest=window.nativeRequest;window.nativeRequest=async(path,body)=>path.endsWith('/read-pdf-image')?{dataBase64:'',eof:true}:nativeRequest(path,body);await reject(()=>api.importNative(),'rejects an incomplete native page transfer');ok(calls.at(-1).path.endsWith('/close-pdf'),'failed native import still closes its file session');
  const nativeAbort=new AbortController();window.nativeRequest=async(path,body)=>{const result=await nativeRequest(path,body);if(path.endsWith('/read-pdf-image'))nativeAbort.abort();return result;};await reject(()=>api.importNative({signal:nativeAbort.signal}),'cancels native import even during its final chunk');ok(calls.at(-1).path.endsWith('/close-pdf'),'cancelled native import closes its file session');
  const chunks=[],outCalls=[];window.nativeRequest=async(path,body)=>{outCalls.push({path,body});if(path.endsWith('/start'))return{id};if(path.endsWith('/write')){ok(body.offset===chunks.reduce((n,c)=>n+c.length,0),'native export offsets stay sequential');chunks.push(Uint8Array.from(atob(body.dataBase64),c=>c.charCodeAt(0)));return{written:true};}if(path.endsWith('/finish'))return{saved:true,cancelled:false};if(path.endsWith('/cancel'))return{cancelled:true};throw Error('Unexpected export call');};
  const saved=await api.exportNative(final,'Proof');const restored=new Uint8Array(chunks.reduce((n,c)=>n+c.length,0));let at=0;for(const chunk of chunks){restored.set(chunk,at);at+=chunk.length;}ok(saved.saved===true&&saved.cancelled===false,'native export completes');ok(restored.length===final.size,'native export transfers exact byte count');ok(outCalls.every(c=>JSON.stringify(c.body).length<=16384),'native export retains 16 KiB bridge request bounds');
  const finalBytes=new Uint8Array(await final.arrayBuffer());ok(restored.every((v,i)=>v===finalBytes[i]),'native export preserves every PDF byte');
  const failed=[];window.nativeRequest=async(path)=>{failed.push(path);if(path.endsWith('/start'))return{id};if(path.endsWith('/write'))throw Error('fixture transfer failure');if(path.endsWith('/cancel'))return{cancelled:true};throw Error('Unexpected failure call');};await reject(()=>api.exportNative(final,'Fail'),'native export reports a failed chunk');ok(failed.at(-1).endsWith('/cancel')&&!failed.some(p=>p.endsWith('/finish')),'failed native export cancels without finishing');
  return results;
 });
 for(const label of result)check(true,label);
 const poisoned=await context.newPage();poisoned.on('request',request=>{if(!request.url().startsWith(origin+'/')&&!request.url().startsWith('data:'))external.push(request.url());});poisoned.on('pageerror',error=>errors.push(error.message));
 await poisoned.goto(origin+'/fixture-poisoned-script');await poisoned.waitForFunction(()=>!!window.DeskNotebookPdf);
 const poisonedResult=await poisoned.evaluate(async()=>{
  if(document.currentScript?.src!=='https://invalid.example/injected-script.js'||typeof window.PDFLib!=='undefined')return false;
  const blob=await DeskNotebookPdf.exportDocument({name:'Bounded loader',pages:[{document:{version:1,strokes:[],notes:'',penOnly:true,updatedAt:'2026-11-06T00:00:00.000Z'}}]});
  return blob.type==='application/pdf'&&blob.size>100;
 });
 check(poisonedResult,'literal PDF-lib import ignores a hostile document.currentScript URL');await poisoned.close();
 check(external.length===0,'all PDF operations remain offline');check(rejected.length===2,'only the two intentional admission probes were rejected');check(errors.length===0,'PDF operations produce no uncaught page errors');
 console.log(checks+' notebook PDF checks passed.');
}
main().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{await browser?.close();});
