(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  const S = { status: '', page: 1, size: 20 };
  const LABEL = { '': 'Tất cả', Pending: 'Đang giữ', Paid: 'Đã thanh toán', Cancelled: 'Huỷ / hoàn', Expired: 'Hết hạn' };

  const filters = () => ({ status: S.status, eventId: $('ev').value, q: $('q').value.trim(), from: $('from').value ? new Date($('from').value).toISOString() : '',
    to: $('to').value ? new Date(new Date($('to').value).getTime() + 864e5).toISOString() : '' });

  async function load() {
    try {
      const d = await API.call('/api/admin/bookings?' + ADM.qs({ ...filters(), page: S.page, size: S.size }));
      const cnt = Object.fromEntries(d.counts.map(c => [c.status, c.count]));
      const all = d.counts.reduce((a, c) => a + c.count, 0);
      $('chips').innerHTML = Object.keys(LABEL).map(k => `<button type="button" data-s="${k}" aria-pressed="${S.status === k}">${LABEL[k]}<span class="n">${k === '' ? all : (cnt[k] || 0)}</span></button>`).join('');
      rows.innerHTML = d.items.map(b => `<tr>
        <td class="mono"><button class="link mono" data-id="${b.id}">${ADM.shortId(b.id)}</button></td>
        <td><b>${esc(b.userName)}</b><small>${esc(b.userEmail)}</small></td>
        <td>${esc(b.eventName)}<small>${esc(b.typeName)} · ${esc(b.zone)} · ×${b.quantity}</small></td>
        <td class="r num"><b>${money(b.totalAmount)}</b></td>
        <td>${ADM.badge(ADM.BOOKING, b.status)}</td>
        <td class="num">${ADM.dt(b.createdAt)}</td></tr>`).join('') || '<tr><td colspan="6" class="empty">Không có đơn hàng phù hợp.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { toast(err.message, 'error'); }
  }

  async function detail(id) {
    const m = ADM.modal({ title: 'Đơn ' + ADM.shortId(id), wide: true, body: '<p class="muted">Đang tải…</p>' });
    async function render() {
      let d; try { d = await API.call('/api/admin/bookings/' + id); } catch (err) { m.body.innerHTML = esc(err.message); return; }
      const b = d.booking;
      m.body.innerHTML = `
        <dl class="kv">
          <dt>Trạng thái</dt><dd>${ADM.badge(ADM.BOOKING, b.status)}</dd>
          <dt>Khách hàng</dt><dd>${esc(b.user.fullName)} · ${esc(b.user.email)}</dd>
          <dt>Sự kiện</dt><dd>${esc(b.eventName)}<br><small class="muted">${ADM.dt(b.eventStartsAt)} · ${esc(b.venue)}</small></dd>
          <dt>Loại vé</dt><dd>${esc(b.typeName)} (${esc(b.zone)}) — ${b.quantity} × ${money(b.unitPrice)}</dd>
          <dt>Tổng tiền</dt><dd>${money(b.totalAmount)}</dd>
          <dt>Tạo lúc</dt><dd>${ADM.dt(b.createdAt)}${b.status === 'Pending' ? ` · giữ đến ${ADM.dt(b.expiresAt)}` : ''}</dd>
          <dt>Mã đầy đủ</dt><dd class="mono">${b.id}</dd>
        </dl>
        ${b.status === 'Expired' ? '<p class="hint warn">Đơn đã hết hạn giữ chỗ. Nếu khách vẫn chuyển khoản, bấm “Xác nhận đã nhận tiền”: hệ thống lấy lại vé nếu còn, hết vé thì báo để bạn hoàn tiền cho khách.</p>' : ''}
        ${b.status === 'Pending' ? '<div class="sect" id="payQr"><h3>Mã QR thanh toán</h3><p class="muted">Đang tạo mã QR…</p></div>' : ''}
        <div class="sect"><h3>Thanh toán</h3><ul class="mini-list">${d.payments.map(p => `<li><span><b>${p.status === 'Refunded' ? 'Hoàn tiền' : p.status === 'Paid' ? 'Thanh toán' : 'Thất bại'}</b> <small class="muted">${esc(p.transactionRef)}</small></span><span>${money(p.amount)} · ${ADM.dt(p.createdAt)}</span></li>`).join('') || '<li class="muted">Chưa có giao dịch.</li>'}</ul></div>
        <div class="sect"><h3>Vé (${d.tickets.length})</h3><ul class="mini-list">${d.tickets.map(t => `<li><span class="mono">${ADM.shortId(t.id)} · ${esc(t.seatInfo)}</span><span>${ADM.badge(ADM.TICKET, t.status)}${t.checkedInAt ? ` <small class="muted">${ADM.dt(t.checkedInAt)}</small>` : ''}</span></li>`).join('') || '<li class="muted">Chưa phát hành vé.</li>'}</ul></div>
        <div class="dlg-f">
          ${b.status === 'Pending' ? '<button class="btn btn-ghost danger" data-do="cancel">Huỷ đơn & trả vé</button>' : ''}
          ${b.status === 'Pending' || b.status === 'Expired' ? '<button class="btn btn-primary" data-do="confirm">✓ Xác nhận đã nhận tiền</button>' : ''}
          ${b.status === 'Paid' ? '<button class="btn btn-ghost" data-do="resend">Gửi lại email vé</button><button class="btn btn-danger" data-do="refund">Hoàn tiền</button>' : ''}
        </div>`;
      if (b.status === 'Pending') {
        const box = m.body.querySelector('#payQr');
        API.call('/api/payments/qr/' + id).then(q => {
          box.innerHTML = `<h3>Mã QR thanh toán</h3><div class="pay-qr-box"><img class="pay-qr" src="${q.image}" alt="Mã QR thanh toán" width="200" height="200" />
            <dl class="kv pay-kv"><dt>Ngân hàng</dt><dd>${esc(q.bankName)}</dd><dt>Số tài khoản</dt><dd class="mono">${esc(q.accountNo)}</dd><dt>Chủ tài khoản</dt><dd>${esc(q.accountName)}</dd>
            <dt>Số tiền</dt><dd>${money(q.amount)}</dd><dt>Nội dung</dt><dd class="mono">${esc(q.content)}</dd></dl></div>`;
        }).catch(err => { box.innerHTML = `<h3>Mã QR thanh toán</h3><p class="muted">${esc(err.message)}</p>`; });
      }
      m.body.querySelectorAll('[data-do]').forEach(btn => btn.addEventListener('click', async () => {
        const k = btn.dataset.do; let reason = '';
        if (k === 'cancel') { reason = await ADM.ask({ title: 'Huỷ đơn giữ chỗ', message: 'Đơn sẽ bị huỷ và số vé được trả lại kho ngay.', label: 'Lý do (không bắt buộc)', confirmText: 'Huỷ đơn', danger: true }); if (reason === null) return; }
        if (k === 'refund') { reason = await ADM.ask({ title: 'Hoàn tiền đơn hàng', message: `Hoàn <b>${money(b.totalAmount)}</b>, thu hồi <b>${b.quantity}</b> vé và trả lại kho. Thao tác không hoàn tác được. (Hệ thống chỉ ghi nhận hoàn tiền — hãy chuyển tiền thật qua cổng thanh toán.)`, label: 'Lý do hoàn tiền *', required: true, confirmText: 'Xác nhận hoàn tiền', danger: true }); if (reason === null) return; }
        if (k === 'confirm') { reason = await ADM.ask({ title: 'Xác nhận đã nhận tiền', message: `Xác nhận đã nhận <b>${money(b.totalAmount)}</b> cho đơn này? Hệ thống sẽ phát hành <b>${b.quantity}</b> vé và gửi email cho khách. Hãy đối chiếu sao kê ngân hàng trước khi xác nhận.`, label: 'Mã giao dịch ngân hàng (không bắt buộc)', confirmText: 'Xác nhận & phát vé' }); if (reason === null) return; }
        const path = { cancel: 'cancel', refund: 'refund', resend: 'resend-email', confirm: 'confirm-payment' }[k];
        const body = k === 'confirm' ? { reference: reason } : { reason };
        if (await ADM.act(btn, () => API.call(`/api/admin/bookings/${id}/${path}`, { method: 'POST', body }))) { await render(); load(); }
      }));
    }
    render();
  }

  /* ---------- Phát vé mời (miễn phí) ---------- */
  async function invite() {
    let evs = [];
    try { evs = (await API.call('/api/admin/events')).filter(e => !e.isPast); } catch (err) { return toast(err.message, 'error'); }
    if (!evs.length) return toast('Chưa có sự kiện sắp diễn ra để phát vé mời', 'error');
    const m = ADM.modal({
      title: 'Phát vé mời', wide: true,
      body: `<form class="dlg-form" novalidate>
        <p class="hint">Vé mời miễn phí (0 đ), trừ vào kho như vé bán, gửi mã QR qua email. Người nhận cần có tài khoản.</p>
        <div class="grid2">
          <div class="field"><label for="iEv">Sự kiện</label><select id="iEv" class="select" style="width:100%">${evs.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')}</select></div>
          <div class="field"><label for="iTt">Loại vé</label><select id="iTt" class="select" style="width:100%"></select></div>
          <div class="field"><label for="iMail">Email người nhận</label><input id="iMail" type="email" autocomplete="off" placeholder="khach@example.com" /></div>
          <div class="field"><label for="iQty">Số lượng (1–50)</label><input id="iQty" type="number" min="1" max="50" step="1" value="1" inputmode="numeric" /></div>
          <div class="field full"><label for="iNote">Ghi chú nội bộ (lưu vào nhật ký)</label><input id="iNote" maxlength="200" placeholder="VD: Khách mời VIP / nhà tài trợ" /></div>
          <div class="field full" id="iSeats"></div>
        </div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Phát vé & gửi email</button></div></form>`
    });
    const tt = $('iTt');
    let types = [], picked = [];
    // Vé có số ghế: loại "khách tự chọn" (VIP) thì admin chọn ghế cho khách mời trên sơ đồ; loại ngẫu nhiên thì hệ thống xếp
    function syncSeats() {
      const t = types.find(x => x.id === tt.value), box = $('iSeats'); picked = []; box.innerHTML = '';
      $('iQty').disabled = false;
      if (!t) return;
      if (t.seatMode === 1) box.innerHTML = '<p class="sp-note">Loại vé này có số ghế: hệ thống tự xếp ghế ngẫu nhiên cho khách mời.</p>';
      else if (t.seatMode === 2) {
        $('iQty').disabled = true; $('iQty').value = '';
        box.innerHTML = '<label>Chọn ghế cho khách mời (số vé = số ghế đã chọn)</label><div id="iSeatBox"></div>';
        SeatPicker.mount($('iSeatBox'), { typeId: t.id, max: () => 50, onChange: l => { picked = l; $('iQty').value = l.length || ''; } });
      }
    }
    tt.addEventListener('change', syncSeats);
    async function loadTypes() {
      tt.innerHTML = '<option>Đang tải…</option>';
      try {
        const d = await API.call('/api/admin/events/' + $('iEv').value);
        types = d.ticketTypes;
        tt.innerHTML = d.ticketTypes.map(t => `<option value="${t.id}" ${t.availableQuantity < 1 ? 'disabled' : ''}>${esc(t.name)} · ${esc(t.zone)} · còn ${t.availableQuantity}</option>`).join('') || '<option value="">Chưa có loại vé</option>';
        syncSeats();
      } catch (err) { tt.innerHTML = '<option value="">Không tải được</option>'; toast(err.message, 'error'); }
    }
    $('iEv').addEventListener('change', loadTypes); loadTypes();
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const seated = (types.find(x => x.id === tt.value) || {}).seatMode === 2;
      const body = { ticketTypeId: tt.value, email: $('iMail').value.trim(), quantity: seated ? picked.length : Number($('iQty').value) || 0, note: $('iNote').value.trim(), seats: seated ? picked : undefined };
      if (seated && !picked.length) return toast('Hãy chọn ít nhất một ghế cho khách mời', 'error');
      if (!body.ticketTypeId) return toast('Chọn loại vé còn vé', 'error');
      if (!/^\S+@\S+\.\S+$/.test(body.email)) return toast('Email không hợp lệ', 'error');
      if (body.quantity < 1 || body.quantity > 50) return toast('Số lượng từ 1 đến 50', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call('/api/admin/invitations', { method: 'POST', body }))) { m.close(); S.page = 1; load(); }
    });
    setTimeout(() => $('iMail').focus(), 50);
  }
  $('invite').addEventListener('click', invite);

  rows.addEventListener('click', e => { const b = e.target.closest('[data-id]'); if (b) detail(b.dataset.id); });
  $('chips').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.status = b.dataset.s; S.page = 1; load(); } });
  ['ev', 'from', 'to'].forEach(i => $(i).addEventListener('change', () => { S.page = 1; load(); }));
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  $('export').addEventListener('click', async e => {
    busy(e.currentTarget, true, 'Đang xuất…');
    try { await ADM.download('/api/admin/bookings/export', filters()); } catch (err) { toast(err.message, 'error'); }
    busy(e.currentTarget, false);
  });

  (async () => {
    try { const evs = await API.call('/api/admin/events'); $('ev').insertAdjacentHTML('beforeend', evs.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')); } catch { /* bỏ qua */ }
    load();
  })();
})();
