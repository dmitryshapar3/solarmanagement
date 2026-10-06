(() => {
  const dialogs = new Map();
  const allowed = new Set(['light', 'dark', 'system']);
  window.smartSolar = {
    getAppearance() { return document.documentElement.dataset.theme || "light"; },
    appearance(value) {
      if (!allowed.has(value)) return;
      document.documentElement.dataset.theme = value;
      document.cookie = `deyeSolarAppearance=${value}; Path=/; Max-Age=31536000; SameSite=Lax${location.protocol === 'https:' ? '; Secure' : ''}`;
    },
    bounds(element) { const bounds = element.getBoundingClientRect(); return [bounds.left, bounds.width]; },
    copy(value) { return navigator.clipboard.writeText(value); },
    paste() { return navigator.clipboard.readText(); },
    location() { return new Promise((resolve, reject) => navigator.geolocation.getCurrentPosition(p => resolve([p.coords.latitude, p.coords.longitude]), reject, { timeout: 15000, maximumAge: 60000 })); },
    dialog(element, id) { if (!element.open) { const cancel = event => { event.preventDefault(); element.querySelector('[data-dialog-close]')?.click(); }; element.addEventListener('cancel', cancel); dialogs.set(id, {element, opener: document.activeElement, cancel}); element.showModal(); } },
    restoreDialog(id) { const record = dialogs.get(id); if (!record) return; record.element.removeEventListener('cancel', record.cancel); if (record.element.open) record.element.close(); if (record.opener?.isConnected) record.opener.focus(); dialogs.delete(id); },
    closeDialog(element) { if (element.open) element.close(); },
    scrollTo(id) { document.getElementById(id)?.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth', block: 'start' }); }
  };
  document.addEventListener('change', event => { if (event.target.matches('[data-appearance]')) window.smartSolar.appearance(event.target.value); });
  document.querySelectorAll('[data-appearance]').forEach(select => { select.value = document.documentElement.dataset.theme || 'light'; });
  document.querySelectorAll('[data-resend-seconds]').forEach(button => {
    const seconds = Math.min(300, Math.max(0, Number(button.dataset.resendSeconds) || 0));
    const ready = button.textContent;
    const until = Date.now() + seconds * 1000;
    const update = () => {
      const remaining = Math.max(0, Math.ceil((until - Date.now()) / 1000));
      button.disabled = remaining > 0;
      button.textContent = remaining ? button.dataset.resendLabel.replace('{0}', String(remaining)) : ready;
      if (!remaining) clearInterval(timer);
    };
    const timer = setInterval(update, 1000); update();
  });
})();
