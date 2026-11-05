// Real index.html/scripts/styles, headless Chromium, fully intercepted APIs.
// No companion process, named pipes, audio, application controls, or external requests.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..');
const webroot=path.join(root,'companion/wwwroot');
const output=path.join(root,'.test-output');fs.mkdirSync(output,{recursive:true});
const key=index=>crypto.createHash('sha256').update('fixture-file-'+index).digest('hex').toUpperCase();
const copy=value=>JSON.parse(JSON.stringify(value));
const sound=index=>({index,key:key(index),name:index===2006?'Long source name '.repeat(5)+'Unique tail 2006':'Sound '+String(index).padStart(4,'0'),artist:'Artist '+index%17,categories:['Category '+index%9]});
const manual=(id,label)=>({id,label,kind:'soundpadPlay',target:'17',value:'both',soundpadKey:key(17),icon:'music',color:'amber',steps:[]});
let sounds=Array.from({length:2007},(_,index)=>sound(index+1));
let offline=false,boardReads=0,controlPosts=0,savePosts=0,checks=0,browser;
let config={profileId:'fixture-profile',profileName:'Soundboard fixture',obsPort:19999,obsPassword:'',micInput:'Mic fixture',desktopInput:'Desktop fixture',mixerInputs:[],profiles:[],favourites:[],pages:[
    {id:'memes',name:'Memes',icon:'music',liveSoundpad:true,liveScenes:false,liveSoundpadOutput:'mic',buttons:[manual('manual-kept','Retained manual button')]},
    {id:'other',name:'Other',icon:'grid',liveSoundpad:false,liveScenes:false,liveSoundpadOutput:'mic',buttons:[manual('other-kept','Other manual button')]}
]};
const savedManual=JSON.stringify(config.pages.map(p=>p.buttons));
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
function board(){
    return {profileId:config.profileId,profileName:config.profileName,pages:config.pages.map(p=>({...p,soundpadWarning:p.liveSoundpad&&offline?'Fixture Soundpad offline':null,buttons:p.liveSoundpad?(offline?[]:sounds.map(s=>({id:'live-soundpad:'+p.id+':'+s.key+':'+s.index,label:s.name.slice(0,60),kind:'soundpadPlay',icon:'speaker',color:'amber',categories:s.categories,searchText:s.name+' '+s.artist,configured:true}))):p.buttons.map(a=>({...a,configured:true}))}))};
}
async function run(){
    const errors=[],external=[],unhandled=[];
    browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
    const context=await browser.newContext({viewport:{width:1280,height:860}});
    await context.addInitScript(()=>localStorage.setItem('deckPadView',JSON.stringify({full:true,size:40})));
    await context.route('**/*',async route=>{
        const request=route.request(),url=new URL(request.url());
        if(url.origin!=='http://tabletdeck.test'){external.push(url.origin);return route.abort();}
        if(url.pathname.startsWith('/api/')){
            const send=(data,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
            if(url.pathname==='/api/board'){boardReads++;return send(board());}
            if(url.pathname==='/api/state')return send({pcConnected:true,obsConnected:false,inputs:{},session:'fixture',revision:1,execution:{session:'fixture',revision:1,active:null}});
            if(url.pathname==='/api/events')return route.fulfill({status:200,contentType:'text/event-stream',body:': Fixture uses authenticated polling fallback.\n\n'});
            if(url.pathname==='/api/execution')return send({session:'fixture',revision:1,active:null});
            if(url.pathname==='/api/admin/info')return send({devices:[{id:'fixture',name:'Fixture tablet'}],addresses:[],httpsPort:18743});
            if(url.pathname==='/api/profile-lock')return send({ok:true});
            if(url.pathname==='/api/profiles')return send({profiles:[{id:config.profileId,name:config.profileName}],warning:''});
            if(url.pathname==='/api/admin/config'){
                if(request.method()==='POST'){config=copy(request.postDataJSON().config);savePosts++;return send({ok:true});}
                return send(copy(config));
            }
            if(['/api/action','/api/soundpad/play','/api/hold','/api/computer/action','/api/audio','/api/mute','/api/apps'].includes(url.pathname)){controlPosts++;return send({error:'Fixture never executes controls'},409);}
            unhandled.push(request.method()+' '+url.pathname);return send({error:'Unexpected fixture API'},404);
        }
        const relative=url.pathname==='/'?'index.html':decodeURIComponent(url.pathname).replace(/^\/+/,''),file=path.resolve(webroot,relative);
        if(!file.startsWith(webroot+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile())return route.fulfill({status:404,body:'Not found'});
        const type={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.png':'image/png','.svg':'image/svg+xml','.ico':'image/x-icon'}[path.extname(file)]||'application/octet-stream';
        return route.fulfill({status:200,contentType:type,body:fs.readFileSync(file)});
    });
    const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
    await page.goto('http://tabletdeck.test/index.html#admin=isolated-fixture');
    await page.locator('#soundpad-board-count').filter({hasText:'2007 sounds'}).waitFor();
    check(await page.locator('#keys>.key').count()===40,'40-key pad shows40 buttons while retaining all2007 sounds');
    check(await page.locator('#soundpad-board-page option').count()===51,'direct jump exposes every one of51 sound pages');
    await page.locator('#soundpad-board-page').selectOption('50');
    check(await page.locator('#keys>.key').count()===7&&(await page.locator('#keys>.key').last().getAttribute('data-id')).endsWith(':2007'),'direct last-page jump reaches sound2007');
    const reachable=new Set();
    for(let index=0;index<51;index++){
        await page.locator('#soundpad-board-page').selectOption(String(index));
        for(const id of await page.locator('#keys>.key').evaluateAll(nodes=>nodes.map(node=>node.dataset.id)))reachable.add(id);
    }
    check(reachable.size===2007,'all2007 sounds are reachable through actual page selection');
    await page.locator('#soundpad-board-page').selectOption('0');
    await page.locator('#pad-toggle').click();
    check(await page.locator('#keys>.key').count()===12&&await page.locator('#soundpad-board-page option').count()===168,'compact layout uses12 buttons and all168 pages');
    await page.locator('#soundpad-board-page').selectOption('167');
    check(await page.locator('#keys>.key').count()===3&&(await page.locator('#keys>.key').last().getAttribute('data-id')).endsWith(':2007'),'compact last page reaches the same complete library');
    await page.locator('#pad-toggle').click();await page.locator('#soundpad-board-page').selectOption('0');

    await page.locator('#soundpad-board-search').fill('Sound 2007');
    check(await page.locator('#keys>.key').count()===1&&(await page.locator('#keys>.key').first().getAttribute('data-id')).endsWith(':2007'),'name search reaches sounds beyond the first visible page');
    check(await page.locator('#soundpad-board-search').evaluate(node=>node===document.activeElement),'typing search preserves keyboard focus');
    await page.locator('#soundpad-board-search').fill('unique tail 2006');
    check(await page.locator('#keys>.key').count()===1&&(await page.locator('#keys>.key').first().getAttribute('data-id')).endsWith(':2006'),'search uses full source names beyond truncated pad labels');
    await page.locator('#soundpad-board-search').fill('ARTIST 16');
    check((await page.locator('#soundpad-board-count').innerText()).includes(sounds.filter(s=>s.artist==='Artist 16').length+' matching'),'artist search covers the entire library case-insensitively');
    await page.locator('#soundpad-board-clear').click();await page.locator('#soundpad-board-category').selectOption('Category 8');
    check((await page.locator('#soundpad-board-count').innerText()).includes(sounds.filter(s=>s.categories.includes('Category 8')).length+' matching'),'category selection covers all source entries');
    await page.locator('#soundpad-board-search').fill('Sound 2006');
    check(await page.locator('#keys>.key').count()===0&&await page.locator('.empty-key').innerText().then(text=>text.includes('No matching sounds')),'combined filters show an explicit empty result');
    await page.locator('#soundpad-board-clear').click();

    for(const [width,height] of [[1280,860],[800,1000],[390,844]]){
        await page.setViewportSize({width,height});
        check(await page.locator('body').evaluate(node=>node.scrollWidth<=innerWidth),'live pad has no horizontal overflow at'+width+'px');
        check(await page.locator('#soundpad-board-search').isVisible()&&await page.locator('#soundpad-board-page').isVisible(),'search and direct page selection remain visible at'+width+'px');
        await page.screenshot({path:path.join(output,'live-soundpad-'+width+'.png')});
    }
    await page.setViewportSize({width:1280,height:860});
    await page.locator('#keys>.key').first().focus();const focusKey=key(1);
    sounds=sounds.map(s=>s.key===focusKey?{...s,index:4999,name:'Reindexed first sound'}:s);
    await page.locator('#keys>.key').first().filter({hasText:'Reindexed first sound'}).waitFor({timeout:12000});
    check(await page.evaluate(value=>document.activeElement?.dataset.id?.includes(value),focusKey),'automatic reindex refresh restores focus by stable file key');
    check((await page.locator('#keys>.key').first().getAttribute('data-id')).endsWith(':4999'),'automatic refresh updates playback runtime index');
    sounds.push(sound(2008));
    await page.locator('#soundpad-board-count').filter({hasText:'2008 sounds'}).waitFor({timeout:12000});
    check(true,'automatic board polling includes newly added sounds without editor save');
    await page.locator('#soundpad-board-page').selectOption('50');
    sounds=sounds.slice(0,3);
    await page.locator('#soundpad-board-count').filter({hasText:'3 sounds'}).waitFor({timeout:12000});
    check(await page.locator('#keys>.key').count()===3&&await page.locator('#pagination').isHidden(),'automatic shrink clamps the last page and preserves all remaining sounds');
    sounds=[];
    await page.locator('.empty-key').filter({hasText:'No live Soundpad sounds available'}).waitFor({timeout:12000});
    check((await page.locator('#soundpad-board-count').innerText()).includes('0 sounds'),'empty library has a truthful zero count and recovery message');
    offline=true;
    await page.locator('#soundpad-board-count').filter({hasText:'Soundpad unavailable'}).waitFor({timeout:12000});
    check(await page.locator('#keys>.key').count()===0,'offline library never leaves stale playable keys');
    await page.screenshot({path:path.join(output,'live-soundpad-offline.png')});
    offline=false;sounds=Array.from({length:2007},(_,index)=>sound(index+1));
    await page.locator('#soundpad-board-count').filter({hasText:'2007 sounds'}).waitFor({timeout:12000});

    // Exercise actual editor toggle handlers and save flow; retain original raw manual arrays.
    const pageOptions=async()=>{if(!await page.locator('.editor-page-options').evaluate(node=>node.open))await page.locator('.editor-page-options summary').click();};
    const source=async value=>{await pageOptions();await page.locator('#editor-source').selectOption(value);};
    await page.evaluate(()=>showEditor());await page.locator('#board-editor').waitFor();await pageOptions();
    check(await page.locator('#editor-source').inputValue()==='soundpad'&&await page.locator('#editor-grid .editor-key').count()===12&&await page.locator('#editor-grid .editor-key:disabled').count()===12&&await page.locator('#add-button').isDisabled(),'editor identifies live Soundpad and presents its preview without manual action editing');
    await source('manual');
    check(await page.locator('.editor-list').isVisible()&&await page.locator('[data-edit-index="0"]').isEnabled()&&await page.locator('[data-edit-index="0"]').innerText().then(text=>text.includes('Retained manual button')),'choosing Manual buttons reveals retained editable actions');
    await source('obs');
    check(await page.evaluate(()=>config.pages[0].liveScenes&&!config.pages[0].liveSoundpad),'OBS page source excludes live Soundpad on the same page');
    await source('soundpad');
    check(await page.evaluate(()=>config.pages[0].liveSoundpad&&!config.pages[0].liveScenes),'Soundpad page source excludes live OBS on the same page');
    await pageOptions();await page.locator('#page-soundpad-output').selectOption('both');
    await page.locator('[data-editor-page="1"]').click();await source('soundpad');
    check(await page.evaluate(()=>config.pages.filter(p=>p.liveSoundpad).length)===1,'enabling another live Soundpad page keeps one live source per profile');
    await page.locator('[data-editor-page="0"]').click();await pageOptions();
    check(await page.locator('#editor-source').inputValue()==='manual'&&await page.locator('[data-edit-index="0"]').innerText().then(text=>text.includes('Retained manual button')),'previous live page returns to its retained manual buttons');
    await source('soundpad');await pageOptions();await page.locator('#page-soundpad-output').selectOption('both');
    await page.screenshot({path:path.join(output,'live-soundpad-editor.png')});
    await page.locator('#save-config').click();await page.waitForFunction(()=>!dirty&&document.querySelector('#save-status').textContent.startsWith('Saved'));
    check(await page.locator('#board-editor').isVisible(),'successful save keeps the visual editor open');
    check(savePosts===1&&config.pages[0].liveSoundpad&&config.pages[0].liveSoundpadOutput==='both'&&!config.pages[1].liveSoundpad,'save retains selected live page and explicit playback routing');
    check(JSON.stringify(config.pages.map(p=>p.buttons))===savedManual,'all editor toggles and save preserve raw manual button identities and routing');
    check(controlPosts===0,'browsing, filtering, refresh and editing send no playback or control action');
    check(errors.length===0,'all actual scripts run without JavaScript errors: '+errors.join('; '));
    check(external.length===0,'fixture contacts no external hosts');
    check(unhandled.length===0,'fixture covers every requested API: '+unhandled.join(', '));
    check(boardReads>=6,'growth/shrink/reindex/offline scenarios use actual repeated board API reads');
    fs.writeFileSync(path.join(output,'live-soundpad-board-results.json'),JSON.stringify({checks,librarySize:2007,boardReads,controlPosts,savePosts,headless:true,actualScripts:true,realSoundpadUsed:false,externalRequests:external,errors},null,2));
    console.log(checks+' live Soundpad rendered checks passed; all APIs intercepted, no Soundpad I/O, audio, controls, or visible browser.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();});
