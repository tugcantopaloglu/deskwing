// Isolated controller tests: never contacts OBS, Soundpad, a tablet, or a live companion.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {fixture}=require('./studio-ui.cjs');let passed=0;
const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
const report={profileId:'profile',profileName:'Board <safe>',checkedAt:'2026-11-05T12:00:00Z',complete:true,notice:'No action ran.',checks:[
 {severity:'ok',code:'file',message:'File exists',pageId:'p',buttonId:'one',actionLabel:'Open editor'},
 {severity:'warning',code:'shortcut-rehearsal',message:'Rehearse this shortcut',pageId:'p',buttonId:'two',actionLabel:'Shortcut'},
 {severity:'error',code:'scene-missing',message:'Scene <gone> is missing',pageId:'p',buttonId:'three',stepPath:'2.1',actionLabel:'Macro <name>'}
]};
function backupFixture(){
 const f=fixture(),c=f.c;Object.assign(c,{busy:false,executionStatus:null,hasActiveHold:()=>false,lastBoard:0,refresh:async()=>{}});
 vm.runInContext(fs.readFileSync(path.join(__dirname,'../companion/wwwroot/boards.js'),'utf8'),c);
 const result={backup:{created:'2026-11-01T00:00:00Z',reason:'Before edit',favourites:2},profiles:[{id:'old',name:'My <board>',pages:2,buttons:4,active:true}],current:[{id:'new',name:'Current',pages:1,buttons:2,active:true}]};
 const changes={profiles:[{id:'old',name:'My <board>',status:'added',pagesAdded:2,pagesRemoved:0,pagesChanged:0,buttonsAdded:4,buttonsRemoved:0,buttonsChanged:0}],favouritesAdded:1,favouritesRemoved:0,settingsChanged:['OBS connection port'],activeProfileChanged:true};
 c.api=async(route,body)=>{f.requests.push({route,body});if(route.endsWith('/preview'))return result;if(route.endsWith('/compare'))return changes;if(route==='/api/profiles')return{profiles:result.current};return{ok:true,id:'recovered'};};
 return{...f,result,changes};
}
async function run(){
 let f=fixture(),c=f.c;c.api=async(route,body)=>{f.requests.push({route,body});return structuredClone(report);};
 await c.showReadiness();check(f.requests.length===1&&f.requests[0].route==='/api/admin/readiness'&&f.requests[0].body===undefined,'readiness makes a read-only request');
 check(c.html.includes('Board &lt;safe&gt;')&&f.$('#readiness-results').innerHTML.includes('Scene &lt;gone&gt;'),'report and target names are escaped');
 check(!f.$('#readiness-results').innerHTML.includes('File exists')&&f.$('#readiness-results').innerHTML.includes('Step 2.1'),'attention view hides passed checks and keeps macro step context');
 f.$('#readiness-all').onclick();check(f.$('#readiness-results').innerHTML.includes('File exists'),'all checks reveals passed targets');
 let edit;c.DeckEditor={openAt:async value=>edit=value};const editButton=f.elements.findLast(node=>node.dataset.readinessEdit==='2');await editButton.onclick();check(edit.pageId==='p'&&edit.buttonId==='three'&&edit.profileId==='profile','edit links include saved profile identity and exact button');
 check(!f.requests.some(r=>r.body!==undefined),'checking and navigating readiness never posts actions');
 f=fixture();c=f.c;let release;c.api=()=>new Promise(resolve=>release=resolve);const pending=c.showReadiness();c.openPanel('Other','Keep my new view');c.featureView='other';release(report);await pending;check(c.html==='Keep my new view','late readiness response cannot replace a newer editor');
 f=fixture();c=f.c;c.native=true;await c.showReadiness();check(!f.requests.length,'tablet cannot invoke desktop readiness API');
 f=fixture();c=f.c;c.dirty=true;await c.showReadiness();check(!f.requests.length,'readiness preserves unsaved edits');
 f=backupFixture();c=f.c;await c.previewBackup('backup');check(c.html.includes('Restore as new profile')&&c.html.includes('What would change')&&c.html.includes('My &lt;board&gt;'),'backup preview offers selective recovery and escaped change summary');
 c.confirmAction=async()=>false;await f.$('#restore-backup-profile').onclick();check(!f.requests.some(r=>r.route.endsWith('/restore-profile')),'declining selective recovery sends no mutation');
 c.confirmAction=async()=>true;f.$('#backup-profile-name').value='Recovered board';await f.$('#restore-backup-profile').onclick();const request=f.requests.find(r=>r.route.endsWith('/restore-profile'));check(request.body.id==='backup'&&request.body.profileId==='old'&&request.body.name==='Recovered board'&&request.body.confirmed,'recovery submits the reviewed backup/profile/name');
 check(!f.requests.some(r=>r.route==='/api/profile'||r.route.endsWith('/backups/restore')),'selective recovery neither switches board nor replaces configuration');
 f=backupFixture();c=f.c;await c.previewBackup('backup');let confirmRelease;c.confirmAction=()=>new Promise(resolve=>confirmRelease=resolve);const restore=f.$('#restore-backup-profile').onclick();await f.$('#restore-backup').onclick();confirmRelease(false);await restore;check(!f.requests.some(r=>/restore/.test(r.route)),'only one restore confirmation can be pending');
 f=backupFixture();c=f.c;const api=c.api;c.api=async(route,body)=>{if(route.endsWith('/compare'))throw Error('Comparison unavailable');return api(route,body);};await c.previewBackup('backup');check(f.$('#restore-backup').disabled&&!f.$('#restore-backup-profile').disabled,'missing comparison blocks full restore but permits separate profile recovery');
 f=backupFixture();c=f.c;f.result.current=Array.from({length:10},(_,i)=>({id:String(i)}));await c.previewBackup('backup');check(f.$('#restore-backup-profile').disabled,'full profile capacity disables new recovery');
 console.log(passed+' readiness and recovery controller checks passed.');
}
if(require.main===module)run().catch(error=>{console.error(error);process.exitCode=1;});
