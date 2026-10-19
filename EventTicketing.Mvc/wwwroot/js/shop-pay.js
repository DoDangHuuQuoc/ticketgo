(() => {
  'use strict';
  if (!requireLogin()) return;
  const root = document.getElementById('app'), id = root.dataset.orderId;
  const qs = new URLSearchParams(location.search);
  const cancelledBack = qs.get('cancel') === 'true' || qs.get('status') === 'CANCELLED';   // khách bấm Huỷ ở trang payOS
  const returned = !!(qs.get('orderCode') || qs.get('status')) && !cancelledBack;           // payOS đưa khách quay về kèm tham số
  const short = () => id.slice(0, 8).toUpperCase();
  let o = null, timer = null, clock = null;

  const stopAll = () => { clearInterval(timer); clearInterval(clock); };
  const items = () => `<ul class="sh-oi">${o.items.map(i => `<li><span>${esc(i.productName)}${i.variantName ? ' (' + esc(i.variantName) + ')' : ''} × ${i.quantity}</span><span>${money(i.unitPrice * i.quantity)}</span></li>`).join('')}</ul>
    ${o.vipDiscount > 0 ? `<div class="sh-sumrow disc"><span>Ưu đãi khách VIP</span><span>−${money(o.vipDiscount)}</span></div>` : ''}${o.couponCode ? `<div class="sh-sumrow disc"><span>Mã ${esc(o.couponCode)}</span><span>−${money(o.discountAmount)}</span></div>` : ''}<div class="sh-total"><span>Tổng cộng</span><b>${money(o.total)}</b></div>`;
  const shell = (title, body) => { root.innerHTML = `<section class="sh-paycard"><h1>${title}</h1><p class="muted">Đơn #${esc(short())}</p>${body}</section>`; };

  function paid() {
    stopAll();
    root.innerHTML = `<div class="sh-paycard sh-end"><div class="sh-ok" role="status">${icon('check', 34)}</div><h1>Thanh toán thành công</h1><p>Đang chuyển sang trang đặt hàng thành công…</p></div>`;
    setTimeout(() => location.replace('/Shop/Success/' + id), 900);
  }
  function closed(msg) {
    stopAll();
    shell('Đơn không còn chờ thanh toán', `<p>${esc(msg)}</p><div class="sh-payact"><a class="btn btn-primary" href="/Shop">Quay lại cửa hàng</a><a class="btn btn-ghost" href="/Shop?tab=orders">Đơn của tôi</a></div>`);
  }
  const left = () => o.expiresAt ? Math.max(0, Math.floor((new Date(/Z|[+-]\d\d:\d\d$/.test(o.expiresAt) ? o.expiresAt : o.expiresAt + 'Z') - Date.now()) / 1000)) : 900;
  const clockTxt = () => { const s = left(); return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };

  let qr = null, qrErr = '';
  async function loadQr() {
    try { qr = await API.call(`/api/shop/orders/${id}/payos/qr`, { method: 'POST' }); qrErr = ''; }
    catch (err) { qr = null; qrErr = err.message; }
  }
  const line = (k, v, raw) => v ? `<div class="sh-qrl"><span>${k}</span><b>${esc(v)}</b><button type="button" class="btn btn-ghost btn-sm" data-copy="${esc(raw ?? v)}">Sao chép</button></div>` : '';

  function pending() {
    const qrHtml = qr ? `<div class="sh-qr"><div><img class="sh-qrimg" src="${qr.image}" alt="Mã QR thanh toán ${money(qr.amount)}" width="200" height="200"><p class="sh-note" style="text-align:center">Quét bằng app ngân hàng bất kỳ</p></div>
        <div class="sh-qrinfo">${line('Ngân hàng', qr.bankName)}${line('Số tài khoản', qr.accountNo)}${line('Chủ tài khoản', qr.accountName)}${line('Số tiền', money(qr.amount), String(qr.amount))}${line('Nội dung', qr.content)}</div></div>
        <p class="sh-note">Mã QR đã điền sẵn số tiền và nội dung. Chuyển khoản xong bạn không cần bấm gì: trang sẽ tự chuyển sang đơn hàng. <a href="${esc(qr.checkoutUrl)}" target="_blank" rel="noopener">Không quét được? Mở trang payOS ở tab mới</a>.</p>`
      : `<p class="sh-warn">${esc(qrErr || 'Đang tạo mã QR…')}</p><div class="sh-payact"><button type="button" class="btn btn-ghost" id="retry">Thử lại</button></div>`;
    shell('Thanh toán đơn lưu niệm', `${items()}
      ${cancelledBack ? '<p class="sh-warn">Bạn đã huỷ thanh toán ở payOS. Có thể quét mã bên dưới để trả tiền hoặc huỷ đơn.</p>' : ''}
      ${qrHtml}
      <div class="sh-wait" role="status" aria-live="polite"><span class="sh-spin" aria-hidden="true"></span><span><b>Đang chờ thanh toán · còn <span id="clk">${clockTxt()}</span></b><small>Tự chuyển sang đơn hàng ngay khi nhận được tiền. Quá hạn đơn tự huỷ.</small></span></div>
      <div class="sh-payact"><button type="button" class="btn btn-ghost" id="cancel">Huỷ đơn</button></div>`);
    document.getElementById('cancel').addEventListener('click', cancelOrder);
    document.getElementById('retry')?.addEventListener('click', async () => { await loadQr(); pending(); });
    clearInterval(clock);
    clock = setInterval(() => { const c = document.getElementById('clk'); if (c) c.textContent = clockTxt(); if (left() <= 0) { clearInterval(clock); poll(); } }, 1000);
  }
  root.addEventListener('click', e => {
    const b = e.target.closest('[data-copy]'); if (!b) return;
    navigator.clipboard?.writeText(b.dataset.copy).then(() => toast('Đã sao chép', 'ok')).catch(() => { });
  });

  async function cancelOrder(e) {
    if (!confirm('Huỷ đơn hàng này?')) return;
    const btn = e.currentTarget; busy(btn, true, 'Đang huỷ…');
    try { await API.call(`/api/shop/orders/${id}/cancel`, { method: 'POST' }); closed('Đơn đã được huỷ, hàng đã trả về kho.'); }
    catch (err) { toast(err.message, 'error'); busy(btn, false); poll(); }
  }

  let polling = false;
  async function poll() {
    if (polling) return; polling = true;
    try {
      const r = await API.call(`/api/shop/orders/${id}/payos/check`, { method: 'POST' });
      if (r.paid) { o = await API.call('/api/shop/orders/' + id); paid(); return; }
      const fresh = await API.call('/api/shop/orders/' + id);
      if (fresh.status === 'Cancelled') { o = fresh; closed('Đơn đã bị huỷ (quá hạn thanh toán hoặc bạn đã huỷ). Bạn có thể đặt lại ở cửa hàng.'); }
    } catch { /* lỗi mạng thoáng qua: lần dò sau thử lại */ }
    finally { polling = false; }
  }

  (async () => {
    try { o = await API.call('/api/shop/orders/' + id); }
    catch (err) { root.innerHTML = `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Không mở được đơn hàng</p><p>${esc(err.message)}</p><p><a class="btn btn-primary" href="/Shop">Về cửa hàng</a></p></div>`; return; }
    if (o.paymentStatus === 'Paid') return paid();
    if (o.paymentMethod !== 'PAYOS') return closed('Đơn này thanh toán khi nhận hàng, không cần trả trước.');
    if (o.status === 'Cancelled') return closed('Đơn đã bị huỷ (quá hạn thanh toán hoặc bạn đã huỷ). Bạn có thể đặt lại ở cửa hàng.');
    await loadQr();
    pending();
    if (returned) poll();                       // vừa quay về từ payOS: hỏi ngay, không chờ webhook
    timer = setInterval(poll, 3000);            // và tiếp tục hỏi mỗi 3 giây (webhook tới trước thì đơn tự Paid)
  })();
})();
