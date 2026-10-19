(() => {
  'use strict';
  if (!requireLogin()) return;
  const root = document.getElementById('app'), id = root.dataset.orderId;
  const when = iso => new Date(/Z|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z').toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
  (async () => {
    let o;
    try { o = await API.call('/api/shop/orders/' + id); }
    catch (err) { root.innerHTML = `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Không mở được đơn hàng</p><p>${esc(err.message)}</p><p><a class="btn btn-primary" href="/Shop">Về cửa hàng</a></p></div>`; return; }
    if (o.paymentMethod === 'PAYOS' && o.paymentStatus !== 'Paid') { location.replace('/Shop/Pay/' + id); return; }   // chưa trả tiền thì về trang thanh toán
    const online = o.paymentMethod === 'PAYOS';
    root.innerHTML = `<section class="sh-paycard">
      <div class="sh-ok" role="status">${icon('check', 34)}</div>
      <h1>Đặt hàng thành công</h1>
      <p class="muted">Đơn #${esc(o.id.slice(0, 8).toUpperCase())} · ${when(o.createdAt)}</p>
      <p>${online ? `Đã thanh toán ${money(o.total)} qua payOS.` : `Bạn thanh toán ${money(o.total)} khi nhận hàng.`} Cửa hàng sẽ xác nhận và giao cho <b>${esc(o.receiverName)}</b>.</p>
      <ul class="sh-oi">${o.items.map(i => `<li><span>${esc(i.productName)}${i.variantName ? ' (' + esc(i.variantName) + ')' : ''} × ${i.quantity}</span><span>${money(i.unitPrice * i.quantity)}</span></li>`).join('')}</ul>
      ${o.vipDiscount > 0 ? `<div class="sh-sumrow disc"><span>Ưu đãi khách VIP</span><span>−${money(o.vipDiscount)}</span></div>` : ''}${o.couponCode ? `<div class="sh-sumrow disc"><span>Mã ${esc(o.couponCode)}</span><span>−${money(o.discountAmount)}</span></div>` : ''}<div class="sh-total"><span>Tổng cộng</span><b>${money(o.total)}</b></div>
      <p class="sh-ship">Giao tới: ${esc(o.receiverName)} · ${esc(o.phone)} · ${esc(o.address)}</p>
      <div class="sh-payact"><a class="btn btn-primary" href="/Shop/Order/${o.id}">Xem chi tiết đơn</a><a class="btn btn-ghost" href="/Shop?tab=orders">Đơn của tôi</a><a class="btn btn-ghost" href="/Shop">Tiếp tục mua sắm</a></div></section>`;
  })();
})();
