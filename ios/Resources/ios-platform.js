'use strict';
// Platform adapter stays outside the shared application. Native policy independently rejects unsupported routes.
(() => {
  function adaptConnection() {
    const mode = document.getElementById('pair-mode');
    if (!mode || mode.dataset.iosAdapted) return;
    mode.dataset.iosAdapted = 'true';
    for (const option of [...mode.options]) if (option.value !== 'wifi') option.remove();
    mode.value = 'wifi';
    const section = mode.closest('.setup');
    if (section) {
      const help = [...section.querySelectorAll('p.help')];
      if (help[0]) help[0].textContent = 'Encrypted Wi-Fi / LAN connection · HTTPS polling';
      if (help[1]) help[1].textContent = 'Keep DeskWing running on your Windows PC and use the same local network. Allow local-network access in iOS Settings when prompted. USB tunnelling and QR pairing are not included in this iOS build.';
    }
    document.getElementById('switch-transport')?.remove();
    document.getElementById('pair-scan')?.remove();
    document.getElementById('scan-pairing')?.remove();
    if (section) for (const button of section.querySelectorAll('button')) {
      if (button.textContent.trim() === 'Scan pairing QR') button.remove();
    }
  }
  const observer = new MutationObserver(adaptConnection);
  observer.observe(document.documentElement, {childList:true,subtree:true});
  adaptConnection();
})();
