'use strict';
// Local-only PDF import and flattened export. PDF objects, scripts and attachments are never re-emitted.
(function () {
  const LIMITS = Object.freeze({pages:20,exportPages:100,sourceBytes:20*1024*1024,pngBytes:1024*1024,totalPngBytes:20*1024*1024,exportBytes:32*1024*1024,side:2048,pixels:4000000,chunkBytes:8192});
  let pdfjsPromise, pdfLibPromise, busy = false;
  const fail = message => new Error(message);
  const checkAbort = signal => { if(signal?.aborted) throw fail('PDF operation cancelled.'); };
  const pause = () => new Promise(resolve=>setTimeout(resolve,0));
  const nativeCall = (path,body,timeout=15000) => {
    if(typeof nativeRequest!=='function')throw fail('Native PDF files are unavailable in this host.');
    return nativeRequest(path,body,'POST',timeout);
  };
  function safeName(value,fallback='Notebook') { const name=String(value||'').replace(/[\\/\u0000-\u001f\u007f]/g,' ').replace(/\.pdf$/i,'').trim().slice(0,80);return name||fallback; }
  function encodedBytes(value){return value.length/4*3-(value.endsWith('==')?2:value.endsWith('=')?1:0);}
  function decodeBase64(value,limit) {
    if(typeof value!=='string'||value.length%4||value.length>Math.ceil(limit/3)*4||!/^[A-Za-z0-9+/]*={0,2}$/.test(value))throw fail('The PDF file data is invalid or too large.');
    const decoded=atob(value);if(decoded.length>limit)throw fail('The PDF file data is too large.');
    const bytes=new Uint8Array(decoded.length);for(let i=0;i<decoded.length;i++)bytes[i]=decoded.charCodeAt(i);return bytes;
  }
  function encodeBase64(bytes){let value='';for(let i=0;i<bytes.length;i++)value+=String.fromCharCode(bytes[i]);return btoa(value);}
  function checkPng(value) {
    const prefix='data:image/png;base64,';
    if(typeof value!=='string'||!value.startsWith(prefix))throw fail('PDF pages require a PNG background.');
    const bytes=decodeBase64(value.slice(prefix.length),LIMITS.pngBytes),signature=[137,80,78,71,13,10,26,10];
    if(bytes.length<33||signature.some((v,i)=>bytes[i]!==v)||String.fromCharCode(...bytes.slice(12,16))!=='IHDR')throw fail('A PDF page background is invalid.');
    const view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),width=view.getUint32(16),height=view.getUint32(20);
    if(width<1||height<1||width>LIMITS.side||height>LIMITS.side||width*height>LIMITS.pixels)throw fail('PDF page images must be at most 2048 pixels per side and four million pixels.');
    return {bytes,width,height};
  }
  function progress(options,done,total){if(typeof options?.onProgress==='function')options.onProgress({done,total});}
  async function run(action){if(busy)throw fail('Finish or cancel the current PDF operation first.');busy=true;try{return await action();}finally{busy=false;}}
  async function readBlob(blob,signal){
    checkAbort(signal);
    return new Uint8Array(await new Promise((resolve,reject)=>{
      const reader=new FileReader(),cleanup=()=>signal?.removeEventListener('abort',cancel),cancel=()=>reader.abort();
      reader.onload=()=>{cleanup();resolve(reader.result);};reader.onerror=()=>{cleanup();reject(fail('The selected PDF could not be read.'));};reader.onabort=()=>{cleanup();reject(fail('PDF operation cancelled.'));};
      signal?.addEventListener('abort',cancel,{once:true});reader.readAsArrayBuffer(blob);
    }));
  }
  async function pdfjs() {
    if(!pdfjsPromise)pdfjsPromise=(async()=>{
      const library=await import('./vendor/pdfjs/pdf.mjs');
      library.GlobalWorkerOptions.workerSrc='/vendor/pdfjs/pdf.worker.mjs';
      // WKURLSchemeHandler origins do not consistently support Worker URLs. The distribution's
      // supported main-thread worker fallback keeps this path offline without allowing blob scripts.
      if(globalThis.DeckCapabilities?.platform==='ios')globalThis.pdfjsWorker=await import('./vendor/pdfjs/pdf.worker.mjs');
      return library;
    })().catch(()=>{pdfjsPromise=null;throw fail('This browser cannot import PDFs. Update Android System WebView, use a current PC browser, or use the tablet app’s native PDF picker.');});
    return pdfjsPromise;
  }
  async function pdfLib() {
    if(globalThis.PDFLib?.PDFDocument)return globalThis.PDFLib;
    if(!pdfLibPromise)pdfLibPromise=import('./vendor/pdf-lib/pdf-lib.min.js').then(()=>{if(!globalThis.PDFLib?.PDFDocument)throw fail('The local PDF exporter did not load.');return globalThis.PDFLib;}).catch(()=>{pdfLibPromise=null;throw fail('The local PDF exporter could not be loaded.');});
    return pdfLibPromise;
  }
  function scaleFor(width,height){if(!Number.isFinite(width)||!Number.isFinite(height)||width<=0||height<=0||width>100000||height>100000)throw fail('The PDF page has unsupported dimensions.');return Math.min(2,LIMITS.side/width,LIMITS.side/height,Math.sqrt(LIMITS.pixels/(width*height)));}
  async function compactPng(canvas,signal){
    let current=canvas;
    for(let attempt=0;attempt<6;attempt++){
      checkAbort(signal);const data=current.toDataURL('image/png'),size=encodedBytes(data.slice(22));
      if(size<=LIMITS.pngBytes){const checked=checkPng(data),result={backgroundPng:data,width:checked.width,height:checked.height};if(current!==canvas){current.width=0;current.height=0;}return result;}
      const factor=Math.min(.8,Math.sqrt(LIMITS.pngBytes/size)*.9),next=document.createElement('canvas');next.width=Math.max(1,Math.floor(current.width*factor));next.height=Math.max(1,Math.floor(current.height*factor));
      if(next.width<64||next.height<64)throw fail('This PDF page is too complex to import within the image limit.');
      const context=next.getContext('2d');if(!context)throw fail('Canvas rendering is unavailable.');context.drawImage(current,0,0,next.width,next.height);
      if(current!==canvas){current.width=0;current.height=0;}current=next;await pause();
    }
    if(current!==canvas){current.width=0;current.height=0;}throw fail('This PDF page is too large to import.');
  }
  async function importBytes(bytes,name,options){
    checkAbort(options.signal);
    if(bytes.length<8||bytes.length>LIMITS.sourceBytes||String.fromCharCode(...bytes.slice(0,5))!=='%PDF-')throw fail('Choose a PDF file no larger than 20 MiB.');
    const library=await pdfjs();checkAbort(options.signal);
    const task=library.getDocument({data:bytes,isEvalSupported:false,disableFontFace:true,useSystemFonts:false,useWasm:false,useWorkerFetch:false,enableXfa:false,stopAtErrors:true,maxImageSize:LIMITS.pixels,canvasMaxAreaInBytes:LIMITS.pixels*4,isOffscreenCanvasSupported:false,isImageDecoderSupported:false,disableWorkerRendering:true,disableAutoFetch:true,disableStream:true,cMapUrl:'/vendor/pdfjs/cmaps/',cMapPacked:true,standardFontDataUrl:'/vendor/pdfjs/standard_fonts/',wasmUrl:'/vendor/pdfjs/wasm/',verbosity:0});
    let password=false,renderTask,expired=false;
    task.onPassword=()=>{password=true;void task.destroy();};
    const cancel=()=>{renderTask?.cancel();void task.destroy();};
    options.signal?.addEventListener('abort',cancel,{once:true});
    const timer=setTimeout(()=>{expired=true;cancel();},45000);
    try{
      const document=await task.promise;checkAbort(options.signal);
      if(document.numPages<1||document.numPages>LIMITS.pages)throw fail('Import PDFs with 1–20 pages. Split larger documents first.');
      const pages=[];let totalBytes=0;
      for(let index=1;index<=document.numPages;index++){
        checkAbort(options.signal);if(expired)throw fail('PDF import timed out. Try a smaller document.');
        const page=await document.getPage(index),base=page.getViewport({scale:1}),viewport=page.getViewport({scale:scaleFor(base.width,base.height)}),canvas=window.document.createElement('canvas');
        canvas.width=Math.max(1,Math.floor(viewport.width));canvas.height=Math.max(1,Math.floor(viewport.height));
        try{
          const context=canvas.getContext('2d',{alpha:false});if(!context)throw fail('Canvas rendering is unavailable.');
          renderTask=page.render({canvasContext:context,viewport,background:'#ffffff',annotationMode:library.AnnotationMode.DISABLE});await renderTask.promise;renderTask=null;checkAbort(options.signal);
          const result=await compactPng(canvas,options.signal);totalBytes+=encodedBytes(result.backgroundPng.slice(22));if(totalBytes>LIMITS.totalPngBytes)throw fail('The imported PDF images exceed 20 MiB. Import fewer pages.');
          pages.push({...result,sourcePage:index});progress(options,index,document.numPages);
        }finally{canvas.width=0;canvas.height=0;page.cleanup();}
        await pause();
      }
      checkAbort(options.signal);return {name:safeName(name),pages,pageCount:pages.length};
    }catch(error){if(password)throw fail('Password-protected PDFs cannot be imported. Save an unlocked copy first.');if(expired)throw fail('PDF import timed out. Try a smaller document.');checkAbort(options.signal);throw fail(error?.message||'The PDF could not be rendered.');}
    finally{clearTimeout(timer);options.signal?.removeEventListener('abort',cancel);renderTask?.cancel();await task.destroy();}
  }
  async function importFile(file,options={}){return run(async()=>{if(!file||typeof file.size!=='number'||file.size<8||file.size>LIMITS.sourceBytes||typeof file.slice!=='function')throw fail('Choose a PDF file no larger than 20 MiB.');return importBytes(await readBlob(file,options.signal),file.name,options);});}
  async function importDataUrl(value,name,options={}){return run(async()=>{const prefix='data:application/pdf;base64,';if(typeof value!=='string'||!value.startsWith(prefix))throw fail('Choose a PDF file.');return importBytes(decodeBase64(value.slice(prefix.length),LIMITS.sourceBytes),name,options);});}
  function validId(value){return typeof value==='string'&&/^[A-Za-z0-9-]{16,100}$/.test(value);}
  async function importNative(options={}){return run(async()=>{
    let id;
    try{
      checkAbort(options.signal);const opened=await nativeCall('/local/notebook/import-pdf',{},0);if(opened?.cancelled)return {cancelled:true,pages:[]};
      id=opened?.id;if(!validId(id)||!Number.isInteger(opened.pageCount)||opened.pageCount<1||opened.pageCount>LIMITS.pages||opened.nativeRaster!==true)throw fail('The native PDF picker returned an unsupported document.');
      const pages=[];let totalBytes=0;
      for(let page=0;page<opened.pageCount;page++){
        checkAbort(options.signal);const image=await nativeCall('/local/notebook/render-pdf',{id,page});
        if(!validId(image?.imageId)||image.mime!=='image/png'||!Number.isInteger(image.size)||image.size<33||image.size>LIMITS.pngBytes)throw fail('The native PDF page is invalid or too large.');
        const bytes=new Uint8Array(image.size);let offset=0;
        while(offset<bytes.length){
          checkAbort(options.signal);const wanted=Math.min(LIMITS.chunkBytes,bytes.length-offset),part=await nativeCall('/local/notebook/read-pdf-image',{id:image.imageId,offset,length:wanted});
          const decoded=decodeBase64(part?.dataBase64,LIMITS.chunkBytes);if(!decoded.length||decoded.length>wanted||part.eof!==(offset+decoded.length===bytes.length))throw fail('The PDF page transfer was incomplete.');
          checkAbort(options.signal);bytes.set(decoded,offset);offset+=decoded.length;
        }
        const backgroundPng='data:image/png;base64,'+encodeBase64(bytes),checked=checkPng(backgroundPng);
        if(checked.width!==image.width||checked.height!==image.height||image.sourcePage!==page+1)throw fail('The PDF page dimensions changed during import.');
        totalBytes+=bytes.length;if(totalBytes>LIMITS.totalPngBytes)throw fail('The PDF images exceed 20 MiB.');
        pages.push({width:checked.width,height:checked.height,backgroundPng,sourcePage:page+1});progress(options,page+1,opened.pageCount);await pause();
      }
      checkAbort(options.signal);return {name:safeName(opened.name),pages,pageCount:pages.length};
    }finally{if(validId(id))try{await nativeCall('/local/notebook/close-pdf',{id},8000);}catch{}}
  });}
  function validateDocument(value){
    if(!value||value.version!==1||!Array.isArray(value.strokes)||value.strokes.length>1200||typeof value.notes!=='string'||value.notes.length>8000)throw fail('The notebook page is invalid.');
    let points=0;
    for(const stroke of value.strokes){
      if(!stroke||!['pen','eraser'].includes(stroke.tool)||!/^#[0-9a-f]{6}$/i.test(stroke.color)||!Number.isFinite(stroke.width)||stroke.width<1||stroke.width>40||!Array.isArray(stroke.points)||!stroke.points.length)throw fail('The notebook contains invalid ink.');
      points+=stroke.points.length;if(points>60000)throw fail('The notebook page has too many points.');
      for(const point of stroke.points)if(!Array.isArray(point)||point.length!==3||!point.every(v=>Number.isFinite(v)&&v>=0&&v<=1))throw fail('The notebook contains an invalid point.');
    }
    return value;
  }
  async function imageFor(data){checkPng(data);return new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>resolve(image);image.onerror=()=>reject(fail('A page background could not be decoded.'));image.src=data;});}
  function drawStroke(context,stroke,width,height){
    const size=pressure=>stroke.width*width/1600*(.3+pressure*1.4);context.save();context.globalCompositeOperation=stroke.tool==='eraser'?'destination-out':'source-over';context.strokeStyle=stroke.color;context.fillStyle=stroke.color;context.lineCap='round';context.lineJoin='round';
    const first=stroke.points[0];context.beginPath();context.arc(first[0]*width,first[1]*height,size(first[2])/2,0,Math.PI*2);context.fill();
    for(let i=1;i<stroke.points.length;i++){const previous=stroke.points[i-1],point=stroke.points[i];context.lineWidth=size((previous[2]+point[2])/2);context.beginPath();context.moveTo(previous[0]*width,previous[1]*height);context.lineTo(point[0]*width,point[1]*height);context.stroke();}context.restore();
  }
  async function rasterPage(page,signal){
    checkAbort(signal);validateDocument(page?.document);
    const width=page.width===undefined?1600:page.width,height=page.height===undefined?1000:page.height,scale=scaleFor(width,height),canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.floor(width*scale));canvas.height=Math.max(1,Math.floor(height*scale));
    const context=canvas.getContext('2d',{alpha:false});if(!context)throw fail('Canvas export is unavailable.');context.fillStyle='#ffffff';context.fillRect(0,0,canvas.width,canvas.height);
    if(page.backgroundPng){const background=await imageFor(page.backgroundPng);checkAbort(signal);context.drawImage(background,0,0,canvas.width,canvas.height);}
    const ink=document.createElement('canvas');ink.width=canvas.width;ink.height=canvas.height;
    try{const layer=ink.getContext('2d');if(!layer)throw fail('Canvas export is unavailable.');for(const stroke of page.document.strokes)drawStroke(layer,stroke,ink.width,ink.height);context.drawImage(ink,0,0);return {canvas,width,height};}
    finally{ink.width=0;ink.height=0;}
  }
  async function exportPNG(page,options={}){return run(async()=>{const raster=await rasterPage(page,options.signal);try{return raster.canvas.toDataURL('image/png');}finally{raster.canvas.width=0;raster.canvas.height=0;}});}
  function noteLines(text,context,maxWidth){
    const lines=[];
    for(const paragraph of text.replace(/\r\n?/g,'\n').replace(/\t/g,'    ').split('\n')){
      if(!paragraph.length){lines.push('');continue;}
      let line='';for(const character of Array.from(paragraph)){if(line&&context.measureText(line+character).width>maxWidth){lines.push(line);line=character;}else line+=character;}lines.push(line);
    }
    return lines;
  }
  async function exportDocument(notebook,options={}){return run(async()=>{
    if(!notebook||!Array.isArray(notebook.pages)||!notebook.pages.length||notebook.pages.length>LIMITS.pages)throw fail('Export notebooks with 1–20 pages.');
    const library=await pdfLib(),output=await library.PDFDocument.create();output.setTitle(safeName(notebook.name));output.setProducer('DeskWing — flattened notebook export');
    let totalPng=0,totalPages=0;
    const append=async(raster)=>{
      if(++totalPages>LIMITS.exportPages)throw fail('The PDF exceeds 100 pages including text notes. Export fewer notebook pages.');
      const data=raster.canvas.toDataURL('image/png'),bytes=decodeBase64(data.slice(22),8*1024*1024);totalPng+=bytes.length;if(totalPng>LIMITS.exportBytes)throw fail('The PDF export is too large. Export fewer pages.');
      const image=await output.embedPng(bytes),page=output.addPage([raster.width,raster.height]);page.drawImage(image,{x:0,y:0,width:raster.width,height:raster.height});
      // Commit this image now so PDF-lib can release its decoded RGB/alpha buffers between pages.
      await image.embed();
    };
    for(let index=0;index<notebook.pages.length;index++){
      checkAbort(options.signal);const raster=await rasterPage(notebook.pages[index],options.signal);
      try{await append(raster);}finally{raster.canvas.width=0;raster.canvas.height=0;}
      const notes=notebook.pages[index].document.notes;
      if(options.includeNotes!==false&&notes.length){
        const canvas=document.createElement('canvas');canvas.width=1200;canvas.height=1600;const context=canvas.getContext('2d',{alpha:false});if(!context)throw fail('Text-note export is unavailable.');
        try{
          context.font='34px Manrope, sans-serif';const lines=noteLines(notes,context,1008),perPage=27,pages=Math.ceil(lines.length/perPage);
          if(totalPages+pages>LIMITS.exportPages)throw fail('The PDF exceeds 100 pages including text notes. Export fewer notebook pages.');
          for(let part=0;part<pages;part++){
            checkAbort(options.signal);context.fillStyle='#ffffff';context.fillRect(0,0,canvas.width,canvas.height);context.fillStyle='#53606a';context.font='24px Manrope, sans-serif';context.fillText('Page '+(index+1)+' · text notes'+(pages>1?' · '+(part+1)+'/'+pages:''),96,80);
            context.fillStyle='#1b2530';context.font='34px Manrope, sans-serif';for(let row=0;row<perPage&&part*perPage+row<lines.length;row++)context.fillText(lines[part*perPage+row],96,160+row*48);
            await append({canvas,width:600,height:800});await pause();
          }
        }finally{canvas.width=0;canvas.height=0;}
      }
      progress(options,index+1,notebook.pages.length);await pause();
    }
    checkAbort(options.signal);const bytes=await output.save({useObjectStreams:true});checkAbort(options.signal);if(bytes.length>LIMITS.exportBytes)throw fail('The PDF export exceeds 32 MiB. Export fewer pages.');return new Blob([bytes],{type:'application/pdf'});
  });}
  async function exportNative(blob,name,options={}){return run(async()=>{
    let id,finished=false;
    try{
      if(!blob||blob.type!=='application/pdf'||!Number.isInteger(blob.size)||blob.size<8||blob.size>LIMITS.exportBytes)throw fail('Export a PDF no larger than 32 MiB.');
      checkAbort(options.signal);const head=await readBlob(blob.slice(0,8),options.signal);if(String.fromCharCode(...head.slice(0,5))!=='%PDF-')throw fail('The PDF export is invalid.');
      const opened=await nativeCall('/local/notebook/export-pdf/start',{name:safeName(name)+'.pdf',size:blob.size},0);if(opened?.cancelled)return {saved:false,cancelled:true};id=opened?.id;if(!validId(id))throw fail('The PDF export could not start.');
      for(let offset=0;offset<blob.size;offset+=LIMITS.chunkBytes){checkAbort(options.signal);const bytes=await readBlob(blob.slice(offset,offset+LIMITS.chunkBytes),options.signal);await nativeCall('/local/notebook/export-pdf/write',{id,offset,dataBase64:encodeBase64(bytes)});progress(options,Math.min(offset+bytes.length,blob.size),blob.size);}
      checkAbort(options.signal);const result=await nativeCall('/local/notebook/export-pdf/finish',{id},0);finished=true;return result;
    }finally{if(validId(id)&&!finished)try{await nativeCall('/local/notebook/export-pdf/cancel',{id},8000);}catch{}}
  });}
  window.DeskNotebookPdf=Object.freeze({importFile,importDataUrl,importNative,exportDocument,exportNative,exportPNG,validateBackground:checkPng,limits:LIMITS,get busy(){return busy;}});
})();
