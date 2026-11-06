'use strict';
// Read-only recovery in an isolated browser origin. No user files, PC API, or tablet are used.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..');
const origin='https://deskwing-notebook-recovery.invalid';
// There is no listening socket or network fallback. Preload only these fixed test
// assets before routing begins; URLs never become filesystem paths.
const assets=new Map([
  [origin+'/',{contentType:'text/html; charset=utf-8',body:Buffer.from('<!doctype html><meta charset="utf-8"><section id="recovery"></section><script src="/fixture-init.js"></script><script src="/notebook-store.js"></script><script src="/pen.js"></script><script src="/notebook-recovery.js"></script>')}],
  [origin+'/fixture-init.js',{contentType:'text/javascript; charset=utf-8',body:Buffer.from('window.native=false;')}],
  ...['pen.js','notebook-store.js','notebook-recovery.js'].map(name=>[origin+'/'+name,{contentType:'text/javascript; charset=utf-8',body:fs.readFileSync(path.join(root,'companion/wwwroot',name))}])
]);
for(const asset of assets.values())assert.ok(asset.body.length<=2*1024*1024,'fixed fixture asset exceeds 2 MiB');
assert.ok([...assets.values()].reduce((total,asset)=>total+asset.body.length,0)<=6*1024*1024,'fixture assets exceed 6 MiB');
function admittedAsset(url,method,body){return method==='GET'&&(body===null||body===undefined)&&assets.get(url);}
async function routeFixture(context){
  await context.route('**/*',async route=>{
    const request=route.request(),asset=admittedAsset(request.url(),request.method(),request.postDataBuffer());
    await route.fulfill(asset?{status:200,contentType:asset.contentType,body:asset.body,headers:{'Cache-Control':'no-store','Content-Security-Policy':"default-src 'none'; script-src 'self'; connect-src 'none'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'",'X-Content-Type-Options':'nosniff'}}:{status:404,contentType:'text/plain; charset=utf-8',body:'Fixture request rejected.',headers:{'Cache-Control':'no-store'}});
  });
}
const selection={libraryId:'a'.repeat(32),notebookId:'b'.repeat(32),pageId:'c'.repeat(32),notebookName:'Notebook <img src=x onerror="window.unsafe=true">',pageName:'Page one'};
const other={...selection,pageId:'d'.repeat(32),pageName:'Page two'};
const key=s=>'deskwing.notebooks.page.v1:'+s.libraryId+':'+s.notebookId+':'+s.pageId;
const document=notes=>({version:1,strokes:[{tool:'pen',color:'#23313f',width:3,points:[[0.1,0.2,0.4],[0.7,0.8,0.8]]}],notes,penOnly:true,updatedAt:'2026-11-06T13:00:00.000Z'});
const bundle=notes=>({bundleVersion:1,localRevision:1,document:document(notes),sync:null,recovery:null,background:null});
let browser,checks=0;
function check(label,value){assert.ok(value,label);checks++;console.log('PASS '+label);}
async function fixture(){
  const context=await browser.newContext({serviceWorkers:'block'});await routeFixture(context);const page=await context.newPage();page.setDefaultTimeout(5000);
  await page.goto(origin);await page.evaluate(()=>DeskNotebookStore.ready());
  await page.evaluate(()=>{
    window.actions=[];window.reports=[];window.rpcCalls=0;window.api=()=>{rpcCalls++;throw Error('Recovery must not call the PC');};
    DeskNotebookRecovery.mount(document.getElementById('recovery'),{openReadOnly:async(selection,bundle,scope)=>actions.push({type:'open',selection,bundle,current:scope.isCurrent()}),saveAsNew:async(selection,bundle,scope)=>{actions.push({type:'save',selection,bundle,current:scope.isCurrent()});bundle.document.notes='callback mutation';},report:(message,error)=>reports.push({message,error})});
  });
  return {page,context,close:()=>context.close()};
}
async function seed(page,options={}){
  await page.evaluate(async({selection,key,document,limited})=>{
    localStorage.setItem('deskwing.pen.v1','legacy drawing kept');localStorage.setItem('deskwing.pen.sync.v1','legacy sync kept');localStorage.setItem('deskwing.pen.recovery.v1','legacy recovery kept');
    await DeskNotebookStore.putPage(key,{document},0);
    await DeskNotebookStore.set('deskwing.notebooks.selection.v1',{version:1,selection});
    if(limited!==undefined)localStorage.setItem(key,typeof limited==='string'?limited:JSON.stringify(limited));
  },{selection,key:key(selection),document:document('durable notes'),limited:options.limited});
}
async function snapshot(page){return page.evaluate(async()=>{
  const db=await new Promise((resolve,reject)=>{const request=indexedDB.open('DeskWingNotebooks',1);request.onsuccess=()=>resolve(request.result);request.onerror=reject;});
  const values=await new Promise((resolve,reject)=>{const transaction=db.transaction('backups','readonly'),request=transaction.objectStore('backups').getAll();request.onsuccess=()=>resolve(request.result);request.onerror=reject;});db.close();
  return JSON.stringify({local:Object.fromEntries(Object.keys(localStorage).sort().map(key=>[key,localStorage.getItem(key)])),values});
});}
(async()=>{
  check('fixture admits only exact preloaded HTTPS asset URLs',Boolean(admittedAsset(origin+'/pen.js','GET',null))&&!admittedAsset(origin+'/pen.js?query=1','GET',null)&&!admittedAsset(origin+'/toString','GET',null));
  check('fixture rejects other origins, methods and request bodies',!admittedAsset('https://outside-fixture.invalid/pen.js','GET',null)&&!admittedAsset(origin+'/pen.js','POST',null)&&!admittedAsset(origin+'/pen.js','GET',Buffer.from('body'))&&!admittedAsset('http://deskwing-notebook-recovery.invalid/pen.js','GET',null));
  browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  {
    const context=await browser.newContext({serviceWorkers:'block'});try{
      await routeFixture(context);const page=await context.newPage();
      const unknown=await page.goto(origin+'/notebook-recovery.js?query=1');
      check('browser requests cannot turn unlisted URLs into fixture assets',unknown.status()===404&&await page.locator('body').textContent()==='Fixture request rejected.');
      const external=await page.goto('https://outside-fixture.invalid/');
      check('off-origin browser requests are rejected without network fallback',external.status()===404&&await page.locator('body').textContent()==='Fixture request rejected.');
    }finally{await context.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page,{limited:bundle('limited notes')});const before=await snapshot(f.page);
      await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);
      check('different local branches are both presented without choosing a winner',await f.page.locator('.notebook-recovery-copy').count()===2&&await f.page.evaluate(()=>actions.length===0));
      check('stored names are rendered as text rather than executable markup',await f.page.locator('#recovery img').count()===0&&await f.page.evaluate(()=>window.unsafe!==true));
      check('cards show stroke, note and date summaries',await f.page.locator('.notebook-recovery-copy').first().textContent().then(text=>text.includes('1 strokes')&&text.includes('13 note characters')));
      await f.page.getByRole('button',{name:'Open read-only copy'}).first().click();
      check('opening a reviewed branch invokes only its read-only callback',await f.page.evaluate(()=>actions.length===1&&actions[0].type==='open'&&actions[0].bundle.document.notes==='durable notes'&&actions[0].current));
      await f.page.getByRole('button',{name:'Save reviewed copy as a new page'}).last().click();
      check('saving as a new page requires an explicit action and passes the selected copy',await f.page.evaluate(()=>actions.length===2&&actions[1].type==='save'&&actions[1].bundle.document.notes==='callback mutation'));
      await f.page.getByRole('button',{name:'Open read-only copy'}).last().click();
      check('callbacks cannot mutate the next action’s preserved source',await f.page.evaluate(()=>actions[2].bundle.document.notes==='limited notes'));
      check('review and actions leave every original browser backup unchanged',await snapshot(f.page)===before);
      check('recovery never invokes a PC API or board action',await f.page.evaluate(()=>rpcCalls===0));
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page,{limited:'{malformed local copy'});const before=await snapshot(f.page);
      await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);
      check('a malformed limited-storage branch cannot block a valid durable page',await f.page.locator('.notebook-recovery-copy').count()===1);
      check('damaged-branch guidance is visible without exposing stored contents',await f.page.locator('#recovery').textContent().then(text=>text.includes('could not be read')&&!text.includes('malformed local copy')));
      check('damaged raw bytes remain untouched after review',await snapshot(f.page)===before);
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      const damaged=bundle('notes still readable');damaged.background={kind:'png',image:'https://invalid.example/tracker',width:1,height:1};
      await seed(f.page,{limited:damaged});await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);
      check('an invalid background does not hide readable ink and notes',await f.page.locator('.notebook-recovery-copy').count()===2&&await f.page.locator('#recovery').textContent().then(text=>text.includes('background unavailable')));
      await f.page.getByRole('button',{name:'Open read-only copy'}).last().click();
      check('invalid image URLs are excluded from the read-only recovery payload',await f.page.evaluate(()=>actions[0].bundle.background===null&&actions[0].bundle.document.notes==='notes still readable'));
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page);
      await f.page.evaluate(async({other,key,document,limitedSelection})=>{await DeskNotebookStore.putPage(key,{document},0);localStorage.setItem('deskwing.notebooks.selection.v1',JSON.stringify(limitedSelection));},{other,key:key(other),document:document('second page'),limitedSelection:{version:1,selection:other}});
      await f.page.evaluate(()=>DeskNotebookRecovery.showSelectionRecovery());
      check('divergent opening choices recover locally before any catalog exists',await f.page.locator('.notebook-recovery-copy').count()===2&&await f.page.evaluate(()=>window.catalog===undefined&&rpcCalls===0));
      check('each recovered opening choice retains its own page identity',await f.page.locator('.notebook-recovery-copy strong').allTextContents().then(values=>values.some(text=>text.endsWith('Page one'))&&values.some(text=>text.endsWith('Page two'))));
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page);await f.page.evaluate(()=>localStorage.setItem('deskwing.notebooks.selection.v1','broken selection'));
      await f.page.evaluate(()=>DeskNotebookRecovery.showSelectionRecovery());
      check('a damaged opening choice cannot block another valid saved selection',await f.page.locator('.notebook-recovery-copy').count()===1&&await f.page.locator('#recovery').textContent().then(text=>text.includes('opening choice could not be read')));
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      const value=bundle('current');value.recovery=document('older retained copy');await seed(f.page,{limited:value});await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);
      check('a distinct retained recovery slot can be explicitly opened',await f.page.locator('.notebook-recovery-copy').count()===3&&await f.page.locator('#recovery').textContent().then(text=>text.includes('retained recovery copy')));
      await f.page.getByRole('button',{name:'Open read-only copy'}).last().click();check('recovery-slot actions receive the retained document rather than current notes',await f.page.evaluate(()=>actions[0].bundle.document.notes==='older retained copy'&&actions[0].bundle.recovery===null));
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page);
      await f.page.evaluate(selection=>{
        const real=DeskNotebookStore;window.releaseRead=null;window.DeskNotebookStore={readBranches:key=>new Promise(resolve=>{releaseRead=()=>real.readBranches(key).then(resolve);})};
        window.pendingRecovery=DeskNotebookRecovery.showPageRecovery(selection);
      },selection);
      await f.page.evaluate(()=>{window.dispatchEvent(new CustomEvent('deskwing-connection-changed'));releaseRead();return pendingRecovery;});
      check('a connection change invalidates late recovery reads',await f.page.locator('.notebook-recovery-copy').count()===0&&await f.page.locator('#recovery').isHidden());
    }finally{await f.close();}
  }
  {
    const f=await fixture();try{
      await seed(f.page);await f.page.evaluate(()=>{
        window.deferred=[];window.scopes=[];DeskNotebookRecovery.mount(document.getElementById('recovery'),{openReadOnly:async(selection,bundle,scope)=>{scopes.push(scope);await new Promise(resolve=>deferred.push(resolve));}});
      });
      await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);await f.page.getByRole('button',{name:'Open read-only copy'}).click();
      check('recovery actions are serialized while a reviewed copy is opening',await f.page.getByRole('button',{name:'Open read-only copy'}).isDisabled());
      await f.page.evaluate(()=>window.dispatchEvent(new CustomEvent('deskwing-workspace-contextchange')));
      check('in-flight callbacks receive an invalid scope and aborted signal after context changes',await f.page.evaluate(()=>!scopes[0].isCurrent()&&scopes[0].signal.aborted));
      await f.page.evaluate(selection=>DeskNotebookRecovery.showPageRecovery(selection),selection);await f.page.getByRole('button',{name:'Open read-only copy'}).click();
      await f.page.evaluate(()=>deferred[0]());await f.page.waitForTimeout(20);
      check('a stale callback cannot unlock a newer recovery action',await f.page.getByRole('button',{name:'Open read-only copy'}).isDisabled());
      await f.page.evaluate(()=>deferred[1]());await f.page.waitForFunction(()=>!document.querySelector('#recovery button').disabled);
      check('the current recovery action releases controls after completion',await f.page.getByRole('button',{name:'Open read-only copy'}).isEnabled());
    }finally{await f.close();}
  }
  console.log(checks+' notebook recovery checks passed. No stored copy, user board, or PC API was changed.');
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{await browser?.close();});
