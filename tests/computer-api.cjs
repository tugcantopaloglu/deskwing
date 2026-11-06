// Isolated HTTP checks. Never sends a valid keyboard action, creates a monitor,
// captures pixels, installs a driver, opens a GUI, or touches live user settings.
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const net=require('node:net');
const http=require('node:http');
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');

const root=path.resolve(__dirname,'..');
const fixture=fs.mkdtempSync(path.join(os.tmpdir(),'tabletdeck-computer-api-'));
const data=path.join(fixture,'data');fs.mkdirSync(data);
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'.test-output/computer-api-build/publish/DeskWing-Companion.exe');
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let server,session,httpPort,httpsPort,controlPort,checks=0;
const check=(condition,label)=>{assert.ok(condition,label);checks++;console.log('PASS '+label);};
const clone=value=>JSON.parse(JSON.stringify(value));

async function freePort(){
    const listener=net.createServer();
    await new Promise((resolve,reject)=>{listener.once('error',reject);listener.listen(0,'127.0.0.1',resolve);});
    const port=listener.address().port;await new Promise(resolve=>listener.close(resolve));return port;
}
async function api(route,body,{admin=true,token,headers={}}={}){
    const response=await fetch(`http://127.0.0.1:${httpPort}`+route,{
        method:body===undefined?'GET':'POST',
        headers:{'Content-Type':'application/json',...(admin?{'X-Deck-Admin':session.token}:{}),...(token?{Authorization:'Bearer '+token}:{}),...headers},
        body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(10000)
    });
    const text=await response.text();
    return {status:response.status,headers:response.headers,data:text?JSON.parse(text):null};
}
async function rawHostMutation(settings){
    // Node 22.14 fetch replaces a supplied Host header with the URL authority.
    // This fixed-loopback HTTP request exercises the PC listener's Host guard;
    // HTTPS would hit its separate admin-port guard and would not test this rule.
    const body=JSON.stringify(settings);
    return await new Promise((resolve,reject)=>{
        const request=http.request({hostname:'127.0.0.1',port:httpPort,path:'/api/admin/computer',method:'POST',headers:{Host:'external.invalid','X-Deck-Admin':session.token,'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)}},response=>{response.resume();response.on('end',()=>resolve(response.statusCode));});
        request.on('error',reject);request.setTimeout(10000,()=>request.destroy(Error('Fixture Host check timed out.')));request.end(body);
    });
}
async function start(){
    const log=fs.openSync(path.join(fixture,'server.log'),'a');
    server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port',String(httpPort),'--https-port',String(httpsPort),'--control-port',String(controlPort)],{windowsHide:true,stdio:['ignore',log,log]});
    fs.closeSync(log);session=null;
    for(let attempt=0;attempt<100;attempt++){
        if(server.exitCode!==null)throw Error('Isolated companion exited: '+fs.readFileSync(path.join(fixture,'server.log'),'utf8'));
        try{
            session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));
            if((await api('/api/admin/computer')).status===200)return;
        }catch{}
        await sleep(100);
    }
    throw Error('Isolated companion did not become ready: '+fixture);
}
async function stop(){
    if(!server)return;
    const running=server;
    try{if(session&&running.exitCode===null)await api('/api/admin/shutdown',{});}catch{}
    for(let attempt=0;attempt<60&&running.exitCode===null;attempt++)await sleep(50);
    if(running.exitCode===null){running.kill();for(let attempt=0;attempt<60&&running.exitCode===null;attempt++)await sleep(50);}
    if(running.exitCode===null)throw Error('Isolated companion did not stop.');
    server=null;session=null;
}

async function run(){
    assert.ok(fs.existsSync(executable),'Build an isolated companion first or set TABLETDECK_COMPANION.');
    httpPort=await freePort();do{httpsPort=await freePort();}while(httpsPort===httpPort);do{controlPort=await freePort();}while(controlPort===httpPort||controlPort===httpsPort);
    fs.writeFileSync(path.join(data,'desktop.json'),JSON.stringify({usbReconnect:false,usbSerial:'',updateNotifications:false,usbApkUpdates:false,usbApkReopen:false}));
    fs.writeFileSync(path.join(data,'config.json'),JSON.stringify({obsPort:19999,pages:[{id:'fixture',name:'Isolated fixture',buttons:[]}]}));
    await start();

    for(const [route,body] of [['/api/computer',undefined],['/api/computer/media',undefined],['/api/computer/media/action',{}],['/api/computer/preset',{presetId:'desktop'}],['/api/computer/follow',{automatic:true}],['/api/computer/action',{}],['/api/computer/display',undefined],['/api/computer/display/frame',{width:960}]]){
        check((await api(route,body,{admin:false})).status===401,'unpaired request rejected: '+route);
    }
    const pin=(await api('/api/admin/pairing',{})).data;
    const paired=await api('/api/pair',{code:pin.code,name:'Isolated Computer API fixture'},{admin:false});
    assert.equal(paired.status,200);const token=paired.data.token;assert.equal(typeof token,'string');
    for(const [route,body] of [['/api/admin/computer',undefined],['/api/admin/computer',{}],['/api/admin/computer/display',{operation:'create',width:1280,height:800,acceptInstalledDriver:true,shareWithTablet:true}],['/api/admin/computer/display',{operation:'share',enabled:true}],['/api/admin/computer/display',{operation:'remove'}]]){
        check((await api(route,body,{admin:false,token})).status===403,'paired tablet cannot administer settings/display: '+route+(body?.operation?' '+body.operation:''));
    }
    const snapshot=(await api('/api/computer',undefined,{admin:false,token})).data;
    check(snapshot.settings.enabled===false&&typeof snapshot.contextToken==='string','paired snapshot defaults controls off and supplies context token');
    const expected=['desktop','youtube','spotify','coding','claude','resolve','photoshop'];
    check(expected.every(id=>snapshot.settings.presets.some(p=>p.id===id)),'all seven application/media presets are available');
    check(!JSON.stringify(snapshot).includes(token),'Computer snapshot does not expose pairing credential');
    const disabled=await api('/api/computer/action',{presetId:snapshot.activePresetId,controlId:snapshot.settings.presets.find(p=>p.id===snapshot.activePresetId).controls[0].id,contextToken:snapshot.contextToken},{admin:false,token});
    check(disabled.status===409&&/Enable Computer controls/.test(disabled.data.error),'disabled controller rejects configured action before keyboard execution');
    check((await api('/api/computer/follow',{automatic:true},{admin:false,token})).status===409,'following cannot enable an opted-out controller');
    const disabledMedia=(await api('/api/computer/media',undefined,{admin:false,token})).data;
    check(disabledMedia.sessions.length===0&&/Enable Computer Copilot/.test(disabledMedia.error),'disabled media snapshot exposes no player metadata');
    check((await api('/api/computer/media/action',{presetId:'spotify',sessionId:'0'.repeat(32),contextToken:'0'.repeat(32),operation:'toggle'},{admin:false,token})).status===409,'disabled media route cannot dispatch a player operation');

    let settings=(await api('/api/admin/computer')).data;
    const original=clone(settings);
    const crossOrigin=await api('/api/admin/computer',settings,{headers:{Origin:'https://external.invalid'}});
    check(crossOrigin.status===403,'cross-origin settings mutation rejected');
    check((await api('/api/admin/computer/display',{operation:'create',width:1280,height:800,acceptInstalledDriver:true},{headers:{Origin:'https://external.invalid'}})).status===403,'cross-origin display creation rejected before driver use');
    check((await api('/api/admin/computer',settings,{headers:{'Sec-Fetch-Site':'cross-site'}})).status===403,'cross-site settings mutation rejected');
    check(await rawHostMutation(settings)===403,'untrusted Host settings mutation rejected');

    for(const preset of settings.presets){
        preset.label='Fixture '+preset.label;
        preset.controls[0].label='Fixture '+preset.controls[0].label;
        preset.controls[0].shortcut=preset.controls[0].global?'PLAYPAUSE':'CTRL+SHIFT+F12';
    }
    settings.enabled=false;settings.automatic=false;settings.manualPresetId='photoshop';
    check((await api('/api/admin/computer',settings)).status===200,'local PC can edit all preset labels and shortcut bindings');
    const edited=(await api('/api/admin/computer')).data;
    check(JSON.stringify(edited)===JSON.stringify(settings),'all preset edits round-trip through the settings API');
    const stored=JSON.parse(fs.readFileSync(path.join(data,'computer-copilot.json'),'utf8'));
    check(JSON.stringify(stored)===JSON.stringify(settings),'settings persisted only in isolated fixture directory');
    for(const preset of edited.presets)check(preset.label.startsWith('Fixture ')&&preset.controls[0].label.startsWith('Fixture '),'preset and binding remain editable: '+preset.id);

    const invalidCases=[
        value=>{value.presets[0].controls[0].shortcut='ENTER';value.presets[0].controls[0].global=false;value.presets[0].applications=['Fixture.exe'];},
        value=>{value.presets[0].controls[0].shortcut='CTRL+S';value.presets[0].controls[0].global=true;},
        value=>{value.presets[1].applications=['C:\\Windows\\notepad.exe'];},
        value=>{value.presets[1].controls[0].shortcut='CTRL+CTRL+P';},
        value=>{value.presets[1].controls[0].shortcut='CTRL';},
        value=>{value.presets[1].controls[1].id=value.presets[1].controls[0].id;},
        value=>{value.manualPresetId='missing';}
    ];
    for(const mutate of invalidCases){const invalid=clone(settings);mutate(invalid);check((await api('/api/admin/computer',invalid)).status===400,'invalid settings rejected without changing saved configuration');}
    check(JSON.stringify((await api('/api/admin/computer')).data)===JSON.stringify(settings),'invalid edits preserve last valid settings');
    check((await api('/api/computer/preset',{presetId:'missing'},{admin:false,token})).status===400,'unknown preset selection rejected');
    check((await api('/api/computer/preset',{presetId:'resolve'},{admin:false,token})).status===200,'paired tablet may select an existing preset without rewriting bindings');
    const selected=(await api('/api/admin/computer')).data;
    check(selected.manualPresetId==='resolve'&&selected.automatic===false&&JSON.stringify(selected.presets)===JSON.stringify(settings.presets),'manual selection persists while preserving PC-configured bindings');
    settings=selected;
    const mediaSettings={...clone(settings),enabled:true,automatic:false,manualPresetId:'spotify'};
    check((await api('/api/admin/computer',mediaSettings)).status===200,'fixture can enable a selected Spotify layout');
    check((await api('/api/computer/follow',{automatic:true},{admin:false,token})).status===200&&(await api('/api/computer')).data.settings.automatic,'paired tablet can resume automatic following');
    check((await api('/api/computer/follow',{automatic:false},{admin:false,token})).status===200&&!(await api('/api/computer')).data.settings.automatic,'paired tablet can pin the active layout');
    check((await api('/api/computer/follow',{automatic:true},{headers:{Origin:'https://external.invalid'}})).status===403,'cross-origin following changes are rejected');
    assert.equal((await api('/api/admin/computer',mediaSettings)).status,200);
    const mediaContext=(await api('/api/computer')).data;
    check((await api('/api/computer/action',{presetId:'spotify',controlId:'media-play',contextToken:mediaContext.contextToken})).status===409,'old Spotify button cannot fall back to a global keyboard play/pause');
    const invalidMedia={presetId:'spotify',sessionId:'0'.repeat(32),contextToken:'0'.repeat(32),operation:'release'};
    check((await api('/api/computer/media/action',invalidMedia,{admin:false,token})).status===400,'unsupported media operation rejected before any Windows session operation');
    check((await api('/api/computer/media/action',{...invalidMedia,presetId:'youtube',operation:'toggle'},{admin:false,token})).status===409,'stale layout cannot dispatch media controls for another player mode');
    check((await api('/api/computer/media/action',invalidMedia,{headers:{Origin:'https://external.invalid'}})).status===403,'cross-origin media controls rejected before dispatch');
    assert.equal((await api('/api/admin/computer',settings)).status,200);
    check((await api('/api/computer/action',{presetId:'resolve',controlId:'reverse',contextToken:'invalid'},{admin:false,token})).status===400,'malformed action context rejected');

    // Enable only to exercise pre-execution rejection paths. No accepted shortcut request is sent.
    const safe=clone(settings);safe.enabled=true;safe.manualPresetId='claude';
    const placeholder=safe.presets.find(p=>p.id==='claude').controls.find(c=>c.shortcut==='');assert.ok(placeholder);
    check((await api('/api/admin/computer',safe)).status===200,'PC can enable contextual controls explicitly');
    const enabled=(await api('/api/computer',undefined,{admin:false,token})).data;
    const stale=await api('/api/computer/action',{presetId:'claude',controlId:placeholder.id,contextToken:'0'.repeat(32)},{admin:false,token});
    check(stale.status===409&&/changed/.test(stale.data.error),'stale action context rejected before execution');
    const unassigned=await api('/api/computer/action',{presetId:'claude',controlId:placeholder.id,contextToken:enabled.contextToken},{admin:false,token});
    check(unassigned.status===409&&(/Assign this shortcut/.test(unassigned.data.error)||/changed/.test(unassigned.data.error)),'unassigned control or changed foreground rejects without keyboard action');
    check((await api('/api/admin/computer',settings)).status===200,'controls disabled again before restart');

    const display=await api('/api/computer/display',undefined,{admin:false,token});
    check(display.status===200&&!display.data.active&&!display.data.sharedWithTablet&&!display.data.independentDesktop,'isolated display status never adopts or shares an existing monitor');
    check(display.data.displays.every(p=>p.isOwned===false),'physical/foreign display inventory never identifies it as TabletDeck-owned');
    check(display.headers.get('cache-control')==='no-store','desktop status is not cached');
    check((await api('/api/computer/display/frame',{width:960},{admin:false,token})).status===409,'paired frame request before create/share is refused without capture');
    check((await api('/api/computer/display/frame',{width:4096},{admin:false,token})).status===400,'unsupported desktop frame size refused before capture');
    check((await api('/api/admin/computer/display',{operation:'create',width:1280,height:800,shareWithTablet:true,acceptInstalledDriver:false})).status===400,'display creation requires explicit installed-driver acceptance');
    check((await api('/api/admin/computer/display',{operation:'create',width:1920,height:1200,shareWithTablet:true,acceptInstalledDriver:true})).status===400,'display creation rejects unsupported size before any driver commands');
    check((await api('/api/admin/computer/display',{operation:'share',enabled:true})).status===409,'sharing cannot be enabled before this process creates its own display');
    const finalDisplay=(await api('/api/computer/display')).data;
    check(!finalDisplay.active&&!finalDisplay.sharedWithTablet&&JSON.stringify(finalDisplay.displays)===JSON.stringify(display.data.displays),'all refused display operations preserve observed desktop topology');

    await stop();await start();
    check(JSON.stringify((await api('/api/admin/computer')).data)===JSON.stringify(settings),'all edited settings and selected preset survive process restart');
    check((await api('/api/computer',undefined,{admin:false,token})).status===200,'paired device remains authenticated after isolated restart');
    check(!(await api('/api/computer/display')).data.sharedWithTablet,'display sharing remains off after restart');
    check(original.presets.length===settings.presets.length,'editing preserves all built-in presets');
    const result={checks,guiOpened:false,keyboardActionsSent:false,realDriverCommandsSent:false,desktopPixelsCaptured:false,liveDataModified:false,fixture};
    fs.mkdirSync(path.join(root,'.test-output'),{recursive:true});fs.writeFileSync(path.join(root,'.test-output/computer-api-results.json'),JSON.stringify(result,null,2));
    console.log(checks+' Computer API checks passed; no keyboard execution, monitor creation, pixel capture, driver installation, or GUI.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{try{await stop();}catch(error){console.error(error);process.exitCode=1;}});
