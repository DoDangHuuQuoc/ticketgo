(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  const CAT = { LightStick: 'Light stick', Apparel: 'Áo & thời trang', Accessory: 'Phụ kiện', Other: 'Khác' };
  const ST = { New: ['Chờ xác nhận', 'b-wait'], Confirmed: ['Đã xác nhận', 'b-queue'], Shipping: ['Đang giao', 'b-queue'], Done: ['Hoàn tất', 'b-go'], Cancelled: ['Đã huỷ', 'b-off'] };
  const NEXT = { New: ['Confirmed', 'Cancelled'], Confirmed: ['Shipping', 'Cancelled'], Shipping: ['Done', 'Cancelled'] };
  const BTN = { Confirmed: 'Xác nhận', Shipping: 'Giao hàng', Done: 'Hoàn tất', Cancelled: 'Huỷ đơn' };
  let products = [], orders = [], ordersLoaded = false;
  const when = iso => new Date(iso).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });

  /* ---------- Tab ---------- */
  let reviews = [], reviewsLoaded = false;
  function setTab(t) {
    const p = t === 'products', o = t === 'orders', r = t === 'reviews';
    $('tabP').setAttribute('aria-selected', p); $('tabO').setAttribute('aria-selected', o); $('tabR').setAttribute('aria-selected', r);
    $('paneP').hidden = !p; $('paneO').hidden = !o; $('paneR').hidden = !r; $('newP').hidden = !p;
    if (o && !ordersLoaded) loadOrders();
    if (r) loadReviews();
  }
  $('tabP').addEventListener('click', () => setTab('products'));
  $('tabO').addEventListener('click', () => setTab('orders'));
  $('tabR').addEventListener('click', () => setTab('reviews'));

  /* ---------- Sản phẩm ---------- */
  function priceText(p) {
    const vs = (p.variants || []).filter(x => x.isActive);
    if (!vs.length) return money(p.price);
    const lo = Math.min(...vs.map(x => x.price)), hi = Math.max(...vs.map(x => x.price));
    return lo === hi ? money(lo) : `${money(lo)} – ${money(hi)}`;
  }
  function drawP() {
    $('rowsP').innerHTML = products.map(p => `<tr class="${p.isActive ? '' : 'dim'}">
      <td><b>${esc(p.name)}</b>${(p.variants || []).length ? `<small style="display:block;color:var(--brand)">${p.variants.length} phân loại: ${esc(p.variants.slice(0, 4).map(x => x.name).join(', '))}${p.variants.length > 4 ? '…' : ''}</small>` : ''}${p.description ? `<small style="display:block;color:var(--muted)">${esc(p.description.length > 70 ? p.description.slice(0, 70) + '…' : p.description)}</small>` : ''}</td>
      <td>${esc(CAT[p.category] || 'Khác')}</td><td class="nw">${priceText(p)}</td>
      <td class="nw">${p.stock <= 0 ? '<span class="badge b-out">Hết hàng</span>' : p.stock <= 5 ? `<span class="badge b-low">Còn ${p.stock}</span>` : p.stock}</td>
      <td><span class="badge ${p.isActive ? 'b-go' : 'b-off'}">${p.isActive ? 'Đang bán' : 'Đã ẩn'}</span></td>
      <td><div class="acts">
        <button class="btn btn-ghost btn-sm" data-a="edit" data-id="${p.id}">Sửa</button>
        <button class="btn btn-ghost btn-sm" data-a="toggle" data-id="${p.id}">${p.isActive ? 'Ẩn' : 'Hiện'}</button>
        <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${p.id}">Xoá</button></div></td></tr>`).join('')
      || '<tr><td colspan="6" class="empty">Chưa có sản phẩm nào. Bấm “+ Thêm sản phẩm” để bắt đầu.</td></tr>';
  }
  async function loadP() {
    try { products = await API.call('/api/admin/shop/products'); drawP(); }
    catch (err) { $('rowsP').innerHTML = '<tr><td colspan="6" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  function formP(it) {
    const isNew = !it, v = it || { category: 'LightStick', isActive: true, price: 0, stock: 0 };
    const m = ADM.modal({
      title: isNew ? 'Thêm sản phẩm' : 'Sửa: ' + it.name,
      body: `<form class="dlg-form" novalidate><div class="grid2">
        <div class="field full"><label for="pName">Tên sản phẩm</label><input id="pName" maxlength="200" autocomplete="off" placeholder="VD: Light stick chính hãng" /></div>
        <div class="field"><label for="pCat">Danh mục</label><select id="pCat" class="select" style="width:100%">${Object.entries(CAT).map(([k, l]) => `<option value="${k}">${l}</option>`).join('')}</select></div>
        <div class="field"><label for="pPrice">Giá (đ)</label><input id="pPrice" type="number" min="0" step="1000" inputmode="numeric" /></div>
        <div class="field"><label for="pStock">Tồn kho</label><input id="pStock" type="number" min="0" step="1" inputmode="numeric" /></div>
        <div class="field full"><div class="var-box" id="pVars"></div></div>
        <div class="field full"><label for="pImg">Ảnh sản phẩm (đường dẫn ảnh, để trống = dùng hình vẽ sẵn)</label><input id="pImg" maxlength="1000" inputmode="url" autocomplete="off" placeholder="https://... hoặc /img/ten-anh.png" /></div>
        <div class="field full"><label for="pDesc">Mô tả ngắn</label><textarea id="pDesc" rows="3" maxlength="2000"></textarea></div>
        <div class="field full"><label class="check"><input type="checkbox" id="pOn" /> Đang bán (hiện cho khách)</label></div></div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">${isNew ? 'Thêm' : 'Lưu'}</button></div></form>`
    });
    $('pName').value = v.name || ''; $('pCat').value = v.category; $('pPrice').value = v.price; $('pStock').value = v.stock;
    $('pImg').value = v.imageUrl || ''; $('pDesc').value = v.description || ''; $('pOn').checked = v.isActive;
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());

    /* Phân loại hàng (kiểu Shopee): mỗi dòng = một loại có giá + tồn kho riêng. Có phân loại đang bán thì giá/tồn của sản phẩm tự tính. */
    let vr = (v.variants || []).map(x => ({ id: x.id, name: x.name, price: x.price, stock: x.stock, isActive: x.isActive }));
    const drawVars = () => {
      $('pVars').innerHTML = `<div class="var-head"><b>Phân loại hàng</b><small>Vd: màu sắc, size. Để trống nếu sản phẩm chỉ có một loại.</small></div>
        ${vr.map((r, i) => `<div class="var-row" data-i="${i}"><input type="text" data-f="name" maxlength="100" placeholder="Tên phân loại (vd: Đỏ, Size M)" value="${esc(r.name)}" aria-label="Tên phân loại"><input type="number" data-f="price" min="0" step="1000" inputmode="numeric" value="${r.price}" aria-label="Giá"><input type="number" data-f="stock" min="0" step="1" inputmode="numeric" value="${r.stock}" aria-label="Tồn kho"><label class="check"><input type="checkbox" data-f="isActive" ${r.isActive ? 'checked' : ''}> Bán</label><button type="button" class="var-rm" data-rm="${i}" aria-label="Xoá phân loại">×</button></div>`).join('')}
        <button type="button" class="btn btn-ghost btn-sm" id="vAdd" style="justify-self:start">+ Thêm phân loại</button>`;
      syncMain();
    };
    const syncMain = () => {
      const act = vr.filter(r => r.isActive && r.name.trim());
      $('pPrice').disabled = $('pStock').disabled = act.length > 0;
      if (act.length) { $('pPrice').value = Math.min(...act.map(r => Number(r.price) || 0)); $('pStock').value = act.reduce((t, r) => t + (Math.floor(Number(r.stock)) || 0), 0); }
    };
    $('pVars').addEventListener('click', e => {
      if (e.target.closest('#vAdd')) { const last = vr[vr.length - 1]; vr.push({ id: null, name: '', price: last ? last.price : Number($('pPrice').value) || 0, stock: 0, isActive: true }); drawVars(); $('pVars').querySelector('.var-row:last-of-type [data-f=name]')?.focus(); return; }
      const rm = e.target.closest('[data-rm]'); if (rm) { vr.splice(Number(rm.dataset.rm), 1); drawVars(); }
    });
    $('pVars').addEventListener('input', e => {
      const row = e.target.closest('.var-row'), f = e.target.dataset.f; if (!row || !f) return;
      const r = vr[Number(row.dataset.i)]; if (!r) return;
      r[f] = f === 'isActive' ? e.target.checked : f === 'name' ? e.target.value : e.target.value === '' ? 0 : Number(e.target.value);
      syncMain();
    });
    drawVars();

    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const variants = vr.filter(r => r.name.trim()).map(r => ({ id: r.id || null, name: r.name.trim(), price: Number(r.price), stock: Math.floor(Number(r.stock)), isActive: !!r.isActive }));
      if (variants.some(r => !(r.price >= 0) || !(r.stock >= 0))) return toast('Giá hoặc tồn kho của một phân loại chưa hợp lệ', 'error');
      const body = { name: $('pName').value.trim(), category: $('pCat').value, price: Number($('pPrice').value), stock: Math.floor(Number($('pStock').value)), imageUrl: $('pImg').value.trim(), description: $('pDesc').value.trim(), isActive: $('pOn').checked, variants };
      if (body.name.length < 2) return toast('Vui lòng nhập tên sản phẩm (tối thiểu 2 ký tự)', 'error');
      if (!(body.price >= 0)) return toast('Giá không hợp lệ', 'error');
      if (!(body.stock >= 0)) return toast('Tồn kho không hợp lệ', 'error');
      const ok = await ADM.act(e.currentTarget.querySelector('[type=submit]'),
        () => API.call(isNew ? '/api/admin/shop/products' : '/api/admin/shop/products/' + it.id, { method: isNew ? 'POST' : 'PUT', body }));
      if (ok) { m.close(); loadP(); }
    });
    setTimeout(() => $('pName').focus(), 50);
  }

  $('rowsP').addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const it = products.find(x => x.id === b.dataset.id); if (!it) return;
    if (b.dataset.a === 'edit') return formP(it);
    if (b.dataset.a === 'toggle') {
      if (await ADM.act(b, () => API.call(`/api/admin/shop/products/${it.id}/active`, { method: 'PUT', body: { isActive: !it.isActive } }))) loadP();
    } else if (b.dataset.a === 'del') {
      const r = await ADM.ask({ title: 'Xoá ' + it.name, message: 'Xoá sản phẩm này? Nếu đã có đơn hàng, sản phẩm chỉ bị ẩn để giữ lịch sử đơn.', confirmText: 'Xoá', danger: true });
      if (r === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/shop/products/' + it.id, { method: 'DELETE' }))) loadP();
    }
  });
  $('newP').addEventListener('click', () => formP(null));

  /* ---------- Đơn hàng ---------- */
  function drawO() {
    const f = $('stF').value, list = orders.filter(o => !f || o.status === f);
    const n = orders.filter(o => o.status === 'New').length;
    $('newN').hidden = !n; $('newN').textContent = n;
    $('rowsO').innerHTML = list.map(o => {
      const st = ST[o.status] || [o.status, 'b-off'];
      const online = o.paymentMethod === 'PAYOS', unpaid = online && o.paymentStatus !== 'Paid';
      const pay = online ? (o.paymentStatus === 'Paid' ? ['payOS · Đã thanh toán', 'b-go'] : o.status === 'Cancelled' ? ['payOS · Chưa trả', 'b-off'] : ['payOS · Chờ khách trả', 'b-wait'])
        : (o.paymentStatus === 'Paid' ? ['COD · Đã thu tiền', 'b-go'] : ['COD · Thu khi giao', 'b-off']);
      return `<tr><td class="nw"><b>#${esc(o.id.slice(0, 8).toUpperCase())}</b><small style="display:block;color:var(--muted)">${when(o.createdAt)}</small></td>
        <td><b>${esc(o.receiverName)}</b><small style="display:block">${esc(o.phone)}${o.email ? ' · ' + esc(o.email) : ''}</small><small style="display:block;color:var(--muted)">${esc(o.address)}${o.note ? ' — ' + esc(o.note) : ''}</small></td>
        <td>${o.items.map(i => `<div>${esc(i.productName)}${i.variantName ? ` <small style="color:var(--brand)">(${esc(i.variantName)})</small>` : ''} × ${i.quantity}</div>`).join('')}</td>
        <td class="nw"><b>${money(o.total)}</b>${o.couponCode ? `<small style="display:block">Mã ${esc(o.couponCode)}: −${money(o.discountAmount)}</small>` : ''}<small style="display:block"><span class="badge ${pay[1]}">${pay[0]}</span></small></td><td><span class="badge ${st[1]}">${st[0]}</span></td>
        <td><div class="acts">${(NEXT[o.status] || []).filter(s => !(unpaid && s !== 'Cancelled')).map(s => `<button class="btn ${s === 'Cancelled' ? 'btn-ghost danger' : 'btn-primary'} btn-sm" data-s="${s}" data-id="${o.id}">${BTN[s]}</button>`).join('')}</div></td></tr>`;
    }).join('') || `<tr><td colspan="6" class="empty">${orders.length ? 'Không có đơn phù hợp.' : 'Chưa có đơn hàng nào.'}</td></tr>`;
  }
  async function loadOrders() {
    try { orders = await API.call('/api/admin/shop/orders'); ordersLoaded = true; drawO(); }
    catch (err) { $('rowsO').innerHTML = '<tr><td colspan="6" class="empty">Không tải được đơn hàng.</td></tr>'; toast(err.message, 'error'); }
  }
  $('stF').addEventListener('change', drawO);
  $('rowsO').addEventListener('click', async e => {
    const b = e.target.closest('[data-s]'); if (!b) return;
    if (b.dataset.s === 'Cancelled') {
      const r = await ADM.ask({ title: 'Huỷ đơn hàng', message: 'Huỷ đơn này và trả hàng về kho? (Đơn đã trả qua payOS thì nhớ hoàn tiền cho khách.)', confirmText: 'Huỷ đơn', danger: true });
      if (r === null) return;
    }
    if (await ADM.act(b, () => API.call(`/api/admin/shop/orders/${b.dataset.id}/status`, { method: 'PUT', body: { status: b.dataset.s } }))) { loadOrders(); loadP(); }
  });

  /* ---------- Đánh giá & báo cáo bình luận ---------- */
  const RSN = { Spam: 'Spam / quảng cáo', Offensive: 'Ngôn từ xúc phạm', Fake: 'Đánh giá giả mạo', Personal: 'Lộ thông tin cá nhân', Other: 'Lý do khác' };
  const stars = n => '★'.repeat(n) + '<span style="color:#d5dbea">' + '★'.repeat(5 - n) + '</span>';
  function drawR() {
    const open = reviews.reduce((s, r) => s + r.reports.filter(x => x.status === 'Open').length, 0);
    $('repN').hidden = !open; $('repN').textContent = open;
    $('rowsR').innerHTML = reviews.map(r => {
      const op = r.reports.filter(x => x.status === 'Open');
      return `<tr class="${r.isHidden ? 'dim' : ''}"><td><b>${esc(r.userName || '—')}</b><small style="display:block;color:var(--muted)">${esc(r.userEmail || '')}</small>
        <small style="display:block">${esc(r.productName)}${r.variantName ? ` <span style="color:var(--brand)">(${esc(r.variantName)})</span>` : ''}</small>
        <small style="display:block;color:var(--muted)">Đơn #${esc(r.orderId.slice(0, 8).toUpperCase())} · ${when(r.createdAt)}</small></td>
        <td style="min-width:240px"><span style="color:#f5a623;letter-spacing:1px">${stars(r.stars)}</span> ${r.isHidden ? '<span class="badge b-off">Đã ẩn</span>' : ''}
          <div style="white-space:pre-line;overflow-wrap:anywhere">${esc(r.content || '(không có nội dung)')}</div>
          ${r.adminReply ? `<div style="margin-top:6px;padding:6px 10px;border-left:3px solid var(--brand);background:var(--surface-2);font-size:.88rem"><b>Cửa hàng:</b> ${esc(r.adminReply)}</div>` : ''}</td>
        <td style="min-width:200px">${r.reports.length ? r.reports.map(x => `<div style="margin-bottom:6px"><span class="badge ${x.status === 'Open' ? 'b-wait' : x.status === 'Resolved' ? 'b-go' : 'b-off'}">${x.status === 'Open' ? 'Chờ xử lý' : x.status === 'Resolved' ? 'Đã ẩn' : 'Bỏ qua'}</span> <b>${esc(RSN[x.reason] || x.reason)}</b>
          <small style="display:block;color:var(--muted)">${esc(x.reporter || '—')} · ${when(x.createdAt)}</small>${x.detail ? `<small style="display:block">${esc(x.detail)}</small>` : ''}</div>`).join('') : '<span style="color:var(--muted)">—</span>'}</td>
        <td><div class="acts">
          ${op.length ? `<button class="btn btn-primary btn-sm" data-r="hide-rep" data-id="${r.id}">Báo cáo đúng — ẩn</button><button class="btn btn-ghost btn-sm" data-r="dismiss" data-id="${r.id}">Bỏ qua báo cáo</button>` : ''}
          <button class="btn btn-ghost btn-sm" data-r="reply" data-id="${r.id}">${r.adminReply ? 'Sửa phản hồi' : 'Phản hồi'}</button>
          <button class="btn btn-ghost btn-sm ${r.isHidden ? '' : 'danger'}" data-r="toggle" data-id="${r.id}">${r.isHidden ? 'Hiện lại' : 'Ẩn'}</button></div></td></tr>`;
    }).join('') || `<tr><td colspan="4" class="empty">${$('rvF').value === 'reported' ? 'Không có báo cáo nào cần xử lý.' : 'Chưa có đánh giá nào.'}</td></tr>`;
  }
  async function loadReviews() {
    try { reviews = await API.call('/api/admin/shop/reviews?filter=' + $('rvF').value); reviewsLoaded = true; drawR(); }
    catch (err) { $('rowsR').innerHTML = '<tr><td colspan="4" class="empty">Không tải được đánh giá.</td></tr>'; toast(err.message, 'error'); }
  }
  $('rvF').addEventListener('change', loadReviews);
  $('rowsR').addEventListener('click', async e => {
    const b = e.target.closest('[data-r]'); if (!b) return;
    const r = reviews.find(x => x.id === b.dataset.id); if (!r) return;
    const u = '/api/admin/shop/reviews/' + r.id;
    if (b.dataset.r === 'hide-rep') { if (await ADM.act(b, () => API.call(u + '/reports', { method: 'PUT', body: { action: 'hide' } }), 'Đã ẩn bình luận và đóng báo cáo')) loadReviews(); }
    else if (b.dataset.r === 'dismiss') { if (await ADM.act(b, () => API.call(u + '/reports', { method: 'PUT', body: { action: 'dismiss' } }), 'Đã bỏ qua báo cáo')) loadReviews(); }
    else if (b.dataset.r === 'toggle') { if (await ADM.act(b, () => API.call(u + '/hidden', { method: 'PUT', body: { hidden: !r.isHidden } }))) loadReviews(); }
    else if (b.dataset.r === 'reply') {
      const t = await ADM.ask({ title: 'Phản hồi đánh giá', message: 'Phản hồi hiển thị công khai dưới bình luận của khách. Để trống rồi xác nhận để xoá phản hồi.', label: 'Nội dung phản hồi', confirmText: 'Lưu' });
      if (t === null) return;
      if (await ADM.act(b, () => API.call(u + '/reply', { method: 'PUT', body: { text: t } }))) loadReviews();
    }
  });

  loadP(); loadOrders(); loadReviews();
})();
