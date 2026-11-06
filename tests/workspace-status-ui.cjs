// Real bundled tablet page, intercepted APIs and a headless browser. No PC controls or audio.
'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const webroot=path.resolve(__dirname,'../companion/wwwroot');
let browser,checks=0,actionPosts=0,configPosts=0,allPosts=0,gate=null,blockNextBoard=false;
const errors=[],postRoutes=[],key='A'.repeat(64),actionId='live-soundpad:memes:'+key+':1';
const saved={profileId:'A'.repeat(32),profileName:'Creative board',workspace:{computerName:'Studio <PC>',connectionCode:'ABCD-1234-5678',version:'1.15.0'},pages:[
  {id:'main',name:'Main',icon:'home',buttons:[{id:'kept',label:'Existing key',kind:'hotkey',configured:true,icon:'keyboard',color:'blue'}]},
  {id:'memes',name:'Memes',icon:'speaker',liveSoundpad:true,liveSoundpadOutput:'mic',soundpadWarning:null,buttons:[{id:actionId,label:'Fixture sound',kind:'soundpadPlay',configured:true,icon:'speaker',color:'amber',categories:[],searchText:'Fixture sound'}]}
]};
const clone=value=>JSON.parse(JSON.stringify(value));
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function waitGate(){for(let i=0;i<100&&!gate;i++)await delay(20);assert.ok(gate,'fixture captured the pending board read');}
async function main(){
  browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  const context=await browser.newContext({viewport:{width:1280,height:860}});
  await context.route('**/*',async route=>{
    const request=route.request(),url=new URL(request.url());
    if(url.origin!=='http://deskwing.test')return route.abort();
    const send=(body,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
    if(url.pathname.startsWith('/api/')){
      if(request.method()==='POST'){allPosts++;postRoutes.push(url.pathname);}
      if(url.pathname==='/api/board'){
        if(blockNextBoard){blockNextBoard=false;await new Promise(resolve=>{gate=resolve;});gate=null;}
        return send(clone(saved));
      }
      if(url.pathname==='/api/state')return send({pcConnected:true,obsConnected:false,inputs:{},session:'fixture',revision:1});
      if(url.pathname==='/api/execution')return send({session:'fixture',revision:1,active:null});
      if(url.pathname==='/api/events')return route.fulfill({status:200,contentType:'text/event-stream',body:': polling fixture\n\n'});
      if(url.pathname==='/api/admin/info')return send({devices:[{id:'fixture',name:'Paired fixture'}],addresses:[],httpsPort:8743});
      if(url.pathname==='/api/profile-lock')return send({ok:true});
      if(url.pathname==='/api/action'){actionPosts++;check(request.postDataJSON().id===actionId,'output test submits only the selected current live sound');return send({ok:true});}
      if(url.pathname==='/api/admin/config'&&request.method()==='POST'){configPosts++;return send({error:'Board mutation forbidden in this fixture'},409);}
      return send({error:'Unused fixture API'},404);
    }
    const relative=url.pathname==='/'?'index.html':decodeURIComponent(url.pathname).replace(/^\/+/,''),file=path.resolve(webroot,relative);
    if(!file.startsWith(webroot+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile())return route.fulfill({status:404,body:'Not found'});
    const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.png':'image/png','.svg':'image/svg+xml','.ico':'image/x-icon'}[path.extname(file)]||'application/octet-stream';
    return route.fulfill({contentType:mime,body:fs.readFileSync(file)});
  });
  const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
  await page.goto('http://deskwing.test/index.html#admin=fixture');
  await page.locator('#workspace-status-name').filter({hasText:'Studio <PC>'}).waitFor();
  check((await page.locator('#workspace-status-detail').innerText()).includes('Creative board'),'tablet keeps its current board visible');
  check(await page.locator('#workspace-status-name').evaluate(node=>node.children.length)===0,'PC names are rendered as text');
  await page.locator('#workspace-status').click();await page.locator('.workspace-check').waitFor();
  check((await page.locator('.workspace-check').innerText()).includes('Microphone'),'guided status explains the saved audio destination');
  check(actionPosts===0&&configPosts===0&&postRoutes.every(route=>route==='/api/profile-lock'),'guided status uses reads and its existing panel lease, without playback or board writes: '+postRoutes.join(', '));
  await page.locator('#close-panel').click();await page.getByRole('button',{name:'Memes',exact:true}).click();
  check((await page.locator('#soundpad-board-route').innerText()).includes('Microphone only'),'live pad makes microphone-only routing explicit');
  const openTest=async()=>{await page.locator('#soundpad-board-test').click();await page.locator('#soundpad-output-choice').waitFor();};
  await openTest();
  check(await page.locator('#soundpad-output-play').isDisabled()&&actionPosts===0,'opening a test never selects or plays a sound automatically');
  await page.locator('#soundpad-output-choice').selectOption('0');
  check(actionPosts===0,'choosing a sound does not play it');
  saved.pages[1].liveSoundpadOutput='both';
  await page.locator('#soundpad-output-play').click();
  await page.locator('#soundpad-output-result').filter({hasText:'output changed'}).waitFor();
  check(actionPosts===0,'changed output is detected before any playback request');
  await page.locator('#close-panel').click();await page.evaluate(()=>refresh(true));
  check((await page.locator('#soundpad-board-route').innerText()).includes('Speakers and microphone'),'pad displays the freshly saved route');
  await openTest();await page.locator('#soundpad-output-choice').selectOption('0');await page.locator('#soundpad-output-play').click();
  await page.locator('#soundpad-output-result').filter({hasText:'Soundpad accepted'}).waitFor();
  check(actionPosts===1&&configPosts===0,'explicit Play runs exactly one selected sound without saving configuration');
  check((await page.locator('#soundpad-output-result').innerText()).includes('If you cannot hear it'),'acknowledgement does not claim audible output was verified');
  await page.locator('#close-panel').click();await openTest();await page.locator('#soundpad-output-choice').selectOption('0');
  blockNextBoard=true;await page.locator('#soundpad-output-play').click();await waitGate();await page.evaluate(()=>{busy=true;});gate();
  await page.locator('#soundpad-output-result').filter({hasText:'Finish the current action'}).waitFor();
  check(await page.evaluate(()=>busy)===true&&actionPosts===1,'a competing action keeps ownership when a pending test is refused');
  await page.evaluate(()=>{busy=false;});await page.locator('#close-panel').click();
  await openTest();await page.locator('#soundpad-output-choice').selectOption('0');blockNextBoard=true;await page.locator('#soundpad-output-play').click();await waitGate();
  await page.locator('#close-panel').click();gate();await delay(150);
  check(actionPosts===1&&!await page.locator('#panel').evaluate(node=>node.open),'closing a pending test suppresses playback and late panel reopening');
  for(const [width,height] of [[1280,860],[800,1000],[390,844]]){await page.setViewportSize({width,height});check(await page.locator('body').evaluate(node=>node.scrollWidth<=innerWidth),'workspace and route status fit '+width+'px width');}
  check(configPosts===0&&saved.pages[0].buttons[0].id==='kept','all checks preserve existing board data');
  check(errors.length===0,'bundled scripts run without page errors: '+errors.join('; '));
  console.log(checks+' workspace status and output checks passed; APIs intercepted, no real sounds or board writes.');
}
main().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();});
