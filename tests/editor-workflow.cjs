// Real desktop document, isolated API fixtures. No companion, OBS, sound playback or keyboard injection.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const core=require('../companion/wwwroot/editor.js');
const root=path.resolve(__dirname,'..'),webroot=path.join(root,'companion/wwwroot'),output=path.join(root,'.test-output');
let checks=0,browser;
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
const clone=value=>structuredClone(value);
const action=(id,label)=>({id,label,kind:'scene',target:'Gameplay',value:'',icon:'monitor',color:'violet',confirm:false,steps:[]});
const first={profileId:'profile-one',profileName:'Streaming',boardRevision:'revision-1',obsPassword:'never-persist-this',certificate:'never-persist-certificate',pages:[{id:'one',name:'Main',icon:'grid',buttons:[action('a','Gameplay'),action('b','Starting soon')]},{id:'two',name:'Other',icon:'grid',buttons:[action('c','Break')]}]};
function pureChecks(){
 const sanitized=JSON.stringify(core.draft(first,'r1'));check(!sanitized.includes('never-persist')&&!sanitized.includes('obsPassword')&&!sanitized.includes('certificate'),'draft excludes connection credentials');
 check(core.readDraft(sanitized,'wrong-profile')===null,'draft cannot load into another profile');
 check(core.readDraft(JSON.stringify({...JSON.parse(sanitized),schema:2}),'profile-one')===null,'unknown draft schema is rejected');
 check(core.readDraft('{invalid','profile-one')===null,'damaged draft is ignored');
 const source=clone(first.pages[0]),target=clone(first.pages[1]),copies=core.moveButtons(source,target,['a'],true);
 check(source.buttons.length===2&&target.buttons.length===2&&copies[0].id!=='a','copy retains source and creates fresh identity');
 core.moveButtons(source,target,['b'],false);check(source.buttons.length===1&&target.buttons.at(-1).id==='b','move transfers selected identity within draft');
 const macro={...action('macro','Sequence'),kind:'macro',steps:[action('nested','Step')]},copy=core.actionCopy(macro,true);
 check(copy.id!=='macro'&&copy.steps[0].id!=='nested','duplicate recursively renews macro identities');
 check(core.moveButtons(source,source,['a'],false).length===0&&source.buttons.length===1,'same-page move is a no-op');
 const changed=clone(first);changed.pages[0].name='Renamed';check(core.difference(first,changed).includes('Renamed'),'conflict comparison identifies changed pages');
}
async function run(){
 pureChecks();let saved=clone(first),other={profileId:'profile-two',profileName:'Recording',boardRevision:'other-1',pages:[{id:'other-page',name:'Recording controls',icon:'grid',buttons:[]}]};let saveCount=0,controlPosts=0,transferBody=null,pendingSave=null,holdSave=false;const errors=[],unhandled=[];
 browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const context=await browser.newContext({viewport:{width:1280,height:800}});
 await context.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());if(url.origin!=='http://127.0.0.1:18999')return route.abort();
  const send=(data,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
  if(url.pathname.startsWith('/api/')){
   if(url.pathname==='/api/board')return send({profileId:saved.profileId,profileName:saved.profileName,pages:saved.pages.map(p=>({...p,buttons:p.buttons.map(a=>({...a,configured:true}))}))});
   if(url.pathname==='/api/state')return send({pcConnected:true,obsConnected:false,inputs:{},session:'fixture',revision:1,execution:{session:'fixture',revision:1,active:null}});
   if(url.pathname==='/api/events')return route.fulfill({status:200,contentType:'text/event-stream',body:': Fixture polling.\n\n'});
   if(url.pathname==='/api/execution')return send({session:'fixture',revision:1,active:null});
   if(url.pathname==='/api/admin/info')return send({devices:[{id:'fixture'}],addresses:[],lanAddresses:[],connectedTablets:1});
   if(url.pathname==='/api/admin/desktop')return send({usbStatus:'Fixture USB',preferences:{}});
   if(url.pathname==='/api/profile-lock')return send({ok:true});
   if(url.pathname==='/api/profiles')return send({profiles:[{id:saved.profileId,name:saved.profileName},{id:other.profileId,name:other.profileName}]});
   if(url.pathname==='/api/admin/profiles/'+other.profileId+'/config')return send(other);
   if(url.pathname==='/api/admin/profiles/transfer'){
    transferBody=request.postDataJSON();if(transferBody.sourceRevision!==saved.boardRevision||transferBody.targetRevision!==other.boardRevision)return send({error:'Profile revision changed'},409);
    core.moveButtons(saved.pages.find(p=>p.id===transferBody.sourcePageId),other.pages.find(p=>p.id===transferBody.targetPageId),transferBody.buttonIds,transferBody.operation==='copy');saved.boardRevision='transfer-source';other.boardRevision='transfer-target';return send({ok:true,sourceRevision:saved.boardRevision,targetRevision:other.boardRevision});
   }
   if(url.pathname==='/api/admin/config'){
    if(request.method()==='GET')return send(saved);const body=request.postDataJSON();if(body.expectedRevision!==saved.boardRevision)return send({error:'This board changed in another editor. Reload the saved board.'},409);
    if(holdSave)await new Promise(resolve=>pendingSave=resolve);saveCount++;saved={...saved,pages:clone(body.config.pages),boardRevision:'saved-'+saveCount};return send({ok:true,boardRevision:saved.boardRevision});
   }
   if(url.pathname==='/api/admin/action-check')return send({ok:true,problems:[],note:'Fixture validation only'});
   if(['/api/action','/api/audio','/api/mute','/api/computer/action','/api/soundpad/play','/api/hold'].includes(url.pathname)){controlPosts++;return send({error:'Controls disabled in fixture'},409);}
   unhandled.push(request.method()+' '+url.pathname);return send({error:'Unexpected fixture API'},404);
  }
  const relative=url.pathname==='/'?'desktop.html':decodeURIComponent(url.pathname).replace(/^\/+/,''),file=path.resolve(webroot,relative);
  if(!file.startsWith(webroot+path.sep)||!fs.existsSync(file))return route.fulfill({status:404,body:'Not found'});
  const type={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.png':'image/png','.svg':'image/svg+xml'}[path.extname(file)]||'application/octet-stream';return route.fulfill({status:200,contentType:type,body:fs.readFileSync(file)});
 });
 const page=await context.newPage();page.on('pageerror',error=>errors.push(error.stack||error.message));page.on('dialog',dialog=>dialog.accept());
 const open=async()=>{await page.locator('#console-editor').click();await page.locator('#page-name').waitFor();};
 await page.goto('http://127.0.0.1:18999/desktop.html#admin=fixture');await open();
 check(await page.locator('#obs-port').count()===0&&await page.locator('#obs-password').count()===0,'board editor does not duplicate OBS credential fields');
 check(await page.locator('.editor-key').count()===2,'saved keys appear as a visual grid');
 const gridBox=await page.locator('#editor-grid').boundingBox();check(gridBox.y<430,'board grid begins in the first viewport ('+Math.round(gridBox.y)+'px)');
 await page.locator('#duplicate-button').click();check(await page.locator('.editor-key').count()===3,'duplicate button appears in visual grid');
 await page.locator('#editor-undo').click();check(await page.locator('.editor-key').count()===2,'undo restores original key count');
 await page.locator('#editor-redo').click();check(await page.locator('.editor-key').count()===3,'redo restores duplication');
 await page.locator('[data-select-key="0"]').check();check(await page.locator('#batch-apply-color').isVisible(),'selection of two keys opens shared styling');
 await page.locator('#batch-color').selectOption('red');await page.locator('#batch-apply-color').click();check(await page.locator('.editor-key.red').count()===2,'batch styling applies only to selected keys');
 await page.locator('[data-select-key="0"]').uncheck();check(await page.locator('#action-label').inputValue()==='Gameplay copy','deselecting one key keeps inspector aligned with remaining selection');await page.locator('[data-select-key="0"]').check();
 await page.locator('.editor-page-options summary').click();await page.locator('#editor-transfer').click();await page.locator('#transfer-page').selectOption('two');await page.locator('#transfer-copy').click();
 await page.locator('[data-editor-page="1"]').click();check(await page.locator('.editor-key').count()===3,'cross-page copy carries selected keys into destination draft');
 await page.locator('.editor-page-options summary').click();await page.locator('#duplicate-page').click();check(await page.locator('[data-editor-page]').count()===3,'page duplication creates a new manual page');
 const draft=await page.evaluate(()=>localStorage.getItem('deckEditorDraft:v1:profile-one'));check(draft&&!draft.includes('never-persist'),'recoverable draft persists board content without credentials');
 await page.reload();await open();check(await page.locator('#draft-resume').isVisible(),'reopening offers saved draft recovery');check(await page.locator('#page-name').isDisabled()&&await page.locator('#import-config').isDisabled(),'unreviewed recovery draft cannot be overwritten by new edits');
 await page.locator('#close-panel').click();check(await page.evaluate(()=>!!localStorage.getItem('deckEditorDraft:v1:profile-one')),'closing an unreviewed recovery prompt preserves its draft');await open();
 await page.locator('#draft-resume').click();check(await page.locator('[data-editor-page]').count()===3,'recovered draft restores unsaved pages');
 await page.locator('#save-config').click();await page.locator('#save-status').filter({hasText:'Saved'}).waitFor();check(saveCount===1&&saved.pages.length===3,'revision-aware save applies recovered pages');
 check(await page.evaluate(()=>localStorage.getItem('deckEditorDraft:v1:profile-one'))===null,'successful save clears recovery draft');
 await page.locator('#action-label').fill('My local revision');saved.boardRevision='external-change';saved.pages[0].name='Changed elsewhere';await page.locator('#save-config').click();await page.locator('#conflict-use').waitFor();
 check(saveCount===1&&await page.locator('#action-label').inputValue()==='My local revision','revision conflict keeps local draft without overwriting saved board');
 await page.locator('#conflict-use').click();await page.locator('#confirm-yes').click();await page.locator('#save-config').click();await page.locator('#save-status').filter({hasText:'Saved'}).waitFor();check(saveCount===2,'reviewed draft saves with freshly acknowledged revision');
 await page.locator('#action-label').fill('Immediate subsequent change');await page.locator('#editor-undo').click();check(await page.locator('#action-label').inputValue()==='My local revision','undo after immediate post-save typing returns to the saved value');
 await page.locator('.editor-page-options summary').click();await page.locator('#editor-transfer').click();await page.locator('#transfer-profile').selectOption('profile-two');await page.locator('#transfer-page option[value="other-page"]').waitFor({state:'attached'});await page.locator('#transfer-copy').click();await page.locator('#confirm-yes').click();await page.locator('#editor-message').filter({hasText:'copied to Recording'}).waitFor();
 check(transferBody.targetRevision==='other-1'&&transferBody.sourceRevision==='saved-2'&&other.pages[0].buttons.length===1,'cross-profile transfer supplies both revisions and explicit destination');
 await page.locator('#action-label').fill('Saving safely');holdSave=true;await page.locator('#save-config').click();while(!pendingSave)await new Promise(resolve=>setTimeout(resolve,10));check(await page.locator('#action-label').isDisabled(),'in-flight save locks editable fields');await page.locator('#close-panel').click();check(await page.locator('#board-editor').isVisible(),'Back waits for an in-flight save instead of offering a misleading discard');holdSave=false;pendingSave();await page.locator('#save-status').filter({hasText:'Saved'}).waitFor();
 const beforeRecoveryRevision=saved.boardRevision;await page.locator('#action-label').fill('A stale recovery draft');saved.boardRevision='later-external-revision';saved.pages[0].name='Newer saved version';
 await page.reload();await open();await page.locator('#draft-resume').click();check(await page.locator('#conflict-use').isVisible(),'stale recovery requires explicit conflict review');
 check((await page.evaluate(()=>JSON.parse(localStorage.getItem('deckEditorDraft:v1:profile-one')))).baseRevision===beforeRecoveryRevision,'reviewing stale recovery retains its original base revision');
 await page.reload();await open();await page.locator('#draft-resume').click();check(await page.locator('#conflict-use').isVisible(),'reloading cannot bypass stale draft conflict review');
 const beforeBlockedSave=saveCount;await page.locator('#save-config').click();check(saveCount===beforeBlockedSave&&await page.locator('#editor-message').innerText().then(text=>text.includes('Review')),'unacknowledged conflict cannot save');
 await page.locator('#conflict-reload').click();await page.locator('#confirm-yes').click();await page.waitForFunction(()=>document.querySelector('#page-name')?.value==='Newer saved version');check(await page.locator('#page-name').inputValue()==='Newer saved version','discarding stale draft reloads latest saved pages');
 for(const [width,height] of [[1280,720],[900,620],[390,844]]){await page.setViewportSize({width,height});await page.locator('#panel-body').evaluate(node=>node.scrollTop=0);check(await page.locator('#panel-body').evaluate(node=>node.scrollWidth<=node.clientWidth+1),'editor content avoids horizontal overflow at '+width+'px');const firstKey=await page.locator('.editor-key').first().boundingBox(),saveBar=await page.locator('#board-editor>.save-bar').boundingBox(),firstLabel=await page.locator('.editor-key>span').first().boundingBox();check(firstKey.y+firstKey.height<=saveBar.y&&firstLabel.y+firstLabel.height<=saveBar.y,'first preview key and label fit above sticky Save at '+width+'×'+height);check(await page.locator('#save-config').evaluate(node=>node.getBoundingClientRect().height>=44),'Save retains a44px target at '+width+'px');}
 check(controlPosts===0,'editor previews never dispatch PC controls');check(errors.length===0,'editor workflow has no uncaught JavaScript errors: '+errors.join('; '));
 fs.mkdirSync(output,{recursive:true});fs.writeFileSync(path.join(output,'editor-workflow-results.json'),JSON.stringify({passed:checks,saveCount,controlPosts,unhandled,errors,fixture:true,realPlayback:false},null,2));
 console.log(checks+' editor workflow checks passed.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>browser?.close());
