'use strict';
// Independent notebook backups. The original scratchpad's storage is never migrated or removed.
(function () {
  const DATABASE='DeskWingNotebooks', STORE='backups', QUOTA_KEY='__deskwing_notebook_quota_v1__';
  const SELECTION='deskwing.notebooks.selection.v1', MAX_ENTRY_BYTES=10*1024*1024, MAX_TOTAL_BYTES=128*1024*1024, MAX_CATALOG_BYTES=256*1024;
  const CATALOG_KEY=/^deskwing\.notebooks\.catalog\.v1:[a-f0-9]{32}$/i;
  const PAGE_KEY=/^deskwing\.notebooks\.page\.v1:[a-f0-9]{32}:[a-f0-9]{32}:[a-f0-9]{32}$/i;
  const KEY=/^deskwing\.notebooks\.page\.v1:[a-f0-9]{32}:[a-f0-9]{32}:[a-f0-9]{32}(?::(?:sync|recovery|background))?$/i;
  const forbidden=new Set(['__proto__','constructor','prototype']);
  let mode='initializing', database=null, opening=null;
  function validKey(key){if(typeof key!=='string'||key!==SELECTION&&!KEY.test(key)&&!CATALOG_KEY.test(key))throw Error('Use a notebook backup key. Original scratchpad keys are not admitted.');return key;}
  function bytes(text){return new TextEncoder().encode(text).length;}
  function serialize(value,key){
    let nodes=0;const seen=new Set();
    function visit(item,depth){
      if(++nodes>600000||depth>20)throw Error('The notebook backup is too complex.');
      if(item===null||typeof item==='string'||typeof item==='boolean')return;
      if(typeof item==='number'){if(!Number.isFinite(item))throw Error('Notebook backups require finite numbers.');return;}
      if(typeof item!=='object'||seen.has(item))throw Error('Notebook backups require ordinary JSON data.');
      const prototype=Object.getPrototypeOf(item);
      if(Array.isArray(item)?prototype!==Array.prototype:prototype!==Object.prototype&&prototype!==null)throw Error('Notebook backups require plain objects and arrays.');
      seen.add(item);
      if(Array.isArray(item)){
        if(item.length>150000)throw Error('The notebook backup has too many entries.');
        for(const property of Reflect.ownKeys(item))if(property!=='length'&&(typeof property!=='string'||!/^(?:0|[1-9][0-9]*)$/.test(property)||Number(property)>=item.length))throw Error('Notebook arrays cannot contain extra fields.');
        for(let index=0;index<item.length;index++){const descriptor=Object.getOwnPropertyDescriptor(item,String(index));if(!descriptor||!Object.hasOwn(descriptor,'value'))throw Error('Sparse arrays and array accessors are unsupported.');visit(descriptor.value,depth+1);}
      }
      else{for(const property of Reflect.ownKeys(item)){if(typeof property!=='string'||forbidden.has(property))throw Error('The notebook backup contains an unsafe key.');const descriptor=Object.getOwnPropertyDescriptor(item,property);if(!descriptor||!Object.hasOwn(descriptor,'value')||!descriptor.enumerable)throw Error('Notebook backups cannot contain accessors or hidden fields.');visit(descriptor.value,depth+1);}}
      seen.delete(item);
    }
    visit(value,0);const text=JSON.stringify(value),size=bytes(text);
    if(size>(key===SELECTION?4096:CATALOG_KEY.test(key)?MAX_CATALOG_BYTES:MAX_ENTRY_BYTES))throw Error('The notebook backup exceeds its local storage limit.');
    return {version:1,json:text,bytes:size};
  }
  function decode(record,key){
    if(!record||record.version!==1||typeof record.json!=='string'||!Number.isSafeInteger(record.bytes)||record.bytes<0||record.bytes>MAX_ENTRY_BYTES||bytes(record.json)!==record.bytes)throw Error('The saved notebook backup could not be read. It was kept unchanged.');
    let value;try{value=JSON.parse(record.json);}catch{throw Error('The saved notebook backup is not valid JSON. It was kept unchanged.');}
    const checked=serialize(value,key);if(checked.bytes!==record.bytes)throw Error('The notebook backup size is invalid.');return value;
  }
  function limitedRead(key){
    const raw=localStorage.getItem(key);if(raw===null)return null;
    let value;try{value=JSON.parse(raw);}catch{throw Error('The local notebook backup is damaged. It was kept unchanged.');}
    serialize(value,key);return value;
  }
  function branchConflict(){const error=Error('Two different local notebook backups exist. Both are kept unchanged. Export and review both copies before editing this page.');error.code='notebook_storage_branch';return error;}
  function requireUnbranchedWrite(key){if(localStorage.getItem(key)!==null){limitedRead(key);const error=Error('An older localStorage notebook copy is preserved for read/export only. Review and export it before editing this page. No stored copy was replaced.');error.code='notebook_storage_readonly';throw error;}}
  function sameValue(a,b,key){return serialize(a,key).json===serialize(b,key).json;}
  function resolveRecord(record,key){
    const current=record===undefined||record===null?null:decode(record,key),limited=limitedRead(key);
    if(current!==null&&limited!==null&&!sameValue(current,limited,key))throw branchConflict();
    return current!==null?current:limited;
  }
  function requireWritable(){if(mode!=='indexeddb'||!database)throw Error('Notebook backups are read-only because durable local storage is unavailable. You can read and export existing copies. Reopen DeskWing to retry storage.');}
  function useFallback(){mode='limited-local-storage';return mode;}
  function ready(){
    if(opening)return opening;
    opening=new Promise(resolve=>{
      if(!window.indexedDB){resolve(useFallback());return;}
      let done=false, request;
      const finish=value=>{if(done)return;done=true;clearTimeout(timer);resolve(value);};
      const timer=setTimeout(()=>finish(useFallback()),8000);
      try{request=window.indexedDB.open(DATABASE,1);}catch{finish(useFallback());return;}
      request.onupgradeneeded=()=>{
        try{const db=request.result;if(!db.objectStoreNames.contains(STORE)){const store=db.createObjectStore(STORE);store.put({version:1,totalBytes:0},QUOTA_KEY);}}
        catch{try{request.transaction.abort();}catch{}}
      };
      request.onerror=()=>finish(useFallback());
      request.onblocked=()=>finish(useFallback());
      request.onsuccess=()=>{
        const db=request.result;if(done){db.close();return;}
        if(!db.objectStoreNames.contains(STORE)){db.close();finish(useFallback());return;}
        database=db;mode='indexeddb';db.onversionchange=()=>{db.close();database=null;mode='closed';};finish(mode);
      };
    });
    return opening;
  }
  function transaction(kind,operation){
    return new Promise((resolve,reject)=>{
      if(!database){reject(Error('Notebook storage was closed. Reopen DeskWing before saving.'));return;}
      let tx,settled=false,result=null,error=null;
      const finish=(ok,value)=>{if(settled)return;settled=true;clearTimeout(timer);ok?resolve(value):reject(value);};
      const timer=setTimeout(()=>{error=Error('Notebook storage did not finish in time. Its previous backup is kept.');try{tx?.abort();}catch{}finish(false,error);},10000);
      try{
        tx=database.transaction(STORE,kind);
        tx.oncomplete=()=>finish(true,result);
        tx.onabort=()=>finish(false,error||Error('The notebook backup was not saved. The previous backup is kept.'));
        tx.onerror=()=>{error=error||Error('The notebook storage request failed. The previous backup is kept.');};
        operation(tx.objectStore(STORE),value=>{result=value;},failure=>{error=failure;try{tx.abort();}catch{finish(false,error);}});
      }catch(failure){finish(false,Error('Notebook storage is unavailable. Keep this page open and export it before leaving.'));}
    });
  }
  async function get(key){
    validKey(key);await ready();
    if(mode==='limited-local-storage')return limitedRead(key);
    const record=await transaction('readonly',(store,result,fail)=>{const request=store.get(key);request.onsuccess=()=>result(request.result??null);request.onerror=()=>fail(Error('The notebook backup could not be loaded.'));});
    return resolveRecord(record,key);
  }
  async function readBranches(key){
    validKey(key);await ready();
    let limited=null,indexed=null,hasLimited=false,hasIndexed=false,limitedError=null,indexedError=null;
    try{
      const raw=localStorage.getItem(key);hasLimited=raw!==null;
      if(hasLimited){limited=JSON.parse(raw);serialize(limited,key);}
    }catch{limited=null;limitedError='The localStorage copy could not be read. Its original data was kept unchanged.';}
    if(mode==='limited-local-storage')indexedError='Durable notebook storage is unavailable. Reopen DeskWing to inspect that copy.';
    else{
      try{
        const record=await transaction('readonly',(store,result,fail)=>{const request=store.get(key);request.onsuccess=()=>result(request.result??null);request.onerror=()=>fail(Error('The notebook backup branches could not be loaded.'));});
        hasIndexed=record!==null;if(hasIndexed)indexed=decode(record,key);
      }catch{indexed=null;indexedError='The IndexedDB copy could not be read. Its original data was kept unchanged.';}
    }
    const diverged=hasIndexed&&hasLimited&&(!!indexedError||!!limitedError||!sameValue(indexed,limited,key));
    return {indexeddb:indexed,limitedLocalStorage:limited,hasIndexedDb:hasIndexed,hasLimitedLocalStorage:hasLimited,diverged,indexeddbError:indexedError,limitedLocalStorageError:limitedError};
  }
  function emptyPage(){return {localRevision:0,document:null,sync:null,recovery:null,background:null};}
  function pageBundle(value){
    if(value===null)return emptyPage();
    if(value&&value.version===1&&Array.isArray(value.strokes)&&typeof value.notes==='string')return {...emptyPage(),document:value};
    if(!value||value.bundleVersion!==1||!Number.isSafeInteger(value.localRevision)||value.localRevision<1)throw Error('The saved notebook page backup is invalid. It was kept unchanged.');
    const keys=Object.keys(value);if(keys.some(key=>!['bundleVersion','localRevision','document','sync','recovery','background'].includes(key))||keys.length!==6)throw Error('The saved notebook page bundle is invalid.');
    for(const key of ['document','sync','recovery','background'])if(value[key]!==null&&(typeof value[key]!=='object'||Array.isArray(value[key])))throw Error('The saved notebook page bundle has invalid data.');
    return {localRevision:value.localRevision,document:value.document,sync:value.sync,recovery:value.recovery,background:value.background};
  }
  function pageKey(key){if(typeof key!=='string'||!PAGE_KEY.test(key))throw Error('Choose an exact notebook page backup key.');return key;}
  function nextPage(current,patch,expected){
    const previous=pageBundle(current);
    if(previous.localRevision!==expected)throw Error('This notebook page changed in another window. Reload its local copy before saving.');
    if(previous.localRevision>=Number.MAX_SAFE_INTEGER)throw Error('The local page version limit was reached. Export this page before continuing.');
    return {bundleVersion:1,localRevision:previous.localRevision+1,document:Object.hasOwn(patch,'document')?patch.document:previous.document,sync:Object.hasOwn(patch,'sync')?patch.sync:previous.sync,recovery:Object.hasOwn(patch,'recovery')?patch.recovery:previous.recovery,background:Object.hasOwn(patch,'background')?patch.background:previous.background};
  }
  async function getPage(key){pageKey(key);const value=pageBundle(await get(key)),readOnly=mode!=='indexeddb'||localStorage.getItem(key)!==null;return {...value,readOnly,storageWarning:readOnly?'This saved notebook copy is available for read/export only. Reopen durable storage or review the preserved copy before editing.':''};}
  async function putPage(key,patch,expectedLocalRevision){
    pageKey(key);
    if(!Number.isSafeInteger(expectedLocalRevision)||expectedLocalRevision<0)throw Error('Load the current local page version before saving.');
    if(!patch||typeof patch!=='object'||Array.isArray(patch)||Object.keys(patch).some(name=>!['document','sync','recovery','background'].includes(name)))throw Error('Use notebook page backup fields only.');
    const frozen=JSON.parse(serialize(patch,key).json);
    for(const name of Object.keys(frozen))if(frozen[name]!==null&&(typeof frozen[name]!=='object'||Array.isArray(frozen[name])))throw Error('Notebook page backup fields must be objects or null.');
    await ready();requireWritable();
    return transaction('readwrite',(store,result,fail)=>{
      let old,quota,received=0;
      const commit=()=>{
        if(++received!==2)return;
        try{
          if(!quota||quota.version!==1||!Number.isSafeInteger(quota.totalBytes)||quota.totalBytes<0||quota.totalBytes>MAX_TOTAL_BYTES)throw Error('The notebook quota record is invalid. Existing backups are kept.');
          const current=resolveRecord(old,key);requireUnbranchedWrite(key);const value=nextPage(current,frozen,expectedLocalRevision),encoded=serialize(value,key),total=quota.totalBytes-(old?.bytes||0)+encoded.bytes;
          if(total<0||total>MAX_TOTAL_BYTES)throw Error('Local notebook backups reached 128 MiB. Existing backups are kept.');
          store.put(encoded,key);store.put({version:1,totalBytes:total},QUOTA_KEY);result(pageBundle(value));
        }catch(failure){fail(failure);}
      };
      const current=store.get(key),metadata=store.get(QUOTA_KEY);current.onsuccess=()=>{old=current.result;commit();};metadata.onsuccess=()=>{quota=metadata.result;commit();};current.onerror=metadata.onerror=()=>fail(Error('The notebook page backup could not be checked.'));
    });
  }
  async function set(key,value){
    validKey(key);if(PAGE_KEY.test(key))throw Error('Save notebook page bundles with putPage and their current local revision.');const encoded=serialize(value,key);await ready();requireWritable();
    return transaction('readwrite',(store,result,fail)=>{
      let old,quota,received=0;
      function commit(){
        if(++received!==2)return;
        try{
          if(!quota||quota.version!==1||!Number.isSafeInteger(quota.totalBytes)||quota.totalBytes<0||quota.totalBytes>MAX_TOTAL_BYTES)throw Error('The notebook quota record is invalid. Existing backups are kept.');
          resolveRecord(old,key);requireUnbranchedWrite(key);
          const oldBytes=old?.bytes||0,total=quota.totalBytes-oldBytes+encoded.bytes;
          if(total<0||total>MAX_TOTAL_BYTES)throw Error('Local notebook backups reached 128 MiB. Existing backups are kept.');
          store.put(encoded,key);store.put({version:1,totalBytes:total},QUOTA_KEY);result(undefined);
        }catch(failure){fail(failure);}
      }
      const current=store.get(key),metadata=store.get(QUOTA_KEY);
      current.onsuccess=()=>{old=current.result;commit();};metadata.onsuccess=()=>{quota=metadata.result;commit();};
      current.onerror=metadata.onerror=()=>fail(Error('The notebook backup could not be checked before saving.'));
    });
  }
  window.DeskNotebookStore=Object.freeze({get,set,getPage,putPage,readBranches,ready,get mode(){return mode;},get atomicPageWrites(){return mode==='indexeddb'&&database!==null;},limits:Object.freeze({entryBytes:MAX_ENTRY_BYTES,totalBytes:MAX_TOTAL_BYTES,selectionBytes:4096,catalogBytes:MAX_CATALOG_BYTES}),selectionKey:SELECTION});
})();
