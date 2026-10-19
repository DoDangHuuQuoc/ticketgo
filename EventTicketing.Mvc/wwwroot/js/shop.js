(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const CAT = { LightStick: 'Light stick', Apparel: 'Áo & thời trang', Accessory: 'Phụ kiện', Other: 'Khác' };
  const STATUS = { New: ['Chờ xác nhận', 'b-wait'], Confirmed: ['Đã xác nhận', 'b-queue'], Shipping: ['Đang giao', 'b-queue'], Done: ['Hoàn tất', 'b-go'], Cancelled: ['Đã huỷ', 'b-off'] };
  const MAXQ = 20;
  let products = [], cat = '', ordersLoaded = false, cart = loadCart(), payosOn = false, applied = null, wallet = [], cpBusy = false;
  let pm = null;   // sản phẩm đang mở trong cửa sổ chi tiết: { p, vid, qty }
  const form = { name: localStorage.getItem('name') || '', phone: '', address: '', note: '', pay: 'COD', cp: '' };
  const PAYST = { Paid: ['Đã thanh toán', 'b-go'], Pending: ['Chờ thanh toán', 'b-wait'], Unpaid: ['Thanh toán khi nhận', 'b-off'] };

  /* ---------- Giỏ hàng lưu ở trình duyệt: mỗi dòng = sản phẩm + phân loại (vid) + số lượng ---------- */
  function loadCart() {
    try { const v = JSON.parse(localStorage.getItem('shopCart') || '[]'); return Array.isArray(v) ? v.filter(x => x && x.id && x.qty > 0).map(x => ({ id: x.id, vid: x.vid || null, qty: x.qty })) : []; }
    catch { return []; }
  }
  function saveCart() { try { localStorage.setItem('shopCart', JSON.stringify(cart)); } catch { /* bỏ qua */ } badge(); }
  const keyOf = (id, vid) => id + '|' + (vid || '');
  const hasV = p => Array.isArray(p.variants) && p.variants.length > 0;
  const variantOf = (p, vid) => (vid && hasV(p)) ? p.variants.find(v => v.id === vid) || null : null;
  const stockOf = (p, v) => v ? v.stock : p.stock;
  const priceOf = (p, v) => v ? v.price : p.price;
  const cap = (p, v) => Math.max(0, Math.min(stockOf(p, v), MAXQ));
  const qtyOf = (id, vid) => cart.find(c => c.id === id && (c.vid || null) === (vid || null))?.qty || 0;
  function priceText(p) {
    if (!hasV(p)) return money(p.price);
    const ps = p.variants.map(v => v.price), lo = Math.min(...ps), hi = Math.max(...ps);
    return lo === hi ? money(lo) : `${money(lo)} – ${money(hi)}`;
  }

  /* ---------- Hình sản phẩm: ảnh thật, hoặc hình light stick vẽ sẵn ---------- */
  function art(p) {
    if (p.imageUrl) return `<img src="${esc(p.imageUrl)}" alt="${esc(p.name)}" loading="lazy">`;
    if (p.category === 'LightStick') {
      const h = Math.abs(hash(p.name || '')) % 360;
      return `<svg class="sh-svg" viewBox="0 0 120 120" aria-hidden="true"><circle cx="60" cy="38" r="32" fill="hsla(${h},90%,60%,.28)"/><circle cx="60" cy="38" r="22" fill="hsla(${h},95%,65%,.35)"/><rect x="52" y="12" width="16" height="54" rx="8" fill="hsl(${h},92%,64%)"/><rect x="49" y="64" width="22" height="9" rx="3" fill="#c9d3ee"/><rect x="54" y="73" width="12" height="34" rx="5" fill="#8fa0cf"/></svg>`;
    }
    return `<span class="sh-ph" aria-hidden="true">${esc((p.name || '?').trim().charAt(0).toUpperCase())}</span>`;
  }
  // Ảnh lỗi (link hỏng) thì thay bằng hình vẽ sẵn
  document.addEventListener('error', e => {
    const img = e.target; if (!(img instanceof HTMLImageElement)) return;
    const holder = img.closest('[data-id]'); const p = products.find(x => x.id === holder?.dataset.id);
    if (p) { const t = document.createElement('div'); t.innerHTML = art({ ...p, imageUrl: '' }); img.replaceWith(...t.childNodes); }
  }, true);

  /* ---------- Tab ---------- */
  function show(k, push) {
    const shop = k !== 'orders';
    $('tab-shop').setAttribute('aria-selected', shop); $('tab-orders').setAttribute('aria-selected', !shop);
    $('pane-shop').hidden = !shop; $('pane-orders').hidden = shop;
    if (push) history.replaceState(null, '', shop ? '/Shop' : '/Shop?tab=orders');
    if (!shop) loadOrders();
  }
  $('tab-shop').addEventListener('click', () => show('shop', true));
  $('tab-orders').addEventListener('click', () => show('orders', true));

  /* ---------- Sản phẩm ---------- */
  function paintChips() {
    const cats = [...new Set(products.map(p => p.category))];
    $('chips').innerHTML = cats.length < 2 ? '' :
      [['', 'Tất cả'], ...cats.map(c => [c, CAT[c] || 'Khác'])].map(([k, l]) => `<button type="button" class="sh-chip" data-cat="${esc(k)}" aria-pressed="${cat === k}">${esc(l)}</button>`).join('');
  }
  function card(p) {
    const out = p.stock <= 0, low = !out && p.stock <= 5;
    const btn = out ? 'Hết hàng' : hasV(p) ? 'Chọn phân loại' : 'Thêm vào giỏ';
    return `<article class="sh-card ${out ? 'out' : ''}" data-id="${p.id}" data-open="${p.id}" tabindex="0" role="button" aria-label="Xem chi tiết ${esc(p.name)}"><div class="sh-img">${art(p)}${out ? '<span class="sh-tag out">Hết hàng</span>' : low ? `<span class="sh-tag">Chỉ còn ${p.stock}</span>` : ''}</div>
      <div class="sh-body"><span class="sh-cat">${esc(CAT[p.category] || 'Khác')}</span><h3>${esc(p.name)}</h3>${p.reviews ? `<div class="sh-rate">${ShopRev.stars(p.rating)}<small>${p.rating.toFixed(1)} (${p.reviews})</small></div>` : ''}${p.description ? `<p class="sh-desc">${esc(p.description)}</p>` : ''}
      <div class="sh-foot"><b class="sh-price">${priceText(p)}</b><button type="button" class="btn btn-primary btn-sm" data-quick="${p.id}" ${out ? 'disabled' : ''}>${btn}</button></div></div></article>`;
  }
  function paintGrid() {
    const list = products.filter(p => !cat || p.category === cat);
    $('grid').innerHTML = list.length ? list.map(card).join('')
      : `<div class="sh-empty" style="grid-column:1/-1"><p class="strong" style="color:var(--ink)">${products.length ? 'Chưa có sản phẩm trong mục này.' : 'Cửa hàng đang cập nhật sản phẩm.'}</p><p>Bạn quay lại sau nhé.</p></div>`;
  }
  async function loadProducts() {
    try { products = await API.call('/api/shop/products'); }
    catch (err) { $('grid').innerHTML = `<div class="sh-empty" style="grid-column:1/-1"><p>Không tải được sản phẩm.</p><p>${esc(err.message)}</p></div>`; return; }
    // bỏ sản phẩm đã ngừng bán / phân loại không còn
    cart = cart.filter(c => { const p = products.find(x => x.id === c.id); return p && (!hasV(p) || variantOf(p, c.vid)); });
    saveCart(); paintChips(); paintGrid(); paintCart();
    if (pm) { const p = products.find(x => x.id === pm.p.id); if (p) { pm.p = p; paintModal(); } else closeModal(); }
  }

  /* ---------- Cửa sổ chi tiết sản phẩm + chọn phân loại (kiểu Shopee) ---------- */
  document.body.insertAdjacentHTML('beforeend', '<div class="sh-md" id="md" hidden></div>');
  function openModal(id) {
    const p = products.find(x => x.id === id); if (!p) return;
    pm = { p, vid: null, qty: 1 };
    paintModal(); $('md').hidden = false; document.body.classList.add('sh-lock');
    $('md').querySelector('.sh-mdx')?.focus();
  }
  function closeModal() { pm = null; $('md').hidden = true; $('md').innerHTML = ''; document.body.classList.remove('sh-lock'); }
  function paintModal() {
    if (!pm) return;
    const { p } = pm, v = variantOf(p, pm.vid), withV = hasV(p);
    const stock = withV && !v ? p.stock : stockOf(p, v), max = withV && !v ? MAXQ : cap(p, v);
    pm.qty = Math.max(1, Math.min(pm.qty, Math.max(1, max)));
    const out = withV && v ? v.stock <= 0 : p.stock <= 0;
    const chips = withV ? `<div class="sh-vrow"><span class="sh-vl">Phân loại</span><div class="sh-vchips" role="radiogroup" aria-label="Phân loại hàng">${p.variants.map(x => `<button type="button" class="sh-vchip" role="radio" data-v="${x.id}" aria-checked="${pm.vid === x.id}" ${x.stock <= 0 ? 'disabled' : ''}>${esc(x.name)}${x.stock <= 0 ? ' · hết hàng' : ''}</button>`).join('')}</div></div>` : '';
    $('md').innerHTML = `<div class="sh-mdbg" data-mdclose></div>
      <div class="sh-mdc" data-id="${p.id}" role="dialog" aria-modal="true" aria-label="${esc(p.name)}">
        <button type="button" class="sh-x sh-mdx" data-mdclose aria-label="Đóng">×</button>
        <div class="sh-mdimg">${art(p)}</div>
        <div class="sh-mdinfo">
          <span class="sh-cat">${esc(CAT[p.category] || 'Khác')}</span>
          <h2>${esc(p.name)}</h2>
          ${p.reviews ? `<div class="sh-rate">${ShopRev.stars(p.rating)}<small>${p.rating.toFixed(1)} · ${p.reviews} đánh giá</small></div>` : ''}
          <div class="sh-mdprice">${withV && !v ? priceText(p) : money(priceOf(p, v))}</div>
          ${chips}
          <div class="sh-vrow"><span class="sh-vl">Số lượng</span><div class="sh-vq">
            <div class="sh-qty"><button type="button" data-mq="-1" aria-label="Giảm" ${pm.qty <= 1 ? 'disabled' : ''}>−</button><span aria-live="polite">${pm.qty}</span><button type="button" data-mq="1" aria-label="Tăng" ${pm.qty >= max ? 'disabled' : ''}>+</button></div>
            <small class="muted">${withV && !v ? (p.stock > 0 ? 'Chọn phân loại để xem số lượng còn' : 'Hết hàng') : out ? 'Hết hàng' : `${stock} sản phẩm có sẵn`}</small></div></div>
          <div class="sh-mdact">
            <button type="button" class="btn btn-ghost" data-mdadd="cart" ${p.stock <= 0 || out ? 'disabled' : ''}>Thêm vào giỏ hàng</button>
            <button type="button" class="btn btn-primary" data-mdadd="buy" ${p.stock <= 0 || out ? 'disabled' : ''}>Mua ngay</button>
          </div>
          ${p.description ? `<div class="sh-mddesc"><h3>Mô tả sản phẩm</h3><p>${esc(p.description)}</p></div>` : ''}
        </div><div class="sh-rvwrap" id="rvBox"></div></div>`;
    ShopRev.mount(document.getElementById('rvBox'), p.id);
  }
  function modalAdd(buy) {
    if (!pm) return;
    const { p } = pm, v = variantOf(p, pm.vid);
    if (hasV(p) && !v) return toast('Vui lòng chọn phân loại hàng', 'error');
    if (!addToCart(p, v ? v.id : null, pm.qty)) return;
    closeModal();
    if (buy) openCart(); else toast('Đã thêm vào giỏ hàng', 'ok');
  }

  /* ---------- Giỏ hàng ---------- */
  function lines() {
    return cart.map(c => { const p = products.find(x => x.id === c.id); return { c, p, v: p ? variantOf(p, c.vid) : null, qty: c.qty }; })
      .filter(l => l.p && (!hasV(l.p) || l.v) && stockOf(l.p, l.v) > 0).map(l => ({ ...l, qty: Math.min(l.qty, cap(l.p, l.v)), price: priceOf(l.p, l.v) }));
  }
  const sub = () => lines().reduce((s, l) => s + l.price * l.qty, 0);
  // Ưu đãi khách VIP: chỉ xem trước (số tiền thật do máy chủ tính lúc đặt hàng, cùng công thức; đơn Shop luôn phải trả tối thiểu 2.000 đ)
  let vip = null;
  const vipOff = () => {
    if (!vip || !vip.isVip || !vip.percent) return 0;
    const s = sub(); let d = Math.round(s * vip.percent / 100);
    if (vip.maxDiscount > 0 && d > vip.maxDiscount) d = vip.maxDiscount;
    return Math.max(0, Math.min(d, s - 2000));
  };
  const total = () => Math.max(0, sub() - vipOff() - (applied ? applied.discount : 0));
  const body = () => lines().map(l => ({ productId: l.p.id, variantId: l.v ? l.v.id : null, quantity: l.qty }));

  /* ---------- Mã giảm giá ---------- */
  async function loadWallet() {
    if (!API.token()) { wallet = []; return; }
    try { wallet = (await API.call('/api/coupons/mine')).filter(c => c.status === 'ok' && c.appliesToShop && !c.eventId); } catch { wallet = []; }
  }
  async function applyCode(code, silent) {
    if (cpBusy) return; cpBusy = true;
    try {
      if (!requireLogin()) return;
      const r = await API.call('/api/shop/coupon/validate', { method: 'POST', body: { items: body(), code } });
      applied = { code: r.code, discount: r.discount, description: r.description }; form.cp = '';
      if (!silent) toast(`Đã áp mã ${r.code}: giảm ${money(r.discount)}`, 'ok');
    } catch (err) { if (silent) { applied = null; toast('Mã ' + code + ' không còn dùng được cho giỏ hàng này: ' + err.message, 'error'); } else toast(err.message, 'error'); }
    finally { cpBusy = false; paintCart(); }
  }
  /* Giỏ hàng đổi (thêm/bớt/xoá) thì tính lại mức giảm của mã đang áp */
  function refreshCoupon() { if (!applied) return; if (!lines().length) { applied = null; paintCart(); return; } applyCode(applied.code, true); }
  function couponBox() {
    if (applied) return `<div class="sh-cp ok"><div><b>Đã áp mã ${esc(applied.code)}</b><small>Giảm ${money(applied.discount)}${applied.description ? ' · ' + esc(applied.description) : ''}</small></div><button type="button" class="sh-rm" id="cpRemove" style="width:auto;height:auto;border:0">Bỏ mã</button></div>`;
    const list = wallet.filter(c => sub() >= (c.minOrder || 0));
    return `<div class="sh-cp"><div class="sh-cpin"><input id="cpCode" maxlength="40" autocomplete="off" spellcheck="false" placeholder="Nhập mã giảm giá" value="${esc(form.cp)}" aria-label="Mã giảm giá"><button type="button" class="btn btn-ghost btn-sm" id="cpApply">Áp dụng</button></div>
      ${list.length ? `<p class="sh-note">Mã bạn đã lưu dùng được cho đơn lưu niệm:</p>${list.map(c => `<div class="sh-cprow"><span><b>${esc(c.code)}</b><small>${c.type === 'Percent' ? 'Giảm ' + c.value + '%' : 'Giảm ' + money(c.value)}${c.minOrder > 0 ? ' · đơn từ ' + money(c.minOrder) : ''}</small></span><button type="button" class="btn btn-ghost btn-sm" data-cpuse="${esc(c.code)}">Áp dụng</button></div>`).join('')}` : ''}</div>`;
  }
  function badge() {
    const n = cart.reduce((s, c) => s + c.qty, 0);
    $('cartN').hidden = !n; $('cartN').textContent = n;
  }
  /* Thêm vào giỏ: gộp với dòng cùng sản phẩm + phân loại, không vượt tồn kho hoặc 20 cái */
  function addToCart(p, vid, qty) {
    const v = variantOf(p, vid), max = cap(p, v), have = qtyOf(p.id, vid);
    if (have >= max) { toast(stockOf(p, v) <= have ? 'Đã thêm hết số hàng còn lại' : `Mỗi sản phẩm mua tối đa ${MAXQ} cái`, 'error'); return false; }
    const n = Math.min(have + qty, max);
    const l = cart.find(c => c.id === p.id && (c.vid || null) === (vid || null));
    if (l) l.qty = n; else cart.push({ id: p.id, vid: vid || null, qty: n });
    if (have + qty > max) toast(`Chỉ thêm được tối đa ${max} cái cho loại này`, 'info');
    saveCart(); paintCart(); refreshCoupon(); return true;
  }
  function setQty(k, q) {
    const [id, vid] = k.split('|'); const p = products.find(x => x.id === id); if (!p) return;
    q = Math.min(q, cap(p, variantOf(p, vid)));
    if (q <= 0) cart = cart.filter(c => keyOf(c.id, c.vid) !== k);
    else { const l = cart.find(c => keyOf(c.id, c.vid) === k); if (l) l.qty = q; }
    saveCart(); paintCart(); refreshCoupon();
  }
  function paintCart() {
    const ls = lines();
    const body = ls.length ? `${ls.map(l => { const k = keyOf(l.p.id, l.v ? l.v.id : null); return `<div class="sh-line" data-id="${l.p.id}"><div class="sh-th">${art(l.p)}</div>
        <div class="sh-ln"><b>${esc(l.p.name)}</b>${l.v ? `<span class="sh-var">Phân loại: ${esc(l.v.name)}</span>` : ''}<small>${money(l.price)}</small>
          <div class="sh-qty"><button type="button" data-dec="${esc(k)}" aria-label="Giảm">−</button><span>${l.qty}</span><button type="button" data-inc="${esc(k)}" aria-label="Tăng">+</button><button type="button" class="sh-rm" data-rm="${esc(k)}" style="width:auto;height:auto;border:0">Xoá</button></div></div>
        <div class="sh-sub">${money(l.price * l.qty)}</div></div>`; }).join('')}
      <div class="sh-form"><h3>Thông tin nhận hàng</h3>
        <div class="sh-f"><label for="fName">Người nhận</label><input id="fName" maxlength="200" autocomplete="name" value="${esc(form.name)}"></div>
        <div class="sh-f"><label for="fPhone">Số điện thoại</label><input id="fPhone" inputmode="tel" maxlength="20" autocomplete="tel" value="${esc(form.phone)}"></div>
        <div class="sh-f"><label for="fAddr">Địa chỉ nhận hàng</label><textarea id="fAddr" rows="2" maxlength="500" autocomplete="street-address">${esc(form.address)}</textarea></div>
        <div class="sh-f"><label for="fNote">Ghi chú (không bắt buộc)</label><input id="fNote" maxlength="500" value="${esc(form.note)}"></div>
        <h3>Mã giảm giá</h3>${couponBox()}
        <h3>Phương thức thanh toán</h3>
        <div class="sh-pm" role="radiogroup" aria-label="Phương thức thanh toán">
          <label class="sh-pmi"><input type="radio" name="shpm" value="COD" ${form.pay !== 'PAYOS' ? 'checked' : ''}><span><b>Thanh toán khi nhận hàng (COD)</b><small>Trả tiền mặt cho người giao hàng</small></span></label>
          ${payosOn ? `<label class="sh-pmi"><input type="radio" name="shpm" value="PAYOS" ${form.pay === 'PAYOS' ? 'checked' : ''}><span><b>Thanh toán online qua payOS</b><small>Quét mã QR, tự xác nhận khi tiền về</small></span></label>` : ''}
        </div></div>`
      : `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Giỏ hàng đang trống</p><p>Chọn vài món lưu niệm bạn thích nhé.</p></div>`;
    $('cart').innerHTML = `<div class="sh-ch"><h2>Giỏ hàng</h2><button type="button" class="sh-x" id="cartX" aria-label="Đóng">×</button></div>
      <div class="sh-cb">${body}</div>
      ${ls.length ? `<div class="sh-cf">${applied || vipOff() > 0 ? `<div class="sh-sumrow"><span>Tạm tính</span><span>${money(sub())}</span></div>${vipOff() > 0 ? `<div class="sh-sumrow disc"><span>★ Ưu đãi VIP${vip.percent ? ` (−${vip.percent}%)` : ''}</span><span>−${money(vipOff())}</span></div>` : ''}${applied ? `<div class="sh-sumrow disc"><span>Mã ${esc(applied.code)}</span><span>−${money(applied.discount)}</span></div>` : ''}` : ''}<div class="sh-total"><span>Tổng cộng</span><b>${money(total())}</b></div>
        <button type="button" class="btn btn-primary" id="placeOrder">${payosOn && form.pay === 'PAYOS' ? `Thanh toán ${money(total())} qua payOS` : 'Đặt hàng · thanh toán khi nhận'}</button>
        <p class="sh-note">${payosOn && form.pay === 'PAYOS' ? 'Bạn sẽ được chuyển sang trang thanh toán có mã QR. Đơn được giữ hàng 15 phút, quá hạn chưa trả sẽ tự huỷ.' : 'Cửa hàng sẽ xác nhận đơn và liên hệ qua số điện thoại bạn để lại.'}</p></div>` : ''}`;
  }
  function openCart() { paintCart(); loadWallet().then(() => paintCart()); $('ov').hidden = false; $('cart').classList.add('open'); $('cart').setAttribute('aria-hidden', 'false'); }
  function closeCart() { $('ov').hidden = true; $('cart').classList.remove('open'); $('cart').setAttribute('aria-hidden', 'true'); }

  async function placeOrder(btn) {
    if (!requireLogin()) return;
    const ls = lines(); if (!ls.length) return toast('Giỏ hàng đang trống', 'error');
    const f = { name: form.name.trim(), phone: form.phone.trim(), address: form.address.trim(), note: form.note.trim() };
    if (f.name.length < 2) return toast('Vui lòng nhập tên người nhận', 'error');
    if (!/^(\+?84|0)\d{9,10}$/.test(f.phone.replace(/[\s.\-]/g, ''))) return toast('Số điện thoại chưa đúng', 'error');
    if (f.address.length < 8) return toast('Vui lòng nhập địa chỉ nhận hàng đầy đủ', 'error');
    const online = payosOn && form.pay === 'PAYOS';
    busy(btn, true, online ? 'Đang chuyển sang trang thanh toán…' : 'Đang đặt hàng…');
    try {
      const r = await API.call('/api/shop/orders', { method: 'POST', body: { items: body(), receiverName: f.name, phone: f.phone, address: f.address, note: f.note, paymentMethod: online ? 'PAYOS' : 'COD', couponCode: applied ? applied.code : null } });
      cart = []; saveCart(); form.note = ''; applied = null;
      if (r.payos) {   // đơn đã tạo + giữ hàng: chuyển khách sang trang payOS; nếu tạo link lỗi thì sang trang thanh toán của đơn để thử lại
        toast(r.message, 'ok'); location.href = '/Shop/Pay/' + r.id;   // trang thanh toán hiện QR ngay, tự chuyển sang đơn khi tiền về
        return;
      }
      location.href = '/Shop/Success/' + r.id;   // COD cũng sang trang "Đặt hàng thành công" như đặt vé
      return;
    } catch (err) { toast(err.message, 'error'); busy(btn, false); loadProducts(); }
  }

  /* ---------- Đơn của tôi: mỗi đơn là một thẻ có ảnh sản phẩm + phân loại, bấm để xem chi tiết ---------- */
  const when = iso => new Date(/Z|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z').toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
  let myOrders = [];
  const itemThumb = i => `<div class="sh-oth">${art({ imageUrl: i.imageUrl, category: i.category, name: i.productName })}</div>`;
  async function loadOrders() {
    const el = $('pane-orders');
    if (!API.token()) { el.innerHTML = `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Đăng nhập để xem đơn hàng</p><p><a class="btn btn-primary" style="margin-top:10px" href="/Account/Login?returnUrl=${encodeURIComponent('/Shop?tab=orders')}">Đăng nhập</a></p></div>`; return; }
    if (ordersLoaded) return;
    el.innerHTML = '<div class="skel" style="height:140px;border-radius:20px"></div>';
    try {
      myOrders = await API.call('/api/shop/orders/mine'); ordersLoaded = true;
      el.innerHTML = myOrders.length ? `<div class="sh-orders">${myOrders.map(o => {
        const st = STATUS[o.status] || [o.status, 'b-off'];
        const online = o.paymentMethod === 'PAYOS', ps = PAYST[o.paymentStatus] || PAYST.Unpaid;
        const waitPay = online && o.paymentStatus === 'Pending' && o.status === 'New';
        const count = o.items.reduce((s, i) => s + i.quantity, 0);
        return `<article class="sh-order"><a class="sh-oh sh-olink" href="/Shop/Order/${o.id}" aria-label="Xem chi tiết đơn ${esc(o.id.slice(0, 8).toUpperCase())}"><div><b>Đơn #${esc(o.id.slice(0, 8).toUpperCase())}</b><br><small>${when(o.createdAt)} · ${online ? 'payOS' : 'COD'}</small></div><span><span class="badge ${ps[1]}">${online || o.paymentStatus === 'Paid' ? ps[0] : 'Thanh toán khi nhận'}</span> <span class="badge ${st[1]}">${st[0]}</span></span></a>
          <div class="sh-ois">${o.items.map(i => `<a class="sh-oit" href="/Shop/Order/${o.id}">${itemThumb(i)}<span class="sh-oin"><b>${esc(i.productName)}</b>${i.variantName ? `<small>Phân loại: ${esc(i.variantName)}</small>` : ''}<small>× ${i.quantity}</small></span><span class="sh-oip">${money(i.unitPrice * i.quantity)}</span></a>`).join('')}</div>
          <div class="sh-of"><span class="sh-ship">${count} sản phẩm · Tổng tiền: <b style="color:var(--ink)">${money(o.total)}</b></span>
          <span class="sh-oact"><a class="btn btn-ghost btn-sm" href="/Shop/Order/${o.id}">Xem chi tiết</a>${o.status === 'Done' ? ` <a class="btn btn-primary btn-sm" href="/Shop/Order/${o.id}?review=1">Đánh giá</a>` : ''}${waitPay ? ` <a class="btn btn-primary btn-sm" href="/Shop/Pay/${o.id}">Thanh toán ngay</a>` : ''}${o.status === 'New' && o.paymentStatus !== 'Paid' ? ` <button type="button" class="btn btn-ghost btn-sm" data-cancel="${o.id}">Huỷ đơn</button>` : ''}${o.status === 'Done' || o.status === 'Cancelled' ? ` <button type="button" class="btn btn-primary btn-sm" data-rebuy="${o.id}">Mua lại</button>` : ''}</span></div></article>`;
      }).join('')}</div>` : `<div class="sh-empty"><p class="strong" style="color:var(--ink)">Bạn chưa có đơn hàng nào</p><p>Chọn vài món lưu niệm ở tab Sản phẩm nhé.</p></div>`;
    } catch (err) { el.innerHTML = `<div class="sh-empty"><p>Không tải được đơn hàng.</p><p>${esc(err.message)}</p></div>`; }
  }
  /* Mua lại: đưa các món (còn bán, còn hàng) của đơn cũ vào giỏ */
  function rebuy(order) {
    let added = 0, skipped = 0;
    for (const i of order.items) {
      const p = products.find(x => x.id === i.productId); const v = p && hasV(p) ? p.variants.find(x => x.name === i.variantName) : null;
      if (!p || (hasV(p) && !v) || stockOf(p, v) <= 0) { skipped++; continue; }
      const have = qtyOf(p.id, v ? v.id : null), want = Math.min(i.quantity, cap(p, v) - have);
      if (want > 0) { const l = cart.find(c => c.id === p.id && (c.vid || null) === (v ? v.id : null)); if (l) l.qty += want; else cart.push({ id: p.id, vid: v ? v.id : null, qty: want }); added++; } else skipped++;
    }
    saveCart(); paintCart(); refreshCoupon();
    if (added) { toast(skipped ? `Đã thêm ${added} món, ${skipped} món hết hàng hoặc ngừng bán` : 'Đã thêm các món vào giỏ hàng', 'ok'); openCart(); } else toast('Các món trong đơn này hiện không còn hàng', 'error');
  }

  /* ---------- Sự kiện ---------- */
  document.addEventListener('click', async e => {
    const t = e.target;
    if (t.closest('[data-mdclose]')) { closeModal(); return; }
    const vb = t.closest('[data-v]'); if (vb && pm) { pm.vid = pm.vid === vb.dataset.v ? null : vb.dataset.v; pm.qty = 1; paintModal(); return; }
    const mq = t.closest('[data-mq]'); if (mq && pm) { pm.qty += Number(mq.dataset.mq); paintModal(); return; }
    const ma = t.closest('[data-mdadd]'); if (ma) { modalAdd(ma.dataset.mdadd === 'buy'); return; }
    const chip = t.closest('[data-cat]'); if (chip) { cat = chip.dataset.cat; paintChips(); paintGrid(); return; }
    const q = t.closest('[data-quick]');
    if (q) {   // nút trên thẻ sản phẩm: có phân loại thì mở cửa sổ chọn, không thì thêm luôn
      const p = products.find(x => x.id === q.dataset.quick); if (!p) return;
      if (hasV(p)) openModal(p.id); else if (addToCart(p, null, 1)) toast('Đã thêm vào giỏ hàng', 'ok');
      return;
    }
    const op = t.closest('[data-open]'); if (op && !t.closest('button, a')) { openModal(op.dataset.open); return; }
    const inc = t.closest('[data-inc]'); if (inc) { const [id, vid] = inc.dataset.inc.split('|'); setQty(inc.dataset.inc, qtyOf(id, vid) + 1); return; }
    const dec = t.closest('[data-dec]'); if (dec) { const [id, vid] = dec.dataset.dec.split('|'); setQty(dec.dataset.dec, qtyOf(id, vid) - 1); return; }
    const rm = t.closest('[data-rm]'); if (rm) { setQty(rm.dataset.rm, 0); return; }
    if (t.closest('#cartBtn')) { openCart(); return; }
    if (t.closest('#cartX') || t.id === 'ov') { closeCart(); return; }
    if (t.closest('#cpApply')) { const v = ($('cpCode').value || '').trim(); if (!v) return toast('Hãy nhập mã giảm giá', 'error'); applyCode(v); return; }
    const cu = t.closest('[data-cpuse]'); if (cu) { applyCode(cu.dataset.cpuse); return; }
    if (t.closest('#cpRemove')) { applied = null; paintCart(); return; }
    const po = t.closest('#placeOrder'); if (po) { placeOrder(po); return; }
    const rb = t.closest('[data-rebuy]'); if (rb) { const o = myOrders.find(x => x.id === rb.dataset.rebuy); if (o) rebuy(o); return; }
    const c = t.closest('[data-cancel]');
    if (c && confirm('Huỷ đơn hàng này?')) {
      busy(c, true, 'Đang huỷ…');
      try { const r = await API.call(`/api/shop/orders/${c.dataset.cancel}/cancel`, { method: 'POST' }); toast(r.message, 'ok'); ordersLoaded = false; loadOrders(); loadProducts(); }
      catch (err) { toast(err.message, 'error'); busy(c, false); }
    }
  });
  document.addEventListener('change', e => {
    if (e.target.name === 'shpm') { form.pay = e.target.value; paintCart(); }
  });
  document.addEventListener('input', e => {
    const m = { fName: 'name', fPhone: 'phone', fAddr: 'address', fNote: 'note', cpCode: 'cp' }[e.target.id];
    if (m) form[m] = m === 'cp' ? e.target.value.toUpperCase().replace(/[^A-Z0-9_-]/g, '') : e.target.value;
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') { if (pm) closeModal(); else closeCart(); }
    if (e.key === 'Enter' && e.target.id === 'cpCode') { e.preventDefault(); document.getElementById('cpApply')?.click(); }
    if ((e.key === 'Enter' || e.key === ' ') && e.target.matches?.('.sh-card[data-open]')) { e.preventDefault(); openModal(e.target.dataset.open); }
  });

  badge();
  if (API.token()) API.call('/api/vip/me').then(r => { vip = r; if ($('cart').classList.contains('open')) paintCart(); }).catch(() => { vip = null; });
  API.call('/api/shop/payos/enabled').then(r => { payosOn = r.enabled === true; if ($('cart').classList.contains('open')) paintCart(); }).catch(() => { payosOn = false; });
  const qs = new URLSearchParams(location.search);
  if (qs.get('tab') === 'orders') show('orders', false);
  loadProducts().then(() => { const pid = qs.get('p'); if (pid && products.some(x => x.id === pid)) openModal(pid); });
})();
