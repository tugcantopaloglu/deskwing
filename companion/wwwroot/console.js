'use strict';
let consoleRefreshPending=false,consoleStopped=false;
function consoleViewChanged(view,title){
  if(!desktopConsole)return;
  if(window.DeskWorkspace?.mode!=='studio'&&window.DeskWorkspace){window.DeskWorkspace.refresh();return;}
  $('#console-overview').hidden=view==='panel'||typeof monitorOpen!=='undefined'&&monitorOpen;
  $('#live-monitor').hidden=view==='panel'||typeof monitorOpen==='undefined'||!monitorOpen;
  $('#console-title').textContent=title||(typeof monitorOpen!=='undefined'&&monitorOpen?'Monitoring':'Studio overview');
}
function renderConsoleSummary(){
  $('#console-board').textContent=`${activeProfileName} · ${pages.length} pages · ${pages.reduce((sum,page)=>sum+page.buttons.length,0)} buttons`;
  $('#console-obs-summary').textContent=state.obsConnected?`Connected · ${state.scene||'No scene selected'}`:state.error||'OBS is not connected. Open OBS setup to check it.';
}
async function refreshConsoleConnections(){
  if(consoleRefreshPending||consoleStopped)return;consoleRefreshPending=true;
  try{const [info,desktop]=await Promise.all([api('/api/admin/info'),api('/api/admin/desktop')]);
    const physical=info.lanAddresses||[];
    $('#console-address').textContent=(physical.length?physical:info.addresses).join(' · ')||'No LAN address found';
    $('#console-paired').textContent=info.devices.length+' saved pairings · '+(info.connectedTablets||0)+' active fast links';$('#console-usb').textContent=desktop.usbStatus;
    $('#console-status').textContent=[info.deviceRecoveryWarning,desktop.recoveryWarning].filter(Boolean).join(' ')||'Status refreshed. Tablet transport status is shown on the tablet.';
  }catch(error){$('#console-status').textContent=error.message+' Reopen the console from the companion tray if this tab has expired.';}finally{consoleRefreshPending=false;}
}
async function consoleNavigate(id,action){
  if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&DeckEditor.isSaving?.()){notice('Wait for the board save to finish before changing views.',true);return;}
  if(typeof setComputerMode==='function'&&!await setComputerMode('studio'))return;
  if(dirty&&!await confirmAction('Discard unsaved changes?','Save in the current editor first, or discard these edits to continue.'))return;
  if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&dirty)DeckEditor.discardDraft();
  dirty=false;editing=false;if(panel.open)panel.close();if(typeof setMonitorOpen==='function')setMonitorOpen(false);
  for(const button of document.querySelectorAll('.console-nav button'))button.removeAttribute('aria-current');$('#'+id)?.setAttribute('aria-current','page');
  if(action)await action();else{consoleViewChanged('overview');await refreshConsoleConnections();}
}
async function stopConsoleCompanion(){
  if(dirty||busy||executionStatus?.active||typeof hasActiveHold==='function'&&hasActiveHold()){notice('Save your edits, finish or cancel the running action, and release held keys before stopping.',true);return;}
  if(!await confirmAction('Stop DeskWing?','Tablet controls and deck sounds will stop. OBS streaming and recording will continue. Start the Windows companion again when you need it.'))return;
  try{setMonitorOpen(false);await api('/api/admin/shutdown',{});consoleStopped=true;$('#console-status').textContent='Companion stopped. Start it from your Windows shortcut to reopen the console.';notice('Companion stopped.');}catch(error){notice(error.message,true);}
}
document.addEventListener('DOMContentLoaded',async()=>{
  if(!desktopConsole)return;
  const routes={'console-home':null,'console-editor':showEditor,'console-connections':showPairing,'console-obs':showObsSetup,'soundpad-button':showSoundpadLibrary,'console-profiles':showProfiles,'console-backups':showBackups,'console-mixer':showMixerSettings,'console-automation':showAutoProfiles,'console-packs':showBoardPacks,'monitor-toggle':()=>setMonitorOpen(true),'apps-button':showAppMixer,'studio-tools':showStudioTools,'console-options':showDesktopSettings,'console-readiness':showReadiness};
  for(const [id,action] of Object.entries(routes)){const button=$('#'+id);if(button)button.onclick=()=>consoleNavigate(id,action);}
  $('#console-computer').onclick=async()=>{if(await setComputerMode('computer'))await showComputerSettings();};
  $('#connection').onclick=()=>consoleNavigate('console-obs',showObsSetup);$('#console-connect-first').onclick=()=>consoleNavigate('console-connections',showPairing);$('#console-edit-first').onclick=()=>consoleNavigate('console-editor',showEditor);$('#console-refresh').onclick=async()=>{await refresh(true);await refreshConsoleConnections();};
  $('#console-stop').onclick=stopConsoleCompanion;
  panel.addEventListener('close',()=>{if(!panel.open)consoleViewChanged('overview');});
  await refresh(true);await refreshConsoleConnections();
  if(new URLSearchParams(location.search).get('section')==='updates')await consoleNavigate('console-options',showDesktopSettings);
});
setInterval(()=>{if(desktopConsole&&!document.hidden&&!panel.open)refreshConsoleConnections();},5000);
