#!/usr/bin/env node
'use strict';

// Explicit regression manifest. Do not discover *.cjs: sanitize-sinks.cjs edits source.
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const net=require('node:net');
const {spawn,spawnSync}=require('node:child_process');
const {once}=require('node:events');

const root=path.resolve(__dirname,'..');
const suites=[
  ...['self','hardening','boards','studio','monitor','extended-display','live-soundpad','update','reliability','aux-recovery','ink','pen','pen-integration'].map(name=>({name:'companion-'+name,group:'core',flag:'--'+(name==='self'?'self-test':name+'-self-test')})),
  ...['headless','execution','boards','desktop','updates-api','computer-api','reliability-api','ink-api','pen-api'].map(name=>({name,group:'core',script:name+'.cjs'})),
  ...['fast','studio','monitor'].map(name=>({name,group:'android',script:name+'.cjs',java:true})),
  ...['studio-ui','monitor-ui','update-ui','tablet-workflows','workspace-modes','readiness-ui','readiness-api','pen-tablet-ui'].map(name=>({name,group:'core',script:name+'.cjs'})),
  {name:'android-control-policy',group:'android',java:true,javaClass:'ControlRequestPolicyHarness',javaSources:['android/src/app/tabletdeck/ControlRequestPolicy.java','tests/java/ControlRequestPolicyHarness.java']},
  {name:'android-bridge-limits',group:'android',java:true,javaClass:'BridgeRequestLimitsHarness',javaSources:['android/src/app/tabletdeck/BridgeRequestLimits.java','tests/java/BridgeRequestLimitsHarness.java']},
  {name:'android-pen-leases',group:'android',java:true,javaClass:'PenLeaseTrackerHarness',javaSources:['android/src/app/tabletdeck/PenLeaseTracker.java','tests/java/PenLeaseTrackerHarness.java']},
  {name:'copilot-backend',group:'core',project:'tests/copilot-backend/CopilotBackendTests.csproj'},
  {name:'media-sessions',group:'core',project:'tests/media-sessions/MediaSessionTests.csproj'},
  {name:'readiness-backend',group:'core',project:'tests/readiness-backend/ReadinessBackendTests.csproj'},
  {name:'ink-backend',group:'core',project:'tests/ink-backend/InkBackendTests.csproj'},
  {name:'shortcut-migration',group:'core',project:'tests/shortcut-migration/ShortcutMigrationTests.csproj'},
  {name:'integration-api',group:'core',integration:true},
  ...['layout','copilot-ui','soundpad-sync-ui','media-touchbar-ui','live-soundpad-board','live-sync','desktop-rendered','editor-workflow','macro-editor','pen-ui','workspace-rendered','ink-rendered'].map(name=>({name,group:'ui',script:name+'.cjs',browser:true})),
  {name:'features',group:'ui',integration:true,browser:true},
  ...['installer','installer-update'].map(name=>({name,group:'installer',script:name+'.cjs'}))
];

function options(args){
  const result={group:'all',only:null,list:false,noBuild:false,failFast:false};
  for(let i=0;i<args.length;i++){
    if(args[i]==='--list')result.list=true;
    else if(args[i]==='--no-build')result.noBuild=true;
    else if(args[i]==='--fail-fast')result.failFast=true;
    else if(args[i]==='--group'&&args[i+1])result.group=args[++i];
    else if(args[i]==='--suite'&&args[i+1])result.only=args[++i].split(',');
    else if(args[i]==='--help'||args[i]==='-h')result.help=true;
    else throw new Error('Unknown or incomplete option: '+args[i]);
  }
  if(!['all','core','android','ui','installer'].includes(result.group))throw new Error('Choose --group all, core, android, ui, or installer.');
  if(result.only){for(const name of result.only)if(!suites.some(suite=>suite.name===name))throw new Error('Unknown suite '+name+'. Use --list.');}
  return result;
}

function selectedSuites(settings){return suites.filter(suite=>settings.only?settings.only.includes(suite.name):settings.group==='all'?suite.group!=='installer':suite.group===settings.group);}
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function requireFile(file,label){if(!fs.statSync(file,{throwIfNoEntry:false})?.isFile())throw new Error(label+' is missing: '+file);return file;}
function version(command,args,label){const result=spawnSync(command,args,{cwd:root,windowsHide:true,encoding:'utf8',timeout:30000});if(result.error||result.status!==0)throw new Error(label+' is unavailable. '+(result.error?.message||result.stderr||result.stdout));return (result.stdout+result.stderr).trim();}
function javaHome(){
  if(process.env.JAVA_HOME)return process.env.JAVA_HOME;
  if(process.env.JAVA_HOME_17_X64)return process.env.JAVA_HOME_17_X64;
  for(const base of [path.join(root,'toolchain/jdk'),path.join(root,'../toolchain/jdk')]){
    if(!fs.existsSync(base))continue;
    for(const entry of fs.readdirSync(base,{withFileTypes:true}))if(entry.isDirectory()&&fs.existsSync(path.join(base,entry.name,'bin/javac.exe')))return path.join(base,entry.name);
  }
  throw new Error('Set JAVA_HOME to a JDK 17 or newer installation. The Android transport, pairing and chat-policy harnesses are required.');
}
function chromePath(){
  if(process.env.TABLETDECK_CHROME)return requireFile(process.env.TABLETDECK_CHROME,'TABLETDECK_CHROME');
  const choices=[path.join(process.env.ProgramFiles||'C:/Program Files','Google/Chrome/Application/chrome.exe'),path.join(process.env['ProgramFiles(x86)']||'C:/Program Files (x86)','Google/Chrome/Application/chrome.exe'),require('playwright').chromium.executablePath()];
  const found=choices.find(file=>fs.existsSync(file));
  if(!found)throw new Error('Install Chrome or run npm --prefix tests exec -- playwright install chromium; alternatively set TABLETDECK_CHROME.');
  return found;
}

async function freePort(){const listener=net.createServer();listener.listen(0,'127.0.0.1');await once(listener,'listening');const port=listener.address().port;await new Promise(resolve=>listener.close(resolve));return port;}

async function main(){
  const settings=options(process.argv.slice(2)),selected=selectedSuites(settings);
  if(settings.help){console.log('Usage: npm --prefix tests test -- [--group all|core|android|ui|installer] [--suite name,name] [--no-build] [--fail-fast] [--list]\nDefault: build an isolated Windows companion and run every core, Android harness, and UI suite, collecting all failures.\nInstaller tests require explicit --group installer and TABLETDECK_SETUP. Audio playback, real keyboard, driver and physical tablet rehearsals are separate opt-in checks.');return;}
  if(settings.list){for(const suite of selected)console.log(suite.group.padEnd(10)+suite.name);return;}
  if(process.platform!=='win32')throw new Error('The complete companion regression requires Windows x64. Use the Windows CI job or a Windows development machine.');
  if(Number(process.versions.node.split('.')[0])<22)throw new Error('Node.js 22 or newer is required.');
  const sdk=version('dotnet',['--version'],'.NET SDK');if(Number(sdk.split('.')[0])<10)throw new Error('.NET SDK 10 or newer is required; selected '+sdk+'.');
  for(const packageName of ['acorn','playwright','ws','dompurify']){try{require.resolve(packageName);}catch{throw new Error('Missing test dependency '+packageName+'. Run npm ci --prefix tests.');}}
  for(const suite of selected){if(suite.script)requireFile(path.join(__dirname,suite.script),'Required suite');if(suite.project)requireFile(path.join(root,suite.project),'Required backend fixture');for(const source of suite.javaSources||[])requireFile(path.join(root,source),'Required Java fixture source');}
  const env={...process.env,TABLETDECK_HEADLESS:'0',TABLETDECK_CAPTURE_ONLY:''};
  if(selected.some(suite=>suite.java)){
    env.JAVA_HOME=javaHome();const javac=requireFile(path.join(env.JAVA_HOME,'bin/javac.exe'),'JDK compiler');requireFile(path.join(env.JAVA_HOME,'bin/java.exe'),'Java runtime');
    const javacVersion=version(javac,['-version'],'JDK compiler');if(Number(javacVersion.match(/javac\s+(\d+)/)?.[1]||0)<17)throw new Error('JDK 17 or newer is required; found '+javacVersion+'.');
    requireFile(path.join(root,'android/vendor/zxing-core-3.5.3.jar'),'Vendored QR harness dependency');
  }
  if(selected.some(suite=>suite.browser))env.TABLETDECK_CHROME=chromePath();
  if(selected.some(suite=>suite.group==='installer'))requireFile(env.TABLETDECK_SETUP||'','TABLETDECK_SETUP (required only for the explicit installer group)');
  const sourceVersion=fs.readFileSync(path.join(root,'companion/TabletDeck.csproj'),'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1];env.TABLETDECK_VERSION=env.TABLETDECK_VERSION||sourceVersion;

  const outputRoot=path.join(root,'.test-output');fs.mkdirSync(outputRoot,{recursive:true});
  const output=fs.mkdtempSync(path.join(outputRoot,'verification-')),logs=path.join(output,'logs');fs.mkdirSync(logs);
  // Runtime secrets use the local profile's temp volume: network/exFAT worktrees
  // cannot always apply the companion's mandatory private-directory ACL.
  env.TABLETDECK_TEST_OUTPUT=fs.mkdtempSync(path.join(os.tmpdir(),'deskwing-verification-'));
  let companion;
  if(settings.noBuild){
    companion=requireFile(env.TABLETDECK_COMPANION||'','TABLETDECK_COMPANION (--no-build)');
    if(!path.resolve(companion).includes(path.sep+'.test-output'+path.sep))throw new Error('--no-build requires an isolated executable beneath a .test-output directory, not the installed/running companion.');
  }else companion=path.join(output,'Windows','TabletDeck-Companion.exe');
  env.TABLETDECK_COMPANION=companion;
  const report={startedAt:new Date().toISOString(),group:settings.group,selected:selected.map(suite=>suite.name),sdk,node:process.versions.node,sourceVersion,build:settings.noBuild?'explicit existing isolated executable':'fresh isolated publish',hardwareTested:false,installerIncluded:selected.some(suite=>suite.group==='installer'),steps:[],complete:false};
  const summary=path.join(output,'summary.json');
  const save=()=>fs.writeFileSync(summary,JSON.stringify(report,null,2));save();
  let activeChild=null,interrupted=false;
  function terminate(child){
    if(!child||child.exitCode!==null||!child.pid)return;
    // The PID belongs to a process this runner spawned; terminate only its tree.
    const taskkill=path.join(process.env.SystemRoot||'C:/Windows','System32/taskkill.exe');
    spawnSync(taskkill,['/PID',String(child.pid),'/T','/F'],{windowsHide:true,stdio:'ignore',timeout:10000});
  }
  const interrupt=()=>{interrupted=true;terminate(activeChild);};process.once('SIGINT',interrupt);process.once('SIGTERM',interrupt);
  async function run(name,command,args,{extraEnv={},timeout=180000,cwd=root}={}){
    if(interrupted)throw new Error('Verification interrupted.');
    const step={name,status:'running',startedAt:new Date().toISOString()},started=Date.now();report.steps.push(step);save();
    console.log('\n['+report.steps.length+'] '+name);
    const log=fs.openSync(path.join(logs,name.replace(/[^a-z0-9-]/gi,'-')+'.log'),'w');
    let child,timedOut=false;
    try{
      child=spawn(command,args,{cwd,windowsHide:true,env:{...env,...extraEnv},stdio:['ignore','pipe','pipe']});activeChild=child;
      for(const [stream,destination] of [[child.stdout,process.stdout],[child.stderr,process.stderr]])stream.on('data',data=>{fs.writeSync(log,data);destination.write(data);});
      const timer=setTimeout(()=>{timedOut=true;terminate(child);},timeout);
      let result;try{result=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('close',(code,signal)=>resolve({code,signal}));});}finally{clearTimeout(timer);}
      if(timedOut)throw new Error(name+' exceeded '+Math.round(timeout/1000)+' seconds. Its owned process tree was stopped.');
      if(result.code!==0)throw new Error(name+' failed with '+(result.signal?'signal '+result.signal:'exit code '+result.code)+'. See '+path.join(logs,name+'.log'));
      step.status='passed';
    }catch(error){step.status='failed';step.error=error.message;throw error;}
    finally{step.elapsedMs=Date.now()-started;activeChild=null;fs.closeSync(log);save();}
  }
  async function integrationFixture(suite){
    const fixture=fs.mkdtempSync(path.join(env.TABLETDECK_TEST_OUTPUT,suite.name+'-')),data=path.join(fixture,'data');fs.mkdirSync(data);
    // Original mock OBS uses 19455; only its companion ports need allocation here.
    const ports=[];while(ports.length<3){const candidate=await freePort();if(!ports.includes(candidate)&&candidate!==19455)ports.push(candidate);}
    const log=fs.openSync(path.join(logs,suite.name+'-server.log'),'w');
    const server=spawn(companion,['--no-browser','--no-tray','--data-dir',data,'--http-port',String(ports[0]),'--https-port',String(ports[1]),'--control-port',String(ports[2])],{cwd:root,windowsHide:true,env,stdio:['ignore',log,log]});
    let session,startError;server.on('error',error=>{startError=error;});
    try{
      for(let attempt=0;attempt<150;attempt++){
        if(startError)throw startError;if(server.exitCode!==null)throw new Error('Isolated integration companion exited before readiness.');
        try{const candidate=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));const response=await fetch('http://127.0.0.1:'+ports[0]+'/api/board',{headers:{'X-Deck-Admin':candidate.token},signal:AbortSignal.timeout(1000)});if(response.ok){session=candidate;break;}}catch{}
        await delay(100);
      }
      if(!session)throw new Error('Isolated integration companion did not become ready. See '+path.join(logs,suite.name+'-server.log'));
      await run(suite.name,process.execPath,[path.join(__dirname,suite.name==='integration-api'?'integration.cjs':'features.cjs')],{extraEnv:{TABLETDECK_TEST_OUTPUT:fixture,TABLETDECK_HEADLESS:suite.name==='integration-api'?'1':'0'}});
    }finally{
      if(server.exitCode===null){
        try{if(session)await fetch('http://127.0.0.1:'+ports[0]+'/api/admin/shutdown',{method:'POST',headers:{'X-Deck-Admin':session.token,'Content-Type':'application/json'},body:'{}',signal:AbortSignal.timeout(2000)});}catch{}
        for(let attempt=0;attempt<30&&server.exitCode===null;attempt++)await delay(100);
        if(server.exitCode===null)terminate(server);
      }
      fs.closeSync(log);
    }
  }
  try{
    console.log('Verification output: '+output);console.log('Private fixture directory: '+env.TABLETDECK_TEST_OUTPUT);
    if(!settings.noBuild)await run('publish','dotnet',['publish',path.join(root,'companion/TabletDeck.csproj'),'-c','Release','-r','win-x64','--self-contained','true','--artifacts-path',path.join(output,'build'),'-o',path.dirname(companion)],{timeout:600000});
    requireFile(companion,'Published companion');
    const failures=[];
    for(const suite of selected){try{
      if(suite.flag)await run(suite.name,companion,[suite.flag,'--no-browser','--no-tray']);
      else if(suite.integration)await integrationFixture(suite);
      else if(suite.javaClass){
        const classes=path.join(env.TABLETDECK_TEST_OUTPUT,'java',suite.name);fs.mkdirSync(classes,{recursive:true});
        await run(suite.name+'-compile',path.join(env.JAVA_HOME,'bin/javac.exe'),['--release','8','-d',classes,...suite.javaSources.map(source=>path.join(root,source))]);
        await run(suite.name,path.join(env.JAVA_HOME,'bin/java.exe'),['-cp',classes,suite.javaClass]);
      }
      else if(suite.project)await run(suite.name,'dotnet',['run','--project',path.join(root,suite.project),'-c','Release','--artifacts-path',path.join(output,'backend-build')],{timeout:300000});
      else await run(suite.name,process.execPath,[path.join(__dirname,suite.script)]);
    }catch(error){failures.push({suite:suite.name,error:error.message});if(settings.failFast||interrupted)throw error;console.error('Continuing after '+suite.name+' failed: '+error.message);}}
    report.failures=failures;
    if(failures.length)throw new Error(failures.length+' of '+selected.length+' suites failed: '+failures.map(failure=>failure.suite).join(', '));
    report.complete=true;console.log('\nAll '+selected.length+' selected regression suites passed.');
  }catch(error){report.error=error.message;throw error;}finally{
    process.removeListener('SIGINT',interrupt);process.removeListener('SIGTERM',interrupt);
    report.finishedAt=new Date().toISOString();report.notRun=selected.filter(suite=>!report.steps.some(step=>step.name===suite.name)).map(suite=>suite.name);save();
    console.log('Result summary: '+summary);
  }
}

main().catch(error=>{console.error('\nVerification failed: '+error.message);process.exitCode=1;});
