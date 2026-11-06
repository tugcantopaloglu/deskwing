// Launch only an isolated companion and the existing mock-OBS API suite. No browser or tray.
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),output=fs.mkdtempSync(path.join(os.tmpdir(),'tabletdeck-integration-')),data=path.join(output,'data');fs.mkdirSync(data);
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'companion/bin/Release/net10.0-windows/win-x64/DeskWing-Companion.exe');
const log=fs.openSync(path.join(output,'server.log'),'w');let server,session;
(async()=>{
 server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port','18740','--https-port','18743'],{windowsHide:true,stdio:['ignore',log,log]});
 server.on('error',error=>{console.error(error.message);});
 for(let i=0;i<80;i++){
  try{const current=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));if((await fetch('http://127.0.0.1:18740/api/board',{headers:{'X-Deck-Admin':current.token}})).ok){session=current;break;}}catch{}
  await new Promise(resolve=>setTimeout(resolve,100));
 }
 if(!session)throw Error('Isolated integration companion failed to start. Inspect '+path.join(output,'server.log'));
 const result=await new Promise((resolve,reject)=>{const test=spawn(process.execPath,[path.join(__dirname,'integration.cjs')],{windowsHide:true,env:{...process.env,TABLETDECK_TEST_OUTPUT:output,TABLETDECK_HEADLESS:'1'},stdio:'inherit'});test.once('error',reject);test.once('exit',resolve);});
 if(result!==0)throw Error('Headless integration failed');
 console.log('No browser or app window was opened. Fixture output: '+output);
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{
 if(server&&server.exitCode===null){const exit=new Promise(resolve=>server.once('exit',resolve));try{if(session)await fetch('http://127.0.0.1:18740/api/admin/shutdown',{method:'POST',headers:{'X-Deck-Admin':session.token,'Content-Type':'application/json'},body:'{}'});else server.kill();}catch{server.kill();}await exit;}
 fs.closeSync(log);
});
