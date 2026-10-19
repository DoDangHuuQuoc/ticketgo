(() => {
  'use strict';
  const root = document.getElementById('app'), id = root.dataset.eventId;
  const HOLD_MS = 10 * 60 * 1000, RING = 389.56;
  let ev = null, mode = 'choose', sel = null, qty = 1, booking = null, bk = null, vip = null;
  let seatPick = null, seatPickFor = null, seatsSel = [];   // loại vé khách tự chọn ghế: bộ chọn ghế + các ghế đã chọn
  let cdT = null, refreshT = null, waitT = null, beatT = null, slotT = null, slotEnd = 0;
  const logged = () => !!API.token();

  const stockText = t => t.soldOut ? 'Hết vé' : (t.availableQuantity <= 20 ? `Chỉ còn ${t.availableQuantity} vé` : `Còn ${t.availableQuantity} vé`);
  const pct = t => t.totalQuantity ? Math.round(t.availableQuantity / t.totalQuantity * 100) : 0;
  const cur = () => ev.ticketTypes.find(t => t.id === sel);
  const maxQty = () => { const t = cur(); return t ? Math.max(1, Math.min(6, t.availableQuantity)) : 6; };
  // Ưu đãi khách VIP (chỉ để xem trước; số tiền thật do máy chủ tính lúc giữ vé, công thức giống nhau)
  const vipOff = sub => {
    if (!vip || !vip.isVip || !vip.percent) return 0;
    let d = Math.round(sub * vip.percent / 100);
    if (vip.maxDiscount > 0 && d > vip.maxDiscount) d = vip.maxDiscount;
    return Math.max(0, Math.min(d, sub - 1000));
  };
  const loginHref = () => '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname);

  /* ----- các bước ----- */
  const seatStep = () => { const t = ev && cur(); return !!t && t.seatMode === 2; };   // loại vé khách tự chọn ghế có thêm bước "Chọn ghế"
  const stepLabels = () => seatStep() ? ['Chọn vé', 'Chọn ghế', 'Thanh toán', 'Nhận vé'] : ['Chọn vé', 'Thanh toán', 'Nhận vé'];
  const stepIdx = () => { const s = seatStep() ? 1 : 0; return ({ choose: 0, seats: 1, hold: 1 + s, expired: 1 + s, done: 2 + s })[mode]; };
  const stepsHTML = () => { const i = stepIdx(); return `<ol class="steps" aria-label="Các bước mua vé">${stepLabels().map((l, k) => `<li class="${k < i ? 'done' : ''}" ${k === i ? 'aria-current="step"' : ''}>${l}</li>`).join('')}</ol>`; };

  /* ----- đầu trang + cột trái (sơ đồ khu vực) ----- */
  const p2 = n => String(n).padStart(2, '0');
  function whenText() { const d = new Date(ev.startsAt); return `${p2(d.getHours())}:${p2(d.getMinutes())}, ${p2(d.getDate())} Tháng ${d.getMonth() + 1}, ${d.getFullYear()}`; }

  function topHTML() {
    return `<div class="bk-top"><a class="bk-back" href="/Event/Detail/${id}">‹ Chi tiết sự kiện</a>
      <h1 class="bk-title">${esc(ev.name)}</h1>
      <div class="bk-sub"><span>${icon('cal', 18)}${whenText()}</span><span>${icon('pin', 18)}${esc(ev.venue)}</span></div></div>`;
  }

  function leftHTML() {
    const poster = `<section class="evd-card" aria-label="Sự kiện"><div class="bk-poster poster">${posterArt(ev.name, ev.posterUrl)}</div></section>`;
    const map = ev.seatMapUrl
      ? `<section class="evd-card sm" aria-labelledby="smT"><h2 id="smT">Sơ đồ chỗ ngồi</h2><div class="evd-card-b" id="smBody">
          <a class="sm-link" href="${esc(ev.seatMapUrl)}" target="_blank" rel="noopener" title="Mở ảnh cỡ lớn"><img class="sm-img" id="smImg" src="${esc(ev.seatMapUrl)}" alt="Sơ đồ chỗ ngồi ${esc(ev.name)}" loading="lazy" decoding="async" referrerpolicy="no-referrer"></a>
          <p class="muted sm-note">Bấm vào ảnh để xem cỡ lớn, sau đó chọn hạng vé ở bên phải.</p></div></section>`
      : '';
    return `${poster}${map}
      <section class="evd-card notes" aria-labelledby="ntT"><h2 id="ntT">Trước khi mua</h2><div class="evd-card-b"><ul>
        <li>Mỗi lần đặt tối đa 6 vé.</li>
        <li>Vé được giữ trong 10 phút. Quá hạn mà chưa thanh toán, vé tự trả lại kho.</li>
        <li>Thanh toán xong, mã QR được gửi qua email và lưu trong mục Vé của tôi.</li></ul></div></section>`;
  }

  /* ----- cột phải ----- */
  const ticketList = () => `<fieldset class="tt-list"><legend class="sr-only">Hạng vé</legend>${ev.ticketTypes.map(t => `
    <div class="tt"><input type="radio" name="tt" id="tt-${t.id}" value="${t.id}" ${t.soldOut ? 'disabled' : ''} ${sel === t.id ? 'checked' : ''}>
      <label for="tt-${t.id}"><span class="tt-name">${esc(t.name)}${t.seatMode === 2 ? '<span class="seat-tag">Chọn ghế</span>' : t.seatMode === 1 ? '<span class="seat-tag">Có số ghế</span>' : ''}</span><span class="tt-price">${money(t.price)}</span>
        <span class="tt-zone">${esc(t.zone)}</span><span class="tt-left" data-stock="${t.id}">${stockText(t)}</span>
        <span class="meter ${pct(t) < 15 ? 'low' : ''}" data-meter="${t.id}"><i style="width:${pct(t)}%"></i></span></label></div>`).join('')}</fieldset>`;

  const buyControls = () => !logged()
    ? `<a class="btn btn-primary btn-block" style="margin-top:18px" href="${loginHref()}">Đăng nhập để mua vé</a>`
    : `<div class="qty-row"><span class="strong" id="qtyLbl">Số lượng</span>
        <div class="stepper" role="group" aria-labelledby="qtyLbl"><button type="button" id="dec" aria-label="Giảm số lượng">${icon('minus')}</button><output id="qty" aria-live="polite">${qty}</output><button type="button" id="inc" aria-label="Tăng số lượng">${icon('plus')}</button></div></div>
       <div id="seatNote"></div>
       <div class="cp-line vip-row" id="vipRow" hidden><span>★ Ưu đãi khách VIP${vip?.percent ? ` (−${vip.percent}%)` : ''}</span><b id="vipOff"></b></div>
       <div class="total-row"><span class="muted">Tổng cộng</span><b id="total">${money(0)}</b></div>
       <button class="btn btn-primary btn-block" id="book" type="button" disabled>Tiếp tục thanh toán</button>`;

  const holdHTML = () => `<div class="ring" role="timer" aria-label="Thời gian giữ vé còn lại">
      <svg width="140" height="140" viewBox="0 0 140 140"><circle class="trk" cx="70" cy="70" r="62" fill="none" stroke-width="8"/>
        <circle class="bar-c" id="ringBar" cx="70" cy="70" r="62" fill="none" stroke-width="8" stroke-linecap="round" stroke-dasharray="${RING}" stroke-dashoffset="0"/></svg>
      <div class="ring-time" id="ringTime">10:00</div></div>
    <div class="summary"><div><span class="muted">Vé</span><b>${bk.qty} × ${esc(bk.name)}</b></div>
      <div><span class="muted">Khu</span><span>${esc(bk.zone)}</span></div>
      <div><span class="muted">Tổng cộng</span><b>${money(booking.totalAmount)}</b></div></div>
    <button class="btn btn-primary btn-block" id="pay" type="button">Thanh toán ${money(booking.totalAmount)}</button>
    <p class="note">Đây là cổng thanh toán giả lập dùng để thử nghiệm.</p>`;

  /* ----- bước chọn ghế: sơ đồ ở cột trái (rộng), tóm tắt + nút ở cột phải ----- */
  const seatsMainHTML = () => { const t = cur(); return `<section class="evd-card seat-step" aria-labelledby="ssT"><h2 id="ssT">Chọn ghế · ${esc(t.name)}</h2><div class="evd-card-b"><div id="seatBox"></div></div></section>`; };
  const seatsPaneHTML = () => { const t = cur(); return `<div class="summary"><div><span class="muted">Vé</span><b>${qty} × ${esc(t.name)}</b></div>
      <div><span class="muted">Khu</span><span>${esc(t.zone)}</span></div>
      <div><span class="muted">Ghế</span><b id="seatList">Chưa chọn</b></div></div>
    <div class="cp-line vip-row" id="vipRow" hidden><span>★ Ưu đãi khách VIP${vip?.percent ? ` (−${vip.percent}%)` : ''}</span><b id="vipOff"></b></div>
    <div class="total-row"><span class="muted">Tổng cộng</span><b id="total">${money(0)}</b></div>
    <button class="btn btn-primary btn-block" id="book" type="button" disabled>Tiếp tục thanh toán</button>
    <button class="btn btn-ghost btn-block" id="backTicket" type="button">‹ Đổi hạng vé / số lượng</button>`; };

  /* ----- ảnh sơ đồ: chỉ là một ảnh theo link, lỗi tải thì báo nhẹ, vẫn mua vé bình thường ----- */
  function bindSeatMapImg() {
    const img = document.getElementById('smImg'); if (!img) return;
    img.addEventListener('error', () => {
      const body = document.getElementById('smBody'); if (!body) return;
      body.innerHTML = `<div class="sm-fail"><p><b>Chưa tải được ảnh sơ đồ.</b></p><a class="btn btn-ghost btn-sm" href="${esc(ev.seatMapUrl)}" target="_blank" rel="noopener">Mở link ảnh</a></div>`;
    });
  }

  function refreshBuy() {
    const el = root.querySelector('.buy'); if (!el) return;
    const main = root.querySelector('.evd-main');
    if (main) { main.innerHTML = mode === 'seats' ? seatsMainHTML() : leftHTML(); if (mode !== 'seats') bindSeatMapImg(); }
    el.innerHTML = stepsHTML() + '<div id="pane"></div>';
    renderPane(); paintSlot();
  }

  function renderPane() {
    clearInterval(cdT);
    const pane = document.getElementById('pane');
    if (mode === 'choose') {
      pane.innerHTML = ticketList() + buyControls();
      seatPick = null;   // bộ chọn ghế chỉ có ở bước "Chọn ghế"; ghế đã chọn (seatsSel) giữ lại nếu khách quay lại đúng hạng vé đó
      bindChoose();
    } else if (mode === 'seats') {
      pane.innerHTML = seatsPaneHTML(); bindSeats();
    } else if (mode === 'hold') {
      pane.innerHTML = holdHTML(); bindHold(); startHoldTimer();
    } else if (mode === 'expired') {
      pane.innerHTML = `<div class="done-box"><h2>Hết thời gian giữ vé</h2><p class="muted" style="margin:10px 0 18px">Vé đã được trả lại kho. Bạn có thể chọn lại.</p><button class="btn btn-primary btn-block" id="again" type="button">Chọn lại vé</button></div>`;
      document.getElementById('again').addEventListener('click', async () => { mode = 'choose'; await refreshStock(); refreshBuy(); });
    } else if (mode === 'done') {
      pane.innerHTML = `<div class="done-box"><div class="done-mark">${icon('check', 34)}</div><h2>Thanh toán thành công</h2>
        <p class="muted" style="margin:10px 0 18px">Mã QR đã được gửi tới email của bạn.</p><a class="btn btn-ink btn-block" href="/Account/MyBookings">Xem vé của tôi</a></div>`;
    }
  }

  /* ----- chọn vé ----- */
  function bindChoose() {
    const book = document.getElementById('book'); if (!book) return;
    const upd = () => {
      const t = cur(), m = maxQty(); if (qty > m) qty = m;
      document.getElementById('qty').textContent = qty;
      const sub = t ? t.price * qty : 0, off = vipOff(sub);
      document.getElementById('vipRow').hidden = !(off > 0);
      document.getElementById('vipOff').textContent = '−' + money(off);
      document.getElementById('total').textContent = money(sub - off);
      // Ghế: loại vé khách tự chọn (2) sẽ có bước chọn ghế riêng sau bước này; loại ngẫu nhiên (1) chỉ báo trước; loại thường (0) không có ghế
      const sm = t ? t.seatMode : 0, toSeats = sm === 2 && !t.soldOut;
      document.getElementById('seatNote').innerHTML = toSeats ? '<p class="sp-note">💺 Bước tiếp theo: chọn ghế trên sơ đồ, sau đó mới thanh toán.</p>'
        : sm === 1 ? '<p class="sp-note">🎟️ Số ghế sẽ được xếp ngẫu nhiên trong khu này sau khi bạn thanh toán, và hiện trên vé.</p>' : '';
      const ol = root.querySelector('.steps'); if (ol && ol.children.length !== stepLabels().length) ol.outerHTML = stepsHTML();
      book.disabled = !t || t.soldOut;
      book.textContent = toSeats ? 'Tiếp tục chọn ghế' : 'Tiếp tục thanh toán';
      document.getElementById('dec').disabled = qty <= 1;
      document.getElementById('inc').disabled = !t || qty >= m;
    };
    root.querySelectorAll('input[name=tt]').forEach(i => i.addEventListener('change', () => { sel = i.value; if (seatPickFor !== sel) seatsSel = []; upd(); }));
    document.getElementById('dec').addEventListener('click', () => { qty = Math.max(1, qty - 1); upd(); });
    document.getElementById('inc').addEventListener('click', () => { qty = Math.min(maxQty(), qty + 1); upd(); });
    book.addEventListener('click', () => { const t = cur(); if (t && t.seatMode === 2) { mode = 'seats'; refreshBuy(); window.scrollTo({ top: 0, behavior: 'smooth' }); } else reserve(book); });
    upd();
  }

  /* ----- bước chọn ghế ----- */
  function bindSeats() {
    const t = cur(), book = document.getElementById('book'), box = document.getElementById('seatBox'), forId = t.id;
    const upd = () => {
      const sub = t.price * qty, off = vipOff(sub), need = seatsSel.length !== qty;
      document.getElementById('vipRow').hidden = !(off > 0);
      document.getElementById('vipOff').textContent = '−' + money(off);
      document.getElementById('total').textContent = money(sub - off);
      document.getElementById('seatList').textContent = seatsSel.length ? seatsSel.join(', ') : 'Chưa chọn';
      book.disabled = need;
      book.textContent = need ? `Chọn đủ ${qty} ghế (còn ${Math.max(0, qty - seatsSel.length)})` : 'Tiếp tục thanh toán';
    };
    document.getElementById('backTicket').addEventListener('click', () => { mode = 'choose'; refreshBuy(); });
    book.addEventListener('click', () => reserve(book));
    const initial = seatPickFor === forId ? seatsSel.slice(0, qty) : [];
    seatPickFor = forId; seatsSel = initial.slice(); seatPick = null;
    SeatPicker.mount(box, { typeId: forId, initial, max: () => qty, onChange: list => { if (mode === 'seats' && seatPickFor === forId) { seatsSel = list; upd(); } } })
      .then(p => { if (mode === 'seats' && seatPickFor === forId) seatPick = p; });
    upd();
  }

  async function reserve(btn) {
    const t = cur(); if (!t) return;
    busy(btn, true, 'Đang giữ vé…');
    try {
      booking = await API.call('/api/bookings', { method: 'POST', body: { ticketTypeId: t.id, quantity: qty, seats: t.seatMode === 2 ? seatsSel : undefined } });
      location.href = '/Event/Checkout/' + booking.bookingId; // sang trang thanh toán riêng
    } catch (err) {
      const code = err.data?.code;
      if (code === 'NOT_VERIFIED') { toast(err.message, 'error'); setTimeout(() => location.href = '/Account/Verify', 1200); return; }
      busy(btn, false);
      if (code === 'QUEUE_WAIT') {   // quá đông: chưa tới lượt, đưa khách vào hàng chờ
        try {
          const st = await API.call(`/api/queue/${id}/join`, { method: 'POST' });
          if (st.status === 'waiting') { showQueue(st); return; }
          slotEnd = Date.now() + (st.slotLeftSeconds || 0) * 1000; paintSlot(); toast('Đã tới lượt bạn. Hãy bấm đặt vé lại.', 'ok'); return;
        } catch (e2) { toast(e2.message, 'error'); return; }
      }
      if (code === 'SOLD_OUT') refreshStock();
      if (code === 'SEAT_TAKEN' && seatPick) seatPick.reload();   // ghế vừa bị người khác lấy: tải lại sơ đồ
      toast(err.message, 'error');
    }
  }

  /* ----- giữ chỗ / thanh toán ----- */
  function startHoldTimer() {
    const end = new Date(booking.expiresAt).getTime();
    const tick = () => {
      const left = Math.max(0, end - Date.now()), t = document.getElementById('ringTime');
      if (!t) { clearInterval(cdT); return; }
      t.textContent = mmss(left);
      document.getElementById('ringBar').style.strokeDashoffset = RING * (1 - Math.min(1, left / HOLD_MS));
      if (left === 0) { clearInterval(cdT); mode = 'expired'; refreshBuy(); }
    };
    tick(); cdT = setInterval(tick, 250);
  }
  function bindHold() {
    document.getElementById('pay').addEventListener('click', async e => {
      const b = e.currentTarget; busy(b, true, 'Đang xử lý…');
      try { await payBooking(booking.bookingId); clearInterval(cdT); mode = 'done'; refreshBuy(); }
      catch (err) { toast(err.message, 'error'); busy(b, false); }
    });
  }

  /* ----- tồn kho gần real-time ----- */
  async function refreshStock() {
    try {
      const fresh = await API.call(`/api/events/${id}`); ev.ticketTypes = fresh.ticketTypes;
      ev.ticketTypes.forEach(t => {
        const s = root.querySelector(`[data-stock="${t.id}"]`); if (s) s.textContent = stockText(t);
        const m = root.querySelector(`[data-meter="${t.id}"]`); if (m) { m.classList.toggle('low', pct(t) < 15); m.firstElementChild.style.width = pct(t) + '%'; }
        const i = root.querySelector(`#tt-${t.id}`); if (i && mode === 'choose') i.disabled = t.soldOut;
      });
    } catch { /* bỏ qua lỗi polling */ }
  }

  /* ----- hàng chờ mua vé: quá đông thì xếp hàng, tới lượt mới được vào chọn vé ----- */
  const etaText = sec => sec <= 60 ? 'Dự kiến chờ dưới 1 phút' : `Dự kiến chờ khoảng ${Math.ceil(sec / 60)} phút`;
  const aheadText = pos => pos <= 1 ? 'Bạn là người kế tiếp, sắp tới lượt.' : `Phía trước bạn còn ${pos - 1} người.`;

  function showQueue(st) {
    if (refreshT) { clearInterval(refreshT); refreshT = null; }
    clearInterval(beatT); beatT = null; clearInterval(slotT); slotT = null; clearTimeout(waitT);
    const start = Math.max(st.position, 1);
    root.innerHTML = `${topHTML()}<section class="wr" role="status" aria-live="polite"><div class="wr-card">
        <span class="wr-kicker">Xếp hàng mua vé</span>
        <h2>Đang có rất đông người mua vé</h2>
        <p class="wr-sub">Để đảm bảo công bằng, mọi người được vào mua vé lần lượt theo thứ tự đến. Bạn sẽ tự động vào trang chọn vé khi tới lượt.</p>
        <div class="wr-pos"><span>Số thứ tự của bạn</span><b id="wrPos">#${st.position}</b></div>
        <div class="wr-bar" aria-hidden="true"><i id="wrBar"></i></div>
        <p class="wr-ahead" id="wrAhead">${aheadText(st.position)}</p>
        <p class="wr-eta" id="wrEta">${etaText(st.etaSeconds)}</p>
        <p class="wr-note">Vui lòng giữ nguyên trang này. Nếu đóng trang hoặc mất kết nối quá khoảng 45 giây, bạn sẽ phải xếp hàng lại từ cuối. Khi tới lượt, bạn có tối đa 10 phút để chọn vé.</p>
        <button type="button" class="btn btn-ghost" id="wrLeave">Rời hàng chờ</button></div></section>`;
    document.getElementById('wrLeave').addEventListener('click', async () => {
      clearTimeout(waitT);
      try { await API.call(`/api/queue/${id}/leave`, { method: 'POST' }); } catch { /* bỏ qua */ }
      location.href = '/Event/Detail/' + id;
    });
    const paint = s => {
      const pos = document.getElementById('wrPos'); if (!pos) return;
      pos.textContent = '#' + s.position;
      document.getElementById('wrAhead').textContent = aheadText(s.position);
      document.getElementById('wrEta').textContent = etaText(s.etaSeconds);
      document.getElementById('wrBar').style.width = Math.max(4, Math.min(100, Math.round((1 - (s.position - 1) / start) * 100))) + '%';
    };
    paint(st);
    const poll = async (every) => {
      waitT = setTimeout(async () => {
        try {
          let s = await API.call(`/api/queue/${id}/status`);
          if (s.status === 'none') s = await API.call(`/api/queue/${id}/join`, { method: 'POST' });   // bị loại do mất kết nối: xếp hàng lại
          if (s.status === 'admitted') { toast('Đến lượt bạn! Bạn có tối đa 10 phút để chọn vé.', 'ok'); showBooking(s); return; }
          paint(s); poll(s.pollSeconds || 4);
        } catch { poll(5); }   // lỗi mạng thoáng qua: thử lại, không mất chỗ ngay
      }, every * 1000);
    };
    poll(st.pollSeconds || 3);
  }

  /* ----- đang trong lượt mua: đồng hồ suất + báo "tôi còn ở đây" để không bị nhường chỗ ----- */
  function paintSlot() {
    const buy = document.getElementById('buy'); if (!buy) return;
    let el = document.getElementById('slotBar');
    if (!slotEnd) { if (el) el.remove(); return; }
    if (!el) { el = document.createElement('div'); el.id = 'slotBar'; el.className = 'wr-slot'; buy.insertBefore(el, document.getElementById('pane')); }
    el.textContent = `Đã tới lượt bạn · còn ${mmss(Math.max(0, slotEnd - Date.now()))} để chọn vé`;
  }

  function startSlot(st) {
    slotEnd = st && st.slotLeftSeconds ? Date.now() + st.slotLeftSeconds * 1000 : 0;
    clearInterval(slotT); clearInterval(beatT);
    if (!logged()) return;
    paintSlot();
    slotT = setInterval(paintSlot, 1000);
    beatT = setInterval(async () => {
      if (mode !== 'choose' && mode !== 'seats') return;
      try {
        let s = await API.call(`/api/queue/${id}/status`);
        if (s.status === 'none') s = await API.call(`/api/queue/${id}/join`, { method: 'POST' });   // hết lượt mua: xếp hàng lại
        if (s.status === 'waiting') { toast('Đã hết lượt mua, bạn được xếp lại vào hàng chờ.', 'error'); showQueue(s); return; }
        slotEnd = s.slotLeftSeconds ? Date.now() + s.slotLeftSeconds * 1000 : 0;
      } catch { /* lỗi mạng thoáng qua: thử lại ở nhịp sau */ }
    }, 10000);
  }

  /* ----- khởi tạo ----- */
  function showBooking(st) {
    mode = 'choose';
    root.innerHTML = `${topHTML()}<div class="evd-body"><div class="evd-main">${leftHTML()}</div><aside class="buy" id="buy" aria-label="Mua vé">${stepsHTML()}<div id="pane"></div></aside></div>`;
    renderPane();
    bindSeatMapImg();
    startSlot(st);
    if (!refreshT) refreshT = setInterval(refreshStock, 5000);
  }

  (async () => {
    try { ev = await API.call(`/api/events/${id}`); }
    catch (err) { root.innerHTML = `<div class="empty" style="grid-column:1/-1">${esc(err.message)}. <a href="/">Về trang sự kiện</a></div>`; return; }
    document.title = 'Mua vé – ' + ev.name + ' – TicketGo';
    if (logged()) {
      try { vip = await API.call('/api/vip/me'); } catch { vip = null; }   // lỗi thì cứ bán giá thường, máy chủ vẫn tự áp VIP khi giữ vé
      try {
        const st = await API.call(`/api/queue/${id}/join`, { method: 'POST' });
        if (st.status === 'waiting') { showQueue(st); return; }
        showBooking(st); return;
      } catch { /* lỗi hàng chờ thì vẫn cho vào; server sẽ chặn khi thật sự quá đông */ }
    }
    showBooking();
  })();
})();
