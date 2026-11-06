'use strict';

function showPrivacySupport(){
  openPanel('Privacy & support',`<section class="setup privacy-support">
    <h3>Your tablet and your paired PC</h3>
    <p>DeskWing is developed by <strong>Tuğcan Topaloğlu</strong>. Core controls connect directly to the Windows PC you pair with. DeskWing does not provide a cloud relay or require a cloud account for that connection.</p>
    <h3>What moves between devices</h3>
    <ul>
      <li>Board controls, app/media status and optional Soundpad activity travel over your paired connection.</li>
      <li>Shared drawings, notes and notebook documents are saved on your devices and can synchronize with the paired PC. Imported PDF pages are rendered locally; rendered page images, annotations and recovery/history copies can be kept in that private storage and shared with the paired PC.</li>
      <li>Display viewing sends the selected PC content to the tablet while sharing is enabled. Pen or touch control sends input only during an explicitly enabled input session.</li>
    </ul>
    <h3>Other services you choose</h3>
    <p>Your handwriting keyboard or iPadOS Scribble performs handwriting recognition under its own settings. Optional Twitch/Kick views contact those providers. Windows release checks and downloads contact GitHub. Their privacy policies apply to those requests.</p>
    <h3>Keeping and deleting your work</h3>
    <p>Stop a viewing or input session to end that session. Unpair on the tablet or revoke it from Tablet connections on the PC to remove its access. Unpairing does not delete your saved board, shared documents, recovery copies or exported files.</p>
    <p>Use the document and backup controls to review or delete saved work. Files you export remain in the destination you choose until you remove them. Device and operating-system backups follow your device settings. Removing app data or uninstalling can remove local copies, so export anything you want to keep first.</p>
    <h3>Support</h3>
    <p>Email <strong>tugcan@tugcan.dev</strong> or open an issue at <span class="help">github.com/tugcantopaloglu/deskwing/issues</span>. Describe your app version, tablet/pen model, connection type and the steps that failed. Share diagnostic information only after reviewing it.</p>
    <p><strong>Keep private:</strong> pairing PINs, device tokens, certificates with private keys, OBS passwords, signing material, and the contents of personal notes/PDFs. Support does not need those secrets.</p>
    <div class="row"><button type="button" id="privacy-copy-email" class="quiet">Copy support email</button><button type="button" id="privacy-copy-policy" class="quiet">Copy online policy address</button></div>
    <p id="privacy-copy-status" class="help" role="status"></p>
    <p class="help">Policy prepared 6 November 2026. Online policy address: https://tugcantopaloglu.github.io/deskwing/privacy.html. Hosting and store distribution are separate release steps; the local policy above is available without opening that site.</p>
  </section>`);
  const copy=async(value,label)=>{
    const status=document.getElementById('privacy-copy-status');
    try{
      if(!navigator.clipboard?.writeText)throw new Error('Clipboard is unavailable');
      await navigator.clipboard.writeText(value);
      if(status?.isConnected)status.textContent=label+' copied.';
    }catch{
      if(status?.isConnected)status.textContent='Copy this manually: '+value;
    }
  };
  document.getElementById('privacy-copy-email').onclick=()=>copy('tugcan@tugcan.dev','Support email');
  document.getElementById('privacy-copy-policy').onclick=()=>copy('https://tugcantopaloglu.github.io/deskwing/privacy.html','Policy address');
}
