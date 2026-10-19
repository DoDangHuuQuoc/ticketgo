(() => {
  'use strict';
  if (!requireLogin()) return;
  const root = document.getElementById('app'), mode = root.dataset.mode, id = root.dataset.id, dlg = document.getElementById('qrDialog');
  const TSTAT = { Valid: ['Hợp lệ', 'ok'], CheckedIn: ['Đã sử dụng', 'off'], Cancelled: ['Đã huỷ', 'bad'] };
  const p2 = n => String(n).padStart(2, '0');
  const when = iso => { const d = new Date(iso); return `${p2(d.getHours())}:${p2(d.getMinutes())}, ${p2(d.getDate())} Tháng ${d.getMonth() + 1}, ${d.getFullYear()}`; };
  const short = t => t.id.slice(0, 8).toUpperCase();

  /* Poster thật: ảnh nền mờ + ảnh chính object-fit: contain để không bị cắt chữ; poster vẽ sẵn (SVG) giữ nguyên */
  const tktArt = t => (t.posterUrl && !/picsum\.photos/.test(t.posterUrl))
    ? `<img class="pbg" src="${esc(t.posterUrl)}" alt="" aria-hidden="true"><img class="pfg" src="${esc(t.posterUrl)}" alt="">`
    : posterArt(t.eventName, t.posterUrl);

  function card(t, { big = false, link = false } = {}) {
    const [lbl, cls] = TSTAT[t.status] || [t.status, 'off'], dim = t.status !== 'Valid';
    return `<article class="tkt ${big ? 'tkt-big' : ''} ${dim ? 'dim' : ''}" aria-label="Vé ${esc(t.seatInfo)}">
      <div class="tkt-l">
        <div class="tkt-poster poster">${tktArt(t)}</div>
        <div class="tkt-info">
          <span class="tkt-st ${cls}">${lbl}</span>
          <h2>${esc(t.eventName)}</h2>
          <div class="ck-row">${icon('cal', 18)}<span>${when(t.startsAt)}</span></div>
          <div class="ck-row">${icon('pin', 18)}<span>${esc(t.venue)}</span></div>
          <dl class="tkt-meta"><div><dt>Hạng vé</dt><dd>${esc(t.typeName)}</dd></div><div><dt>Khu vực</dt><dd>${esc(t.zone)}</dd></div><div><dt>${String(t.seatInfo).startsWith('#') ? 'Vé số' : 'Ghế'}</dt><dd>${esc(t.seatInfo)}</dd></div></dl>
        </div>
      </div>
      <div class="tkt-r">
        <button type="button" class="tkt-qr" data-qr="${t.id}" aria-label="Phóng to mã QR"><span class="skel qr-ph"></span></button>
        <b class="tkt-code">${short(t)}</b>
        <p>${dim ? (t.status === 'CheckedIn' ? 'Vé đã được soát lúc vào cổng.' : 'Vé này không còn hiệu lực.') : 'Đưa mã QR này cho nhân viên soát vé tại cổng.'}</p>
        ${link ? `<a class="btn tkt-btn" href="/Account/Ticket/${t.id}">Xem chi tiết vé</a>` : ''}
      </div></article>`;
  }

  function loadQr(list) {
    list.forEach(t => API.blobUrl(`/api/tickets/${t.id}/qr`).then(url => {
      const b = root.querySelector(`[data-qr="${t.id}"]`); if (!b) return;
      b.innerHTML = `<img src="${url}" alt="Mã QR vé ${esc(t.seatInfo)}">`; b.dataset.url = url;
    }).catch(() => { }));
  }

  root.addEventListener('click', e => {
    const q = e.target.closest('.tkt-qr'); if (q?.dataset.url) { document.getElementById('qrBig').src = q.dataset.url; dlg.showModal(); return; }
    if (e.target.closest('#print')) window.print();
    const dl = e.target.closest('#dl'); if (dl) { const a = document.createElement('a'); a.href = dl.dataset.url; a.download = 've-' + dl.dataset.code + '.png'; a.click(); }
  });
  document.getElementById('qrClose').addEventListener('click', () => dlg.close());
  dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });

  /* Thêm vào lịch: dùng thông tin sự kiện của vé */
  const calArgs = t => ({ id: t.eventId || t.id, name: t.eventName, venue: t.venue, startsAt: t.startsAt, url: location.origin + (t.eventId ? '/Event/Detail/' + t.eventId : location.pathname) });
  const calBtns = t => `<a class="btn ck-ghost" href="${esc(CAL.gcal(calArgs(t)))}" target="_blank" rel="noopener">Google Lịch</a>`;

  const fail = msg => { root.innerHTML = `<div class="ck-end"><h1>Không mở được trang này</h1><p>${esc(msg)}</p><a class="btn ck-pay" href="/Account/MyBookings">Về Vé của tôi</a></div>`; };

  async function order() {
    const [bk, list] = await Promise.all([API.call('/api/bookings/' + id), API.call('/api/tickets/booking/' + id)]);
    document.title = 'Đơn hàng – ' + bk.eventName + ' – TicketGo';
    if (bk.status === 'Pending') { location.replace('/Event/Checkout/' + id); return; }
    const paid = bk.status === 'Paid';
    root.innerHTML = `<div class="ck-end ck-order"><div class="ck-mark ${paid ? '' : 'bad'}">${icon(paid ? 'check' : 'x', 34)}</div>
        <h1>${paid ? 'Đặt vé thành công' : 'Đơn hàng không còn hiệu lực'}</h1>
        <p>${paid ? `Thanh toán thành công. Cảm ơn bạn! ${bk.quantity} vé đã được gửi tới email và lưu trong mục Vé của tôi.` : 'Đơn này đã hết hạn hoặc bị huỷ.'}</p>
        <p class="ck-mini">Mã đơn <b>TG${id.replace(/-/g, '').slice(0, 8).toUpperCase()}</b> · Tổng ${money(bk.totalAmount)}</p></div>
      <div class="tkt-list">${list.map(t => card(t, { link: true })).join('')}</div>
      <div class="ck-actions"><a class="btn ck-pay" href="/Account/MyBookings">Vé của tôi</a><a class="btn ck-ghost" href="/">Xem thêm sự kiện</a>${paid && list.length ? calBtns(list[0]) : ''}</div>`;
    loadQr(list);
  }

  async function ticket() {
    const t = await API.call('/api/tickets/' + id);
    document.title = 'Vé – ' + t.eventName + ' – TicketGo';
    root.innerHTML = `<div class="ck-head"><div><a class="ck-back" href="/Account/MyBookings">‹ Vé của tôi</a><h1>Vé của bạn</h1></div>
        <div class="ck-actions tight"><button class="btn ck-ghost" id="print" type="button">In vé</button><button class="btn ck-pay" id="dl" type="button" data-code="${short(t)}" disabled>Tải mã QR</button>${t.status === 'Valid' ? calBtns(t) : ''}</div></div>
      ${card(t, { big: true })}
      <ul class="ck-tips"><li>Mỗi mã QR chỉ dùng được một lần. Không chia sẻ ảnh vé công khai.</li><li>Nên tăng độ sáng màn hình khi quét tại cổng.</li></ul>`;
    loadQr([t]);
    const iv = setInterval(() => { const b = root.querySelector('[data-qr]'); if (b?.dataset.url) { const d = document.getElementById('dl'); d.dataset.url = b.dataset.url; d.disabled = false; clearInterval(iv); } }, 300);
  }

  (mode === 'order' ? order() : ticket()).catch(err => fail(err.message));
})();
