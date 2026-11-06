'use strict';
// Actual notebook and ink documents in isolated Chromium. No device, live board, or native UI access.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {createHash}=require('node:crypto');
const {chromium}=require('playwright');

const root=path.resolve(__dirname,'..');
const libraryId='1'.repeat(32),bookId='2'.repeat(32),firstPage='3'.repeat(32),secondPage='4'.repeat(32),workspaceId='5'.repeat(32);
const empty=()=>({version:1,strokes:[],notes:'',penOnly:true,updatedAt:'2026-11-06T12:00:00.000Z'});
const legacy=()=>({...empty(),notes:'Original device scratchpad',strokes:[{tool:'pen',color:'#23313f',width:3,points:[[.1,.2,.5],[.4,.6,.8]]}]});
const clone=value=>JSON.parse(JSON.stringify(value));
const origin='https://deskwing-notebook-fixture.invalid';
const maxRequestBytes=4*1024*1024,maxAssetBytes=2*1024*1024;
// These are loaded once before any browser request. No request supplies a filesystem path.
function preloadAsset(relative,contentType){
  const descriptor=fs.openSync(path.join(root,relative),'r');
  try{
    const size=fs.fstatSync(descriptor);
    assert.ok(size.isFile()&&Number.isSafeInteger(size.size)&&size.size>0&&size.size<=maxAssetBytes,'Fixture asset must be a bounded ordinary file');
    const bytes=Buffer.alloc(size.size);let offset=0;
    while(offset<bytes.length){const read=fs.readSync(descriptor,bytes,offset,bytes.length-offset,offset);assert.ok(read>0,'Fixture asset changed during preload');offset+=read;}
    assert.equal(fs.fstatSync(descriptor).size,size.size,'Fixture asset changed during preload');
    return{status:200,contentType,body:bytes};
  }finally{fs.closeSync(descriptor);}
}
const assets=new Map([
  ['/pen.js',preloadAsset('companion/wwwroot/pen.js','text/javascript')],
  ['/notebook-ui.js',preloadAsset('companion/wwwroot/notebook-ui.js','text/javascript')],
  ['/notebook-store.js',preloadAsset('companion/wwwroot/notebook-store.js','text/javascript')],
  ['/pen.css',preloadAsset('companion/wwwroot/pen.css','text/css')]
]);
let nonce=0,state,browser,checks=0;
const errors=[],allRequests=[];
const hash=value=>createHash('sha256').update(String(++nonce)+JSON.stringify(value)).digest('hex').toUpperCase();
const newId=()=>String(++nonce).padStart(32,'0');
function snapshot(bid,pid,document=empty(),background=null){return{notebookId:bid,pageId:pid,workspaceId:pid,revision:hash(document),document:clone(document),recoveryWarning:'',background};}
function reset(){
  nonce=100;
  const first=snapshot(bookId,firstPage),second=snapshot(bookId,secondPage);
  state={legacy:{workspaceId,revision:hash(legacy()),document:legacy(),recoveryWarning:''},catalogRevision:hash('catalog'),books:[{id:bookId,name:'Research',pages:[{id:firstPage,name:'First page',archived:false,legacy:false},{id:secondPage,name:'Second page',archived:false,legacy:false}]}],pages:new Map([[firstPage,first],[secondPage,second]]),histories:new Map(),requests:[],holds:[],holdPage:null,holdRequests:[],holdConfirm:false,failPageSaves:false,failNextCreatePage:false,closed:false};
}
function catalog(){return{libraryId,revision:state.catalogRevision,notebooks:state.books.filter(book=>!book.archived).map(book=>({...clone(book),pages:book.pages.map(page=>{const saved=state.pages.get(page.id);return{...clone(page),updatedAt:saved.document.updatedAt,strokeCount:saved.document.strokes.length,noteLength:saved.document.notes.length,background:saved.background?{...saved.background,image:null}:null};})})),archivedNotebooks:state.books.filter(book=>book.archived).map(clone)};}
function savePage(pid,document){const current=state.pages.get(pid),history=state.histories.get(pid)||[];history.unshift({id:current.revision,created:new Date().toISOString(),document:clone(current.document)});state.histories.set(pid,history.slice(0,20));const saved={...current,revision:hash(document),document:clone(document)};state.pages.set(pid,saved);return saved;}
function send(status,value){return{status,contentType:'application/json',headers:{'Cache-Control':'no-store'},body:JSON.stringify(value)};}
const record=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
const fields=(value,names)=>Object.keys(value).every(key=>names.includes(key));
const validId=value=>typeof value==='string'&&/^[a-f0-9]{32}$/i.test(value);
const validRevision=value=>typeof value==='string'&&/^[a-f0-9]{64}$/i.test(value);
const validName=value=>typeof value==='string'&&value.trim().length>0&&value.length<=100;
function validDocument(value){
  if(!record(value)||!fields(value,['version','strokes','notes','penOnly','updatedAt'])||value.version!==1||typeof value.notes!=='string'||value.notes.length>8000||typeof value.penOnly!=='boolean'||typeof value.updatedAt!=='string'||value.updatedAt.length>40||!Number.isFinite(Date.parse(value.updatedAt))||!Array.isArray(value.strokes)||value.strokes.length>1200)return false;
  let points=0;
  for(const stroke of value.strokes){
    if(!record(stroke)||!fields(stroke,['tool','color','width','points'])||!['pen','eraser'].includes(stroke.tool)||typeof stroke.color!=='string'||!/^#[a-f0-9]{6}$/i.test(stroke.color)||typeof stroke.width!=='number'||!Number.isFinite(stroke.width)||stroke.width<1||stroke.width>40||!Array.isArray(stroke.points)||!stroke.points.length||stroke.points.length>60000-points)return false;
    points+=stroke.points.length;
    for(const point of stroke.points)if(!Array.isArray(point)||point.length!==3||!point.every(number=>typeof number==='number'&&Number.isFinite(number)&&number>=0&&number<=1))return false;
  }
  return Buffer.byteLength(JSON.stringify(value))<=3500000;
}
function validBackground(value){
  return value===undefined||value===null||(record(value)&&fields(value,['kind','image','width','height','sourceName','sourcePage'])&&value.kind==='png'&&typeof value.image==='string'&&value.image.length<=1400000&&/^data:image\/png;base64,[a-z0-9+/]+={0,2}$/i.test(value.image)&&Number.isInteger(value.width)&&value.width>0&&value.width<=2048&&Number.isInteger(value.height)&&value.height>0&&value.height<=2048&&typeof value.sourceName==='string'&&value.sourceName.length<=200&&Number.isInteger(value.sourcePage)&&value.sourcePage>=1&&value.sourcePage<=20);
}
function validBody(pathname,body){
  if(!record(body)||!fields(body,['expectedRevision','document','operation','notebookId','pageId','name','firstPageName','background','versionId','confirmed','expectedPageRevision'])||!validRevision(body.expectedRevision))return false;
  if(pathname==='/api/ink'||/^\/api\/notebooks\/[a-f0-9]{32}\/pages\/[a-f0-9]{32}$/i.test(pathname))return validDocument(body.document);
  if(pathname.endsWith('/history/restore'))return validRevision(body.versionId);
  if(pathname!=='/api/notebooks'||typeof body.operation!=='string')return false;
  if(body.operation==='createNotebook')return validName(body.name)&&(body.firstPageName===undefined||validName(body.firstPageName))&&validBackground(body.background);
  if(!validId(body.notebookId))return false;
  if(body.operation==='createPage')return validName(body.name)&&validBackground(body.background);
  if(body.operation==='renameNotebook')return validName(body.name);
  if(body.operation==='restoreNotebook')return true;
  if(!validId(body.pageId))return false;
  if(body.operation==='renamePage')return validName(body.name);
  if(['duplicatePage','archivePage','restorePage'].includes(body.operation))return true;
  return body.operation==='purgeHistory'&&body.confirmed===true&&validRevision(body.expectedPageRevision);
}
function admitRequest(request){
  if(typeof request.url!=='string'||request.url.length>1024)return{error:send(400,{error:'Invalid fixture address'})};
  let url;try{url=new URL(request.url);}catch{return{error:send(400,{error:'Invalid fixture address'})};}
  // Every browser request is fulfilled in memory or rejected. No listener or real network fallback exists.
  if(url.origin!==origin||url.username||url.password||url.hash)return{error:send(403,{error:'External fixture request blocked'})};
  const isPage=/^\/api\/notebooks\/[a-f0-9]{32}\/pages\/[a-f0-9]{32}(?:\/history(?:\/restore)?)?$/i.test(url.pathname);
  if(url.pathname!=='/'&&url.pathname!=='/favicon.ico'&&!assets.has(url.pathname)&&url.pathname!=='/api/ink'&&url.pathname!=='/api/notebooks'&&!isPage)return{error:send(404,{error:'Unknown fixture address'})};
  const query=[...url.searchParams];
  if(query.length>1||query.some(([key,value])=>!(url.pathname==='/'&&key==='store'&&value==='1')&&!((url.pathname==='/api/ink'||isPage)&&!url.pathname.includes('/history')&&key==='revision'&&validRevision(value))))return{error:send(400,{error:'Invalid fixture query'})};
  if(!['GET','POST'].includes(request.method)||(!url.pathname.startsWith('/api/')&&request.method!=='GET')||(url.pathname.endsWith('/history')&&request.method!=='GET')||(url.pathname.endsWith('/history/restore')&&request.method!=='POST'))return{error:send(405,{error:'Invalid fixture method'})};
  let body;
  if(request.method==='POST'){
    if(typeof request.contentType!=='string'||request.contentType.toLowerCase()!=='application/json')return{error:send(415,{error:'Fixture accepts JSON only'})};
    if(!Buffer.isBuffer(request.bytes)||!request.bytes.length||request.bytes.length>maxRequestBytes)return{error:send(413,{error:'Fixture request too large'})};
    try{body=JSON.parse(request.bytes.toString('utf8'));}catch{return{error:send(400,{error:'Invalid fixture JSON'})};}
    if(!validBody(url.pathname,body))return{error:send(400,{error:'Invalid fixture API envelope'})};
  }else if(request.bytes?.length)return{error:send(400,{error:'Unexpected fixture body'})};
  return{url,body};
}
async function handler(request){
  const admitted=admitRequest(request);if(admitted.error)return admitted.error;
  const{url,body}=admitted;
  if(!url.pathname.startsWith('/api/')){
    if(assets.has(url.pathname))return assets.get(url.pathname);
    if(url.pathname==='/favicon.ico')return{status:204,body:''};
    return{status:200,contentType:'text/html',body:`<!doctype html><html><head><meta charset="utf-8"><style>:root{--canvas:#15191e;--surface:#232a31;--outline:#48505a;--ink:#f4f5f7;--secondary:#b5bec8;--cyan:#8de3ec;--amber:#e9b77d}*{box-sizing:border-box}body{margin:0;background:var(--canvas);color:var(--ink);font:16px sans-serif}button{color:inherit;background:var(--surface);border:1px solid var(--outline);border-radius:6px;padding:8px}[hidden]{display:none!important}</style><link rel="stylesheet" href="/pen.css"><script>window.native=false;window.notice=()=>{};window.confirmAction=async()=>{if(window.fixtureHoldConfirm)return await new Promise(resolve=>window.fixtureResolveConfirm=resolve);return true;};window.api=async(route,body)=>{const response=await fetch(route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});const value=await response.json();if(!response.ok)throw Error(value.error||'Fixture request failed');return value;};window.fixtureWorkspaceContext='fixture-connection-1';window.fixtureContextRequests=[];window.captureWorkspaceContext=async()=>fixtureWorkspaceContext;window.apiWithWorkspaceContext=async(path,body,context)=>{if(context!==fixtureWorkspaceContext)throw Error('The fixture PC connection changed.');fixtureContextRequests.push({path,context});const response=await api(path,body);if(context!==fixtureWorkspaceContext)throw Error('The fixture PC connection changed before delivery.');return response;};window.fixtureObservers=[];window.IntersectionObserver=class{constructor(callback){this.callback=callback;this.targets=[];fixtureObservers.push(this)}observe(target){this.targets.push(target)}disconnect(){this.targets=[]}};window.fixtureShowPreviews=()=>{for(const observer of fixtureObservers)if(observer.targets.length)observer.callback(observer.targets.map(target=>({target,isIntersecting:true})))};</script></head><body>${url.searchParams.has('store')?'<script src="/notebook-store.js"></script>':''}<script src="/pen.js"></script><script src="/notebook-ui.js"></script><script>window.addEventListener('DOMContentLoaded',()=>DeskPen.setActive(true));</script></body></html>`};
  }
  const recorded={method:request.method,path:url.pathname,query:url.search,body:clone(body??null)};state.requests.push(recorded);allRequests.push(recorded);
  if(state.offlineNotebooks&&url.pathname.startsWith('/api/notebooks'))return send(503,{error:'Fixture PC is offline.'});
  if(url.pathname==='/api/ink'){
    if(request.method==='GET')return send(200,url.searchParams.get('revision')===state.legacy.revision?{unchanged:true,workspaceId:state.legacy.workspaceId,revision:state.legacy.revision}:state.legacy);
    if(body.expectedRevision!==state.legacy.revision)return send(409,{error:'The scratchpad changed. Review the PC copy.'});
    state.legacy={...state.legacy,revision:hash(body.document),document:clone(body.document)};return send(200,state.legacy);
  }
  if(url.pathname==='/api/notebooks'){
    if(request.method==='GET')return send(200,catalog());
    if(body.expectedRevision!==state.catalogRevision)return send(409,{error:'The notebook library changed. Reload it.'});
    const book=state.books.find(book=>book.id===body.notebookId);
    switch(body.operation){
      case 'createNotebook':{const bid=newId(),pid=newId();state.books.push({id:bid,name:body.name,pages:[{id:pid,name:body.firstPageName||'Page 1',archived:false,legacy:false}]});state.pages.set(pid,snapshot(bid,pid,empty(),body.background||null));break;}
      case 'createPage':{if(state.failNextCreatePage){state.failNextCreatePage=false;return send(503,{error:'Fixture page storage is unavailable.'});}if(!book)return send(400,{error:'Choose a notebook.'});const pid=newId();book.pages.push({id:pid,name:body.name,archived:false,legacy:false});state.pages.set(pid,snapshot(book.id,pid,empty(),body.background||null));break;}
      case 'renameNotebook':book.name=body.name;break;
      case 'renamePage':book.pages.find(page=>page.id===body.pageId).name=body.name;break;
      case 'duplicatePage':{const from=state.pages.get(body.pageId),pid=newId(),old=book.pages.find(page=>page.id===body.pageId);book.pages.push({id:pid,name:old.name+' copy',archived:false,legacy:false});state.pages.set(pid,snapshot(book.id,pid,from.document,from.background));break;}
      case 'archivePage':book.pages.find(page=>page.id===body.pageId).archived=true;break;
      case 'restorePage':book.pages.find(page=>page.id===body.pageId).archived=false;break;
      case 'restoreNotebook':book.archived=false;break;
      case 'purgeHistory':if(!body.confirmed||body.expectedPageRevision!==state.pages.get(body.pageId).revision)return send(409,{error:'The page changed. Review its history again.'});state.histories.delete(body.pageId);break;
      default:return send(400,{error:'Unexpected notebook operation.'});
    }
    state.catalogRevision=hash(catalog());return send(200,catalog());
  }
  const match=/^\/api\/notebooks\/([a-f0-9]{32})\/pages\/([a-f0-9]{32})(\/history(?:\/restore)?)?$/.exec(url.pathname);
  if(match){
    const [,bid,pid,suffix]=match,current=state.pages.get(pid);if(!current||current.notebookId!==bid)return send(404,{error:'Notebook page unavailable.'});
    if(suffix==='/history')return send(200,{versions:(state.histories.get(pid)||[]).map(version=>({id:version.id,created:version.created,strokeCount:version.document.strokes.length,noteLength:version.document.notes.length}))});
    if(suffix==='/history/restore'){
      if(body.expectedRevision!==current.revision)return send(409,{error:'The page changed. Review it before restoring.'});
      const chosen=(state.histories.get(pid)||[]).find(version=>version.id===body.versionId);if(!chosen)return send(400,{error:'History version not found.'});return send(200,savePage(pid,chosen.document));
    }
    if(request.method==='GET'){
      if(state.holdPage===pid){await new Promise(resolve=>state.holdRequests.push(resolve));}
      const saved=state.pages.get(pid);return send(200,url.searchParams.get('revision')===saved.revision?{notebookId:bid,pageId:pid,unchanged:true,workspaceId:saved.workspaceId,revision:saved.revision,recoveryWarning:''}:saved);
    }
    if(state.failPageSaves)return send(503,{error:'Fixture notebook storage is offline.'});
    if(body.expectedRevision!==current.revision)return send(409,{error:'The page changed. Review its newest copy.'});
    return send(200,savePage(pid,body.document));
  }
  return send(404,{error:'Unexpected fixture API'});
}
function release(){if(!state)return;state.holdPage=null;for(const resolve of state.holdRequests.splice(0))resolve();}
function check(label,value){assert.ok(value,label);checks++;console.log('PASS '+label);}
async function fixture(options={}){
  reset();const context=await browser.newContext({viewport:{width:1200,height:900},serviceWorkers:'block'});
  await context.route('**/*',async route=>{
    const request=route.request();
    try{await route.fulfill(await handler({url:request.url(),method:request.method(),contentType:request.headers()['content-type'],bytes:request.postDataBuffer()}));}
    catch(error){errors.push('Fixture route failure: '+String(error));await route.fulfill(send(500,{error:'Fixture routing failed'})).catch(()=>{});}
  });
  await context.addInitScript(({document,limitedSelection})=>{if(localStorage.getItem('deskwing.pen.v1')===null)localStorage.setItem('deskwing.pen.v1',JSON.stringify(document));if(limitedSelection){Object.defineProperty(window,'indexedDB',{configurable:true,value:undefined});localStorage.setItem('deskwing.notebooks.selection.v1',JSON.stringify({version:limitedSelection.selection.version,selection:limitedSelection.selection.selection}));localStorage.setItem('deskwing.notebooks.catalog.v1:'+limitedSelection.selection.selection.libraryId,JSON.stringify(limitedSelection.selection.catalog));localStorage.setItem(limitedSelection.key,JSON.stringify(limitedSelection.document));}},{document:legacy(),limitedSelection:options.limitedSelection||null});
  const page=await context.newPage();page.setDefaultTimeout(7000);page.on('pageerror',error=>errors.push(error.message));
  await page.goto(origin+(options.store!==false?'/?store=1':''));await page.locator('#pen-canvas').waitFor();if(!options.limitedSelection)await page.waitForFunction(()=>DeskPen.notebooks.state().phase==='synced');
  return{page,context,async close(){release();await context.close();}};
}
async function openLibrary(page){await page.locator('#pen-notebooks').click();await page.waitForFunction(()=>document.querySelector('#pen-notebook-panel')?.getAttribute('aria-busy')==='false');await page.locator('[data-page-id="'+firstPage+'"]').waitFor();}
async function openPage(page,pid){await page.locator('[data-page-id="'+pid+'"]').click();await page.waitForFunction(pid=>DeskPen.notebooks.state().page?.pageId===pid,pid);await page.waitForFunction(()=>DeskPen.notebooks.state().phase==='synced');}
async function idle(page){await page.waitForFunction(()=>document.querySelector('#pen-notebook-panel')?.getAttribute('aria-busy')==='false');}
async function notes(page,value){await page.locator('#pen-notes').fill(value);await page.evaluate(()=>DeskPen.notebooks.flush());}

(async()=>{
  reset();
  const get=(url,method='GET')=>({url,method});
  check('the fixture blocks external origins, URL credentials and unknown asset paths before accessing state',admitRequest(get('https://external.invalid/pen.js')).error.status===403&&admitRequest(get('https://user@deskwing-notebook-fixture.invalid/')).error.status===403&&admitRequest(get(origin+'/missing.js')).error.status===404&&state.requests.length===0);
  check('the fixture rejects unrecognized methods, duplicate query parameters and overlong addresses',admitRequest(get(origin+'/api/ink','DELETE')).error.status===405&&admitRequest(get(origin+'/?store=1&store=1')).error.status===400&&admitRequest(get(origin+'/?'+('x'.repeat(1024)))).error.status===400);
  const post=bytes=>({url:origin+'/api/ink',method:'POST',contentType:'application/json',bytes});
  check('fixture JSON input has an enforced byte cap and rejects malformed or scalar envelopes',admitRequest(post(Buffer.alloc(maxRequestBytes+1))).error.status===413&&admitRequest(post(Buffer.from('{'))).error.status===400&&admitRequest(post(Buffer.from('null'))).error.status===400&&admitRequest(post(Buffer.from('[]'))).error.status===400);
  check('fixture API envelopes reject non-string revisions, extra document fields and out-of-range pen coordinates',admitRequest(post(Buffer.from(JSON.stringify({expectedRevision:{length:64},document:empty()})))).error.status===400&&admitRequest(post(Buffer.from(JSON.stringify({expectedRevision:state.legacy.revision,document:{...empty(),extra:{unbounded:[]}}})))).error.status===400&&admitRequest(post(Buffer.from(JSON.stringify({expectedRevision:state.legacy.revision,document:{...legacy(),strokes:[{tool:'pen',color:'#23313f',width:3,points:[[2,.2,.5]]}]}})))).error.status===400);
  browser=await chromium.launch({headless:true,...(process.env.TABLETDECK_CHROME?{executablePath:process.env.TABLETDECK_CHROME}:{})});
  let f=await fixture();
  try{
    check('opening the legacy scratchpad does not discover or mutate notebook data automatically',!state.requests.some(request=>request.path==='/api/notebooks'));
    await openLibrary(f.page);check('explicit library discovery uses the real catalog summary and keeps the original scratchpad active',await f.page.locator('.notebook-page').count()===2&&await f.page.evaluate(()=>DeskPen.notebooks.state().page===null));
    await openPage(f.page,firstPage);const originalBackup=await f.page.evaluate(()=>localStorage.getItem('deskwing.pen.v1'));
    await f.page.locator('#pen-notes').fill('Notes saved before switching');await openPage(f.page,secondPage);
    check('changing pages saves pending notes to the previous page before opening another',state.pages.get(firstPage).document.notes==='Notes saved before switching'&&await f.page.locator('#pen-notes').inputValue()==='');
    check('notebook edits never replace the legacy local backup or legacy remote document',await f.page.evaluate(()=>localStorage.getItem('deskwing.pen.v1'))===originalBackup&&state.legacy.document.notes==='Original device scratchpad');
    check('opened notebook pages keep separately scoped local document and sync backups',await f.page.evaluate(async({libraryId,bookId,firstPage,secondPage})=>{const first=await DeskNotebookStore.getPage(`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${firstPage}`),second=await DeskNotebookStore.getPage(`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${secondPage}`);return first.document.notes==='Notes saved before switching'&&!!first.sync&&!!second.document&&!!second.sync;},{libraryId,bookId,firstPage,secondPage}));
    await f.page.locator('#notebook-name').fill('Meeting notes');await f.page.getByRole('button',{name:'New notebook',exact:true}).click();await idle(f.page);
    const created=state.books.find(book=>book.name==='Meeting notes');check('creating a named notebook adds a separate notebook and opens its initial page',!!created&&await f.page.evaluate(id=>DeskPen.notebooks.state().page?.notebookId===id,created.id));
    await f.page.locator('#notebook-page-name').fill('Agenda');await f.page.getByRole('button',{name:'New page',exact:true}).click();await idle(f.page);
    check('adding a named page keeps existing pages and opens the new page',created.pages.length===2&&created.pages.some(page=>page.name==='Agenda')&&await f.page.evaluate(()=>DeskPen.notebooks.state().page?.pageName==='Agenda'));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);
    await f.page.evaluate(()=>{const real=IDBDatabase.prototype.transaction;window.fixtureRestoreStorage=()=>IDBDatabase.prototype.transaction=real;IDBDatabase.prototype.transaction=function(stores,mode,...rest){if(mode==='readwrite')throw new DOMException('Quota exceeded','QuotaExceededError');return real.call(this,stores,mode,...rest);};});
    await f.page.locator('#pen-notes').fill('Must stay on this page');await f.page.locator('[data-page-id="'+secondPage+'"]').click();await idle(f.page);
    check('a failed local backup blocks page switching and preserves the current draft',await f.page.evaluate(()=>DeskPen.notebooks.state().page.pageId)==firstPage&&await f.page.locator('#pen-notes').inputValue()==='Must stay on this page'&&/backup|save|storage/i.test(await f.page.locator('#notebook-status').textContent()));
    await f.page.evaluate(()=>fixtureRestoreStorage());
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);state.holdPage=secondPage;
    await f.page.locator('[data-page-id="'+secondPage+'"]').click();await f.page.waitForFunction(()=>document.querySelector('#pen-notebook-panel').getAttribute('aria-busy')==='true');
    await f.page.locator('#pen-notebooks').click();release();await idle(f.page);
    check('closing the library prevents a late page response from replacing the original scratchpad',await f.page.evaluate(()=>DeskPen.notebooks.state().page===null)&&await f.page.locator('#pen-notes').inputValue()==='Original device scratchpad');
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);state.holdPage=secondPage;
    await f.page.locator('[data-page-id="'+secondPage+'"]').click();await f.page.waitForFunction(()=>document.querySelector('#pen-notebook-panel').getAttribute('aria-busy')==='true');
    await f.page.evaluate(()=>DeskPen.setActive(false));release();await f.page.waitForTimeout(100);
    check('leaving Ink & notes prevents a late notebook response from opening a page',await f.page.evaluate(()=>!DeskPen.isActive()&&DeskPen.notebooks.state().page===null));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);
    await notes(f.page,'Saved first version');await notes(f.page,'Saved second version');
    await f.page.getByRole('button',{name:'Page history',exact:true}).click();await idle(f.page);
    check('history displays real 64-character version identities returned by the backend',await f.page.locator('#notebook-history button').count()>=2&&!/invalid/.test(await f.page.locator('#notebook-status').textContent()));
    const restore=f.page.locator('#notebook-history button').filter({hasText:/^Restore /}).first();await restore.click();await idle(f.page);
    check('explicit history restoration sends the reviewed page revision and opens a durably saved retained version',state.requests.some(request=>request.path.endsWith('/history/restore')&&request.body!==null&&typeof request.body==='object'&&typeof request.body.versionId==='string'&&request.body.versionId.length===64&&typeof request.body.expectedRevision==='string'&&request.body.expectedRevision.length===64)&&state.pages.get(firstPage).document.notes==='Saved first version'&&await f.page.locator('#pen-notes').inputValue()==='Saved first version'&&/Previous page version restored/.test(await f.page.locator('#notebook-status').textContent())&&!await f.page.locator('#pen-status').evaluate(node=>node.classList.contains('pen-error')));
    await f.page.getByRole('button',{name:'Page history',exact:true}).click();await idle(f.page);await f.page.evaluate(()=>window.fixtureHoldConfirm=true);
    await f.page.locator('#notebook-history button').filter({hasText:/^Restore /}).first().click();await f.page.waitForFunction(()=>typeof fixtureResolveConfirm==='function');
    const before=state.requests.filter(request=>request.path.endsWith('/history/restore')).length;await f.page.locator('#pen-notes').fill('New draft during confirmation');await f.page.evaluate(()=>fixtureResolveConfirm(true));await idle(f.page);
    check('a delayed restore confirmation cannot overwrite a newer local draft',state.requests.filter(request=>request.path.endsWith('/history/restore')).length===before&&await f.page.locator('#pen-notes').inputValue()==='New draft during confirmation'&&/changed/.test(await f.page.locator('#notebook-status').textContent()));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);await f.page.getByRole('button',{name:'Zoom in',exact:true}).click();await f.page.getByRole('button',{name:'Zoom in',exact:true}).click();
    await f.page.locator('#pen-canvas').scrollIntoViewIfNeeded();
    await f.page.locator('#pen-canvas').evaluate(canvas=>{const rect=canvas.getBoundingClientRect();for(const [type,x,y]of[['pointerdown',.25,.35],['pointermove',.6,.7],['pointerup',.6,.7]])canvas.dispatchEvent(new PointerEvent(type,{pointerId:9,pointerType:'pen',button:0,buttons:type==='pointerup'?0:1,pressure:.65,clientX:rect.left+rect.width*x,clientY:rect.top+rect.height*y,bubbles:true,cancelable:true}));});
    const drawn=await f.page.evaluate(()=>DeskPen.notebooks.state());check('zoomed pen input stays normalized to the visible transformed page',drawn.zoom>1&&Math.abs(drawn.document.strokes[0].points[0][0]-.25)<.001&&Math.abs(drawn.document.strokes[0].points[0][1]-.35)<.001);
    await f.page.locator('#notebook-pan').click();const before=await f.page.evaluate(()=>DeskPen.notebooks.state());
    await f.page.locator('#pen-canvas').evaluate(canvas=>{const rect=canvas.getBoundingClientRect();for(const [type,dx,dy]of[['pointerdown',0,0],['pointermove',80,45],['pointerup',80,45]])canvas.dispatchEvent(new PointerEvent(type,{pointerId:12,pointerType:'mouse',button:0,buttons:type==='pointerup'?0:1,clientX:rect.left+rect.width*.5+dx,clientY:rect.top+rect.height*.5+dy,bubbles:true,cancelable:true}));});
    const panned=await f.page.evaluate(()=>DeskPen.notebooks.state());check('Pan moves the zoomed page without adding ink strokes',panned.panX>before.panX&&panned.panY>before.panY&&panned.document.strokes.length===before.document.strokes.length);
    await f.page.getByRole('button',{name:'Fit page',exact:true}).click();check('Fit page returns to an unpanned drawing surface',await f.page.evaluate(()=>{const state=DeskPen.notebooks.state();return state.zoom===1&&state.panX===0&&state.panY===0&&!state.panMode;}));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);
    await f.page.evaluate(()=>{
      const originalCreate=URL.createObjectURL.bind(URL),originalRevoke=URL.revokeObjectURL.bind(URL);
      window.fixturePdfCase='valid';window.fixtureObjectUrlOverride=null;window.fixtureObjectUrls=[];window.fixtureRevokedUrls=[];window.fixturePdfClicks=[];
      URL.createObjectURL=blob=>{fixtureObjectUrls.push({type:blob.type,size:blob.size,fresh:blob!==window.fixtureExportBlob});return fixtureObjectUrlOverride===null?originalCreate(blob):fixtureObjectUrlOverride;};
      URL.revokeObjectURL=value=>{fixtureRevokedUrls.push(value);originalRevoke(value);};
      HTMLAnchorElement.prototype.click=function(){fixturePdfClicks.push({href:this.href,download:this.download});};
      window.DeskNotebookPdf={exportDocument:async()=>{
        let value;
        switch(fixturePdfCase){
          case 'wrong MIME':value=new Blob(['%PDF-1.7\n%%EOF'],{type:'text/html'});break;
          case 'non-Blob':value={type:'application/pdf',size:14,slice(){return new Blob(['%PDF-1.7\n%%EOF']);}};break;
          case 'oversized PDF':value=new Blob([new Uint8Array(32*1024*1024+1)],{type:'application/pdf'});break;
          case 'HTML payload':value=new Blob(['<!doctype html><p>%%EOF'],{type:'application/pdf'});break;
          case 'missing EOF':value=new Blob(['%PDF-1.7\nincomplete payload'],{type:'application/pdf'});break;
          case 'unsupported header':value=new Blob(['%PDF-9.9\n%%EOF'],{type:'application/pdf'});break;
          default:value=new Blob(['%PDF-1.7\nfixture inert export\n%%EOF\n'],{type:'application/pdf'});
        }
        window.fixtureExportBlob=value;return value;
      }};
    });
    for(const kind of ['wrong MIME','non-Blob','oversized PDF','HTML payload','missing EOF','unsupported header']){
      await f.page.evaluate(kind=>{window.fixturePdfCase=kind;fixtureObjectUrls.length=0;fixturePdfClicks.length=0;},kind);
      await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await idle(f.page);
      check('PDF export rejects '+kind+' before creating any file URL or download',await f.page.evaluate(()=>fixtureObjectUrls.length===0&&fixturePdfClicks.length===0)&&/invalid|incomplete/.test(await f.page.locator('#notebook-status').textContent()));
    }
    for(const address of ['https://external.invalid/export.pdf','blob:https://external.invalid/11111111-1111-1111-1111-111111111111',origin+'/not-a-blob','blob:'+origin+'/11111111-1111-1111-1111-111111111111?download=1']){
      await f.page.evaluate(address=>{window.fixturePdfCase='valid';window.fixtureObjectUrlOverride=address;fixtureObjectUrls.length=0;fixtureRevokedUrls.length=0;fixturePdfClicks.length=0;},address);
      await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await idle(f.page);
      check('PDF export revokes and blocks an unverified object URL '+address,await f.page.evaluate(address=>fixtureObjectUrls.length===1&&fixtureRevokedUrls.length===1&&fixtureRevokedUrls[0]===address&&fixturePdfClicks.length===0,address)&&/address.*verified/.test(await f.page.locator('#notebook-status').textContent()));
    }
    await f.page.evaluate(()=>{window.fixtureObjectUrlOverride=null;fixtureObjectUrls.length=0;fixturePdfClicks.length=0;});
    await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await idle(f.page);
    check('a valid PDF envelope uses a fresh immutable PDF Blob and an exact same-origin UUID object URL',await f.page.evaluate(()=>fixtureObjectUrls.length===1&&fixtureObjectUrls[0].fresh&&fixtureObjectUrls[0].type==='application/pdf'&&fixturePdfClicks.length===1&&fixturePdfClicks[0].href.startsWith('blob:'+location.origin+'/')&&fixturePdfClicks[0].download.endsWith('.pdf')));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await f.page.evaluate(()=>{const canvas=document.createElement('canvas');canvas.width=32;canvas.height=24;const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,32,24);const png=canvas.toDataURL();window.DeskNotebookPdf={importFile:async()=>({name:'Fixture PDF',pages:[1,2].map(sourcePage=>({width:32,height:24,backgroundPng:png,sourcePage}))})};});
    state.failNextCreatePage=true;await f.page.locator('#notebook-pdf-file').setInputFiles({name:'fixture.pdf',mimeType:'application/pdf',buffer:Buffer.from('%PDF-1.7 isolated fixture')});await idle(f.page);
    const imported=state.books.find(book=>book.name==='Fixture PDF');check('a partially failed PDF import retains the new notebook and every completed page',!!imported&&imported.pages.length===1&&!!state.pages.get(imported.pages[0].id).background&&state.books.find(book=>book.id===bookId).pages.length===2);
    check('partial import reports a recoverable error instead of claiming a complete import',/unavailable|stopped|remaining|imported|retry/i.test(await f.page.locator('#notebook-status').textContent())&&!/^Imported 2 PDF pages/.test(await f.page.locator('#notebook-status').textContent()));
    await f.page.getByRole('button',{name:'Resume PDF import',exact:true}).click();await idle(f.page);
    check('resuming a partial PDF import adds only missing source pages without duplicating its notebook',state.books.filter(book=>book.name==='Fixture PDF').length===1&&imported.pages.length===2&&new Set(imported.pages.map(page=>state.pages.get(page.id).background.sourcePage)).size===2);
  }finally{await f.close();}

  f=await fixture();try{
    const png=await f.page.evaluate(()=>{const canvas=document.createElement('canvas');canvas.width=32;canvas.height=24;canvas.getContext('2d').fillRect(0,0,32,24);return canvas.toDataURL();});
    for(const pid of [firstPage,secondPage,'7'.repeat(32),'8'.repeat(32)]){if(!state.pages.has(pid)){state.books[0].pages.push({id:pid,name:'Thumbnail '+pid[0],legacy:false,archived:false});state.pages.set(pid,snapshot(bookId,pid));}state.pages.get(pid).background={kind:'png',image:png,width:32,height:24,sourceName:'Preview PDF',sourcePage:1};}
    await f.page.evaluate(()=>{const OriginalImage=window.Image;window.fixturePendingDecodes=[];window.Image=function(){const image=new OriginalImage();let loaded;Object.defineProperty(image,'onload',{configurable:true,get:()=>loaded,set:value=>loaded=value});image.addEventListener('load',()=>{if(loaded)fixturePendingDecodes.push(()=>loaded?.());});return image;};});
    await openLibrary(f.page);await f.page.evaluate(()=>fixtureShowPreviews());await f.page.waitForFunction(()=>fixturePendingDecodes.length===2);
    check('thumbnail concurrency remains bounded while PNG decoding callbacks are pending',state.requests.filter(request=>request.method==='GET'&&/^\/api\/notebooks\/.+\/pages\//.test(request.path)).length===2);
    await f.page.evaluate(()=>fixturePendingDecodes.shift()());await f.page.waitForFunction(()=>fixturePendingDecodes.length===2);
    check('finishing one thumbnail decode admits exactly one queued page preview',state.requests.filter(request=>request.method==='GET'&&/^\/api\/notebooks\/.+\/pages\//.test(request.path)).length===3);
    await f.page.evaluate(()=>{for(const release of fixturePendingDecodes.splice(0))release();});
  }finally{await f.close();}

  for(const nativePicker of [false,true]){
    f=await fixture();try{
      await openLibrary(f.page);await openPage(f.page,firstPage);const marker=state.requests.length;
      await f.page.evaluate(nativePicker=>{
        window.native=nativePicker;window.fixtureImportCalls=0;const original=DeskPen;
        window.DeskPen={...original,notebooks:{...original.notebooks,flush:()=>new Promise(resolve=>window.fixtureReleaseImportFlush=resolve)}};
        const read=async()=>{fixtureImportCalls++;return{cancelled:true,pages:[]};};window.DeskNotebookPdf={importFile:read,importNative:read};
      },nativePicker);
      if(nativePicker)await f.page.getByRole('button',{name:'Import PDF',exact:true}).click();
      else await f.page.locator('#notebook-pdf-file').setInputFiles({name:'before-flush.pdf',mimeType:'application/pdf',buffer:Buffer.from('%PDF-1.7\n%%EOF')});
      await f.page.waitForFunction(()=>typeof fixtureReleaseImportFlush==='function');
      await f.page.evaluate(async()=>{window.fixtureWorkspaceContext='fixture-replaced-during-flush';window.dispatchEvent(new Event('deskwing-workspace-contextchange'));await DeskNotebookUi.refresh();fixtureReleaseImportFlush();await new Promise(resolve=>setTimeout(resolve,50));});
      check('changing the PC context while import flush is pending cannot start the retired '+(nativePicker?'native picker':'file read')+' under the refreshed catalog',await f.page.evaluate(()=>fixtureImportCalls===0&&fixtureContextRequests.some(request=>request.path==='/api/notebooks'&&request.context==='fixture-replaced-during-flush'))&&!state.requests.slice(marker).some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')));
    }finally{await f.close();}
  }

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);let downloads=0;f.page.on('download',()=>downloads++);
    await f.page.evaluate(()=>{
      window.fixtureRaceUrls=[];window.fixtureRaceClicks=[];const original=URL.createObjectURL.bind(URL);
      URL.createObjectURL=value=>{fixtureRaceUrls.push(value);return original(value);};
      HTMLAnchorElement.prototype.click=function(){fixtureRaceClicks.push(this.href);};
      window.DeskNotebookPdf={
        importFile:(_file,{signal})=>{window.fixtureOldImportSignal=signal;return new Promise(resolve=>window.fixtureResolveOldImport=resolve);},
        exportDocument:(_document,{signal})=>{window.fixtureNewExportSignal=signal;return new Promise(resolve=>window.fixtureResolveNewExport=resolve);}
      };
    });
    await f.page.locator('#notebook-pdf-file').setInputFiles({name:'retired-import.pdf',mimeType:'application/pdf',buffer:Buffer.from('%PDF-1.7\n%%EOF')});
    await f.page.waitForFunction(()=>typeof fixtureResolveOldImport==='function');
    await f.page.evaluate(()=>{window.fixtureWorkspaceContext='fixture-connection-after-import';window.dispatchEvent(new Event('deskwing-workspace-contextchange'));});await idle(f.page);
    await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await f.page.waitForFunction(()=>typeof fixtureResolveNewExport==='function');
    await f.page.evaluate(async()=>{const canvas=document.createElement('canvas');canvas.width=32;canvas.height=24;fixtureResolveOldImport({name:'Retired import must not return',pages:[{width:32,height:24,backgroundPng:canvas.toDataURL(),sourcePage:1}]});await new Promise(resolve=>setTimeout(resolve,50));});
    check('a retired import cannot finish a newer export or create a notebook after the PC context changes',await f.page.evaluate(()=>fixtureOldImportSignal.aborted&&!fixtureNewExportSignal.aborted&&document.getElementById('pen-notebook-panel').getAttribute('aria-busy')==='true')&&!state.books.some(book=>book.name==='Retired import must not return'));
    await f.page.getByRole('button',{name:'Cancel PDF task',exact:true}).click();
    const cancelled=await f.page.evaluate(()=>fixtureNewExportSignal.aborted);
    await f.page.evaluate(()=>fixtureResolveNewExport(new Blob(['%PDF-1.7\nrace export must not save\n%%EOF'],{type:'application/pdf'})));await idle(f.page);
    check('Cancel PDF still aborts the newer export after a retired import resolves, without creating any file URL or download',cancelled&&downloads===0&&await f.page.evaluate(()=>fixtureRaceUrls.length===0&&fixtureRaceClicks.length===0));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await f.page.evaluate(()=>{window.native=true;window.fixtureHidden=false;Object.defineProperty(document,'hidden',{configurable:true,get:()=>fixtureHidden});window.DeskNotebookPdf={importNative:()=>new Promise(resolve=>window.fixtureResolveImport=resolve)};});
    await f.page.getByRole('button',{name:'Import PDF',exact:true}).click();await f.page.waitForFunction(()=>typeof fixtureResolveImport==='function');
    await f.page.evaluate(()=>{window.fixtureHidden=true;DeskPen.suspend();const canvas=document.createElement('canvas');canvas.width=32;canvas.height=24;fixtureResolveImport({name:'Native picked PDF',pages:[{width:32,height:24,backgroundPng:canvas.toDataURL(),sourcePage:1}]});});
    await f.page.waitForTimeout(100);check('a native picker result does not commit notebook pages while its original workspace is hidden',!state.books.some(book=>book.name==='Native picked PDF'));
    await f.page.evaluate(()=>{window.fixtureHidden=false;DeskPen.resume();});await idle(f.page);
    check('ordinary picker suspension can resume and finish import in the original workspace',state.books.some(book=>book.name==='Native picked PDF')&&await f.page.evaluate(()=>DeskPen.notebooks.state().page!==null));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await f.page.evaluate(()=>{window.native=true;window.DeskNotebookPdf={importNative:()=>new Promise(resolve=>window.fixtureResolveImport=resolve)};});
    await f.page.getByRole('button',{name:'Import PDF',exact:true}).click();await f.page.waitForFunction(()=>typeof fixtureResolveImport==='function');
    await f.page.evaluate(()=>{DeskPen.setActive(false);const canvas=document.createElement('canvas');canvas.width=32;canvas.height=24;fixtureResolveImport({name:'Stale picked PDF',pages:[{width:32,height:24,backgroundPng:canvas.toDataURL(),sourcePage:1}]});});await f.page.waitForTimeout(100);
    check('a late native picker result cannot import after an explicit workspace change',!state.books.some(book=>book.name==='Stale picked PDF')&&await f.page.evaluate(()=>DeskPen.notebooks.state().page===null));
  }finally{await f.close();}

  f=await fixture();try{
    await openLibrary(f.page);await openPage(f.page,firstPage);const before=state.requests.filter(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')).length;
    await f.page.evaluate(()=>{window.fixtureWorkspaceContext='fixture-connection-2';window.dispatchEvent(new Event('deskwing-workspace-contextchange'));});
    await notes(f.page,'Draft retained after connection change');await f.page.waitForTimeout(100);
    check('a changed PC context unbinds the open notebook and retains its draft without posting it',await f.page.evaluate(()=>DeskPen.notebooks.state().page?.bound===false)&&state.requests.filter(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')).length===before&&state.pages.get(firstPage).document.notes==='');
    await f.page.evaluate(()=>DeskNotebookUi.refresh());await f.page.waitForFunction(()=>DeskPen.notebooks.state().phase==='synced');
    check('fresh catalog verification binds subsequent notebook writes to the newly captured PC context',state.pages.get(firstPage).document.notes==='Draft retained after connection change'&&await f.page.evaluate(()=>fixtureContextRequests.some(request=>request.path.endsWith('/pages/'+'3'.repeat(32))&&request.context==='fixture-connection-2')));
  }finally{await f.close();}

  f=await fixture();try{
    await f.page.evaluate(()=>{window.native=true;delete window.captureWorkspaceContext;delete window.apiWithWorkspaceContext;});await f.page.locator('#pen-notebooks').click();await idle(f.page);
    check('a native host without immutable workspace-context helpers fails closed before notebook traffic',!state.requests.some(request=>request.path.startsWith('/api/notebooks'))&&/Reopen DeskWing/.test(await f.page.locator('#notebook-status').textContent()));
  }finally{await f.close();}

  f=await fixture({store:false});try{
    await openLibrary(f.page);await f.page.locator('[data-page-id="'+firstPage+'"]').click();await idle(f.page);
    check('a missing durable-backup module cannot silently enable notebook editing through legacy localStorage',await f.page.evaluate(()=>DeskPen.notebooks.state().page===null)&&/storage|Reopen/.test(await f.page.locator('#notebook-status').textContent())&&await f.page.locator('#pen-notes').inputValue()==='Original device scratchpad');
  }finally{await f.close();}

  f=await fixture({store:true});try{
    await f.page.waitForFunction(()=>DeskNotebookStore.atomicPageWrites===true);await openLibrary(f.page);await openPage(f.page,firstPage);await notes(f.page,'First indexed notebook');await openPage(f.page,secondPage);await notes(f.page,'Second indexed notebook');
    const prefix=`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${secondPage}`;
    check('notebook IDB bundles retain a committed local revision and document independently of legacy storage',await f.page.evaluate(async prefix=>{const saved=await DeskNotebookStore.getPage(prefix);return saved.localRevision>0&&saved.document.notes==='Second indexed notebook'&&localStorage.getItem(prefix)===null;},prefix));
    state.offlineNotebooks=true;const marker=state.requests.length;await f.page.reload();await f.page.waitForFunction(pid=>DeskPen.notebooks.state().page?.pageId===pid,secondPage);await f.page.waitForTimeout(100);
    check('reload restores the last selected notebook from its durable local copy without binding a PC',await f.page.evaluate(()=>DeskPen.notebooks.state().page?.bound===false)&&await f.page.locator('#pen-notes').inputValue()==='Second indexed notebook'&&!state.requests.slice(marker).some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')));
    await notes(f.page,'Offline changes kept locally');check('offline notebook edits remain local until a fresh catalog is verified',state.pages.get(secondPage).document.notes==='Second indexed notebook'&&!state.requests.slice(marker).some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')));
    state.offlineNotebooks=false;await f.page.evaluate(()=>DeskNotebookUi.refresh());await f.page.waitForFunction(()=>DeskPen.notebooks.state().phase==='synced');
    check('a verified reconnect shares the restored page’s pending local changes with its original PC library',state.pages.get(secondPage).document.notes==='Offline changes kept locally'&&await f.page.evaluate(()=>DeskPen.notebooks.state().page?.bound===true));
  }finally{await f.close();}

  f=await fixture({store:true});try{
    const big=label=>({...empty(),notes:label+' '+('x'.repeat(7000)),strokes:[{tool:'pen',color:'#23313f',width:3,points:Array.from({length:50000},()=>[.12345678901234567,.6543210987654321,.543210987654321])}]});
    state.pages.set(firstPage,snapshot(bookId,firstPage,big('Large first page')));state.pages.set(secondPage,snapshot(bookId,secondPage,big('Large second page')));
    await openLibrary(f.page);await openPage(f.page,firstPage);await idle(f.page);await openPage(f.page,secondPage);await idle(f.page);
    const keys=[firstPage,secondPage].map(pid=>`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${pid}`);
    const stored=await f.page.evaluate(async keys=>{let total=0;const notes=[];for(const key of keys){const page=await DeskNotebookStore.getPage(key);total+=new TextEncoder().encode(JSON.stringify(page.document)).length;notes.push(page.document.notes.slice(0,17));if(page.document.strokes[0].points.length!==50000)throw Error('Large point list changed');}return{total,notes,localStorageCopies:keys.filter(key=>localStorage.getItem(key)!==null).length};},keys);
    check('two large notebook pages exceeding 5 MiB together survive in IndexedDB without localStorage copies',stored.total>5*1024*1024&&stored.localStorageCopies===0&&stored.notes[0].startsWith('Large first page')&&stored.notes[1].startsWith('Large second page'));
  }finally{await f.close();}

  f=await fixture({store:true});try{
    for(let index=0;index<22;index++){const pid=String(index+900).padStart(32,'0');state.books[0].pages.push({id:pid,name:'Library page '+index+' with a descriptive name',archived:false,legacy:false});state.pages.set(pid,snapshot(bookId,pid));}
    check('the selection-limit fixture includes a real catalog larger than 4 KiB',Buffer.byteLength(JSON.stringify(catalog()))>4096);
    await openLibrary(f.page);await openPage(f.page,firstPage);await idle(f.page);
    check('a large catalog can still remember its selected page inside the bounded selection record',await f.page.evaluate(async()=>{const value=await DeskNotebookStore.get(DeskNotebookStore.selectionKey);return value?.selection.pageId==='3'.repeat(32)&&new TextEncoder().encode(JSON.stringify(value)).length<=DeskNotebookStore.limits.selectionBytes;}));
  }finally{await f.close();}

  f=await fixture({store:true});try{
    await openLibrary(f.page);await openPage(f.page,firstPage);await idle(f.page);await notes(f.page,'Committed before storage closed');
    const marker=state.requests.length,prefix=`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${firstPage}`;
    const closed=await f.page.evaluate(async prefix=>{
      const field=document.getElementById('pen-notes');field.value='Unsaved draft rescued after storage closed';field.dispatchEvent(new Event('input',{bubbles:true}));
      return await new Promise((resolve,reject)=>{const request=indexedDB.open('DeskWingNotebooks',2);request.onerror=()=>reject(request.error);request.onsuccess=()=>{const database=request.result,transaction=database.transaction('backups','readonly'),read=transaction.objectStore('backups').get(prefix);read.onerror=()=>reject(read.error);read.onsuccess=()=>{const saved=JSON.parse(read.result.json);database.close();resolve({writable:DeskNotebookStore.atomicPageWrites,notes:saved.document.notes});};};});
    },prefix);
    await f.page.locator('[data-page-id="'+secondPage+'"]').click();await idle(f.page);
    check('durable storage closing during an unsaved edit blocks page switching and preserves both the draft and previous backup',!closed.writable&&closed.notes==='Committed before storage closed'&&await f.page.evaluate(()=>DeskPen.notebooks.state().page?.pageId==='3'.repeat(32))&&await f.page.locator('#pen-notes').inputValue()==='Unsaved draft rescued after storage closed'&&state.pages.get(firstPage).document.notes==='Committed before storage closed');
    await f.page.evaluate(()=>{window.fixtureDraftExports=[];window.DeskNotebookPdf={exportDocument:async value=>{fixtureDraftExports.push(value.pages[0].document.notes);return new Blob(['%PDF-1.7 isolated unsaved-draft rescue\n%%EOF'],{type:'application/pdf'});}};});
    const download=f.page.waitForEvent('download');await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await download;await idle(f.page);
    check('current-page PDF rescue exports the in-memory draft after storage closes without posting or committing it',await f.page.evaluate(()=>fixtureDraftExports.length===1&&fixtureDraftExports[0]==='Unsaved draft rescued after storage closed')&&!state.requests.slice(marker).some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')));
  }finally{await f.close();}

  const cachedDocument={...empty(),notes:'Read-only notebook rescue',strokes:[{tool:'pen',color:'#23313f',width:3,points:[[.2,.3,.5],[.4,.6,.8]]}]};
  const limitedSelection={key:`deskwing.notebooks.page.v1:${libraryId}:${bookId}:${firstPage}`,document:cachedDocument,selection:{version:1,selection:{libraryId,notebookId:bookId,pageId:firstPage,notebookName:'Research',pageName:'First page'},catalog:{libraryId,revision:'A'.repeat(64),notebooks:[{id:bookId,name:'Research',pages:[{id:firstPage,name:'First page',legacy:false,archived:false}]}],archivedNotebooks:[]}}};
  f=await fixture({store:true,limitedSelection});try{
    state.offlineNotebooks=true;await f.page.waitForFunction(()=>DeskPen.notebooks.state().page?.pageId==='3'.repeat(32));
    check('unavailable durable storage opens a preserved notebook copy in read-only mode',await f.page.evaluate(()=>DeskNotebookStore.atomicPageWrites===false&&DeskPen.notebooks.state().localReadOnly)&&await f.page.locator('#pen-notes').getAttribute('readonly')!==null);
    const before=await f.page.evaluate(key=>localStorage.getItem(key),limitedSelection.key);await f.page.locator('#pen-notebooks').click();await idle(f.page);
    await f.page.evaluate(()=>{window.fixtureRescueExports=[];window.DeskNotebookPdf={exportDocument:async value=>{fixtureRescueExports.push({notes:value.pages[0].document.notes,strokeCount:value.pages[0].document.strokes.length});return new Blob(['%PDF-1.7 isolated local rescue\n%%EOF'],{type:'application/pdf'});}};});
    const download=f.page.waitForEvent('download');await f.page.getByRole('button',{name:'Export current page PDF',exact:true}).click();await download;await idle(f.page);
    check('read-only current-page PDF rescue exports saved ink and notes without requiring a remote write',await f.page.evaluate(()=>fixtureRescueExports.length===1&&fixtureRescueExports[0].notes==='Read-only notebook rescue'&&fixtureRescueExports[0].strokeCount===1)&&await f.page.evaluate(key=>localStorage.getItem(key),limitedSelection.key)===before&&!state.requests.some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks')));
  }finally{await f.close();}

  f=await fixture({store:true,limitedSelection});try{
    await f.page.waitForFunction(()=>DeskPen.notebooks.state().page?.pageId==='3'.repeat(32));
    const before=await f.page.evaluate(key=>localStorage.getItem(key),limitedSelection.key);await f.page.evaluate(()=>DeskNotebookUi.refresh());await f.page.evaluate(()=>DeskPen.syncNow());await f.page.waitForTimeout(100);
    check('verifying an empty PC page cannot auto-share or replace a preserved read-only fallback copy',state.pages.get(firstPage).document.notes===''&&!state.requests.some(request=>request.method==='POST'&&request.path.startsWith('/api/notebooks'))&&await f.page.locator('#pen-notes').inputValue()==='Read-only notebook rescue'&&await f.page.locator('#pen-sync-now').isDisabled()&&await f.page.evaluate(key=>localStorage.getItem(key),limitedSelection.key)===before);
  }finally{await f.close();}
  check('notebook workflows never submit a board or settings mutation',!allRequests.some(request=>/^\/api\/(admin\/config|board|profile)/.test(request.path)));
  check('notebook UI completes without uncaught script errors',errors.length===0);
  console.log(`${checks} notebook UI, per-page backup, revision, history, zoom/pan and partial PDF checks passed. No real user board or tablet was accessed.`);
})().catch(error=>{console.error(error.stack||error);process.exitCode=1;}).finally(async()=>{release();if(browser)await browser.close();});
