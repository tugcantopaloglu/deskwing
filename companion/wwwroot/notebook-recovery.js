'use strict';
// Read-only review of preserved local notebook branches. This module never writes storage or calls the PC.
(function () {
  const SELECTION='deskwing.notebooks.selection.v1', MAX_CARDS=8, MAX_DOCUMENT_BYTES=3500000;
  const id=value=>typeof value==='string'&&/^[a-f0-9]{32}$/i.test(value);
  const revision=value=>typeof value==='string'&&/^[a-f0-9]{64}$/i.test(value);
  let container=null, callbacks={}, generation=0, busy=false, cards=0, activeAction=null;
  const element=(tag,text,className)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;if(className)node.className=className;return node;};
  function notify(message,error=false){if(typeof callbacks.report==='function')callbacks.report(message,error);}
  function selected(value){
    if(!value||typeof value!=='object'||![value.libraryId,value.notebookId,value.pageId].every(id))throw Error('This saved page choice is invalid.');
    const clean={libraryId:value.libraryId,notebookId:value.notebookId,pageId:value.pageId};
    for(const [key,fallback] of [['notebookName','Notebook'],['pageName','Page']]){if(value[key]!==undefined&&(typeof value[key]!=='string'||value[key].length>80))throw Error('This saved page name is invalid.');clean[key]=value[key]||fallback;}
    return clean;
  }
  function selectionRecord(value){if(!value||typeof value!=='object'||value.version!==1)throw Error('This saved opening choice is invalid.');return value.selection===null?null:selected(value.selection);}
  function pageKey(selection){return `deskwing.notebooks.page.v1:${selection.libraryId}:${selection.notebookId}:${selection.pageId}`;}
  function documentCopy(value){
    if(!window.DeskPenCore?.normalizeDocument)throw Error('The ink reader is unavailable. Reopen DeskWing.');
    const clean=window.DeskPenCore.normalizeDocument(value);
    if(new TextEncoder().encode(JSON.stringify(clean)).length>MAX_DOCUMENT_BYTES)throw Error('This saved ink copy exceeds the page limit.');
    return clean;
  }
  function backgroundCopy(value){
    if(value===undefined||value===null)return null;
    if(typeof value!=='object'||value.kind!=='png'||typeof value.image!=='string'||value.image.length>1500000||!/^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(value.image)||!Number.isInteger(value.width)||!Number.isInteger(value.height)||value.width<1||value.height<1||value.width>2048||value.height>2048||value.width*value.height>4000000)throw Error('The saved page background is invalid.');
    let header;try{header=atob(value.image.slice(22,86));}catch{throw Error('The saved page background is invalid.');}
    if(header.length<24||[137,80,78,71,13,10,26,10].some((byte,index)=>header.charCodeAt(index)!==byte)||header.slice(12,16)!=='IHDR')throw Error('The saved page background is invalid.');
    const number=offset=>((header.charCodeAt(offset)*16777216)+(header.charCodeAt(offset+1)*65536)+(header.charCodeAt(offset+2)*256)+header.charCodeAt(offset+3));
    if(number(16)!==value.width||number(20)!==value.height)throw Error('The saved background dimensions do not match its image.');
    if(value.sourceName!==undefined&&(typeof value.sourceName!=='string'||value.sourceName.length>256))throw Error('The saved background name is invalid.');
    if(value.sourcePage!==undefined&&(!Number.isInteger(value.sourcePage)||value.sourcePage<1||value.sourcePage>100))throw Error('The saved source page is invalid.');
    return {kind:'png',image:value.image,width:value.width,height:value.height,sourceName:value.sourceName||'Recovered page',sourcePage:value.sourcePage||1};
  }
  function bundleCopy(value){
    const legacy=value&&value.version===1&&Array.isArray(value.strokes),source=legacy?{document:value}:value;
    if(!source||typeof source!=='object'||!source.document)throw Error('This saved branch has no readable page.');
    if(!legacy&&(source.bundleVersion!==1||!Number.isSafeInteger(source.localRevision)||source.localRevision<1))throw Error('The saved page bundle is invalid.');
    const document=documentCopy(source.document),sync=source.sync&&id(source.sync.workspaceId)&&revision(source.sync.revision)&&revision(source.sync.hash)?{workspaceId:source.sync.workspaceId,revision:source.sync.revision,hash:source.sync.hash}:null;
    let background=null,backgroundError=false,recovery=null,recoveryError=false;
    try{background=backgroundCopy(source.background);}catch{backgroundError=true;}
    if(source.recovery!==undefined&&source.recovery!==null){try{recovery=documentCopy(source.recovery);}catch{recoveryError=true;}}
    return {bundle:{localRevision:legacy?0:source.localRevision,document,sync,recovery,background},recoveryError,backgroundError};
  }
  function fresh(token){return token===generation&&container?.isConnected;}
  function message(text,error=false){const node=element('p',text,error?'help pen-error':'help');container.append(node);return node;}
  function reset(title){
    generation++;busy=false;cards=0;activeAction?.abort();activeAction=null;if(!container)throw Error('Open the notebook recovery panel first.');
    container.hidden=false;container.replaceChildren(element('h4',title));
    message('Review these saved copies before editing. Opening a read-only copy does not replace stored data. Export it, or save a reviewed copy as a new page.');
    return generation;
  }
  function branchEntries(result){
    if(!result||typeof result!=='object')throw Error('Saved copies could not be inspected.');
    return [
      {name:'Durable local backup',kind:'indexeddb',present:result.hasIndexedDb===true,value:result.indexeddb,error:!!result.indexeddbError},
      {name:'Limited storage backup',kind:'limitedLocalStorage',present:result.hasLimitedLocalStorage===true,value:result.limitedLocalStorage,error:!!result.limitedLocalStorageError}
    ];
  }
  function boundedDate(value){if(typeof value!=='string'||value.length>40)return 'No saved date';const date=new Date(value);return Number.isNaN(date.getTime())?'No saved date':date.toLocaleString();}
  async function invoke(action,selection,bundle,token){
    if(busy||!fresh(token))return;
    if(typeof callbacks[action]!=='function'){notify('This recovery action is unavailable. Reopen the notebook view.',true);return;}
    busy=true;activeAction=typeof AbortController==='function'?new AbortController():null;const actionController=activeAction;container.querySelectorAll('button').forEach(node=>node.disabled=true);
    try{await callbacks[action](JSON.parse(JSON.stringify(selection)),JSON.parse(JSON.stringify(bundle)),{isCurrent:()=>fresh(token)&&!actionController?.signal.aborted,signal:actionController?.signal});}
    catch{if(fresh(token))notify('The recovery action could not finish. The original saved copies are unchanged. Export the current page before leaving.',true);}
    finally{if(token===generation){busy=false;activeAction=null;if(container?.isConnected)container.querySelectorAll('button').forEach(node=>node.disabled=false);}}
  }
  function addCard(selection,bundle,label,token){
    if(!fresh(token)||cards>=MAX_CARDS)return;cards++;
    const article=element('article',undefined,'notebook-recovery-copy');article.dataset.recoveryBranch=label;
    article.append(element('strong',`${selection.notebookName} / ${selection.pageName}`),element('p',label,'help'),element('p',`${bundle.document.strokes.length} strokes · ${bundle.document.notes.length} note characters · ${boundedDate(bundle.document.updatedAt)}`,'help'));
    const actions=element('div',undefined,'notebook-actions'),open=element('button','Open read-only copy','quiet');open.type='button';open.addEventListener('click',()=>{void invoke('openReadOnly',selection,bundle,token);});actions.append(open);
    if(typeof callbacks.saveAsNew==='function'){const save=element('button','Save reviewed copy as a new page','quiet');save.type='button';save.addEventListener('click',()=>{void invoke('saveAsNew',selection,bundle,token);});actions.append(save);}
    article.append(actions);container.append(article);
  }
  async function addPageBranches(selection,token){
    let result;try{result=await window.DeskNotebookStore.readBranches(pageKey(selection));}catch{if(fresh(token))message('The saved page copies could not be inspected. Their original data is kept unchanged.',true);return;}
    if(!fresh(token))return;
    let readable=0;
    for(const branch of branchEntries(result)){
      if(!fresh(token))return;
      if(branch.error){message(`${branch.name} could not be read. Its original data is kept unchanged.`,true);continue;}
      if(!branch.present||branch.value===null)continue;
      try{
        const {bundle,recoveryError,backgroundError}=bundleCopy(branch.value);addCard(selection,bundle,branch.name+(backgroundError?' · background unavailable':''),token);readable++;
        if(backgroundError)message(`${branch.name} has an unreadable page background. Its ink and notes can still be opened; the original background data is kept.`,true);
        if(recoveryError)message(`${branch.name} has a damaged recovery slot. Its readable current page is available above; the original data is kept.`,true);
        if(bundle.recovery&&JSON.stringify(bundle.recovery)!==JSON.stringify(bundle.document))addCard(selection,{...bundle,document:bundle.recovery,recovery:null},branch.name+' · retained recovery copy',token);
      }catch{message(`${branch.name} contains an invalid page. Its original data is kept unchanged.`,true);}
    }
    if(!readable)message('No readable local page copy was found for this choice. Reconnect its PC to inspect the saved notebook.',true);
  }
  async function showPageRecovery(value){
    const token=reset('Saved local page copies');
    try{const selection=selected(value);if(!window.DeskNotebookStore?.readBranches)throw Error('Local recovery is unavailable.');await addPageBranches(selection,token);if(fresh(token))notify('Choose a saved copy to read or export. No stored copy has been replaced.');}
    catch{if(fresh(token))message('The local recovery reader could not open this page. Its original copies are kept unchanged.',true);}
  }
  async function showSelectionRecovery(){
    const token=reset('Saved opening choices');
    try{
      if(!window.DeskNotebookStore?.readBranches)throw Error('Local recovery is unavailable.');
      const result=await window.DeskNotebookStore.readBranches(SELECTION);if(!fresh(token))return;
      const seen=new Set();let choices=0;
      for(const branch of branchEntries(result)){
        if(!fresh(token))return;
        if(branch.error){message(`${branch.name} opening choice could not be read. Its original data is kept unchanged.`,true);continue;}
        if(!branch.present||branch.value===null)continue;
        try{const selection=selectionRecord(branch.value);if(!selection){message(`${branch.name} opens the original scratchpad.`);continue;}const key=pageKey(selection);if(seen.has(key))continue;seen.add(key);choices++;await addPageBranches(selection,token);}
        catch{message(`${branch.name} opening choice is invalid. Its original data is kept unchanged.`,true);}
      }
      if(!choices&&fresh(token))message('No saved notebook opening choice could be recovered. The original scratchpad remains available.');
    }catch{if(fresh(token))message('Saved opening choices could not be inspected. Existing notebook backups are kept unchanged.',true);}
  }
  function invalidate(){generation++;busy=false;activeAction?.abort();activeAction=null;if(container){container.replaceChildren();container.hidden=true;}}
  function mount(target,handlers={}){if(!target||typeof target.replaceChildren!=='function')throw Error('A recovery panel is required.');invalidate();container=target;callbacks=handlers;container.hidden=true;return Object.freeze({showSelectionRecovery,showPageRecovery,clear:invalidate});}
  window.DeskNotebookRecovery=Object.freeze({mount,showSelectionRecovery,showPageRecovery,clear:invalidate});
  for(const event of ['pagehide','deskwing-connection-changed','deskwing-workspace-contextchange'])window.addEventListener(event,invalidate);
})();
