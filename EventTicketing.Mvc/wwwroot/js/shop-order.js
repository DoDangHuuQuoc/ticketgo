(() => {
  'use strict';
  if (!requireLogin()) return;
  const root = document.getElementById('app'), id = root.dataset.orderId;
  const iso = s => (/Z|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z');
  const when = s => new Date(iso(s)).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
  const code = o => o.id.slice(0, 8).toUpperCase();
  let o = null, revs = {}, editing = null;
  const rkey = i => i.productId + '|' + (i.variantName || '');
  const qsOrder = new URLSearchParams(location.search);

  const BANNER = {
    New: ['Chờ xác nhận', 'Đơn hàng đã được ghi nhận và đang chờ cửa hàng xác nhận.', 'wait'],
    Confirmed: ['Đã xác nhận', 'Cửa hàng đã xác nhận và đang chuẩn bị hàng cho bạn.', 'go'],
    Shipping: ['Đang giao hàng', 'Đơn hàng đang trên đường giao đến bạn.', 'go'],
    Done: ['Hoàn tất', 'Đơn hàng đã giao thành công. Cảm ơn bạn đã mua sắm!', 'ok'],
    Cancelled: ['Đã huỷ', 'Đơn hàng đã bị huỷ, hàng đã được trả về kho.', 'off']
  };

  /* Các bước trạng thái như Shopee. Đơn payOS có thêm bước "Đã thanh toán"; đơn huỷ chỉ còn "Đặt hàng → Đã huỷ" */
  function steps() {
    const online = o.paymentMethod === 'PAYOS', paid = o.paymentStatus === 'Paid';
    if (o.status === 'Cancelled') return [{ t: 'Đặt hàng', time: o.createdAt, done: true }, { t: 'Đã huỷ', done: true, bad: true }];
    const rank = { New: 0, Confirmed: 1, Shipping: 2, Done: 3 }[o.status] ?? 0;
    const list = [{ t: 'Đặt hàng', time: o.createdAt, done: true }];
    if (online) list.push({ t: 'Đã thanh toán', time: o.paidAt, done: paid });
    list.push({ t: 'Đã xác nhận', done: rank >= 1 }, { t: 'Đang giao', done: rank >= 2 }, { t: 'Hoàn tất', done: rank >= 3 });
    const cur = list.findIndex(s => !s.done);
    if (cur > 0) list[cur].cur = true;
    return list;
  }
  const stepsHtml = () => `<ol class="sh-steps">${steps().map(s => `<li class="${s.done ? 'done' : ''} ${s.cur ? 'cur' : ''} ${s.bad ? 'bad' : ''}"><span class="sh-sdot">${s.done ? (s.bad ? icon('x', 18) : icon('check', 18)) : ''}</span><b>${esc(s.t)}</b>${s.time ? `<small>${when(s.time)}</small>` : ''}</li>`).join('')}</ol>`;

  const thumb = i => {
    if (i.imageUrl) return `<img src="${esc(i.imageUrl)}" alt="${esc(i.productName)}" loading="lazy">`;
    if (i.category === 'LightStick') { const h = Math.abs(hash(i.productName || '')) % 360; return `<svg class="sh-svg" viewBox="0 0 120 120" aria-hidden="true"><circle cx="60" cy="38" r="30" fill="hsla(${h},90%,60%,.3)"/><rect x="52" y="12" width="16" height="54" rx="8" fill="hsl(${h},92%,64%)"/><rect x="49" y="64" width="22" height="9" rx="3" fill="#c9d3ee"/><rect x="54" y="73" width="12" height="34" rx="5" fill="#8fa0cf"/></svg>`; }
    return `<span class="sh-ph" aria-hidden="true">${esc((i.productName || '?').trim().charAt(0).toUpperCase())}</span>`;
  };


  /* ---------- Đánh giá từng món (chỉ đơn Hoàn tất) ---------- */
  function reviewBlock(i) {
    if (o.status !== 'Done') return '';
    const k = rkey(i), r = revs[k];
    if (editing === k) {
      return `<form class="sh-rvform" data-rvform="${esc(k)}" novalidate>
        <div class="rv-fl"><span>Chất lượng sản phẩm</span>${ShopRev.picker('s', r ? r.stars : 0)}</div>
        <textarea maxlength="1000" rows="4" placeholder="Hãy chia sẻ nhận xét của bạn về sản phẩm này để giúp người mua khác nhé (không bắt buộc)">${esc(r?.content || '')}</textarea>
        <div class="rv-fa"><small class="muted rv-cnt">${(r?.content || '').length}/1000</small><span class="grow"></span>
          <button type="button" class="btn btn-ghost btn-sm" data-rv-cancel>Huỷ</button><button type="submit" class="btn btn-primary btn-sm">${r ? 'Lưu thay đổi' : 'Gửi đánh giá'}</button></div></form>`;
    }
    if (!r) return `<div class="sh-rvrow"><span class="muted">Bạn chưa đánh giá sản phẩm này</span><button type="button" class="btn btn-primary btn-sm" data-rv-open="${esc(k)}">Đánh giá</button></div>`;
    return `<div class="sh-rvbox"><div class="rv-top">${ShopRev.stars(r.stars)}<b>${ShopRev.LABEL[r.stars]}</b><small class="muted">${ShopRev.day(r.createdAt)}${r.edited ? ' · đã chỉnh sửa' : ''}</small></div>
      ${r.isHidden ? '<p class="rv-hid">Đánh giá này đã bị cửa hàng ẩn vì vi phạm quy định nên không hiển thị công khai.</p>' : ''}
      ${r.content ? `<p class="rv-tx">${esc(r.content)}</p>` : ''}
      ${r.adminReply ? `<div class="rv-reply"><b>Phản hồi của cửa hàng</b><p>${esc(r.adminReply)}</p></div>` : ''}
      <div class="rv-acts"><button type="button" class="rv-lk" data-rv-open="${esc(k)}">Sửa</button><button type="button" class="rv-lk danger" data-rv-del="${r.id}">Xoá</button></div></div>`;
  }
  const pendingReviews = () => o.status === 'Done' ? o.items.filter(i => !revs[rkey(i)]).length : 0;
  async function loadReviews() {
    revs = {};
    if (o.status !== 'Done') return;
    try { (await API.call(`/api/shop/orders/${id}/reviews`)).forEach(r => { revs[r.productId + '|' + (r.variantName || '')] = r; }); } catch { /* không có đánh giá */ }
  }

  function paint() {
    const b = BANNER[o.status] || [o.status, '', 'wait'];
    const online = o.paymentMethod === 'PAYOS', paid = o.paymentStatus === 'Paid';
    const waitPay = online && o.paymentStatus === 'Pending' && o.status === 'New';
    const canCancel = o.status === 'New' && !paid;
    const count = o.items.reduce((s, i) => s + i.quantity, 0);
    root.innerHTML = `
      <div class="sh-odtop"><a class="sh-back" href="/Shop?tab=orders">‹ Đơn của tôi</a><span class="sh-odid">MÃ ĐƠN HÀNG: <b>#${esc(code(o))}</b> <span class="sh-odst ${b[2]}">${esc(b[0])}</span></span></div>
      <section class="sh-odcard sh-odban ${b[2]}"><div><b>${esc(b[0])}</b><p>${esc(waitPay ? 'Đơn đang chờ bạn thanh toán qua payOS. Quá hạn chưa trả đơn sẽ tự huỷ.' : b[1])}</p></div>${waitPay ? `<a class="btn btn-primary" href="/Shop/Pay/${o.id}">Thanh toán ngay</a>` : pendingReviews() ? `<button type="button" class="btn btn-gold" data-rv-first>Đánh giá ngay (${pendingReviews()})</button>` : ''}</section>
      <section class="sh-odcard">${stepsHtml()}</section>
      <section class="sh-odcard"><h2>Địa chỉ nhận hàng</h2>
        <p class="sh-odname"><b>${esc(o.receiverName)}</b> <span class="muted">${esc(o.phone)}</span></p><p>${esc(o.address)}</p>${o.note ? `<p class="muted">Ghi chú: ${esc(o.note)}</p>` : ''}</section>
      <section class="sh-odcard"><h2>Sản phẩm <small class="muted">(${count})</small></h2>
        <div class="sh-odlist">${o.items.map(i => `<div class="sh-odrow"><div class="sh-odit"><a class="sh-odth" href="/Shop?p=${i.productId}" aria-label="Xem ${esc(i.productName)}">${thumb(i)}</a>
          <div class="sh-odin"><a href="/Shop?p=${i.productId}"><b>${esc(i.productName)}</b></a>${i.variantName ? `<span class="sh-var">Phân loại hàng: ${esc(i.variantName)}</span>` : ''}<span class="muted">× ${i.quantity}</span></div>
          <div class="sh-odpr">${i.quantity > 1 ? `<small class="muted">${money(i.unitPrice)} × ${i.quantity}</small>` : ''}<b>${money(i.unitPrice * i.quantity)}</b></div></div>${reviewBlock(i)}</div>`).join('')}</div>
        <div class="sh-odsum">
          <div><span>Tổng tiền hàng</span><span>${money(o.subtotal)}</span></div>
          ${o.vipDiscount > 0 ? `<div class="disc"><span>Ưu đãi khách VIP</span><span>−${money(o.vipDiscount)}</span></div>` : ''}
          ${o.discountAmount > 0 ? `<div class="disc"><span>Giảm giá${o.couponCode ? ` (mã ${esc(o.couponCode)})` : ''}</span><span>−${money(o.discountAmount)}</span></div>` : ''}
          <div><span>Phí vận chuyển</span><span>Do cửa hàng báo khi giao</span></div>
          <div class="tot"><span>Thành tiền</span><b>${money(o.total)}</b></div>
          <div><span>Phương thức thanh toán</span><span>${online ? 'payOS (chuyển khoản QR)' : 'Thanh toán khi nhận hàng (COD)'}</span></div>
          <div><span>Trạng thái thanh toán</span><span class="badge ${paid ? 'b-go' : online && o.status !== 'Cancelled' ? 'b-wait' : 'b-off'}">${paid ? 'Đã thanh toán' : online && o.status !== 'Cancelled' ? 'Chờ thanh toán' : o.status === 'Cancelled' ? 'Không thanh toán' : 'Thu tiền khi giao'}</span></div>
        </div></section>
      <section class="sh-odcard"><h2>Thông tin đơn hàng</h2>
        <div class="sh-odsum">
          <div><span>Mã đơn hàng</span><span><b>#${esc(code(o))}</b> <button type="button" class="sh-copy" id="copyId">Sao chép</button></span></div>
          <div><span>Thời gian đặt hàng</span><span>${when(o.createdAt)}</span></div>
          ${o.paidAt ? `<div><span>Thời gian thanh toán</span><span>${when(o.paidAt)}</span></div>` : ''}
        </div></section>
      <div class="sh-odact"><a class="btn btn-ghost" href="/Shop">Tiếp tục mua sắm</a>
        ${o.status === 'Done' || o.status === 'Cancelled' ? '<button type="button" class="btn btn-primary" id="rebuy">Mua lại</button>' : ''}
        ${canCancel ? '<button type="button" class="btn btn-ghost danger" id="cancel">Huỷ đơn</button>' : ''}</div>`;
    document.title = `Đơn #${code(o)} – TicketGo`;
  }

  async function load() {
    try { o = await API.call('/api/shop/orders/' + id); }
    catch (err) { root.innerHTML = `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Không mở được đơn hàng</p><p>${esc(err.message)}</p><p><a class="btn btn-primary" href="/Shop?tab=orders">Về đơn của tôi</a></p></div>`; return; }
    await loadReviews(); paint();
    if (qsOrder.get('review') && pendingReviews()) { qsOrder.delete('review'); const f = o.items.find(i => !revs[rkey(i)]); if (f) { editing = rkey(f); paint(); document.querySelector('.sh-rvform')?.scrollIntoView({ block: 'center' }); } }
  }

  /* Mua lại: ghép từng món (theo tên phân loại) với sản phẩm hiện tại, bỏ vào giỏ rồi về cửa hàng */
  async function rebuy(btn) {
    busy(btn, true, 'Đang thêm…');
    try {
      const products = await API.call('/api/shop/products');
      let cart; try { cart = JSON.parse(localStorage.getItem('shopCart') || '[]'); if (!Array.isArray(cart)) cart = []; } catch { cart = []; }
      let added = 0;
      for (const i of o.items) {
        const p = products.find(x => x.id === i.productId); if (!p) continue;
        const withV = Array.isArray(p.variants) && p.variants.length > 0, v = withV ? p.variants.find(x => x.name === i.variantName) : null;
        if (withV && !v) continue;
        const stock = v ? v.stock : p.stock; if (stock <= 0) continue;
        const l = cart.find(c => c.id === p.id && (c.vid || null) === (v ? v.id : null)), have = l ? l.qty : 0, want = Math.min(i.quantity, Math.min(stock, 20) - have);
        if (want <= 0) continue;
        if (l) l.qty += want; else cart.push({ id: p.id, vid: v ? v.id : null, qty: want });
        added++;
      }
      if (!added) { toast('Các món trong đơn này hiện không còn hàng', 'error'); busy(btn, false); return; }
      localStorage.setItem('shopCart', JSON.stringify(cart));
      location.href = '/Shop';
    } catch (err) { toast(err.message, 'error'); busy(btn, false); }
  }

  root.addEventListener('click', async e => {
    const t = e.target;
    if (t.closest('#copyId')) { try { await navigator.clipboard.writeText(code(o)); toast('Đã sao chép mã đơn', 'ok'); } catch { toast('Mã đơn: ' + code(o), 'info'); } return; }
    const ro = t.closest('[data-rv-open]'); if (ro) { editing = ro.dataset.rvOpen; paint(); const f = root.querySelector('.sh-rvform'); f?.scrollIntoView({ block: 'center', behavior: 'smooth' }); f?.querySelector('textarea')?.focus(); return; }
    if (t.closest('[data-rv-first]')) { const f = o.items.find(i => !revs[rkey(i)]); if (f) { editing = rkey(f); paint(); const fm = root.querySelector('.sh-rvform'); fm?.scrollIntoView({ block: 'center', behavior: 'smooth' }); fm?.querySelector('textarea')?.focus(); } return; }
    if (t.closest('[data-rv-cancel]')) { editing = null; paint(); return; }
    const st = t.closest('[data-star]'); if (st) { ShopRev.setPick(st.closest('.rv-pick'), +st.dataset.star); return; }
    const rd = t.closest('[data-rv-del]');
    if (rd && confirm('Xoá đánh giá này?')) {
      busy(rd, true, 'Đang xoá…');
      try { await API.call(`/api/shop/reviews/${rd.dataset.rvDel}`, { method: 'DELETE' }); ShopRev.invalidate(); toast('Đã xoá đánh giá', 'ok'); await loadReviews(); paint(); }
      catch (err) { toast(err.message, 'error'); busy(rd, false); }
      return;
    }
    const rb = t.closest('#rebuy'); if (rb) { rebuy(rb); return; }
    const c = t.closest('#cancel');
    if (c && confirm('Huỷ đơn hàng này?')) {
      busy(c, true, 'Đang huỷ…');
      try { const r = await API.call(`/api/shop/orders/${id}/cancel`, { method: 'POST' }); toast(r.message, 'ok'); await load(); }
      catch (err) { toast(err.message, 'error'); busy(c, false); }
    }
  });
  root.addEventListener('input', e => { if (e.target.matches('.sh-rvform textarea')) e.target.closest('.sh-rvform').querySelector('.rv-cnt').textContent = e.target.value.length + '/1000'; });
  root.addEventListener('submit', async e => {
    const f = e.target.closest('.sh-rvform'); if (!f) return; e.preventDefault();
    const k = f.dataset.rvform, it = o.items.find(i => rkey(i) === k); if (!it) return;
    const stars = +(f.querySelector('.rv-pick').dataset.val || (revs[k]?.stars ?? 0));
    if (!stars) { toast('Vui lòng chọn số sao', 'error'); return; }
    const btn = f.querySelector('[type=submit]'); busy(btn, true, 'Đang gửi…');
    try {
      const r = await API.call(`/api/shop/orders/${id}/reviews`, { method: 'PUT', body: { productId: it.productId, variantName: it.variantName || null, stars, content: f.querySelector('textarea').value } });
      ShopRev.invalidate(it.productId); toast(r.message, 'ok'); editing = null; await loadReviews(); paint();
    } catch (err) { toast(err.message, 'error'); busy(btn, false); }
  });
  load();
})();
