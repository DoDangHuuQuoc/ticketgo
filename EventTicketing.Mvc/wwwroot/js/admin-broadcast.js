(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  const AUD = { buyers: 'Đã từng mua', event: 'Mua sự kiện', nobuy: 'Chưa mua', all: 'Tất cả' };
  let timer = null, events = [], recipients = 0;

  async function count() {
    const a = $('aud').value; $('evW').hidden = a !== 'event';
    if (a === 'event' && !$('ev').value) { recipients = 0; $('count').textContent = 'Hãy chọn sự kiện.'; return; }
    try {
      const d = await API.call('/api/admin/broadcast/recipients?' + ADM.qs({ audience: a, eventId: a === 'event' ? $('ev').value : '' }));
      recipients = d.count;
      $('count').innerHTML = `Sẽ gửi tới <b>${d.count}</b> người nhận${d.count > d.max ? ` <span style="color:var(--bad)">(vượt giới hạn ${d.max} người/đợt)</span>` : ''}.`;
    } catch (err) { $('count').textContent = err.message; }
  }

  async function history() {
    try {
      const list = await API.call('/api/admin/broadcast');
      $('rows').innerHTML = list.map(b => {
        const running = b.status === 'Sending', p = b.total ? Math.round(100 * (b.sent + b.failed) / b.total) : 0;
        return `<tr><td class="nw num">${ADM.dt(b.createdAt)}<small>${esc(b.createdBy)}</small></td><td><b>${esc(b.subject)}</b></td><td>${esc(AUD[b.audience] || b.audience)}</td>
          <td class="r num">${b.sent}/${b.total}${b.failed ? `<small style="color:var(--bad)">${b.failed} lỗi</small>` : ''}</td>
          <td>${running ? `<span class="badge b-wait">Đang gửi ${p}%</span>` : '<span class="badge b-go">Hoàn tất</span>'}</td></tr>`;
      }).join('') || '<tr><td colspan="5" class="empty">Chưa gửi đợt nào.</td></tr>';
      clearTimeout(timer);
      if (list.some(b => b.status === 'Sending')) timer = setTimeout(history, 4000);
    } catch (err) { toast(err.message, 'error'); }
  }

  const body = test => ({ subject: $('sub').value, message: $('msg').value, audience: $('aud').value, eventId: $('aud').value === 'event' ? $('ev').value || null : null, testOnly: test });

  $('aud').addEventListener('change', count); $('ev').addEventListener('change', count);
  $('reload').addEventListener('click', history);
  $('test').addEventListener('click', async () => { await ADM.act($('test'), () => API.call('/api/admin/broadcast', { method: 'POST', body: body(true) })); });
  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    if (!$('sub').value.trim() || !$('msg').value.trim()) return toast('Vui lòng nhập tiêu đề và nội dung', 'error');
    if (!recipients) return toast('Nhóm này chưa có người nhận', 'error');
    const r = await ADM.ask({ title: 'Gửi email hàng loạt', message: `Gửi “${esc($('sub').value.trim())}” tới <b>${recipients}</b> người? Không thể thu hồi sau khi gửi.`, confirmText: 'Gửi ngay' });
    if (r === null) return;
    if (await ADM.act($('go'), () => API.call('/api/admin/broadcast', { method: 'POST', body: body(false) }))) history();
  });

  (async () => {
    try { events = await API.call('/api/admin/events'); $('ev').innerHTML = events.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join(''); } catch { }
    count(); history();
  })();
})();
