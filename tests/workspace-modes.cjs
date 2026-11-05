// Navigation policy tests: no browser, native bridge, companion or control execution.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const nodes=new Map(),classes=new Set(),listeners={},calls=[];let accepted=true,allowed=true;
function node(id){if(!nodes.has(id))nodes.set(id,{value:'studio',textContent:'',open:false,setAttribute(){},removeAttribute(){}});return nodes.get(id);}
const context={console,document:{body:{classList:{toggle(name,value){value?classes.add(name):classes.delete(name);}}},getElementById:node,querySelector:()=>null,querySelectorAll:()=>[],addEventListener:(name,fn)=>listeners[name]=fn},desktopConsole:true,dirty:false,editing:false,panel:{open:false,close(){this.open=false;calls.push('close');}},computerNavigationAllowed:()=>allowed,confirmAction:async()=>accepted,DeckEditor:{isOpen:()=>true,discardDraft:()=>calls.push('discard')},setMonitorOpen:()=>{},stopComputerDisplayView:()=>calls.push('stop-display'),refreshComputerDisplay:async()=>calls.push('read-display'),notice:message=>calls.push('notice:'+message),renderComputer:()=>calls.push('render'),setComputerMode:async mode=>{calls.push('base:'+mode);return true;}};
context.window=context;context.DeskPen={setActive:active=>calls.push('pen:'+active)};context.DeskPenTablet={setActive:active=>calls.push('pentablet:'+active)};vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(__dirname,'../companion/wwwroot/modes.js'),'utf8'),context);
let passed=0;function check(value,label){assert.ok(value,label);passed++;console.log('PASS '+label);}
(async()=>{
 check(await context.setWorkspaceMode('pen'),'ink opens');check(node('app-mode').value==='pen'&&calls.includes('pen:true'),'ink selection and lifecycle agree');
 calls.length=0;check(await context.setComputerMode('studio'),'existing console route exits ink');check(calls.includes('pen:false')&&node('app-mode').value==='studio','ink deactivates when console navigates');
 context.dirty=true;accepted=false;calls.length=0;check(!await context.setWorkspaceMode('pen'),'declined draft discard blocks ink');check(!calls.includes('discard')&&!calls.includes('pen:true')&&context.dirty,'cancel preserves editor and draft');
 accepted=true;check(await context.setWorkspaceMode('pen'),'confirmed discard enters ink');check(calls.includes('discard')&&!context.dirty,'explicit discard removes draft before navigation');
 allowed=false;calls.length=0;check(!await context.setWorkspaceMode('computer'),'active action guard blocks workspace change');check(node('app-mode').value==='pen'&&calls.length===0,'blocked switch keeps ink selected');
 allowed=true;check(await context.setWorkspaceMode('display'),'display mode opens');check(calls.includes('read-display')&&!calls.some(x=>/create|share/.test(x)),'opening display only reads capability state');check(classes.has('display-workspace-mode')&&node('app-mode').value==='display','display gets dedicated surface state');
 await context.setWorkspaceMode('computer');check(!classes.has('display-workspace-mode')&&calls.includes('stop-display'),'leaving display stops frame view');
 context.renderComputer();check(node('app-mode').value==='computer','context refresh retains workspace selection');check(node('console-title').textContent==='Computer Copilot','context refresh retains the computer workspace heading');
 await context.setWorkspaceMode('pen');context.DeskWorkspace.refresh();check(node('console-title').textContent==='Ink & notes','late panel close can restore the active ink heading');
 calls.length=0;check(await context.setWorkspaceMode('pentablet'),'Windows pen workspace opens');check(calls.includes('pen:false')&&calls.includes('pentablet:true')&&node('app-mode').value==='pentablet','Windows pen activation leaves shared ink');check(node('console-title').textContent==='Pen tablet','Windows pen has its own workspace heading');
 calls.length=0;await context.setWorkspaceMode('pen');check(calls.includes('pentablet:false')&&calls.includes('pen:true'),'leaving Windows pen releases its session through its lifecycle');
 context.DeckEditor.isSaving=()=>true;check(!await context.setWorkspaceMode('studio')&&node('app-mode').value==='pen','in-flight editor writes block workspace navigation');
 console.log(passed+' workspace navigation checks passed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
