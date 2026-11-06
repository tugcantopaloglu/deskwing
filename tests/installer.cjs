const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const {spawnSync}=require('node:child_process');
const setup=process.env.TABLETDECK_SETUP;assert.ok(setup,'Set TABLETDECK_SETUP to the published setup executable.');
const parent=path.join(os.tmpdir(),'TabletDeckStudioSetupTests'),install=path.join(parent,'TabletDeckStudio');
assert.ok(!fs.existsSync(parent),'Refusing to reuse an existing installer test directory.');
const realInstalls=['DeskWing','TabletDeckStudio'].map(name=>path.join(process.env.LOCALAPPDATA,'Programs',name));
const signature=()=>JSON.stringify(realInstalls.map(file=>fs.existsSync(file)?fs.statSync(file).mtimeMs:null));const before=signature();
let passed=0;function check(value,label){assert.ok(value,label);passed++;console.log('PASS '+label);}
function run(args){return spawnSync(setup,args,{windowsHide:true,encoding:'utf8',timeout:60000});}
const invalid=run(['--test-mode','--quiet','--unknown-option']);check(invalid.status!==0&&!fs.existsSync(parent),'unknown installer option cannot silently perform an installation');
let result=run(['--verify-payload']);check(result.status===0&&result.stdout.includes('PASS embedded payload'),'embedded package checksums verify');
result=run(['--test-mode','--quiet']);check(result.status===0&&fs.existsSync(path.join(install,'Windows','DeskWing-Companion.exe'))&&fs.existsSync(path.join(install,'.tabletdeck-install.json')),'isolated install creates managed files without launching app');
fs.writeFileSync(path.join(install,'personal-test.txt'),'preserve');result=run(['--test-mode','--quiet']);
const backups=fs.readdirSync(parent).filter(name=>name.startsWith('DeskWing-previous-'));
check(result.status===0&&backups.length===1&&fs.readFileSync(path.join(parent,backups[0],'personal-test.txt'),'utf8')==='preserve','update retains previous installation and untracked files');
fs.writeFileSync(path.join(install,'personal-test.txt'),'keep');result=run(['--test-mode','--uninstall']);check(result.status===0&&!fs.existsSync(path.join(install,'Windows','DeskWing-Companion.exe'))&&fs.existsSync(path.join(install,'personal-test.txt')),'uninstall removes owned program files and preserves untracked files');
result=run(['--test-mode','--uninstall']);check(result.status!==0&&fs.readFileSync(path.join(install,'personal-test.txt'),'utf8')==='keep','uninstaller refuses an unmanaged folder');
check(signature()===before,'installer test mode leaves actual installation untouched');
// Every path below is within the one new, fixed test directory checked above.
function clean(directory){const absolute=path.resolve(directory);assert.ok(absolute===parent||absolute.startsWith(parent+path.sep));for(const entry of fs.readdirSync(absolute,{withFileTypes:true})){const file=path.join(absolute,entry.name);assert.ok(!entry.isSymbolicLink(),'Unexpected link in installer test');if(entry.isDirectory())clean(file);else fs.unlinkSync(file);}fs.rmdirSync(absolute);}
clean(parent);console.log(passed+' installer checks passed; no GUI, shortcuts, startup writes, or app launch.');
