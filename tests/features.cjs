const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const https=require('node:https');
const {chromium}=require('playwright');
const root=path.resolve(process.env.TABLETDECK_TEST_OUTPUT||path.join(__dirname,'../.test-output'));
const session=JSON.parse(fs.readFileSync(path.join(root,'data/admin-session.json'),'utf8'));
const base='http://127.0.0.1:'+session.httpPort;
let checks=0,browser;
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
async function admin(route,body){const response=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});return {status:response.status,data:await response.json()};}
async function tablet(route,body,token=''){
 return new Promise((resolve,reject)=>{const req=https.request({host:'127.0.0.1',port:session.httpsPort,path:route,method:body===undefined?'GET':'POST',rejectUnauthorized:false,agent:false,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})}},res=>{let text='';res.on('data',part=>text+=part);res.on('end',()=>{let data;try{data=JSON.parse(text);}catch{data={};}resolve({status:res.statusCode,data});});});req.on('error',reject);req.end(body===undefined?undefined:JSON.stringify(body));});
}
async function drag(page,source,target,touch=false){
 await target.scrollIntoViewIfNeeded();await source.scrollIntoViewIfNeeded();
 const from=await source.boundingBox(),to=await target.boundingBox();assert.ok(from&&to);
 const x1=from.x+from.width/2,y1=from.y+from.height/2,x2=to.x+to.width/2,y2=to.y+to.height/2;
 if(touch){const cdp=await page.context().newCDPSession(page);await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:x1,y:y1,id:1}]});for(let i=1;i<=8;i++)await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:x1+(x2-x1)*i/8,y:y1+(y2-y1)*i/8,id:1}]});await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await cdp.detach();}
 else{await page.mouse.move(x1,y1);await page.mouse.down();await page.mouse.move(x2,y2,{steps:10});await page.mouse.up();}
}
async function run(){
 check((await tablet('/api/apps')).status===401,'app session list requires authentication');
 check((await tablet('/api/soundpad/library')).status===401,'Soundpad library requires authentication');
 check((await tablet('/api/layout',{pages:[]})).status===401,'layout updates require authentication');
 const pairing=(await admin('/api/admin/pairing',{})).data;const paired=await tablet('/api/pair',{code:pairing.code,name:'Feature tests'});assert.equal(paired.status,200);const token=paired.data.token;
 check((await tablet('/api/admin/soundpad/import',{indices:[1]},token)).status===403,'tablet cannot import or edit PC action targets');
 const appList=await tablet('/api/apps',undefined,token);check(appList.status===200&&Array.isArray(appList.data.apps),'Windows playback session enumeration endpoint');
 check((await tablet('/api/apps',{id:'F'.repeat(64),percent:-1},token)).status===400,'app volume rejects negative values');
 check((await tablet('/api/apps',{id:'F'.repeat(64),percent:50},token)).status===409,'stale audio session rejected without changing another app');
 const before=(await admin('/api/admin/config')).data;
 const board=(await admin('/api/board')).data;
 const order=board.pages.map(p=>({id:p.id,buttonIds:p.buttons.map(b=>b.id)}));order[0].buttonIds.reverse();
 check((await tablet('/api/layout',{pages:order},token)).status===200,'paired tablet can reorder existing buttons');
 const after=(await admin('/api/admin/config')).data;const actionMap=c=>Object.fromEntries(c.pages.flatMap(p=>p.buttons.map(a=>[a.id,JSON.stringify(a)])));
 check(JSON.stringify(Object.entries(actionMap(before)).sort())===JSON.stringify(Object.entries(actionMap(after)).sort()),'layout saves preserve every action target and setting');
 const invalid=structuredClone(order);invalid[0].buttonIds[0]=invalid[0].buttonIds[1];
 check((await tablet('/api/layout',{pages:invalid},token)).status===400,'layout rejects duplicate IDs');
 check((await tablet('/api/layout',{pages:order.slice(1)},token)).status===400,'layout rejects omitted pages');
 await admin('/api/admin/config',{config:before,replacePassword:false});

 browser=await chromium.launch({executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 const page=await browser.newPage({viewport:{width:1280,height:800},hasTouch:true});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/desktop.html#admin='+session.token);await page.locator('#console-editor').click();await page.locator('.drag-handle').first().waitFor();
 const initialIds=(await admin('/api/board')).data.pages[0].buttons.map(b=>b.id);
 await drag(page,page.locator('.drag-handle').first(),page.locator('.drag-handle').nth(2));
 await page.getByRole('button',{name:'Save changes',exact:true}).click();await page.waitForTimeout(150);
 let current=(await admin('/api/board')).data.pages[0].buttons.map(b=>b.id);check(current[0]!==initialIds[0]&&current.includes(initialIds[0]),'PC drag-and-drop order persists');

 // The shipping loopback root is the PC console. Serve the actual tablet HTML
 // at a fixture-only route so its layout controls can use this isolated admin
 // session without preview mode suppressing writes or a real Android bridge.
 await page.route(base+'/tablet-feature-fixture',route=>route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(__dirname,'../companion/wwwroot/index.html'),'utf8')}));
 await page.goto(base+'/tablet-feature-fixture#admin='+session.token);await page.locator('.key').first().waitFor();

 await page.getByRole('button',{name:'Edit',exact:true}).click();await page.locator('.editor-page-options > summary').click();await page.getByRole('button',{name:'Arrange board',exact:true}).click();await page.waitForFunction(()=>arranging===true);await page.locator('body.arranging').waitFor();
 let actions=0;await page.route('**/api/action',route=>{actions++;return route.fulfill({json:{ok:true}});});
 await page.locator('.key').first().click();check(actions===0,'arrange mode prevents triggering actions');await page.getByRole('button',{name:'Close settings',exact:true}).click();
 const original=current[0];await drag(page,page.locator('.key').first(),page.locator('.key').nth(3));await page.getByRole('button',{name:'Save order',exact:true}).click();await page.waitForTimeout(150);
 current=(await admin('/api/board')).data.pages[0].buttons.map(b=>b.id);check(current[0]!==original,'board drag order saves through validated layout endpoint');
 await page.getByRole('button',{name:'Edit',exact:true}).click();await page.locator('.editor-page-options > summary').click();await page.getByRole('button',{name:'Arrange board',exact:true}).click();await page.waitForFunction(()=>arranging===true);
 const saved=[...current];await drag(page,page.locator('.key').first(),page.locator('.key').nth(2),true);await page.getByRole('button',{name:'Cancel',exact:true}).click();await page.waitForTimeout(150);
 check(JSON.stringify((await admin('/api/board')).data.pages[0].buttons.map(b=>b.id))===JSON.stringify(saved),'touch drag can be cancelled without saving');
 await page.getByRole('button',{name:'Edit',exact:true}).click();await page.locator('.editor-page-options > summary').click();await page.getByRole('button',{name:'Arrange board',exact:true}).click();await page.waitForFunction(()=>arranging===true);await page.locator('.key').first().focus();await page.keyboard.press('Alt+ArrowRight');await page.getByRole('button',{name:'Save order',exact:true}).click();await page.waitForTimeout(100);
 check((await admin('/api/board')).data.pages[0].buttons[0].id!==saved[0],'keyboard alternative reorders board buttons');

 const capture=path.join(root,'features');fs.mkdirSync(capture,{recursive:true});
 const shortConfig=(await admin('/api/admin/config')).data,longConfig=structuredClone(shortConfig);
 for(let i=13;i<=14;i++)longConfig.pages[0].buttons.push({...structuredClone(longConfig.pages[0].buttons[0]),id:'cross-slice-'+i,label:'Extra button '+i});
 await admin('/api/admin/config',{config:longConfig,replacePassword:false});await page.reload();await page.locator('.key').first().waitFor();await page.setViewportSize({width:1024,height:600});
 await page.getByRole('button',{name:'Edit',exact:true}).click();await page.locator('.editor-page-options > summary').click();await page.getByRole('button',{name:'Arrange board',exact:true}).click();await page.waitForFunction(()=>arranging===true);
 await page.getByRole('button',{name:'Next',exact:true}).tap();await page.getByRole('button',{name:'Extra button 13',exact:true}).tap();
 await page.getByLabel('Move to position',{exact:true}).selectOption('0');await page.screenshot({path:path.join(capture,'arrange-position.png')});await page.getByRole('button',{name:'Move button',exact:true}).tap();
 check(await page.locator('.key').first().getAttribute('data-id')==='cross-slice-13','touch position picker moves a key across groups of twelve');
 await page.getByRole('button',{name:'Save order',exact:true}).tap();await page.waitForTimeout(150);
 check((await admin('/api/board')).data.pages[0].buttons[0].id==='cross-slice-13','cross-group touch move persists without changing actions');
 await admin('/api/admin/config',{config:shortConfig,replacePassword:false});await page.reload();await page.locator('.key').first().waitFor();await page.setViewportSize({width:1280,height:800});

 const apps=[{id:'A'.repeat(64),name:'Discord',device:'Test headphones',percent:80,muted:false,active:true},{id:'B'.repeat(64),name:'Game',device:'Test speakers',percent:60,muted:false,active:true}];let appWrites=[];
 await page.route('**/api/apps',async route=>{if(route.request().method()==='POST'){const body=route.request().postDataJSON();appWrites.push(body);const target=apps.find(a=>a.id===body.id);if(body.percent!==undefined)target.percent=body.percent;if(body.muted!==undefined)target.muted=body.muted;return route.fulfill({json:{ok:true}});}return route.fulfill({json:{apps}});});
 await page.getByRole('button',{name:'App volumes',exact:true}).click();await page.locator('.app-session').first().waitFor();check(await page.locator('.app-session').count()===2,'app mixer shows separate playback sessions');
 await page.getByLabel('Find an app', {exact:true}).fill('Discord');check(await page.locator('.app-session').count()===1,'app mixer filters by application');
 const slider=page.getByRole('slider',{name:'Discord playback volume',exact:true});await slider.focus();await slider.press('Home');await page.waitForTimeout(200);
 check(appWrites.some(w=>w.id===apps[0].id&&w.percent===0),'app volume slider sends an explicit percentage');
 await page.getByRole('button',{name:'Mute Discord',exact:true}).click();await page.getByRole('button',{name:'Unmute Discord',exact:true}).waitFor();check(apps[0].muted,'app mute sends explicit state');
 await page.screenshot({path:path.join(capture,'apps-desktop.png')});
 await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(capture,'apps-mobile.png')});check(await page.evaluate(()=>document.querySelector('#panel-body').scrollWidth<=document.querySelector('#panel-body').clientWidth),'app mixer has no compact horizontal overflow');
 await page.getByRole('button',{name:'Close settings',exact:true}).click();await page.setViewportSize({width:1280,height:800});

 const sounds=[{index:1,name:'Air horn',artist:'Demo',duration:'1.2',categories:['Alerts']},{index:2,name:'Applause',artist:'Demo',duration:'3.0',categories:['Crowd']},{index:3,name:'<img src=x onerror=alert(1)>',artist:'Fixture',duration:'1',categories:['Alerts']}];let plays=[],imports=[];
 await page.route('**/api/soundpad/library',route=>route.fulfill({json:{sounds,warning:null}}));
 await page.route('**/api/soundpad/play',route=>{plays.push(route.request().postDataJSON());return route.fulfill({json:{ok:true}});});
 await page.route('**/api/admin/soundpad/import',route=>{imports.push(route.request().postDataJSON());return route.fulfill({json:{ok:true}});});
 await page.getByRole('button',{name:'Soundpad',exact:true}).click();await page.locator('.library-row').first().waitFor();check(await page.locator('.library-row').count()===3,'Soundpad library shows names from fixture metadata');
 check(await page.locator('#library-results img').count()===0,'Soundpad library names are rendered as text, not markup');
 await page.getByLabel('Category',{exact:true}).selectOption('Crowd');check(await page.locator('.library-row').count()===1,'Soundpad category filter');await page.getByLabel('Category',{exact:true}).selectOption('');
 await page.getByLabel('Search sounds',{exact:true}).fill('horn');check(await page.locator('.library-row').count()===1,'Soundpad library search');
 await page.getByRole('button',{name:'Play Air horn',exact:true}).click();check(plays.at(-1).index===1&&plays.at(-1).output==='mic','library play sends the selected sound and output');
 await page.getByRole('checkbox',{name:'Select Air horn',exact:true}).check();await page.getByRole('button',{name:'Add selected buttons',exact:true}).click();await page.waitForTimeout(50);check(imports.at(-1).indices[0]===1&&imports.at(-1).output==='mic','library import sends selected indices and routing');
 await page.getByLabel('Search sounds',{exact:true}).fill('');await page.screenshot({path:path.join(capture,'library-desktop.png')});
 await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(capture,'library-mobile.png')});check(await page.evaluate(()=>document.querySelector('#panel-body').scrollWidth<=document.querySelector('#panel-body').clientWidth),'Soundpad browser has no compact horizontal overflow');
 await page.getByRole('button',{name:'Close settings',exact:true}).click();await page.setViewportSize({width:1024,height:600});
 await page.getByRole('button',{name:'Edit',exact:true}).click();await page.locator('.editor-page-options > summary').click();await page.getByRole('button',{name:'Arrange board',exact:true}).click();await page.waitForFunction(()=>arranging===true);await page.screenshot({path:path.join(capture,'arrange-tablet.png')});
 await page.getByRole('button',{name:'Cancel',exact:true}).click();await page.screenshot({path:path.join(capture,'board-tablet.png')});
 check(errors.length===0,'new feature flows have no browser JavaScript errors');
 await browser.close();browser=null;await admin('/api/admin/config',{config:before,replacePassword:false});
 const device=(await admin('/api/admin/info')).data.devices.filter(d=>d.name==='Feature tests').at(-1);await admin('/api/admin/revoke',{id:device.id});
 fs.writeFileSync(path.join(root,'feature-results.json'),JSON.stringify({passed:checks,errors,screenshots:capture,fixtureQualification:'Soundpad browser and app mixer UI use fixtures; layout endpoints and Windows session enumeration use the real companion.'},null,2));
 console.log(checks+' new feature checks passed.');
}
run().catch(async error=>{console.error(error);if(browser)await browser.close();process.exitCode=1;});
