(() => {
  'use strict';
  if (!requireLogin()) return;
  const tabs = { tickets: ['tab-tickets', 'pane-tickets'], orders: ['tab-orders', 'pane-orders'] };
  const $ = id => document.getElementById(id);
  const dlg = $('qrDialog');
  const TSTAT = { Valid: ['Hợp lệ', 'b-go'], CheckedIn: ['Đã sử dụng', 'b-off'], Cancelled: ['Đã huỷ', 'b-bad'] };
  const BSTAT = { Pending: ['Chờ thanh toán', 'b-wait'], Paid: ['Đã thanh toán', 'b-go'], Expired: ['Hết hạn', 'b-off'], Cancelled: ['Đã huỷ', 'b-bad'] };

  function show(k) { for (const [key, [t, p]] of Object.entries(tabs)) { $(t).setAttribute('aria-selected', key === k); $(p).hidden = key !== k; } }
  Object.entries(tabs).forEach(([k, [t]]) => $(t).addEventListener('click', () => show(k)));

  const empty = (title, sub) => `<div class="empty"><p class="strong" style="color:var(--ink)">${title}</p><p>${sub}</p><a class="btn btn-primary" style="margin-top:14px" href="/">Xem sự kiện</a></div>`;

  async function loadTickets() {
    const el = $('pane-tickets'), list = await API.call('/api/tickets/mine');
    if (!list.length) { el.innerHTML = empty('Bạn chưa có vé nào.', 'Chọn một sự kiện để bắt đầu.'); return; }
    el.innerHTML = list.map(t => {
      const d = dateParts(t.startsAt), [lbl, cls] = TSTAT[t.status] || [t.status, 'b-off'];
      return `<article class="tk ${t.status !== 'Valid' ? 'dim' : ''}">
        <div class="tk-qr"><button type="button" data-cap="${esc(t.eventName)} – vé ${esc(t.seatInfo)}" aria-label="Phóng to mã QR"><div class="skel qr-ph" data-qr="${t.id}"></div></button></div>
        <div class="tk-info"><span class="badge ${cls}" style="justify-self:start">${lbl}</span>
          <h3>${esc(t.eventName)}</h3>
          <div class="fact">${icon('clock')}<span>${esc(d.full)}, ${d.time}</span></div>
          <div class="fact">${icon('pin')}<span>${esc(t.venue)}</span></div>
          <p><b>${esc(t.typeName)}</b> <span class="muted">${esc(t.zone)}, ${String(t.seatInfo).startsWith('#') ? 'vé ' : 'ghế '}${esc(t.seatInfo)}</span></p>
          <a class="btn btn-primary" style="justify-self:start;margin-top:6px" href="/Account/Ticket/${t.id}">Xem vé</a></div></article>`;
    }).join('');
    list.forEach(t => API.blobUrl(`/api/tickets/${t.id}/qr`).then(url => {
      const ph = el.querySelector(`[data-qr="${t.id}"]`); if (ph) ph.outerHTML = `<img src="${url}" alt="Mã QR vé ${esc(t.seatInfo)}">`;
    }).catch(() => { }));
  }

  async function loadOrders() {
    const el = $('pane-orders'), list = await API.call('/api/bookings/mine');
    if (!list.length) { el.innerHTML = empty('Chưa có đơn hàng nào.', 'Đơn của bạn sẽ hiện ở đây sau khi giữ vé.'); return; }
    el.innerHTML = list.map(b => {
      const [lbl, cls] = BSTAT[b.status] || [b.status, 'b-off'], pending = b.status === 'Pending';
      return `<div class="order"><div><h3 style="font-size:1.05rem">${esc(b.eventName)}</h3>
          <p class="muted">${b.quantity} vé ${esc(b.typeName)}, ${esc(b.zone)}</p>
          ${pending ? `<p class="muted">Giữ tới ${new Date(b.expiresAt).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' })}</p>` : ''}</div>
        <div class="order-r"><b>${money(b.totalAmount)}</b><span class="badge ${cls}">${lbl}</span>
          ${pending ? `<a class="btn btn-primary" href="/Event/Checkout/${b.id}">Thanh toán</a>` : ''}${b.status === 'Paid' ? `<a class="btn btn-ghost" href="/Account/Order/${b.id}">Xem vé</a>` : ''}${b.status === 'Expired' ? `<a class="btn btn-ghost" href="/Event/Checkout/${b.id}">Đã trả tiền? Kiểm tra</a>` : ''}</div></div>`;
    }).join('');
  }

  $('pane-tickets').addEventListener('click', e => {
    const b = e.target.closest('button[data-cap]'), img = b?.querySelector('img'); if (!img) return;
    $('qrBig').src = img.src; $('qrCap').textContent = b.dataset.cap; dlg.showModal();
  });
  $('qrClose').addEventListener('click', () => dlg.close());
  dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });

  $('pane-orders').addEventListener('click', async e => {
    const b = e.target.closest('button[data-pay]'); if (!b) return;
    busy(b, true, 'Đang xử lý…');
    try { await payBooking(b.dataset.pay); toast('Thanh toán thành công. Vé đã được gửi qua email.', 'ok'); await Promise.all([loadTickets(), loadOrders()]); show('tickets'); }
    catch (err) { toast(err.message, 'error'); busy(b, false); }
  });

  $('pane-tickets').innerHTML = '<div class="skel" style="height:190px;border-radius:22px"></div>';
  Promise.all([loadTickets(), loadOrders()]).catch(err => toast(err.message, 'error'));
})();
