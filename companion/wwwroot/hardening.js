'use strict';
Object.assign(kinds,{holdHotkey:'Hold keyboard shortcut / push to talk',voicemeeter:'Voicemeeter · set gain or mute'});
Object.assign(targetHints,{holdHotkey:'Matching push-to-talk key, for example F13',voicemeeter:'Strip[0].Gain or Bus[0].Mute (indices start at 0)'});
let deckHold=null;
function hasActiveHold(){return deckHold!==null;}
async function releaseDeckHold(){
  const hold=deckHold;if(!hold)return;deckHold=null;hold.released=true;clearInterval(hold.timer);hold.button.classList.remove('holding');
  if(hold.lease)try{await api('/api/hold',{operation:'release',lease:hold.lease});}catch{notice('Connection lost. Held keys release automatically within three seconds.',true);}
}
async function beginDeckHold(button,id){
  if(deckHold||preview)return;
  const hold={button,released:false,lease:'',timer:null,renewing:false};deckHold=hold;button.classList.add('holding');
  try {
    const response=await api('/api/hold',{operation:'begin',id});hold.lease=response.lease;
    if(hold.released){await api('/api/hold',{operation:'release',lease:hold.lease});return;}
    hold.timer=setInterval(async()=>{if(hold.renewing||hold.released)return;hold.renewing=true;try{await api('/api/hold',{operation:'renew',lease:hold.lease});}catch(error){if(deckHold===hold){await releaseDeckHold();notice(error.message,true);}}finally{hold.renewing=false;}},800);
  }catch(error){if(deckHold===hold)await releaseDeckHold();notice(error.message,true);}
}
function attachHoldButtons(){
  if(typeof arranging!=='undefined'&&arranging)return;
  const page=pages.find(page=>page.id===pageId);if(!page)return;
  for(const button of document.querySelectorAll('.key')){
    const action=page.buttons.find(action=>action.id===button.dataset.id);if(action?.kind!=='holdHotkey')continue;
    button.classList.add('hold-key');button.setAttribute('aria-description','Keep pressed to hold the configured key. Releasing stops the hold.');
    button.onclick=()=>{};
    button.onpointerdown=event=>{if(event.button!==0||!event.isPrimary)return;event.preventDefault();button.setPointerCapture(event.pointerId);beginDeckHold(button,action.id);};
    button.onpointerup=button.onpointercancel=button.onlostpointercapture=()=>releaseDeckHold();
    button.onkeydown=event=>{if(![' ','Enter'].includes(event.key))return;event.preventDefault();if(!event.repeat)beginDeckHold(button,action.id);};
    button.onkeyup=event=>{if([' ','Enter'].includes(event.key)){event.preventDefault();releaseDeckHold();}};
    button.onblur=()=>releaseDeckHold();
  }
}
window.addEventListener('blur',releaseDeckHold);window.addEventListener('pagehide',releaseDeckHold);
document.addEventListener('visibilitychange',()=>{if(document.hidden)releaseDeckHold();});

async function showDesktopSettings(){
  if(native||preview)return;
  if(dirty){notice('Save or discard your editor changes first.',true);return;}
  featureView='desktop';editing=false;
  openPanel('Windows options','<p role="status">Loading Windows options…</p><p id="desktop-load-error" role="alert"></p>');let generation=panelGeneration;
  const owns=()=>panel.open&&panelGeneration===generation&&featureView==='desktop';
  try {
    const data=await api('/api/admin/desktop');if(!owns())return;
    openPanel('Desktop settings',`<p>Keep the companion available from the Windows tray. Closing the desktop window leaves tablet control running; use the tray menu to exit.</p><div class="row"><label class="check"><input id="desktop-startup" type="checkbox" ${data.startWithWindows?'checked':''}>Start with Windows</label><label class="check"><input id="desktop-usb" type="checkbox" ${data.settings.usbReconnect?'checked':''}>Reconnect USB automatically</label><label class="check"><input id="desktop-updates" type="checkbox" ${data.settings.updateNotifications?'checked':''}>Check for updates when the app opens</label></div><label>USB tablet serial (optional with one tablet)<input id="desktop-serial" maxlength="128" value="${esc(data.settings.usbSerial)}" placeholder="Shown by platform-tools/adb.exe devices"></label><p class="help">USB reconnect checks every five seconds. USB debugging and PC authorization are still required. With multiple tablets, enter a serial.</p><p id="desktop-status" role="status" class="help">${esc(data.usbStatus)}</p><div class="row"><button id="desktop-save" class="primary">Save desktop settings</button><button id="desktop-check" class="quiet">Check for updates</button><button id="desktop-release" class="quiet">View release</button></div><p id="desktop-update-status" role="status" class="help"></p>`);
    generation=panelGeneration;let saving=false;
    const inputs=['#desktop-startup','#desktop-usb','#desktop-updates','#desktop-serial'];for(const id of inputs)$(id).oninput=()=>{dirty=true;};
    let releaseUrl=data.update.url;
    const display=result=>{$('#desktop-update-status').textContent=result.error||(result.available?`Version ${result.latestVersion} is available. Installed: ${result.currentVersion}.`:`Installed: ${result.currentVersion}. ${result.latestVersion?'Latest stable: '+result.latestVersion+'.':'Choose Check for updates to contact GitHub.'}`);releaseUrl=result.url;};display(data.update);
    $('#desktop-save').onclick=async()=>{if(saving||!owns())return;const button=$('#desktop-save'),body={startWithWindows:$('#desktop-startup').checked,settings:{usbReconnect:$('#desktop-usb').checked,usbSerial:$('#desktop-serial').value.trim(),updateNotifications:$('#desktop-updates').checked}};saving=true;button.disabled=true;for(const id of inputs)$(id).disabled=true;try{await api('/api/admin/desktop',body);if(!owns())return;dirty=false;$('#desktop-status').textContent='Settings saved. USB status updates within five seconds.';}catch(error){if(owns())$('#desktop-status').textContent=error.message;}finally{saving=false;if(owns()){button.disabled=false;for(const id of inputs)$(id).disabled=false;}}};
    $('#desktop-check').onclick=async()=>{if(!owns())return;const button=$('#desktop-check');button.disabled=true;try{const result=await api('/api/admin/updates',{});if(owns())display(result);}catch(error){if(owns())$('#desktop-update-status').textContent=error.message;}finally{if(button.isConnected)button.disabled=false;}};
    $('#desktop-release').onclick=()=>{if(typeof releaseUrl==='string'&&/^https:\/\/github\.com\/tugcantopaloglu\/deskwing\/releases(?:\/tag\/v\d+\.\d+\.\d+)?$/.test(releaseUrl))window.open(releaseUrl,'_blank','noopener,noreferrer');};
    if(typeof attachUpdateControls==='function')attachUpdateControls(data,owns,display);
  }catch(error){if(owns())$('#desktop-load-error').textContent=error.message;}
}
async function remapSoundpad(linkLegacy){
  if(linkLegacy&&!await confirmAction('Link existing Soundpad buttons?','First verify their indices still play the intended sounds in Soundpad All sounds. Linking records those current files; an old incorrect index cannot be recovered automatically.'))return;
  try{const result=await api('/api/admin/soundpad/remap',{linkLegacy,confirmed:linkLegacy});notice(`${result.updated} indices updated, ${result.linked} sounds linked, ${result.missing} missing or ambiguous.`);await refresh(true);}catch(error){notice(error.message,true);}
}
