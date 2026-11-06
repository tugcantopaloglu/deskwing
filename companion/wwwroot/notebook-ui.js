'use strict';
// Notebook controls operate on separate, revision-checked ink documents. Boards are never submitted.
(function () {
  let host=null, panel=null, toggle=null, status=null, bookSelect=null, bookName=null, pageName=null, pageGrid=null, historyList=null;
  let catalog=null, selectedBookId='', busy=false, epoch=0, previewEpoch=0, pdfAbort=null, archivedList=null, pendingImport=null, catalogContext=null, catalogOffline=false, restoreAttempted=false, recovery=null, lastRecoverySelection=null, operationId=0;
  const previewObservers=new Set();
  const previews=new Map(), previewJobs=[], previewRunning=new Set();
  const id=value=>typeof value==='string'&&/^[a-f0-9]{32}$/i.test(value);
  const revision=value=>typeof value==='string'&&/^[a-f0-9]{64}$/i.test(value);
  const node=(tag,text,className)=>{const result=document.createElement(tag);if(text!==undefined)result.textContent=text;if(className)result.className=className;return result;};
  const button=(text,action,className='quiet')=>{const result=node('button',text,className);result.type='button';result.addEventListener('click',action);return result;};
  const pen=()=>window.DeskPen?.notebooks;
  const active=()=>window.DeskPen?.isActive()===true&&!document.hidden;
  const nativeHost=()=>typeof native!=='undefined'&&native===true;
  async function captureContext(){if(typeof captureWorkspaceContext==='function')return await captureWorkspaceContext();if(nativeHost())throw Error('Reopen DeskWing to update its notebook connection.');return 'same-origin';}
  function request(path,body,timeout,context=catalogContext){if(!context)throw Error('Open the notebook library to verify its PC connection first.');if(typeof apiWithWorkspaceContext==='function')return apiWithWorkspaceContext(path,body,context,timeout);if(nativeHost())throw Error('Reopen DeskWing to update its notebook connection.');return api(path,body,timeout);}
  async function ask(title,copy){if(typeof confirmAction!=='function')throw Error('The confirmation dialog is unavailable. Reopen DeskWing before changing saved versions.');return await confirmAction(title,copy);}
  function report(message,error=false){if(status){status.textContent=message;status.classList.toggle('pen-error',error);}}
  function currentBook(){return catalog?.notebooks.find(book=>book.id===selectedBookId)||null;}
  function endpoint(bookId,pageId){if(!id(bookId)||!id(pageId))throw Error('Choose a valid notebook page.');return `/api/notebooks/${bookId}/pages/${pageId}`;}
  function normalizeCatalog(value){
    if(!value||!id(value.libraryId)||!revision(value.revision)||!Array.isArray(value.notebooks)||value.notebooks.length>40)throw Error('The PC returned an invalid notebook library.');
    let pageCount=0;
    const normalizeBooks=values=>values.map(book=>{
      if(!book||!id(book.id)||typeof book.name!=='string'||book.name.length>80||!Array.isArray(book.pages))throw Error('The notebook list is invalid.');
      const pages=book.pages.map(page=>{if(!page||!id(page.id)||typeof page.name!=='string'||page.name.length>80)throw Error('The page list is invalid.');pageCount++;return {...page};});
      if(new Set(pages.map(page=>page.id)).size!==pages.length)throw Error('The page list contains duplicate identities.');
      return {...book,pages};
    });
    if(value.archivedNotebooks!==undefined&&!Array.isArray(value.archivedNotebooks))throw Error('The archived notebook list is invalid.');
    const books=normalizeBooks(value.notebooks),archived=normalizeBooks(value.archivedNotebooks||[]),all=[...books,...archived];
    if(pageCount>100||all.length>40||new Set(all.map(book=>book.id)).size!==all.length)throw Error('The notebook library exceeds its supported size.');
    return {...value,notebooks:books,archivedNotebooks:archived};
  }
  function setCatalog(value){catalog=normalizeCatalog(value);if(!currentBook())selectedBookId=catalog.notebooks[0]?.id||'';render();}
  function setBusy(value){busy=value;panel?.setAttribute('aria-busy',String(value));panel?.querySelectorAll('button,input,select').forEach(control=>control.disabled=value);const cancel=panel?.querySelector('#notebook-cancel-pdf');if(cancel)cancel.disabled=false;if(toggle)toggle.disabled=false;}
  async function run(action){if(busy)return;const token=++operationId,requestEpoch=epoch;setBusy(true);try{await action();}catch(error){if(requestEpoch===epoch&&active()){report(error?.message||'The notebook action could not finish. Your saved pages are kept.',true);if(error?.code==='notebook_storage_branch'){try{await showBackupRecovery();}catch{}}}}finally{if(token===operationId)setBusy(false);}}
  async function refresh(){
    if(!active())throw Error('Open Ink & notes before loading notebooks.');
    const requestEpoch=epoch;
    try{const context=await captureContext();if(requestEpoch!==epoch||!active())return false;const value=await request('/api/notebooks',undefined,15000,context);
      if(requestEpoch!==epoch||!active())return false;catalogContext=context;catalogOffline=false;setCatalog(value);pen()?.bind(catalog.libraryId,context);report('Choose a notebook page. Your original scratchpad is kept separately.');return true;
    }catch(error){if(requestEpoch===epoch&&active()&&catalog){catalogContext=null;catalogOffline=true;pen()?.unbind();render();report('PC unavailable. Saved local pages can be read and exported; reconnect before sharing.',true);return true;}throw error;}
  }
  async function mutate(body,context=catalogContext){
    if(!catalog||catalogOffline||!context||context!==catalogContext||!active())throw Error('Reconnect and load the notebook library first.');
    const requestEpoch=epoch,expectedRevision=catalog.revision;
    try{
      const value=await request('/api/notebooks',{...body,expectedRevision},20000,context);
      if(requestEpoch!==epoch||!active())throw Error('The view changed. Reopen the library to see the saved result.');
      setCatalog(value);return catalog;
    }catch(error){
      if(requestEpoch===epoch&&active()&&/changed|revision|conflict/i.test(error?.message||'')){try{await refresh();}catch{}}
      throw error;
    }
  }
  function validateSnapshot(value){
    if(!value||!id(value.notebookId)||!id(value.pageId)||!id(value.workspaceId)||!revision(value.revision)||!value.document)throw Error('The PC returned an invalid notebook page.');
    window.DeskPenCore.normalizeDocument(value.document);return value;
  }
  async function openPage(bookId,pageId){
    const requestEpoch=epoch;
    const book=catalog?.notebooks.find(item=>item.id===bookId),page=book?.pages.find(item=>item.id===pageId),selection={libraryId:catalog.libraryId,notebookId:bookId,pageId,notebookName:book?.name||'Notebook',pageName:page?.name||'Page',connectionContext:catalogContext};
    lastRecoverySelection=selection;
    if(catalogOffline){await pen().openCached(selection);await rememberSelection();render();report('Opened a saved local page. Reconnect and refresh the library before sharing.');return;}
    await pen().flush();
    const context=catalogContext,value=validateSnapshot(await request(endpoint(bookId,pageId),undefined,15000,context));
    if(requestEpoch!==epoch||!active())return;
    if(value.notebookId!==bookId||value.pageId!==pageId)throw Error('The PC returned a different page. No local page was replaced.');
    await pen().open(value,selection);
    if(requestEpoch!==epoch||!active())return;
    selectedBookId=bookId;lastRecoverySelection=null;await rememberSelection();render();report(`Opened ${book?.name||'Notebook'} · ${page?.name||'Page'}. Changes save to this page.`+(pen().state().localReadOnly?' Offline storage is read-only here; export current page or PNG to keep a separate copy.':''));
  }
  async function createBook(){
    await pen().flush();const entered=bookName.value.trim(),name=entered&&entered!==currentBook()?.name?entered:'Untitled notebook',known=new Set(catalog.notebooks.map(book=>book.id));
    await mutate({operation:'createNotebook',name});const created=catalog.notebooks.find(book=>!known.has(book.id));
    if(created){selectedBookId=created.id;render();if(created.pages[0])await openPage(created.id,created.pages[0].id);}
  }
  async function createPage(){
    const book=currentBook();if(!book)throw Error('Choose a notebook.');await pen().flush();
    const known=new Set(book.pages.map(page=>page.id)),entered=pageName.value.trim(),current=pen().state().page,name=entered&&entered!==book.pages.find(page=>page.id===current?.pageId)?.name?entered:`Page ${book.pages.filter(page=>!page.archived).length+1}`;
    await mutate({operation:'createPage',notebookId:book.id,name});const created=currentBook()?.pages.find(page=>!known.has(page.id));
    if(created)await openPage(book.id,created.id);
  }
  async function renameBook(){const book=currentBook();if(!book)throw Error('Choose a notebook.');const name=bookName.value.trim();if(!name)throw Error('Enter a notebook name.');await mutate({operation:'renameNotebook',notebookId:book.id,name});report('Notebook renamed.');}
  function selectedPage(){const current=pen()?.state().page;if(!current||current.notebookId!==selectedBookId)throw Error('Open a page in this notebook first.');return current;}
  async function renamePage(){const page=selectedPage(),name=pageName.value.trim();if(!name)throw Error('Enter a page name.');await mutate({operation:'renamePage',notebookId:page.notebookId,pageId:page.pageId,name});report('Page renamed.');}
  async function duplicatePage(){const page=selectedPage();await pen().flush();const known=new Set(currentBook().pages.map(item=>item.id));await mutate({operation:'duplicatePage',notebookId:page.notebookId,pageId:page.pageId});const copy=currentBook()?.pages.find(item=>!known.has(item.id));if(copy)await openPage(page.notebookId,copy.id);}
  async function showHistory(){
    const page=selectedPage(),requestEpoch=epoch;await pen().flush();
    const context=catalogContext,result=await request(endpoint(page.notebookId,page.pageId)+'/history',undefined,15000,context);
    if(requestEpoch!==epoch||!active())return;
    if(!Array.isArray(result?.versions)||result.versions.length>20)throw Error('The page history is invalid.');
    historyList.replaceChildren();historyList.hidden=false;
    const title=node('h4','Saved page versions');historyList.append(title);
    if(!result.versions.length)historyList.append(node('p','No previous saved versions yet.','help'));
    for(const version of result.versions){
      if(!revision(version.id)||typeof version.created!=='string')throw Error('The page history contains an invalid version.');
      const date=new Date(version.created),label=Number.isNaN(date.getTime())?'Saved version':date.toLocaleString();
      const restore=button(`Restore ${label}`,()=>run(async()=>{
        await pen().flush();const reviewed=pen().state(),key=JSON.stringify([reviewed.document.strokes,reviewed.document.notes]);
        if(!await ask('Restore this page version?','The current page is kept in history before the selected version replaces it.'))return;
        const current=pen().state();if(current.page?.pageId!==page.pageId||current.page?.notebookId!==page.notebookId||current.revision!==reviewed.revision||JSON.stringify([current.document.strokes,current.document.notes])!==key)throw Error('The open page changed. Load its history again.');
        const expectedRevision=reviewed.revision;
        await request(endpoint(page.notebookId,page.pageId)+'/history/restore',{expectedRevision,versionId:version.id},15000,context);
        await openPage(page.notebookId,page.pageId);historyList.hidden=true;report('Previous page version restored. The replaced version remains in history.');
      }));historyList.append(restore);
    }
    const purge=button('Clear saved history',()=>run(async()=>{
      await pen().flush();const reviewed=pen().state(),key=JSON.stringify([reviewed.document.strokes,reviewed.document.notes]);
      if(!await ask('Clear this page’s saved history?','Only previous saved versions are removed. The current page stays.'))return;
      const current=pen().state();if(current.page?.pageId!==page.pageId||current.page?.notebookId!==page.notebookId||current.revision!==reviewed.revision||JSON.stringify([current.document.strokes,current.document.notes])!==key)throw Error('The page changed while the confirmation was open. Review its history again.');
      await mutate({operation:'purgeHistory',notebookId:page.notebookId,pageId:page.pageId,expectedPageRevision:reviewed.revision,confirmed:true});historyList.replaceChildren(node('p','Saved history cleared. The current page is unchanged.','help'));
    }));historyList.append(purge);
  }
  async function renderThumbnail(canvas,snapshot,jobEpoch){
    const context=canvas.getContext('2d');context.clearRect(0,0,canvas.width,canvas.height);context.fillStyle='#f6f3e9';context.fillRect(0,0,canvas.width,canvas.height);
    const draw=()=>{const ink=document.createElement('canvas');try{ink.width=canvas.width;ink.height=canvas.height;const ctx=ink.getContext('2d');for(const stroke of snapshot.document.strokes){ctx.save();ctx.globalCompositeOperation=stroke.tool==='eraser'?'destination-out':'source-over';ctx.lineCap=ctx.lineJoin='round';ctx.strokeStyle=ctx.fillStyle=stroke.color;const first=stroke.points[0],scale=canvas.width/1600;ctx.beginPath();ctx.arc(first[0]*canvas.width,first[1]*canvas.height,stroke.width*(0.3+first[2]*1.4)*scale/2,0,Math.PI*2);ctx.fill();for(let i=1;i<stroke.points.length;i++){const a=stroke.points[i-1],b=stroke.points[i];ctx.lineWidth=stroke.width*(0.3+(a[2]+b[2])*0.7)*scale;ctx.beginPath();ctx.moveTo(a[0]*canvas.width,a[1]*canvas.height);ctx.lineTo(b[0]*canvas.width,b[1]*canvas.height);ctx.stroke();}ctx.restore();}context.drawImage(ink,0,0);}finally{ink.width=0;ink.height=0;}};
    if(snapshot.background?.image){
      if(typeof snapshot.background.image!=='string'||snapshot.background.image.length>1500000||!/^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(snapshot.background.image))throw Error('The preview background is invalid.');
      const image=new Image();try{await new Promise((resolve,reject)=>{image.onload=resolve;image.onerror=()=>reject(Error('The page background could not be decoded.'));image.src=snapshot.background.image;});if(jobEpoch!==previewEpoch||!canvas.isConnected)return;if(image.naturalWidth>2048||image.naturalHeight>2048||image.naturalWidth*image.naturalHeight>4000000)throw Error('The preview background is too large.');context.drawImage(image,0,0,canvas.width,canvas.height);draw();}finally{image.onload=null;image.onerror=null;image.removeAttribute('src');}
    }else if(jobEpoch===previewEpoch&&canvas.isConnected)draw();
  }
  function loadPreview(job){
    if(job.epoch!==previewEpoch||!job.canvas.isConnected)return;
    if(previewRunning.size>=2){previewJobs.push(job);return;}
    const key=job.bookId+':'+job.pageId;previewRunning.add(job);
    Promise.resolve().then(async()=>{
      if(catalogOffline){const cached=await window.DeskNotebookStore?.getPage(`deskwing.notebooks.page.v1:${catalog.libraryId}:${job.bookId}:${job.pageId}`);if(cached?.document&&job.epoch===previewEpoch&&job.canvas.isConnected)await renderThumbnail(job.canvas,{document:cached.document,background:cached.background},job.epoch);return;}
      const value=validateSnapshot(await request(endpoint(job.bookId,job.pageId),undefined,15000,job.context));
      if(job.epoch!==previewEpoch||!job.canvas.isConnected)return;
      await renderThumbnail(job.canvas,value,job.epoch);if(job.epoch===previewEpoch&&job.canvas.isConnected)previews.set(key,value.revision);
    }).catch(()=>{if(job.canvas.isConnected)job.canvas.setAttribute('aria-label','Preview unavailable; open the page to retry.');}).finally(()=>{previewRunning.delete(job);let next;while((next=previewJobs.shift()))if(next.epoch===previewEpoch){loadPreview(next);break;}});
  }
  function render(){
    if(!panel||!catalog)return;previewEpoch++;previewJobs.length=0;for(const observer of previewObservers)observer.disconnect();previewObservers.clear();bookSelect.replaceChildren();
    for(const book of catalog.notebooks){const option=node('option',book.name);option.value=book.id;bookSelect.append(option);}
    bookSelect.value=selectedBookId;const book=currentBook();bookName.value=book?.name||'';
    const state=pen()?.state(),selected=state?.page;pageName.value=selected?.notebookId===book?.id?(book?.pages.find(page=>page.id===selected.pageId)?.name||''):'';
    pageGrid.replaceChildren();historyList.hidden=true;
    if(toggle)toggle.textContent=selected?`Notebooks · ${book?.id===selected.notebookId?book.name:selected.notebookName} / ${book?.pages.find(page=>page.id===selected.pageId)?.name||selected.pageName}`:'Notebooks';
    if(!book){pageGrid.append(node('p','Create a notebook to add pages.','help'));return;}
    for(const page of book.pages.filter(page=>!page.archived)){
      const card=button('',()=>run(()=>openPage(book.id,page.id)),'quiet notebook-page');card.dataset.pageId=page.id;card.setAttribute('aria-pressed',String(selected?.notebookId===book.id&&selected?.pageId===page.id));
      const canvas=node('canvas');canvas.width=160;canvas.height=page.background?Math.max(60,Math.min(220,Math.round(160*page.background.height/page.background.width))):100;canvas.setAttribute('aria-hidden','true');
      card.append(canvas,node('strong',page.name),node('span',page.background?'PDF page':page.legacy?'Original shared scratchpad':'Ink and notes','help'));pageGrid.append(card);
      const job={bookId:book.id,pageId:page.id,canvas,epoch:previewEpoch,context:catalogContext};
      if(typeof IntersectionObserver==='function'){const observer=new IntersectionObserver(entries=>{if(entries.some(entry=>entry.isIntersecting)){observer.disconnect();previewObservers.delete(observer);loadPreview(job);}});previewObservers.add(observer);observer.observe(card);}else if(pageGrid.children.length<=4)loadPreview(job);
    }
    renderArchived();
    setBusy(busy);
  }
  function renderArchived(){
    if(!archivedList||!catalog)return;archivedList.replaceChildren(node('summary','Archived notebooks and pages'));
    let count=0;
    for(const book of catalog.archivedNotebooks||[]){count++;const row=node('div',undefined,'notebook-actions');row.append(node('span',book.name),button('Restore notebook',()=>run(async()=>{await mutate({operation:'restoreNotebook',notebookId:book.id});report('Notebook restored.');})));
      if(!book.pages.some(page=>page.legacy))row.append(button('Remove permanently',()=>run(async()=>{if(!await ask('Permanently remove this archived notebook?','Its pages and retained history will be removed. This cannot be undone.'))return;await mutate({operation:'deleteNotebook',notebookId:book.id,confirmed:true});report('Archived notebook permanently removed.');}),'danger'));archivedList.append(row);}
    for(const book of catalog.notebooks)for(const page of book.pages.filter(item=>item.archived)){count++;const row=node('div',undefined,'notebook-actions');row.append(node('span',book.name+' / '+page.name),button('Restore page',()=>run(async()=>{await mutate({operation:'restorePage',notebookId:book.id,pageId:page.id});report('Page restored.');})));
      if(!page.legacy)row.append(button('Remove permanently',()=>run(async()=>{if(!await ask('Permanently remove this archived page?','Its ink, PDF background and retained history will be removed. This cannot be undone.'))return;await mutate({operation:'deletePage',notebookId:book.id,pageId:page.id,confirmed:true});report('Archived page permanently removed.');}),'danger'));archivedList.append(row);}
    if(!count)archivedList.append(node('p','No archived pages or notebooks.','help'));
  }
  async function waitForOriginalWorkspace(requestEpoch,generation,signal){
    const until=Date.now()+15000;
    while(!active()&&Date.now()<until&&requestEpoch===epoch&&pen().state().generation===generation&&!signal.aborted)await new Promise(resolve=>setTimeout(resolve,50));
    if(!active()||requestEpoch!==epoch||pen().state().generation!==generation||signal.aborted)throw Error('The workspace changed. No current page was replaced.');
  }
  async function importPdf(file){
    if(!window.DeskNotebookPdf)throw Error('The PDF tools could not load. Reopen DeskWing and try again.');
    if(!catalog)throw Error('Load the notebook library before importing a PDF.');
    const requestEpoch=epoch,generation=pen().state().generation,importCatalog=catalog,context=catalogContext,libraryId=importCatalog.libraryId,originalIds=new Set([...importCatalog.notebooks,...importCatalog.archivedNotebooks].map(book=>book.id)),importAbort=new AbortController();pdfAbort=importAbort;
    try{
      await pen().flush();
      if(importAbort.signal.aborted||requestEpoch!==epoch||!active()||pen().state().generation!==generation||catalog!==importCatalog||catalogContext!==context)throw Error('PDF import stopped because the workspace changed.');
      const options={signal:importAbort.signal,onProgress:value=>{if(!importAbort.signal.aborted&&requestEpoch===epoch)report(typeof value==='string'?value:'Reading PDF pages…');}};
      const imported=nativeHost()?await window.DeskNotebookPdf.importNative(options):await window.DeskNotebookPdf.importFile(file,options);
      if(imported.cancelled)return;
      await waitForOriginalWorkspace(requestEpoch,generation,importAbort.signal);
      if(catalog?.libraryId!==libraryId||catalogContext!==context)throw Error('PDF import stopped because the PC connection changed.');
      if(!Array.isArray(imported.pages)||!imported.pages.length||imported.pages.length>20)throw Error('Choose a PDF with 1 to 20 pages.');
      const task={name:imported.name,pages:imported.pages,bookId:null,completed:0,libraryId,context,originalIds};pendingImport=task;
      await continuePdfImport(task,requestEpoch,generation,importAbort.signal);
    }finally{if(pdfAbort===importAbort)pdfAbort=null;}
  }
  function matchesImportedPage(page,source,index){const background=page.background;return background&&background.sourceName===source.name&&background.sourcePage===(source.pages[index].sourcePage||index+1)&&background.width===source.pages[index].width&&background.height===source.pages[index].height;}
  async function continuePdfImport(task,requestEpoch,generation,signal){
    if(!task||pendingImport!==task)throw Error('There is no partial PDF import to resume.');
    try{
      for(let index=0;index<task.pages.length;index++){
        if(signal.aborted||requestEpoch!==epoch||!active()||pen().state().generation!==generation||task.context!==catalogContext||pendingImport!==task)throw Error('PDF import stopped because the workspace changed.');
        let book=task.bookId?catalog.notebooks.find(item=>item.id===task.bookId):null;
        if(task.bookId&&!book)throw Error('The imported notebook is no longer active. Restore it before resuming.');
        if(!task.bookId){
          const candidates=[...catalog.notebooks,...catalog.archivedNotebooks].filter(item=>!task.originalIds.has(item.id)&&item.pages.some(page=>matchesImportedPage(page,task,0)));
          if(candidates.length>1)throw Error('More than one imported notebook matches this PDF. Choose it from the library instead of retrying automatically.');
          if(candidates.length===1){if(!catalog.notebooks.some(item=>item.id===candidates[0].id))throw Error('The partially imported notebook is archived. Restore it before resuming.');book=candidates[0];task.bookId=book.id;}
        }
        const existing=book?.pages.filter(page=>matchesImportedPage(page,task,index))||[];
        if(existing.length>1)throw Error('An imported PDF page was duplicated. Review the notebook before resuming.');
        if(existing.length===1){task.completed=Math.max(task.completed,index+1);continue;}
        const source=task.pages[index],background={kind:'png',image:source.backgroundPng,width:source.width,height:source.height,sourceName:task.name,sourcePage:source.sourcePage||index+1};
        if(index===0&&!task.bookId){await mutate({operation:'createNotebook',name:task.name.slice(0,80)||'Imported PDF',firstPageName:'Page 1',background});book=catalog.notebooks.find(item=>!task.originalIds.has(item.id)&&item.pages.some(page=>matchesImportedPage(page,task,0)));if(!book)throw Error('Reload the library to find the imported notebook.');task.bookId=book.id;}
        else{if(!task.bookId)throw Error('The PDF notebook could not be identified.');await mutate({operation:'createPage',notebookId:task.bookId,name:`Page ${index+1}`,background});}
        task.completed=index+1;report(`Imported ${task.completed} of ${task.pages.length} PDF pages. Saved pages are kept if import stops.`);
      }
      if(signal.aborted||requestEpoch!==epoch||pendingImport!==task)throw Error('PDF import stopped because the workspace changed.');
      selectedBookId=task.bookId;const book=currentBook(),first=book?.pages.find(page=>matchesImportedPage(page,task,0));render();if(first)await openPage(book.id,first.id);
      if(signal.aborted||requestEpoch!==epoch||pendingImport!==task)throw Error('PDF import stopped because the workspace changed.');
      pendingImport=null;report(`Imported ${task.completed} PDF pages. PDF backgrounds are rendered images; ink and notes remain editable.`);
    }catch(error){throw Error(`${task.completed} of ${task.pages.length} PDF pages saved. Existing pages are kept. Use Resume PDF import to retry the remaining pages. ${error?.message||'Import did not finish.'}`);}
  }
  async function resumePdfImport(){
    const task=pendingImport;if(!task)throw Error('No partial PDF import is waiting.');
    const requestEpoch=epoch,generation=pen().state().generation,initialContext=catalogContext,libraryId=task.libraryId,context=task.context,resumeAbort=new AbortController();pdfAbort=resumeAbort;
    try{
      await pen().flush();
      if(resumeAbort.signal.aborted||requestEpoch!==epoch||!active()||pen().state().generation!==generation||pendingImport!==task||catalogContext!==initialContext)throw Error('PDF import stopped because the workspace changed.');
      const refreshed=await refresh();
      if(!refreshed||resumeAbort.signal.aborted||requestEpoch!==epoch||!active()||pen().state().generation!==generation||pendingImport!==task)throw Error('PDF import stopped because the workspace changed.');
      if(catalogOffline||catalog?.libraryId!==libraryId||catalogContext!==context||task.context!==context)throw Error('Reconnect the original PC before resuming this PDF import.');
      await continuePdfImport(task,requestEpoch,generation,resumeAbort.signal);
    }finally{if(pdfAbort===resumeAbort)pdfAbort=null;}
  }
  async function generatedPdfBlob(value,signal){
    // Only our flattened PDF exporter may cross the file boundary. Neither remote snapshots nor
    // a replacement module can supply an HTML blob, arbitrary URL, or unbounded download here.
    const limit=32*1024*1024;
    if(!(value instanceof Blob)||value.type!=='application/pdf'||!Number.isSafeInteger(value.size)||value.size<14||value.size>limit)throw Error('The generated PDF is invalid or exceeds 32 MiB. Export fewer pages.');
    const header=await value.slice(0,8).text(),tail=await value.slice(Math.max(0,value.size-1024)).text();
    if(signal?.aborted)throw Error('PDF export cancelled.');
    if(!/^%PDF-(?:1\.[0-7]|2\.0)$/.test(header)||!/%%EOF[\t\n\f\r ]*$/.test(tail))throw Error('The generated PDF is incomplete. No file was saved.');
    // Reassert the exact PDF media type on a fresh immutable Blob before either native or web save.
    return new Blob([value],{type:'application/pdf'});
  }
  function downloadPdf(blob,name){
    const objectUrl=URL.createObjectURL(blob);
    try{
      // Browser-created object URLs are permitted only for this exact origin and a UUID token.
      // No server data, location path, query, fragment, or PDF content becomes an href.
      const prefix='blob:'+location.origin+'/',token=objectUrl.slice(prefix.length);
      if(!objectUrl.startsWith(prefix)||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(token))throw Error('The PDF download address could not be verified. No file was saved.');
      const link=node('a');link.href=prefix+token;link.download=name;link.rel='noopener';document.body.append(link);
      try{link.click();}finally{link.remove();}
    }catch(error){URL.revokeObjectURL(objectUrl);throw error;}
    setTimeout(()=>URL.revokeObjectURL(objectUrl),30000);
  }
  async function exportPdf(currentOnly=false){
    if(!window.DeskNotebookPdf)throw Error('The PDF tools could not load.');const initial=pen().state();if(initial.editing)throw Error('Finish the current stroke or text entry before exporting.');
    const book=currentBook(),nameSource=currentOnly?(initial.page?.notebookName||'DeskWing-page'):book?.name;
    if(!currentOnly){await pen().flush();if(!book||catalogOffline)throw Error('Connect to the PC and choose a notebook, or export the current local page.');}
    const pages=currentOnly?[]:book.pages.filter(page=>!page.archived);if(!currentOnly&&(!pages.length||pages.length>20))throw Error('Export 1 to 20 pages at a time, or choose Export current page.');
    const requestEpoch=epoch,generation=initial.generation,context=catalogContext,exported=[],exportAbort=new AbortController();pdfAbort=exportAbort;
    try{
      if(currentOnly)exported.push({document:initial.document,backgroundPng:initial.page?.background?.image||null,width:initial.width,height:initial.height});
      for(const page of pages){if(!active()||requestEpoch!==epoch||exportAbort.signal.aborted||pen().state().generation!==generation)throw Error('PDF export stopped because the workspace changed.');const value=validateSnapshot(await request(endpoint(book.id,page.id),undefined,15000,context));exported.push({document:value.document,backgroundPng:value.background?.image||null,width:value.background?.width||1600,height:value.background?.height||1000});report(`Preparing PDF page ${exported.length} of ${pages.length}…`);}
      const blob=await generatedPdfBlob(await window.DeskNotebookPdf.exportDocument({name:nameSource,pages:exported},{signal:exportAbort.signal}),exportAbort.signal);
      if(requestEpoch!==epoch||!active()||pen().state().generation!==generation)return;
      const name=(nameSource.replace(/[\\/:*?"<>|\u0000-\u001f\u007f]/g,'_').slice(0,70)||'DeskWing-notebook')+'.pdf';
      if(nativeHost()){const result=await window.DeskNotebookPdf.exportNative(blob,name,{signal:exportAbort.signal});if(result.cancelled)return;if(!result.saved)throw Error('The PDF was not saved.');}
      else downloadPdf(blob,name);
      report('PDF saved with page images, ink and text-note pages. Editable ink and notes stay in the notebook.');
    }finally{if(pdfAbort===exportAbort)pdfAbort=null;}
  }
  function mount(target){
    if(host===target&&panel?.isConnected)return;host=target;const actions=host.querySelector('.pen-heading-actions'),toolbox=host.querySelector('#pen-toolbox');if(!actions||!toolbox)return;
    toggle=button('Notebooks',()=>{epoch++;panel.hidden=!panel.hidden;toggle.setAttribute('aria-expanded',String(!panel.hidden));if(!panel.hidden)void run(refresh);});toggle.id='pen-notebooks';toggle.setAttribute('aria-expanded','false');actions.append(toggle);
    panel=node('section',undefined,'notebook-panel');panel.id='pen-notebook-panel';panel.hidden=true;panel.setAttribute('aria-label','Notebook library');
    const title=node('h3','Notebooks'),intro=node('p','Open named pages here. Your original shared scratchpad stays available.','help');panel.append(title,intro);
    const original=button('Original scratchpad',()=>run(async()=>{await pen().openScratchpad();await rememberSelection();render();report('Original scratchpad opened. Its saved data and recovery copy are kept.');}));original.id='notebook-original';
    const refreshButton=button('Refresh library',()=>run(refresh));const top=node('div',undefined,'notebook-actions');top.append(original,refreshButton);panel.append(top);
    bookSelect=node('select');bookSelect.id='notebook-select';bookSelect.setAttribute('aria-label','Notebook');bookSelect.addEventListener('change',()=>{selectedBookId=bookSelect.value;render();});panel.append(bookSelect);
    const bookLabel=node('label','Notebook name');bookName=node('input');bookName.id='notebook-name';bookName.maxLength=80;bookName.placeholder='Untitled notebook';bookLabel.append(bookName);panel.append(bookLabel);
    const bookActions=node('div',undefined,'notebook-actions');bookActions.append(button('New notebook',()=>run(createBook)),button('Rename notebook',()=>run(renameBook)),button('Archive notebook',()=>run(async()=>{const book=currentBook();if(!book||book.pages.some(page=>page.legacy))throw Error('The original shared Scratchpad stays available. Archive its other pages individually.');await pen().flush();if(!await ask('Archive this notebook?','Its pages stay in Archived notebooks and can be restored.'))return;if(pen().state().page?.notebookId===book.id)await pen().openScratchpad();await mutate({operation:'archiveNotebook',notebookId:book.id});await rememberSelection();report('Notebook archived. Its pages and history are kept.');})));panel.append(bookActions);
    pageGrid=node('div',undefined,'notebook-pages');pageGrid.id='notebook-pages';pageGrid.setAttribute('aria-label','Notebook pages');panel.append(pageGrid);
    const pageLabel=node('label','Page name');pageName=node('input');pageName.id='notebook-page-name';pageName.maxLength=80;pageName.placeholder='New page';pageLabel.append(pageName);panel.append(pageLabel);
    const pageActions=node('div',undefined,'notebook-actions');pageActions.append(button('New page',()=>run(createPage)),button('Rename page',()=>run(renamePage)),button('Duplicate page',()=>run(duplicatePage)),button('Page history',()=>run(showHistory)),button('Archive page',()=>run(async()=>{const page=selectedPage();await pen().flush();if(!await ask('Archive this page?','It stays in Archived pages and can be restored.'))return;await pen().openScratchpad();await mutate({operation:'archivePage',notebookId:page.notebookId,pageId:page.pageId});render();report('Page archived. Its ink and background are retained.');})));panel.append(pageActions);
    historyList=node('div',undefined,'notebook-history');historyList.id='notebook-history';historyList.hidden=true;panel.append(historyList);
    const view=node('div',undefined,'notebook-actions');const zoomLabel=node('span','100%','help');zoomLabel.id='notebook-zoom';
    const updateView=options=>{const state=pen().setView(options);zoomLabel.textContent=Math.round(state.zoom*100)+'%';panButton.setAttribute('aria-pressed',String(state.panMode));};
    const panButton=button('Pan',()=>updateView({panMode:!pen().state().panMode}));panButton.id='notebook-pan';panButton.setAttribute('aria-pressed','false');
    view.append(button('Zoom out',()=>updateView({zoom:pen().state().zoom/1.25})),zoomLabel,button('Zoom in',()=>updateView({zoom:pen().state().zoom*1.25})),button('Fit page',()=>updateView({zoom:1,panX:0,panY:0,panMode:false})),panButton);panel.append(view);
    const file=node('input');file.id='notebook-pdf-file';file.type='file';file.accept='application/pdf,.pdf';file.hidden=true;file.addEventListener('change',()=>{const selected=file.files?.[0];file.value='';if(selected)void run(()=>importPdf(selected));});panel.append(file);
    const pdf=node('div',undefined,'notebook-actions'),cancel=button('Cancel PDF task',()=>{pdfAbort?.abort();report('Stopping PDF task. Imported pages already saved are kept.');});cancel.id='notebook-cancel-pdf';pdf.append(button('Import PDF',()=>{if(nativeHost())void run(()=>importPdf(null));else file.click();}),button('Resume PDF import',()=>run(resumePdfImport)),button('Export notebook PDF',()=>run(()=>exportPdf(false))),button('Export current page PDF',()=>run(()=>exportPdf(true))),cancel);panel.append(pdf);
    archivedList=node('details',undefined,'notebook-archived');archivedList.append(node('summary','Archived notebooks and pages'));panel.append(archivedList);
    const recoveryBox=node('section',undefined,'notebook-recovery');recoveryBox.id='notebook-recovery';panel.append(button('Review local backup copies',()=>run(showBackupRecovery)),recoveryBox);
    if(window.DeskNotebookRecovery)recovery=window.DeskNotebookRecovery.mount(recoveryBox,{openReadOnly:async(selection,bundle,scope)=>{if(!scope.isCurrent())return;const opened=await pen().openCached(selection,{recoveryBundle:bundle,guard:scope.isCurrent});if(opened&&scope.isCurrent())report('Reviewed local copy opened read-only. Export its current page PDF, PNG or notes; the original backups are unchanged.');},saveAsNew:saveReviewedCopyAsNew,report});
    status=node('p','Open the library to load your notebooks.','help');status.id='notebook-status';status.setAttribute('role','status');panel.append(status);toolbox.prepend(panel);
    const drawer=host.querySelector('.pen-drawer-actions');if(drawer){const open=button('Notebook library',()=>{epoch++;panel.hidden=false;toggle.setAttribute('aria-expanded','true');void run(refresh);});open.id='notebook-open-drawer';drawer.append(open);}
    if(!restoreAttempted){restoreAttempted=true;void restoreSelection();}
  }
  async function rememberSelection(){if(!window.DeskNotebookStore?.atomicPageWrites)return false;const selected=pen().state().page;try{if(selected&&catalog?.libraryId===selected.libraryId)await window.DeskNotebookStore.set('deskwing.notebooks.catalog.v1:'+selected.libraryId,catalog);await window.DeskNotebookStore.set('deskwing.notebooks.selection.v1',{version:1,selection:selected?{libraryId:selected.libraryId,notebookId:selected.notebookId,pageId:selected.pageId,notebookName:selected.notebookName,pageName:selected.pageName}:null});return true;}catch(error){report(error?.message||'The page is open, but its opening preference could not be saved.',true);return false;}}
  async function restoreSelection(){
    if(!window.DeskNotebookStore)return;const requestEpoch=epoch,initial=pen().state(),content=JSON.stringify({strokes:initial.document.strokes,notes:initial.document.notes});
    try{await window.DeskNotebookStore.ready();const saved=await window.DeskNotebookStore.get('deskwing.notebooks.selection.v1');if(requestEpoch!==epoch||!active()||!saved?.selection||saved.version!==1)return;
      const cachedCatalog=normalizeCatalog(await window.DeskNotebookStore.get('deskwing.notebooks.catalog.v1:'+saved.selection.libraryId));if(saved.selection.libraryId!==cachedCatalog.libraryId)throw Error('The saved notebook library does not match its page.');
      const opened=await pen().openCached(saved.selection,{expectedGeneration:initial.generation,expectedContent:content});if(!opened||requestEpoch!==epoch||!active())return;
      catalogContext=null;catalogOffline=true;selectedBookId=saved.selection.notebookId;setCatalog(cachedCatalog);report('Saved local page restored. Refresh the notebook library to verify its PC before sharing.');
    }catch(error){report(error?.message||'The saved notebook could not be opened; its backup is kept.',true);if(error?.code==='notebook_storage_branch'){panel.hidden=false;toggle.setAttribute('aria-expanded','true');try{await recovery?.showSelectionRecovery();}catch{}}}
  }
  async function showBackupRecovery(){if(!recovery)throw Error('The local recovery tools did not load. Reopen DeskWing before reviewing backup copies.');panel.hidden=false;toggle.setAttribute('aria-expanded','true');const selected=lastRecoverySelection||pen().state().page;if(selected)await recovery.showPageRecovery(selected);else await recovery.showSelectionRecovery();}
  async function saveReviewedCopyAsNew(selection,bundle,scope){
    if(scope&&!scope.isCurrent())return;
    if(!active()||catalogOffline||!catalog||catalog.libraryId!==selection.libraryId||!catalogContext)throw Error('Connect the original PC and refresh its notebook library before saving a recovered copy.');
    const reviewed=window.DeskPenCore.normalizeDocument(bundle.document),requestEpoch=epoch,context=catalogContext,book=catalog.notebooks.find(item=>item.id===selection.notebookId);
    if(!book)throw Error('Restore the original notebook before adding a recovered page.');
    if(!await ask('Save this reviewed copy as a new page?','Existing pages and local backups stay unchanged. The new page is created in the connected original notebook.'+(!bundle.background?' This copy has no page background; only its ink and notes are copied.':'')))return;
    if(requestEpoch!==epoch||context!==catalogContext||!active()||scope&&!scope.isCurrent())throw Error('The PC connection changed. Review the copy again.');
    const known=new Set(book.pages.map(page=>page.id)),name=((selection.pageName||'Page')+' recovered').slice(0,80),body={operation:'createPage',notebookId:book.id,name};if(bundle.background)body.background=bundle.background;
    await mutate(body,context);const created=catalog.notebooks.find(item=>item.id===book.id)?.pages.find(page=>!known.has(page.id));if(!created)throw Error('Refresh the library to find the new recovered page.');
    const fresh=validateSnapshot(await request(endpoint(book.id,created.id),undefined,15000,context));
    if(requestEpoch!==epoch||context!==catalogContext||!active()||scope&&!scope.isCurrent())throw Error('The PC connection changed. The new empty page is kept; no old page was replaced.');
    await request(endpoint(book.id,created.id),{expectedRevision:fresh.revision,document:reviewed},15000,context);report('Reviewed copy saved as a new page. Original pages and both backup branches remain unchanged.');render();
  }
  function suspend(){epoch++;operationId++;setBusy(false);pdfAbort?.abort();for(const observer of previewObservers)observer.disconnect();previewObservers.clear();previewJobs.length=0;previewEpoch++;}
  function connectionChanged(){suspend();catalogContext=null;catalog=null;catalogOffline=true;pendingImport=null;pen()?.unbind();bookSelect?.replaceChildren();pageGrid?.replaceChildren();historyList?.replaceChildren();report('PC connection changed. The local page is kept; refresh the library before sharing.',true);}
  window.DeskNotebookUi=Object.freeze({mount,refresh,normalizeCatalog,suspend});
  window.addEventListener('pagehide',suspend);
  window.addEventListener('deskwing-connection-changed',connectionChanged);
  window.addEventListener('deskwing-workspace-contextchange',connectionChanged);
})();
