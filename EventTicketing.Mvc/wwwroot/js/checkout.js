(() => {
  'use strict';
  if (!requireLogin()) return;
  const root = document.getElementById('app'), id = root.dataset.bookingId;
  const HOLD_MS = 10 * 60 * 1000;
  let b = null, method = 'qr', timer = null, poll = null, paying = false, done = false;
  let wallet = [];   // mã khách đã lưu và dùng được cho đơn này
  const skipKey = 'ck-skip-' + id;
  const skipped = () => { try { return JSON.parse(sessionStorage.getItem(skipKey) || '[]'); } catch { return []; } };
  const skip = c => { try { sessionStorage.setItem(skipKey, JSON.stringify([...skipped(), c])); } catch { /* bỏ qua */ } };
  const p2 = n => String(n).padStart(2, '0');
  const when = iso => { const d = new Date(iso); return `${p2(d.getHours())}:${p2(d.getMinutes())}, ${p2(d.getDate())} Tháng ${d.getMonth() + 1}, ${d.getFullYear()}`; };
  const code = () => 'TG' + id.replace(/-/g, '').slice(0, 8).toUpperCase();

  let METHODS = {
    qr:   { name: 'Quét mã QR ngân hàng', sub: 'VietQR – dùng được với mọi ứng dụng ngân hàng', ic: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 14h3v3h-3zM20 14v3M14 20h3M20 20h1"/>' },
    atm:  { name: 'Thẻ ATM nội địa', sub: 'Thẻ có đăng ký Internet Banking', ic: '<rect x="2.5" y="5" width="19" height="14" rx="2.5"/><path d="M2.5 10h19M6 15h4"/>' },
    card: { name: 'Thẻ Visa / Mastercard / JCB', sub: 'Thẻ tín dụng, ghi nợ quốc tế', ic: '<rect x="2.5" y="5" width="19" height="14" rx="2.5"/><circle cx="9.5" cy="12" r="3"/><circle cx="14.5" cy="12" r="3"/>' },
    momo: { name: 'Ví MoMo', sub: 'Thanh toán bằng ứng dụng MoMo', ic: '<circle cx="12" cy="12" r="9"/><path d="M8 15V9l4 4 4-4v6"/>' }
  };
  /* payOS: bật khi server đã cấu hình khoá. Khi đó thay các cổng giả lập (ATM/Visa/MoMo) bằng payOS thật */
  let payosOn = false;
  const qs = new URLSearchParams(location.search);
  const payosCancel = qs.get('cancel') === 'true' || qs.get('status') === 'CANCELLED';
  const payosBack = !!(qs.get('orderCode') || qs.get('status')) && !payosCancel;   // payOS đưa khách quay về kèm tham số
  const PAYOS = { name: 'payOS – QR / app ngân hàng', sub: 'Thanh toán thật, tự xác nhận khi tiền về', ic: '<rect x="2.5" y="5" width="19" height="14" rx="2.5"/><path d="M2.5 10h19M6 15h5"/><path d="M16 14.5l1.5 1.5 3-3"/>' };
  const svg = (p, s = 22) => `<svg width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${p}</svg>`;

  /* Mã VietQR thật do server sinh: đúng số tiền của đơn + nội dung chuyển khoản. Hiện ngay khi mở trang. */
  let qr = null, qrErr = '';
  async function loadQr() {
    try { qr = payosOn ? await API.call('/api/payments/payos/qr/' + id, { method: 'POST' }) : await API.call('/api/payments/qr/' + id); qrErr = ''; }
    catch (err) {
      qr = null; qrErr = err.message;
      if (err.data?.code === 'NOT_PENDING') await checkPaid();   // đơn vừa được thanh toán/đóng
    }
  }
  /* Ví voucher: chỉ lấy mã còn dùng được, đúng sự kiện, đủ điều kiện đơn tối thiểu; tính sẵn số tiền sẽ giảm */
  async function loadWallet() {
    try {
      const vipOff = b.vipDiscount || 0, sub = b.totalAmount + b.discountAmount + vipOff, base = sub - vipOff;   // mã giảm tính trên số tiền còn lại sau ưu đãi VIP
      wallet = (await API.call('/api/coupons/mine'))
        .filter(c => c.status === 'ok' && (!c.eventId || c.eventId === b.eventId) && sub >= (c.minOrder || 0))
        .map(c => {
          let off = c.type === 'Percent' ? Math.round(base * c.value / 100) : c.value;
          if (c.maxDiscount > 0 && off > c.maxDiscount) off = c.maxDiscount;
          return { ...c, off: Math.max(0, Math.min(off, base - 1000)) };
        })
        .filter(c => c.off > 0).sort((a, b2) => b2.off - a.off);
    } catch { wallet = []; }   // lỗi ví mã không được chặn việc thanh toán
  }

  /* Khung mã giảm giá luôn hiện: đã áp mã thì báo + nút Bỏ mã; chưa áp thì có ô nhập mã và danh sách mã đã lưu để chọn Áp dụng / Không */
  function couponBox() {
    if (b.couponCode) {
      return `<div class="ck-cp ok"><div><b>Đã áp dụng mã ${esc(b.couponCode)}</b><small>Giảm ${money(b.discountAmount)} cho đơn này</small></div>
        <button type="button" class="ck-copy" data-act="cp-remove">Bỏ mã</button></div>`;
    }
    const off = skipped(), list = wallet.filter(c => !off.includes(c.code));
    return `<div class="ck-cp ask" role="region" aria-label="Mã giảm giá"><p class="ck-cp-t">Mã giảm giá</p>
      <div class="ck-cp-in"><input id="cpCode" type="text" placeholder="Nhập mã giảm giá" autocomplete="off" maxlength="40" aria-label="Nhập mã giảm giá">
        <button type="button" class="ck-copy ck-cp-yes" data-act="cp-code">Áp dụng</button></div>
      ${list.length ? `<p class="ck-cp-sub">Mã bạn đã lưu: áp dụng cho đơn này?</p>` + list.map(c => `<div class="ck-cp-row"><div><b>${esc(c.code)}</b><small>${VC.short(c)} · tiết kiệm ${money(c.off)}</small></div>
        <span class="ck-cp-btns"><button type="button" class="ck-copy ck-cp-yes" data-act="cp-apply" data-code="${esc(c.code)}">Áp dụng</button>
        <button type="button" class="ck-cp-no" data-act="cp-skip" data-code="${esc(c.code)}">Không</button></span></div>`).join('')
        : `<p class="ck-cp-sub">Bạn chưa lưu mã nào dùng được cho đơn này. <a href="/Promotions">Xem khuyến mãi</a></p>`}</div>`;
  }

  /* Áp/gỡ mã: số tiền đơn đổi nên phải nạp lại đơn và sinh lại mã QR theo số tiền mới */
  async function changeCoupon(fn, btn) {
    if (paying || done) return;
    btn && (btn.disabled = true);
    try {
      await fn();
      b = await API.call('/api/bookings/' + id);
      await Promise.all([loadQr(), loadWallet()]);
      document.querySelector('.ck-side').innerHTML = sideHTML();
      document.getElementById('panel').innerHTML = payPanel();
      document.getElementById('payArea').innerHTML = payArea();
    } catch (err) { toast(err.message, 'error'); btn && (btn.disabled = false); }
  }

  const copyBtn = (v, label) => `<button type="button" class="ck-copy" data-copy="${esc(v)}" aria-label="Sao chép ${label}">Sao chép</button>`;
  const qrLine = (k, v, raw) => `<div class="ck-qrl"><span>${k}</span><b>${esc(v)}</b>${copyBtn(raw ?? v, k.toLowerCase())}</div>`;

  function payPanel() {
    if (method === 'qr') {
      if (!qr) return `<div class="ck-qrwait"><p class="ck-note-in">${esc(qrErr || 'Đang tạo mã QR…')}</p>${qrErr ? '<button type="button" class="ck-copy" data-act="retry">Thử lại</button>' : ''}</div>`;
      return `<div class="ck-qr"><div><img class="ck-qrimg" src="${qr.image}" alt="Mã VietQR thanh toán ${money(qr.amount)}" width="200" height="200" /><p class="ck-mini">Quét bằng app ngân hàng bất kỳ</p></div>
      <div class="ck-qrinfo">${qrLine('Ngân hàng', qr.bankName)}${qrLine('Số tài khoản', qr.accountNo)}${qr.accountName ? qrLine('Chủ tài khoản', qr.accountName) : ''}${qrLine('Số tiền', money(qr.amount), String(qr.amount))}${qrLine('Nội dung', qr.content)}
      <p class="ck-mini">Mã QR đã điền sẵn số tiền và nội dung. Giữ nguyên nội dung <b>${esc(qr.content)}</b> để hệ thống nhận diện đơn. Chuyển khoản xong bạn không cần bấm gì: trang sẽ tự mở vé có mã QR.</p>${qr.checkoutUrl ? `<p class="ck-mini">Không quét được? <a href="${esc(qr.checkoutUrl)}" target="_blank" rel="noopener">Mở trang thanh toán payOS ở tab mới</a> (tab này vẫn ở lại và tự chuyển sang vé khi trả xong).</p>` : ''}</div></div>`;
    }
    if (method === 'payos') return `<p class="ck-note-in">${payosBack ? 'Đang xác nhận thanh toán với payOS… Trang sẽ tự mở vé ngay khi tiền về.' : 'Bạn sẽ được chuyển sang trang thanh toán payOS để quét mã QR hoặc mở app ngân hàng. Thanh toán xong hệ thống tự xác nhận và mở vé cho bạn, không cần bấm gì thêm.'}</p>`;
    return `<p class="ck-note-in">Bạn sẽ được chuyển tới cổng ${esc(METHODS[method].name)} để xác nhận thanh toán. Đây là cổng giả lập dùng để thử nghiệm: bấm <b>Thanh toán</b> để hoàn tất đơn.</p>`;
  }

  /* Khu nút bên phải: QR thì chỉ báo "đang chờ", cổng giả lập thì có nút Thanh toán */
  function payArea() {
    if (method === 'qr') {
      return `<div class="ck-wait" role="status" aria-live="polite"><span class="ck-spin" aria-hidden="true"></span><span><b>Đang chờ thanh toán</b><small>Tự chuyển sang vé ngay khi nhận được tiền</small></span></div>`;
    }
    if (method === 'payos' && payosBack) return `<div class="ck-wait" role="status" aria-live="polite"><span class="ck-spin" aria-hidden="true"></span><span><b>Đang xác nhận thanh toán</b><small>Tự mở vé ngay khi payOS báo đã nhận tiền</small></span></div>`;
    if (method === 'payos') return `<button class="btn ck-pay" id="pay" type="button">Thanh toán ${money(b.totalAmount)} qua payOS</button>`;
    return `<button class="btn ck-pay" id="pay" type="button">Thanh toán ${money(b.totalAmount)}</button>`;
  }

  function mainHTML() {
    return `<section class="ck-card"><h2><span class="ck-n">1</span>Thông tin nhận vé</h2><div class="ck-b">
        <div class="ck-kv"><span>Người nhận</span><b>${esc(localStorage.getItem('name') || 'Tài khoản của bạn')}</b></div>
        <p class="ck-mini">Vé điện tử (mã QR) được gửi về email tài khoản và lưu trong mục <a href="/Account/MyBookings">Vé của tôi</a>.</p></div></section>
      <section class="ck-card" id="payCard"><h2><span class="ck-n">2</span>Thanh toán</h2><div class="ck-b">
        <div class="ck-pm" role="radiogroup" aria-label="Phương thức thanh toán">${Object.entries(METHODS).map(([k, m]) => `
          <label class="ck-pmi"><input type="radio" name="pm" value="${k}" ${k === method ? 'checked' : ''}><span class="ck-pmc"><i>${svg(m.ic)}</i><span><b>${m.name}</b><small>${m.sub}</small></span><u></u></span></label>`).join('')}</div>
        <div class="ck-panel" id="panel">${payPanel()}</div>
        <p class="ck-mini" style="margin-top:14px">Khi thanh toán, bạn đồng ý với <a href="#">Điều khoản sử dụng</a> và <a href="#">Chính sách hoàn/đổi vé</a>.</p></div></section>`;
  }

  function sideHTML() {
    return `<div class="ck-ticket">
      <div class="ck-poster poster">${posterArt(b.eventName, b.posterUrl)}</div>
      <div class="ck-tb"><h3>${esc(b.eventName)}</h3>
        <div class="ck-row">${icon('cal', 17)}<span>${when(b.startsAt)}</span></div>
        <div class="ck-row">${icon('pin', 17)}<span>${esc(b.venue)}</span></div></div>
      <div class="ck-perf"></div>
      <div class="ck-tb">
        <div class="ck-line"><span>${b.quantity} × ${esc(b.typeName)}<small>${esc(b.zone)}</small></span><b>${money(b.unitPrice * b.quantity)}</b></div>
        ${b.seats ? `<div class="ck-line"><span>Ghế đã chọn</span><b>${esc(b.seats.split(',').join(', '))}</b></div>` : ''}
        ${b.vipDiscount > 0 ? `<div class="ck-line"><span>Ưu đãi khách VIP</span><b class="ck-off">−${money(b.vipDiscount)}</b></div>` : ''}
        ${b.discountAmount > 0 ? `<div class="ck-line"><span>Giảm giá</span><b class="ck-off">−${money(b.discountAmount)}</b></div>` : ''}
        <div id="cpBox">${couponBox()}</div>
        <div class="ck-line"><span>Phí dịch vụ</span><b>0 đ</b></div>
        <div class="ck-total"><span>Tổng cộng</span><b>${money(b.totalAmount)}</b></div>
        <div id="payArea">${payArea()}</div>
        <p class="ck-mini center">Mã đơn <b>${code()}</b></p></div></div>`;
  }

  function render() {
    root.innerHTML = `<div class="ck-head"><div><a class="ck-back" href="/Event/Book/${b.eventId}">‹ Chọn lại vé</a><h1>Thanh toán</h1></div>
        <div class="ck-timer" role="timer" aria-label="Thời gian giữ vé còn lại">${icon('clock', 20)}<span>Hoàn tất trong</span><b id="cd">10:00</b></div></div>
      <ol class="ck-steps" aria-label="Các bước"><li class="done">Chọn vé</li><li aria-current="step">Thanh toán</li><li>Nhận vé</li></ol>
      <div class="ck-grid"><div class="ck-main">${mainHTML()}</div><aside class="ck-side" aria-label="Tóm tắt đơn hàng">${sideHTML()}</aside></div>`;
    root.querySelectorAll('input[name=pm]').forEach(i => i.addEventListener('change', async () => {
      method = i.value;
      if (method === 'qr' && !qr) await loadQr();
      document.getElementById('panel').innerHTML = payPanel(); document.getElementById('payArea').innerHTML = payArea();
    }));
    root.addEventListener('click', async e => {
      if (e.target.closest('#pay')) { doPay({ currentTarget: e.target.closest('#pay') }); return; }
      const ca = e.target.closest('[data-act^=cp-]');
      if (ca) {
        const act = ca.dataset.act;
        if (act === 'cp-skip') { skip(ca.dataset.code); document.getElementById('cpBox').innerHTML = couponBox(); }
        else if (act === 'cp-code') {
          const v = (document.getElementById('cpCode')?.value || '').trim();
          if (!v) { toast('Hãy nhập mã giảm giá', 'error'); document.getElementById('cpCode')?.focus(); return; }
          changeCoupon(() => API.call(`/api/bookings/${id}/coupon`, { method: 'PUT', body: { code: v } }), ca);
        }
        else if (act === 'cp-apply') changeCoupon(() => API.call(`/api/bookings/${id}/coupon`, { method: 'PUT', body: { code: ca.dataset.code } }), ca);
        else if (act === 'cp-remove') changeCoupon(() => API.call(`/api/bookings/${id}/coupon`, { method: 'DELETE' }), ca);
        return;
      }
      if (e.target.closest('[data-act=retry]')) { await loadQr(); document.getElementById('panel').innerHTML = payPanel(); document.getElementById('payArea').innerHTML = payArea(); return; }
      const c = e.target.closest('[data-copy]'); if (!c) return;
      navigator.clipboard?.writeText(c.dataset.copy).then(() => { c.textContent = 'Đã chép'; setTimeout(() => { c.textContent = 'Sao chép'; }, 1500); }, () => toast('Không sao chép được', 'error'));
    });
    root.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target.id === 'cpCode') { e.preventDefault(); root.querySelector('[data-act=cp-code]')?.click(); } });
    startTimer();
    startPolling();
  }

  function startTimer() {
    clearInterval(timer);
    const end = new Date(b.expiresAt).getTime();
    const tick = () => {
      const left = Math.max(0, end - Date.now()), el = document.getElementById('cd'); if (!el) return;
      el.textContent = mmss(left);
      el.closest('.ck-timer').classList.toggle('low', left < 60000);
      if (left === 0 && !paying && !done) { clearInterval(timer); stopPolling(); checkPaid(true); }   // hết giờ: kiểm tra lần cuối xem tiền đã về chưa
    };
    tick(); timer = setInterval(tick, 500);
  }

  function expired(msg) {
    clearInterval(timer); stopPolling();
    root.innerHTML = `<div class="ck-end"><div class="ck-mark bad">${icon('x', 34)}</div><h1>${msg || 'Hết thời gian giữ vé'}</h1>
      <p>Vé đã được trả lại kho. Bạn có thể chọn lại vé để tiếp tục.</p>
      <a class="btn ck-pay" href="/Event/Book/${b.eventId}">Chọn lại vé</a></div>`;
  }

  /* Thanh toán xong → hiện màn "thành công" rồi tự mở vé có mã QR, khách không cần bấm gì */
  function success() {
    if (done) return; done = true;
    clearInterval(timer); stopPolling();
    root.innerHTML = `<div class="ck-end"><div class="ck-mark">${icon('check', 34)}</div><h1>Thanh toán thành công</h1><p>Đang chuyển sang trang đặt vé thành công…</p></div>`;
    setTimeout(() => location.replace('/Account/Order/' + id), 900);
  }

  /* Dò trạng thái đơn: server đánh dấu Paid khi nhận được tiền (webhook SePay) */
  let checking = false, payosWait = false, payosTick = 0;
  async function checkPaid(final) {
    if (checking || done) return; checking = true;
    try {
      if (payosOn && (payosBack || ((method === 'qr' || payosWait) && ++payosTick % 2 === 0))) {   // quay về từ payOS, hoặc đang chờ khách trả ở tab payOS (hỏi ~5s/lần): hỏi thẳng payOS (không phụ thuộc webhook có tới được server hay không)
        try { const r = await API.call(`/api/payments/payos/check/${id}`, { method: 'POST' }); if (r.paid) { success(); return; } } catch { /* thử lại ở lần dò sau */ }
      }
      const cur = await API.call('/api/bookings/' + id);
      if (cur.status === 'Paid') { success(); return; }
      if (cur.status !== 'Pending') { expired(cur.status === 'Cancelled' ? 'Đơn đã bị huỷ' : 'Đơn đã hết hạn giữ vé'); return; }
      if (final && new Date(cur.expiresAt).getTime() <= Date.now()) expired();
    } catch (err) {
      if (final) expired();   // lỗi mạng ở lần kiểm tra cuối: để khách chọn lại cho an toàn
    } finally { checking = false; }
  }
  function startPolling() { stopPolling(); poll = setInterval(() => checkPaid(false), 2500); }
  function stopPolling() { clearInterval(poll); poll = null; }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && b && !done) checkPaid(false); });   // quay lại từ app ngân hàng là kiểm tra ngay
  window.addEventListener('focus', () => { if (b && !done) checkPaid(false); });

  /* Mở payOS ở TAB MỚI, tab này ở lại chờ: trả tiền xong tab này tự chuyển sang "Đặt vé thành công",
     dù tab payOS quay về trang web có bị lỗi mạng hay không. */
  async function doPayos(e) {
    const btn = e.currentTarget; busy(btn, true, 'Đang mở trang thanh toán payOS…');
    const w = window.open('', '_blank');   // phải mở ngay trong lúc bấm, nếu không trình duyệt chặn popup
    try {
      const r = await API.call(`/api/payments/payos/create/${id}`, { method: 'POST' });
      if (!w) { location.href = r.checkoutUrl; return; }   // popup bị chặn: đi cùng tab như cũ
      w.location.href = r.checkoutUrl;
      payosWait = true; paying = false;
      document.getElementById('payArea').innerHTML = `<div class="ck-wait" role="status" aria-live="polite"><span class="ck-spin" aria-hidden="true"></span><span><b>Đang chờ bạn thanh toán ở tab payOS</b><small>Trả tiền xong, trang này tự chuyển sang vé. <a href="${esc(r.checkoutUrl)}" target="_blank" rel="noopener">Mở lại trang payOS</a></small></span></div>`;
      checkPaid(false);
    }
    catch (err) { try { w && w.close(); } catch { /* bỏ qua */ } toast(err.message, 'error'); busy(btn, false); }
  }

  async function doPay(e) {
    if (method === 'payos') return doPayos(e);
    const btn = e.currentTarget; paying = true; busy(btn, true, 'Đang xử lý thanh toán…');
    try { await payBooking(id); success(); }
    catch (err) { paying = false; toast(err.message, 'error'); busy(btn, false); }
  }

  (async () => {
    try { b = await API.call('/api/bookings/' + id); }
    catch (err) { root.innerHTML = `<div class="ck-end"><h1>Không mở được đơn hàng</h1><p>${esc(err.message)}</p><a class="btn ck-pay" href="/">Về trang sự kiện</a></div>`; return; }
    document.title = 'Thanh toán – ' + b.eventName + ' – TicketGo';
    if (b.status === 'Paid') { location.replace('/Account/Order/' + id); return; }
    try { payosOn = (await API.call('/api/payments/payos/enabled')).enabled === true; } catch { payosOn = false; }
    if (b.status !== 'Pending' || new Date(b.expiresAt).getTime() <= Date.now()) {
      // Đơn hết hạn nhưng khách có thể đã trả tiền (tiền về trễ / trang quay về bị lỗi): hỏi payOS trước khi báo hết hạn
      if (payosOn && b.status !== 'Cancelled') { try { const r = await API.call(`/api/payments/payos/check/${id}`, { method: 'POST' }); if (r.paid) { success(); return; } } catch { /* bỏ qua */ } }
      expired(b.status === 'Cancelled' ? 'Đơn đã bị huỷ' : 'Đơn đã hết hạn giữ vé'); return;
    }
    if (payosOn) { METHODS = { qr: { name: 'payOS – Quét mã QR / app ngân hàng', sub: 'Thanh toán thật, tự xác nhận khi tiền về', ic: METHODS.qr.ic } }; method = 'qr'; }   // QR payOS hiện ngay trên trang: khách không phải rời website, trang tự chuyển sang vé khi payOS báo đã nhận tiền
    await Promise.all([method === 'qr' ? loadQr() : Promise.resolve(), loadWallet()]);
    render();
    if (payosOn && payosCancel) toast('Bạn đã huỷ thanh toán payOS. Bấm Thanh toán để thử lại.', 'info');
    if (payosOn && payosBack) checkPaid(false);
  })();
})();
