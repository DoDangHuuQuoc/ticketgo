(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  const S = { status: '', page: 1, size: 20 };
  const LABEL = { '': 'Tất cả', Valid: 'Hợp lệ', CheckedIn: 'Đã vào cổng', Cancelled: 'Đã thu hồi' };

  async function load() {
    try {
      const d = await API.call('/api/admin/tickets?' + ADM.qs({ eventId: $('ev').value, status: S.status, q: $('q').value.trim(), page: S.page, size: S.size }));
      const cnt = Object.fromEntries(d.counts.map(c => [c.status, c.count])), all = d.counts.reduce((a, c) => a + c.count, 0);
      $('chips').innerHTML = Object.keys(LABEL).map(k => `<button type="button" data-s="${k}" aria-pressed="${S.status === k}">${LABEL[k]}<span class="n">${k === '' ? all : (cnt[k] || 0)}</span></button>`).join('');
      rows.innerHTML = d.items.map(t => `<tr>
        <td class="mono"><b>${t.code}</b><small>${esc(t.seatInfo)}</small></td>
        <td><b>${esc(t.userName)}</b><small>${esc(t.userEmail)}</small></td>
        <td>${esc(t.eventName)}<small>${esc(t.typeName)} · ${esc(t.zone)}</small></td>
        <td>${ADM.badge(ADM.TICKET, t.status)}${t.checkedInAt ? `<small>${ADM.dt(t.checkedInAt)}</small>` : ''}</td>
        <td><div class="acts">
          ${t.status === 'Valid' ? `<button class="btn btn-ghost btn-sm" data-a="in" data-id="${t.id}">Check-in tay</button><button class="btn btn-ghost btn-sm danger" data-a="cancel" data-id="${t.id}">Thu hồi</button>` : ''}
          ${t.status === 'CheckedIn' ? `<button class="btn btn-ghost btn-sm" data-a="undo" data-id="${t.id}">Hoàn tác</button>` : ''}
        </div></td></tr>`).join('') || '<tr><td colspan="5" class="empty">Không có vé phù hợp.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { toast(err.message, 'error'); }
  }

  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const id = b.dataset.id, k = b.dataset.a;
    if (k === 'in') { if (await ADM.act(b, () => API.call(`/api/admin/tickets/${id}/check-in`, { method: 'POST' }))) load(); return; }
    const cfg = k === 'undo'
      ? { title: 'Hoàn tác check-in', message: 'Vé quay về trạng thái hợp lệ và có thể quét lại.', path: 'undo-check-in', confirmText: 'Hoàn tác' }
      : { title: 'Thu hồi vé', message: 'Mã QR của vé này sẽ không còn dùng được. Thao tác không hoàn tiền và không trả kho — nếu cần hãy dùng “Hoàn tiền” ở trang Đơn hàng.', path: 'cancel', confirmText: 'Thu hồi', danger: true };
    const reason = await ADM.ask({ ...cfg, label: 'Lý do *', required: true });
    if (reason === null) return;
    if (await ADM.act(b, () => API.call(`/api/admin/tickets/${id}/${cfg.path}`, { method: 'POST', body: { reason } }))) load();
  });

  $('chips').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.status = b.dataset.s; S.page = 1; load(); } });
  $('ev').addEventListener('change', () => { $('export').disabled = $('notify').disabled = !$('ev').value; S.page = 1; load(); });
  $('notify').addEventListener('click', async () => {
    const id = $('ev').value; if (!id) return;
    const name = $('ev').selectedOptions[0]?.textContent || '';
    let info; try { info = await API.call(`/api/admin/events/${id}/recipients`); } catch (err) { return toast(err.message, 'error'); }
    if (!info.count) return toast('Sự kiện này chưa có khách đã thanh toán', 'error');
    const m = ADM.modal({
      title: 'Thông báo cho khách', wide: true,
      body: `<form class="dlg-form" novalidate>
        <p class="hint">Gửi email tới <b>${info.count}</b> khách đã thanh toán sự kiện <b>${esc(name)}</b> (mỗi người một thư, tên sự kiện/địa điểm/giờ được tự chèn). Việc gửi chạy nền.</p>
        <div class="field"><label for="nSub">Tiêu đề</label><input id="nSub" maxlength="150" placeholder="VD: Thay đổi giờ mở cửa" /></div>
        <div class="field"><label for="nMsg">Nội dung</label><textarea id="nMsg" rows="6" maxlength="3000" placeholder="Nội dung thông báo gửi tới khách…"></textarea></div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Gửi cho ${info.count} khách</button></div></form>`
    });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const body = { subject: $('nSub').value.trim(), message: $('nMsg').value.trim() };
      if (body.subject.length < 3) return toast('Tiêu đề tối thiểu 3 ký tự', 'error');
      if (body.message.length < 5) return toast('Nội dung tối thiểu 5 ký tự', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call(`/api/admin/events/${id}/notify`, { method: 'POST', body }))) m.close();
    });
    setTimeout(() => $('nSub').focus(), 50);
  });
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  $('export').addEventListener('click', async e => {
    busy(e.currentTarget, true, 'Đang xuất…');
    try { await ADM.download('/api/admin/tickets/export', { eventId: $('ev').value, status: S.status }); } catch (err) { toast(err.message, 'error'); }
    busy(e.currentTarget, false);
  });
  (async () => {
    try { const evs = await API.call('/api/admin/events'); $('ev').insertAdjacentHTML('beforeend', evs.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')); } catch { /* bỏ qua */ }
    load();
  })();
})();
