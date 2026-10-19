(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  function preview() {
    const p = $('prev');
    p.className = 'site-banner tone-' + $('bTone').value; p.style.opacity = $('bOn').checked ? 1 : .45;
    $('pvText').textContent = $('bText').value.trim() || 'Nội dung banner sẽ hiện ở đây';
    $('pvLink').hidden = !$('bLink').value.trim();
  }
  async function load() {
    try {
      const s = await API.call('/api/admin/site/settings');
      $('hotline').value = s.hotline; $('email').value = s.contactEmail; $('bOn').checked = s.bannerEnabled; $('bText').value = s.bannerText;
      $('bLink').value = s.bannerLink; $('bTone').value = s.bannerTone; $('cOn').checked = s.chatbotEnabled;
      $('hotline').placeholder = s.defaultHotline; $('email').placeholder = s.defaultEmail;
      $('hotHint').textContent = 'Mặc định: ' + s.defaultHotline; $('mailHint').textContent = 'Mặc định: ' + s.defaultEmail;
      $('upd').textContent = s.updatedAt ? 'Cập nhật lần cuối: ' + ADM.dt(s.updatedAt) : '';
      preview();
    } catch (err) { toast(err.message, 'error'); }
  }
  ['bOn', 'bText', 'bLink', 'bTone'].forEach(id => $(id).addEventListener('input', preview));
  $('bOn').addEventListener('change', preview);
  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    const body = { hotline: $('hotline').value, contactEmail: $('email').value, bannerEnabled: $('bOn').checked, bannerText: $('bText').value,
      bannerLink: $('bLink').value, bannerTone: $('bTone').value, chatbotEnabled: $('cOn').checked };
    if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call('/api/admin/site/settings', { method: 'PUT', body }))) load();
  });
  load();
})();
