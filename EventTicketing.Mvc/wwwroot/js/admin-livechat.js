(() => {
  'use strict';
  if (!ADM.guard(['Admin', 'Staff'])) return;
  const $ = ADM.$;
  if (localStorage.getItem('role') !== 'Admin') { document.querySelectorAll('#admTabs a:not([data-staff])').forEach(a => a.remove()); document.querySelectorAll('.adm-grp').forEach(g => g.querySelector('a') || g.remove()); }
  const utc = iso => new Date(/Z|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
  const clock = iso => utc(iso).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
  const ago = iso => { const s = Math.max(0, (Date.now() - utc(iso)) / 1000); return s < 60 ? 'vừa xong' : s < 3600 ? Math.floor(s / 60) + ' phút' : s < 86400 ? Math.floor(s / 3600) + ' giờ' : Math.floor(s / 86400) + ' ngày'; };
  let items = [], cur = null, shown = '', lastUnread = -1, busy = false;

  function beep() { try { const c = new (window.AudioContext || window.webkitAudioContext)(), o = c.createOscillator(), g = c.createGain(); o.frequency.value = 760; g.gain.value = .06; o.connect(g); g.connect(c.destination); o.start(); o.stop(c.currentTime + .12); } catch { /* bị chặn âm thanh */ } }

  function drawList() {
    const open = items.filter(c => c.status === 'Open');
    $('lcCount').textContent = open.length ? open.length + ' đang mở' : '';
    $('lcItems').innerHTML = items.map(c => `<li><button type="button" class="lc-it ${c.id === cur ? 'on' : ''} ${c.status !== 'Open' ? 'off' : ''}" data-id="${c.id}">
      <span class="lc-it-t"><b>${esc(c.name)}</b><small>${ago(c.lastMessageAt)}</small></span>
      <span class="lc-it-p">${esc(c.lastText || '—')}</span>
      <span class="lc-it-f">${c.status !== 'Open' ? '<em>Đã đóng</em>' : c.hasAccount ? '<em>Có tài khoản</em>' : '<em>Khách vãng lai</em>'}${c.staffUnread ? `<i class="lc-badge">${c.staffUnread}</i>` : ''}</span></button></li>`).join('')
      || '<li class="empty">Chưa có cuộc chat nào. Khi khách bấm “Chat với nhân viên” ở khung chatbot, cuộc chat sẽ hiện ở đây.</li>';
  }
  async function loadList() {
    try {
      const d = await API.call('/api/admin/livechat'); items = d.items;
      if (lastUnread >= 0 && d.unread > lastUnread) beep();
      lastUnread = d.unread; drawList();
      document.title = (d.unread ? `(${d.unread}) ` : '') + 'Chat trực tiếp';
    } catch { /* lỗi mạng thoáng qua */ }
  }

  function drawConv(d) {
    $('lcName').textContent = d.name; $('lcMeta').textContent = d.hasAccount ? 'Khách có tài khoản' : 'Khách vãng lai';
    const closed = d.status !== 'Open';
    $('lcClose').hidden = closed; $('lcForm').hidden = closed;
    const key = d.messages.map(m => m.id).join() + d.status;
    if (key === shown) return; shown = key;
    const box = $('lcMsgs'), atEnd = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    box.innerHTML = d.messages.map(m => `<div class="lc-m ${m.fromStaff ? 'staff' : 'cus'}"><div>${esc(m.text)}</div><small>${esc(m.fromStaff ? m.sender : d.name)} · ${clock(m.createdAt)}</small></div>`).join('')
      + (closed ? '<p class="lc-end">Cuộc chat đã đóng.</p>' : '');
    if (atEnd || box.dataset.fresh !== cur) { box.scrollTop = box.scrollHeight; box.dataset.fresh = cur; }
  }
  async function loadConv() {
    if (!cur) return;
    try { drawConv(await API.call('/api/admin/livechat/' + cur)); } catch (err) { if (/không tìm thấy/i.test(err.message)) cur = null; }
  }
  async function open(id) {
    cur = id; shown = ''; $('lcEmpty').hidden = true; $('lcConv').hidden = false; $('lcMsgs').innerHTML = '';
    await loadConv(); drawList(); loadList(); $('lcText').focus();
  }

  $('lcItems').addEventListener('click', e => { const b = e.target.closest('[data-id]'); if (b) open(b.dataset.id); });
  async function send() {
    const t = $('lcText').value.trim(); if (!t || !cur || busy) return;
    busy = true;
    try { await API.call(`/api/admin/livechat/${cur}/send`, { method: 'POST', body: { text: t } }); $('lcText').value = ''; await loadConv(); loadList(); }
    catch (err) { toast(err.message, 'error'); } finally { busy = false; }
  }
  $('lcForm').addEventListener('submit', e => { e.preventDefault(); send(); });
  $('lcText').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } });
  $('lcClose').addEventListener('click', async e => {
    if (await ADM.ask({ title: 'Đóng cuộc chat', message: 'Đóng cuộc chat này? Khách sẽ được báo cuộc chat đã kết thúc.', confirmText: 'Đóng' }) === null) return;
    if (await ADM.act(e.currentTarget, () => API.call(`/api/admin/livechat/${cur}/close`, { method: 'POST' }))) { shown = ''; await loadConv(); loadList(); }
  });

  loadList();
  setInterval(() => { if (!document.hidden) { loadList(); loadConv(); } }, 3000);
})();
