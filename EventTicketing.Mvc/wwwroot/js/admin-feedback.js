(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, S = { page: 1, size: 20 };
  let items = [];
  const CAT = { Order: 'Đơn hàng', Payment: 'Thanh toán', Ticket: 'Vé / email vé', Refund: 'Hoàn / đổi vé', Event: 'Sự kiện', Other: 'Khác' };
  const ST = { New: ['Mới', 'b-wait'], InProgress: ['Đang xử lý', 'b-queue'], Resolved: ['Đã xong', 'b-go'] };
  const short = (t, n = 140) => t.length > n ? t.slice(0, n) + '…' : t;

  async function load() {
    try {
      const d = await API.call('/api/admin/feedback?' + ADM.qs({ q: $('q').value.trim(), status: $('status').value, category: $('cat').value, page: S.page, size: S.size }));
      items = d.items;
      const n = k => (d.counts.find(c => c.status === k) || {}).n || 0;
      $('counts').textContent = `${n('New')} mới · ${n('InProgress')} đang xử lý · ${n('Resolved')} đã xong`;
      $('rows').innerHTML = items.map(f => `<tr class="${f.status === 'Resolved' ? 'dim' : ''}">
        <td><b>${esc(f.name)}</b><small>${esc(f.email)}</small>${f.userId ? '<small>Có tài khoản</small>' : '<small>Khách vãng lai</small>'}</td>
        <td>${esc(CAT[f.category] || f.category)}</td>
        <td style="min-width:260px;white-space:pre-wrap">${esc(short(f.message))}${f.adminReply ? `<small>💬 ${esc(short(f.adminReply, 100))}</small>` : ''}</td>
        <td>${ADM.badge(ST, f.status)}</td>
        <td class="nw num">${ADM.dt(f.createdAt)}</td>
        <td><div class="acts">
          <button class="btn btn-ghost btn-sm" data-a="reply" data-id="${f.id}">${f.adminReply ? 'Xem / trả lời lại' : 'Trả lời'}</button>
          ${f.status === 'New' ? `<button class="btn btn-ghost btn-sm" data-a="prog" data-id="${f.id}">Đang xử lý</button>` : ''}
          ${f.status !== 'Resolved' ? `<button class="btn btn-ghost btn-sm" data-a="done" data-id="${f.id}">Đã xong</button>` : ''}
          <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${f.id}">Xoá</button>
        </div></td></tr>`).join('') || '<tr><td colspan="6" class="empty">Chưa có phản hồi nào.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { $('rows').innerHTML = '<tr><td colspan="6" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  function reply(f) {
    const m = ADM.modal({
      title: 'Trả lời ' + f.name, wide: true,
      body: `<form class="dlg-form" novalidate>
        <p class="muted">${esc(f.email)} · ${esc(CAT[f.category] || f.category)} · ${ADM.dt(f.createdAt)}</p>
        <div class="field"><label>Nội dung khách gửi</label><div style="white-space:pre-wrap;padding:10px 12px;background:var(--surface-2);border:1px solid var(--line);border-radius:var(--r-sm)">${esc(f.message)}</div></div>
        ${f.adminReply ? `<p class="hint">Đã trả lời bởi ${esc(f.repliedBy || '')} lúc ${ADM.dt(f.repliedAt)}. Gửi lại sẽ thay câu trả lời cũ.</p>` : ''}
        <div class="field"><label for="fbR">Câu trả lời</label><textarea id="fbR" rows="5" maxlength="2000" placeholder="Nhập câu trả lời cho khách…"></textarea></div>
        <div class="field"><label class="check"><input type="checkbox" id="fbMail" checked /> Gửi email cho khách</label></div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Đóng</button><button type="submit" class="btn btn-primary">Gửi trả lời</button></div></form>`
    });
    $('fbR').value = f.adminReply || '';
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      if (!$('fbR').value.trim()) return toast('Vui lòng nhập câu trả lời', 'error');
      const ok = await ADM.act(e.currentTarget.querySelector('[type=submit]'),
        () => API.call(`/api/admin/feedback/${f.id}/reply`, { method: 'PUT', body: { reply: $('fbR').value, sendEmail: $('fbMail').checked } }));
      if (ok) { m.close(); load(); }
    });
    setTimeout(() => $('fbR').focus(), 50);
  }

  $('rows').addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const f = items.find(x => x.id === b.dataset.id); if (!f) return;
    const a = b.dataset.a;
    if (a === 'reply') return reply(f);
    if (a === 'prog' || a === 'done') {
      if (await ADM.act(b, () => API.call(`/api/admin/feedback/${f.id}/status`, { method: 'PUT', body: { status: a === 'prog' ? 'InProgress' : 'Resolved' } }))) load();
    } else if (a === 'del') {
      if (await ADM.ask({ title: 'Xoá phản hồi', message: 'Xoá hẳn phản hồi của ' + esc(f.name) + '? Không thể khôi phục.', confirmText: 'Xoá', danger: true }) === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/feedback/' + f.id, { method: 'DELETE' }))) load();
    }
  });
  $('status').addEventListener('change', () => { S.page = 1; load(); });
  $('cat').addEventListener('change', () => { S.page = 1; load(); });
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  load();
})();
