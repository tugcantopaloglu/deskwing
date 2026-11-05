const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../companion/wwwroot');
const output=path.join(path.resolve(process.env.TABLETDECK_TEST_OUTPUT||path.join(__dirname,'../.test-output')),'layout');
fs.mkdirSync(output,{recursive:true});
const html=fs.readFileSync(path.join(root,'index.html'),'utf8'),allowed=new Set(['index.html','fonts/Manrope.ttf']);
for(const [tag] of html.matchAll(/<(?:script|link)\b[^>]*>/gi)){
 const attribute=name=>tag.match(new RegExp('\\b'+name+'\\s*=\\s*(["\\\'])(.*?)\\1','i'))?.[2];
 const script=/^<script\b/i.test(tag);if(!script&&attribute('rel')!=='stylesheet')continue;
 const asset=attribute(script?'src':'href');if(!asset)continue;
 assert.match(asset,/^(?:[\w-]+\/)*[\w.-]+\.(?:js|css)$/,'fixture assets must be explicit local script or stylesheet paths');
 allowed.add(asset);
}
// Intercept a synthetic HTTPS origin inside the browser. No local web server,
// certificate exception, or real network connection is needed for this fixture.
const realRoot=fs.realpathSync(root),assets=new Map([...allowed].map(route=>{
 const file=fs.realpathSync(path.join(root,route)),relative=path.relative(realRoot,file);
 assert.ok(relative&&!path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep),'fixture assets must remain inside wwwroot');
 return [route,{body:fs.readFileSync(file),type:route.endsWith('.js')?'application/javascript':route.endsWith('.css')?'text/css':route.endsWith('.ttf')?'font/ttf':'text/html'}];
}));
let checks=0,browser;
function check(value,label){assert.ok(value,label);checks++;console.log('PASS '+label);}
async function fixture(page){
 await page.goto('https://layout.test/?preview=1');
 await page.locator('.key').first().waitFor();
 await page.evaluate(()=>{
  const icons=Object.keys(paths);
  pages[0].buttons=Array.from({length:55},(_,i)=>({id:'fixture-'+i,label:i===0?'A very long custom streaming action label':i===1?'Gameplay':icons[i%icons.length]+' '+(i+1),kind:i===1?'scene':'sound',target:i===1?'Gameplay':'',icon:icons[i%icons.length],color:['violet','amber','blue','red'][i%4],configured:true}));
  pageId=pages[0].id;slice=0;renderBoard();
 });
}
(async()=>{
 browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const page=await browser.newPage({viewport:{width:1067,height:667},hasTouch:true}),served=new Set();
 await page.route('**/*',route=>{
  const request=new URL(route.request().url());
  const asset=request.origin==='https://layout.test'?assets.get(request.pathname.slice(1)||'index.html'):null;
  if(asset)served.add(request.pathname.slice(1)||'index.html');
  return asset?route.fulfill({status:200,contentType:asset.type,body:asset.body}):route.fulfill({status:404,body:''});
 });
 const errors=[];page.on('pageerror',error=>errors.push(error.message));
 await fixture(page);
 check([...allowed].filter(asset=>/\.(?:js|css)$/.test(asset)).every(asset=>served.has(asset)),'fixture loads every script and stylesheet referenced by the current tablet document');
 check(await page.locator('.key').count()===12,'Studio keeps twelve keys');
 await page.getByRole('button',{name:'Full pad',exact:true}).click();
 check(await page.locator('.key').count()===24,'Full pad displays twenty-four keys');
 check(await page.locator('.mixer').isHidden(),'Full pad gives mixer space to buttons');
 await page.getByRole('button',{name:'Next',exact:true}).click();
 check(await page.locator('.key').first().getAttribute('data-id')==='fixture-24','twenty-four-key pagination reaches the correct next action');
 await page.locator('#pad-density').selectOption('40');
 check(await page.locator('.key').count()===40,'forty-key layout preserves the current action group');
 await page.getByRole('button',{name:'Next',exact:true}).click();
 check(await page.locator('.key').first().getAttribute('data-id')==='fixture-40','forty-key pagination reaches the correct next action');
 await page.reload();await page.locator('.key').first().waitFor();
 check(await page.locator('body').evaluate(node=>node.classList.contains('full-pad')&&node.classList.contains('pad-40')),'full-pad preference survives reload');
 await fixture(page);
 await page.evaluate(()=>{arranging=true;changePadView(false);});
 check(await page.locator('body').evaluate(node=>node.classList.contains('full-pad')),'layout cannot change while arranging');
 await page.locator('#pad-density').selectOption('24');
 check(await page.locator('#pad-density').inputValue()==='40','blocked density change restores its displayed value');
 await page.evaluate(()=>{arranging=false;});
 await page.evaluate(()=>{deckHold={};changePadView(false);deckHold=null;});
 check(await page.locator('body').evaluate(node=>node.classList.contains('full-pad')),'layout cannot replace a held-key control');
 await page.getByRole('button',{name:'Exit full pad',exact:true}).click();
 check(await page.locator('.mixer').isVisible()&&await page.locator('.key').count()===12,'exit restores Studio controls and twelve-key view');
 for(const [width,height] of [[1067,667],[1024,600],[1440,900],[640,360]]){
  await page.setViewportSize({width,height});
  for(const size of [12,24,40]){
   await page.evaluate(size=>{slice=0;changePadView(size!==12,size===40?40:24);document.querySelector('#notice').hidden=true;},size);
   const problems=await page.evaluate(()=>{
    const issues=[];
    for(const key of document.querySelectorAll('.key')){
     const bounds=key.getBoundingClientRect();
     if(bounds.width<44||bounds.height<44)issues.push('small touch target');
     for(const child of key.querySelectorAll('svg,.key-label,.active-tag')){
      const box=child.getBoundingClientRect();
      if(box.top<bounds.top-1||box.bottom>bounds.bottom+1||box.left<bounds.left-1||box.right>bounds.right+1)issues.push('key content clipped: '+key.title);
     }
     const shape=key.querySelector('path');const box=shape.getBBox();
     if(box.x<-2||box.y<-2||box.x+box.width>26||box.y+box.height>26)issues.push('icon outside padded viewBox');
     const tag=key.querySelector('.active-tag');if(tag){const a=tag.getBoundingClientRect(),b=key.querySelector('svg').getBoundingClientRect();if(a.left<b.right&&a.right>b.left&&a.top<b.bottom&&a.bottom>b.top)issues.push('active badge overlaps icon');}
    }
    const header=document.querySelector('.topbar');if(header.scrollWidth>header.clientWidth+1)issues.push('toolbar overflow');
    const exit=document.querySelector('#pad-toggle').getBoundingClientRect();if(exit.right>innerWidth+1||exit.width<44||exit.height<44)issues.push('exit unavailable');
    return issues;
   });
   check(problems.length===0,`${width}x${height} / ${size} keys: touch targets, icon/label fit, and exit control (${problems.join('; ')})`);
   if(process.env.DECK_CAPTURE_LAYOUT!=='0')await page.screenshot({path:path.join(output,`${width}-${height}-${size}.png`)});
  }
 }
 await page.setViewportSize({width:1067,height:667});
 await page.evaluate(()=>{changePadView(true,24);arranging=true;attachBoardReorder();});
 await page.locator('.key').first().focus();
 await page.keyboard.press('Alt+ArrowDown');
 check(await page.locator('.key').nth(6).getAttribute('data-id')==='fixture-0','keyboard reordering uses six columns in twenty-four-key view');
 await page.evaluate(()=>showMovePosition(pages[0],'fixture-0'));
 await page.locator('#move-position').selectOption('50');
 await page.locator('#move-position-apply').click();
 check(await page.locator('.key').nth(2).getAttribute('data-id')==='fixture-0','move position reaches the correct group in full-pad view');
 check(errors.length===0,'no browser JavaScript errors');
 console.log(`${checks} layout checks passed; synthetic board only, no PC actions triggered.`);
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{await browser?.close();});
