'use strict';
let soundpadBoardFilter={pageId:'',query:'',category:'',view:'all'},soundpadBoardCategories='';
const soundpadBoardSaved={favourites:{items:[],loaded:false,pending:false,error:'',updated:0},recent:{items:[],loaded:false,pending:false,error:'',updated:0}};
function soundpadBoardKey(action){
  const key=action.soundpadKey||String(action.id||'').split(':').at(-2)||'';
  return /^[a-f0-9]{64}$/i.test(key)?key.toUpperCase():'';
}
function soundpadBoardSelection(page,view=soundpadBoardFilter.view){
  if(view==='all')return {buttons:page.buttons,missing:0};
  const saved=soundpadBoardSaved[view];if(!saved?.loaded)return {buttons:[],missing:0};
  const identities=new Map();for(const action of page.buttons){const key=soundpadBoardKey(action);if(key)identities.set(key,[...(identities.get(key)||[]),action]);}
  const buttons=[],seen=new Set();let missing=0;
  for(const item of saved.items){const key=String(item.key||'').toUpperCase();if(seen.has(key))continue;seen.add(key);const found=identities.get(key)||[];if(found.length===1&&found[0].configured!==false)buttons.push(found[0]);else missing++;}
  return {buttons,missing};
}
function soundpadBoardMatches(page){
  const selected=soundpadBoardSelection(page),query=soundpadBoardFilter.query.trim().toLocaleLowerCase(),category=soundpadBoardFilter.category;
  return {...selected,buttons:selected.buttons.filter(a=>(!category||(a.categories||[]).includes(category))&&(!query||[a.label,a.searchText,...(a.categories||[])].join(' ').toLocaleLowerCase().includes(query)))};
}
function soundpadBoardActive(){
  return !document.hidden&&!(typeof panel!=='undefined'&&panel.open)&&!(typeof computerMode!=='undefined'&&computerMode!=='studio')&&pages.find(page=>page.id===pageId)?.liveSoundpad===true;
}
async function refreshSoundpadBoardView(force=false){
  const view=soundpadBoardFilter.view,saved=soundpadBoardSaved[view];
  if(!saved||saved.pending||!soundpadBoardActive()||typeof preview!=='undefined'&&preview||!force&&Date.now()-saved.updated<5000)return;
  saved.pending=true;saved.error='';renderBoard();
  try{
    const result=await api(view==='favourites'?'/api/soundpad/favourites':'/api/soundpad/recent',undefined,10000),items=view==='favourites'?result.favourites:result.sounds;
    if(!Array.isArray(items))throw new Error('The saved sound list could not be read.');
    saved.items=items.filter(item=>item&&/^[a-f0-9]{64}$/i.test(item.key)).slice(0,view==='favourites'?200:30);saved.loaded=true;saved.error=result.warning||'';
  }catch(error){saved.error='Could not refresh '+(view==='recent'?'recent sounds':'favourites')+'. '+error.message;}
  finally{saved.pending=false;saved.updated=Date.now();if(soundpadBoardActive()&&soundpadBoardFilter.view===view)renderBoard();}
}
function chooseSoundpadBoardView(view){
  if(!['all','favourites','recent'].includes(view))return;
  soundpadBoardFilter.view=view;slice=0;renderBoard();
  if(view!=='all')refreshSoundpadBoardView(true);
}
function soundpadBoardEmpty(page){
  if(!page.buttons.length)return {title:'No live Soundpad sounds available',message:'Keep Soundpad running on the PC. This page follows its entire loaded library automatically.'};
  const view=soundpadBoardFilter.view,saved=soundpadBoardSaved[view];
  if(saved&&!saved.loaded)return {title:saved.error?'Saved sounds unavailable':'Loading '+(view==='recent'?'recent sounds':'favourites')+'…',message:saved.error||'Reading your saved list from the PC.'};
  if(saved&&!saved.items.length)return view==='recent'?{title:'No recent sounds yet',message:'Play a linked sound from your board or library. Your last 30 sounds appear here.'}:{title:'No favourites yet',message:'Star sounds in the Soundpad library, then return to this pad.'};
  const selection=soundpadBoardSelection(page);
  if(saved&&!selection.buttons.length&&selection.missing)return {title:'Saved sounds are unavailable',message:'These files are missing or have duplicate identities in Soundpad. Choose All or review the library on the PC.'};
  return {title:'No matching sounds',message:'Clear the search and category, or choose All to see the complete library.'};
}
function soundpadBoardButtons(page){
  let toolbar=document.getElementById('soundpad-board-toolbar');
  if(!page.liveSoundpad){if(toolbar)toolbar.hidden=true;return page.buttons;}
  if(!toolbar){
    toolbar=document.createElement('div');toolbar.id='soundpad-board-toolbar';toolbar.className='soundpad-board-toolbar';
    const views=document.createElement('div');views.className='soundpad-board-views';views.setAttribute('role','group');views.setAttribute('aria-label','Soundpad views');
    for(const [view,label] of [['all','All'],['favourites','Favourites'],['recent','Recent']]){const button=document.createElement('button');button.type='button';button.id='soundpad-board-'+view;button.className='quiet';button.textContent=label;button.onclick=()=>chooseSoundpadBoardView(view);views.append(button);}
    const search=document.createElement('input');search.id='soundpad-board-search';search.type='search';search.placeholder='Search all Soundpad sounds';search.setAttribute('aria-label','Search all Soundpad sounds');search.autocomplete='off';
    const category=document.createElement('select');category.id='soundpad-board-category';category.setAttribute('aria-label','Soundpad category');
    const clear=document.createElement('button');clear.id='soundpad-board-clear';clear.className='quiet';clear.textContent='Clear';clear.onclick=()=>{soundpadBoardFilter.query='';soundpadBoardFilter.category='';search.value='';category.value='';slice=0;renderBoard();search.focus({preventScroll:true});};
    const count=document.createElement('span');count.id='soundpad-board-count';count.className='help';count.setAttribute('role','status');
    toolbar.append(views,search,category,clear,count);document.getElementById('keys').before(toolbar);
    search.oninput=()=>{soundpadBoardFilter.query=search.value;slice=0;renderBoard();};category.onchange=()=>{soundpadBoardFilter.category=category.value;slice=0;renderBoard();};
  }
  toolbar.hidden=false;
  const search=document.getElementById('soundpad-board-search'),category=document.getElementById('soundpad-board-category');
  if(soundpadBoardFilter.pageId!==page.id){soundpadBoardFilter={pageId:page.id,query:'',category:'',view:'all'};search.value='';soundpadBoardCategories='';}
  for(const view of ['all','favourites','recent'])document.getElementById('soundpad-board-'+view).setAttribute('aria-pressed',String(soundpadBoardFilter.view===view));
  const categories=[...new Set(page.buttons.flatMap(a=>a.categories||[]))].sort((a,b)=>a.localeCompare(b)),signature=JSON.stringify(categories);
  if(signature!==soundpadBoardCategories){soundpadBoardCategories=signature;category.replaceChildren(new Option('All categories',''),...categories.map(name=>new Option(name,name)));}
  if(!categories.includes(soundpadBoardFilter.category))soundpadBoardFilter.category='';category.value=soundpadBoardFilter.category;
  const query=soundpadBoardFilter.query.trim().toLocaleLowerCase();
  const selected=soundpadBoardMatches(page),matches=selected.buttons,saved=soundpadBoardSaved[soundpadBoardFilter.view],viewName=soundpadBoardFilter.view==='all'?'':soundpadBoardFilter.view==='recent'?'recent ':'favourite ';
  const status=document.getElementById('soundpad-board-count');
  status.textContent=page.soundpadWarning&&!page.buttons.length?'Soundpad unavailable · Open Soundpad on the PC.':saved&&!saved.loaded?(saved.pending?'Loading '+viewName+'sounds…':saved.error||'Choose this view to load your saved sounds.'):`${matches.length} ${query||soundpadBoardFilter.category?'matching · ':''}${viewName}sounds${soundpadBoardFilter.view==='all'&&(query||soundpadBoardFilter.category)?' · '+page.buttons.length+' total sounds':''} · Live sync${selected.missing?' · '+selected.missing+' unavailable':''}${saved?.error?' · '+saved.error:''}`;
  status.setAttribute('aria-busy',String(saved?.pending===true));
  return matches;
}
function soundpadBoardPagination(page,total){
  if(!page.liveSoundpad)return;
  const select=document.createElement('select');select.id='soundpad-board-page';select.setAttribute('aria-label','Jump to sound page');
  for(let index=0;index<total;index++)select.add(new Option(`Page ${index+1} of ${total}`,String(index)));
  select.value=String(slice);select.onchange=()=>{slice=Number(select.value);renderBoard();document.getElementById('soundpad-board-page')?.focus({preventScroll:true});};
  document.querySelector('#pagination>span')?.replaceWith(select);
}
function rememberSoundpadBoardFocus(page){
  const focused=document.activeElement;
  return page.liveSoundpad&&focused?.classList.contains('key')?focused.dataset.id?.split(':').at(-2):null;
}
function restoreSoundpadBoardFocus(key){
  if(!key)return;
  const linked=[...document.querySelectorAll('#keys>.key')].find(node=>node.dataset.id?.split(':').at(-2)===key);
  (linked||document.getElementById('soundpad-board-search'))?.focus({preventScroll:true});
}
setInterval(()=>{if(soundpadBoardActive())refreshSoundpadBoardView();},5000);
