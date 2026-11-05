'use strict';
// Isolated window-launch and startup behavior. No real companion or native window is opened.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../companion/wwwroot/ink-window.js'),'utf8');
let checks=0;
function check(condition,label){assert.ok(condition,label);checks++;console.log('PASS '+label);}
class Node {
 constructor(tag){this.tagName=tag;this.children=[];this.listeners={};this.attributes={};this.classes=new Set();this.classList={add:value=>this.classes.add(value),contains:value=>this.classes.has(value)||this.className?.split(' ').includes(value),toggle:(value,enabled)=>enabled?this.classes.add(value):this.classes.delete(value)};this.textContent='';this.disabled=false;}
 append(...nodes){for(const node of nodes){node.parent=this;this.children.push(node);}}
 addEventListener(name,handler){this.listeners[name]=handler;}
 setAttribute(name,value){this.attributes[name]=value;}
 querySelector(selector){return walk(this).find(node=>selector.startsWith('.')?node.className?.split(' ').includes(selector.slice(1)):node.tagName===selector)||null;}
 remove(){if(this.parent)this.parent.children=this.parent.children.filter(node=>node!==this);}
 focus(){this.focused=true;}
}
function walk(root){return root.children.flatMap(child=>[child,...walk(child)]);}
function fixture({desktop=true,search='',built=false,compact=false}={}){
 const body=new Node('body'),host=new Node('main');host.id='pen-workspace';body.append(host);
 const events={},windowEvents={},requests=[],notices=[],modes=[],observers=[];
 let request=async()=>({ok:true}),activate=async()=>true,resizes=0;
 const document={body,title:'DeskWing',createElement:tag=>new Node(tag),getElementById:id=>walk(body).find(node=>node.id===id)||null,querySelector:selector=>selector==='#pen-workspace .pen-heading-actions'?host.querySelector('.pen-heading-actions'):null,addEventListener:(name,handler)=>{events[name]=handler;}};
 class Observer{constructor(callback){this.callback=callback;this.disconnected=false;observers.push(this);}observe(node,options){this.node=node;this.options=options;}disconnect(){this.disconnected=true;}}
 const context={console,URLSearchParams,document,location:{search},desktopConsole:desktop,MutationObserver:Observer,api:(...args)=>{requests.push(args);return request(...args);},notice:(...args)=>notices.push(args),setWorkspaceMode:mode=>{modes.push(mode);return activate(mode);},requestAnimationFrame:fn=>fn(),DeskPen:{resize:()=>resizes++},addEventListener:(name,handler)=>windowEvents[name]=handler};
 context.window=context;
 function mount(){const actions=new Node('div');actions.className='pen-heading-actions';host.append(actions);return actions;}
 if(built)mount();
 if(compact){
  const box=new Node('div');box.id='pen-toolbox';const drawer=new Node('div');drawer.className='pen-drawer-actions';const conflict=new Node('section');conflict.className='pen-conflict';conflict.hidden=true;const status=new Node('p');status.id='pen-status';const sync=new Node('strong');sync.id='pen-sync-state';sync.textContent='Shared with your PC';box.append(drawer,conflict,status,sync);
  const notes=new Node('section');notes.className='pen-notes';const heading=new Node('div');heading.className='pen-notes-heading';const field=new Node('textarea');field.id='pen-notes';notes.append(heading,field);const canvas=new Node('canvas');canvas.id='pen-canvas';host.append(box,notes,canvas);
 }
 vm.createContext(context);vm.runInContext(source,context);
 return{document,host,events,windowEvents,requests,notices,modes,observers,mount,get resizes(){return resizes;},setRequest:fn=>request=fn,setActivate:fn=>activate=fn,initialize:()=>events.DOMContentLoaded?.()};
}
const settle=async()=>{await Promise.resolve();await Promise.resolve();await Promise.resolve();};
(async()=>{
 const tablet=fixture({desktop:false,built:true,search:'?workspace=ink-window'});tablet.initialize();check(!tablet.document.body.classes.has('ink-window-mode')&&tablet.modes.length===0&&!tablet.document.getElementById('pen-open-window'),'tablet surfaces cannot open native PC windows');
 const main=fixture();main.initialize();check(main.observers.length===1&&main.observers[0].node===main.host,'main window watches only the lazy ink host');
 const actions=main.mount();main.observers[0].callback();const button=main.document.getElementById('pen-open-window');check(button?.textContent==='Open ink window'&&actions.children.length===1,'open-window action appears when ink mounts');
 main.observers[0].callback();check(actions.children.length===1&&main.observers[0].disconnected,'mount observation stops without duplicate buttons');
 let complete;main.setRequest(()=>new Promise(resolve=>complete=resolve));const pending=button.listeners.click();button.listeners.click();check(main.requests.length===1&&button.disabled,'repeated launch taps share one in-flight request');
 check(main.requests[0][0]==='/api/admin/ink/window'&&Object.keys(main.requests[0][1]).length===0,'launch uses only the authenticated local admin route');
 complete({ok:true});await pending;check(!button.disabled&&main.notices.at(-1)[0].startsWith('Ink window opened'),'successful launch restores the action');
 main.setRequest(async()=>{throw new Error('Native window unavailable');});await button.listeners.click();check(!button.disabled&&main.notices.at(-1)[1]===true&&main.notices.at(-1)[0]==='Native window unavailable','failed launch remains retryable and reports the error');
 const early=fixture({built:true,search:'?workspace=other'});early.initialize();check(early.document.getElementById('pen-open-window')&&early.observers.length===0&&!early.document.body.classes.has('ink-window-mode'),'existing ink and unrelated query values keep the regular companion');
 const dedicated=fixture({built:true,search:'?workspace=ink-window'});check(dedicated.document.body.classes.has('ink-window-mode')&&dedicated.document.title==='DeskWing Ink','dedicated window gets its own surface before initialization');dedicated.initialize();await settle();check(dedicated.modes.join(',')==='pen'&&!dedicated.document.getElementById('pen-open-window')&&dedicated.observers.length===0,'dedicated startup opens shared ink without offering another window');
 const blocked=fixture({search:'?workspace=ink-window'});blocked.setActivate(async()=>false);blocked.initialize();await settle();const retry=blocked.document.body.querySelector('button');check(retry?.textContent==='Open ink'&&!retry.disabled,'blocked navigation shows a reachable retry instead of a blank window');blocked.setActivate(async()=>true);await retry.listeners.click();check(!blocked.document.body.querySelector('button')&&blocked.modes.length===2,'successful retry removes its fallback without replacing shared ink');
 const unopened=fixture();unopened.initialize();unopened.windowEvents.pagehide();check(unopened.observers[0].disconnected,'closing an unused main window releases its pending mount observer');
 const compact=fixture({built:true,compact:true,search:'?workspace=ink-window'});compact.initialize();await settle();const tools=compact.document.getElementById('ink-window-tools'),notes=compact.document.getElementById('ink-window-notes');
 check(tools?.textContent==='Show tools'&&!compact.host.classes.has('ink-window-tools-open')&&!compact.host.classes.has('ink-window-notes-open'),'dedicated window starts with its canvas unobstructed');
 tools.listeners.click();check(compact.host.classes.has('ink-window-tools-open')&&tools.attributes['aria-expanded']==='true','compact tools are explicitly revealed with accessible state');
 notes.listeners.click();check(!compact.host.classes.has('ink-window-tools-open')&&compact.host.classes.has('ink-window-notes-open')&&compact.document.getElementById('pen-notes').focused,'notes open independently and focus the existing shared field');
 compact.document.getElementById('ink-window-hide-notes').listeners.click();check(!compact.host.classes.has('ink-window-notes-open')&&tools.focused,'notes close back to canvas and a reachable tool control');
 const conflict=compact.host.querySelector('.pen-conflict');conflict.hidden=false;compact.observers[0].callback();check(tools.classes.has('ink-window-warning')&&tools.attributes['aria-label'].includes('attention'),'hidden tools advertise unresolved sync conflicts');
 conflict.hidden=true;compact.document.getElementById('pen-sync-state').textContent='Offline · local copy kept';compact.observers[0].callback();check(tools.classes.has('ink-window-warning'),'offline sync state remains visible through the compact indicator');
 compact.document.getElementById('pen-sync-state').textContent='Shared with your PC';compact.observers[0].callback();check(!tools.classes.has('ink-window-warning'),'resolved sharing clears its warning indicator');
 tools.listeners.click();compact.document.getElementById('pen-canvas').listeners.pointerdown();check(!compact.host.classes.has('ink-window-tools-open')&&compact.resizes>3,'drawing closes overlays and asks ink to refit the full paper');
 tools.listeners.click();let prevented=false;compact.events.keydown({key:'Escape',preventDefault(){prevented=true;}});check(prevented&&!compact.host.classes.has('ink-window-tools-open'),'Escape closes compact tools without entering browser fullscreen');
 tools.listeners.click();compact.host.classes.add('is-fullscreen');compact.events.keydown({key:'Escape',preventDefault(){throw new Error('Compact mode must not intercept fullscreen Escape.');}});check(compact.host.classes.has('ink-window-tools-open'),'fullscreen keeps ownership of its own Escape and tool state');
 compact.windowEvents.pagehide();check(compact.observers[0].disconnected,'dedicated warning observer is released on window close');
 console.log(checks+' ink-window UI checks passed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
