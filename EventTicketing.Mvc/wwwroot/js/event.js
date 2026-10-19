(() => {
  'use strict';
  const root = document.getElementById('app'), id = root.dataset.eventId;
  let ev = null;
  const p2 = n => String(n).padStart(2, '0');
  const whenText = () => { const d = new Date(ev.startsAt); return `${p2(d.getHours())}:${p2(d.getMinutes())}, ${p2(d.getDate())} Tháng ${d.getMonth() + 1}, ${d.getFullYear()}`; };
  const venueParts = () => { const v = String(ev.venue || '').trim(), i = v.indexOf(','); return i > 0 ? [v.slice(0, i).trim(), v.slice(i + 1).trim()] : [v, '']; };
  const allSold = () => !ev.ticketTypes.length || ev.ticketTypes.every(t => t.soldOut);
  const stockText = t => t.soldOut ? 'Hết vé' : (t.availableQuantity <= 20 ? `Chỉ còn ${t.availableQuantity} vé` : `Còn ${t.availableQuantity} vé`);
  const bookHref = () => '/Event/Book/' + id;

  /* ----- đếm ngược, chia sẻ, thêm vào lịch ----- */
  const startDate = () => new Date(ev.startsAt);
  const calArgs = () => ({ id, name: ev.name, venue: ev.venue, startsAt: ev.startsAt, url: location.href });
  const gcalHref = () => CAL.gcal(calArgs());

  async function shareEvent() {
    const data = { title: ev.name, text: `${ev.name} – ${whenText()}`, url: location.href };
    try { if (navigator.share) { await navigator.share(data); return; } }
    catch (e) { if (e && e.name === 'AbortError') return; }   // khách tự đóng hộp chia sẻ: không báo lỗi
    try { await navigator.clipboard.writeText(location.href); toast('Đã sao chép liên kết sự kiện', 'ok'); }
    catch { toast('Không sao chép được, hãy sao chép địa chỉ trên thanh trình duyệt', 'error'); }
  }

  function tick() {
    const el = document.getElementById('cd'); if (!el) return;
    const ms = startDate().getTime() - Date.now();
    if (ms <= 0) { el.classList.add('live'); el.innerHTML = '<b>Sự kiện đã bắt đầu</b>'; return; }
    const s = Math.floor(ms / 1000), d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
    el.innerHTML = `<span>Bắt đầu sau</span> <b>${d > 0 ? d + ' ngày ' : ''}${p2(h)}:${p2(m)}:${p2(s % 60)}</b>`;
  }

  // Ghi nhớ sự kiện vừa xem (chỉ lưu id trên trình duyệt) để trang chủ hiện mục "Bạn đã xem gần đây"
  function rememberView() {
    try {
      const a = JSON.parse(localStorage.getItem('tg_recent') || '[]').filter(x => x !== id); a.unshift(id);
      localStorage.setItem('tg_recent', JSON.stringify(a.slice(0, 8)));
    } catch { /* trình duyệt chặn lưu trữ: bỏ qua */ }
  }

  function heroHTML() {
    const prices = ev.ticketTypes.map(t => t.price), from = prices.length ? Math.min(...prices) : null, [vn, va] = venueParts();
    return `<section class="evd-hero" aria-label="Thông tin sự kiện">
      <div class="evd-info">
        <h1 class="evd-title">${esc(ev.name)}</h1>
        <div class="evd-meta">
          <div class="evd-row"><span class="evd-ic">${icon('cal', 22)}</span><b class="g">${whenText()}</b></div>
          <div class="evd-row"><span class="evd-ic">${icon('pin', 22)}</span><div><b class="g">${esc(vn)}</b>${va ? `<span class="addr">${esc(va)}</span>` : ''}</div></div>
        </div>
        <div class="evd-count" id="cd" role="timer" aria-live="off"></div>
        <div class="evd-acts" aria-label="Chia sẻ và thêm vào lịch">
          <button type="button" class="btn btn-ghost btn-sm" id="btnShare">${icon('share', 16)} Chia sẻ</button>
          <a class="btn btn-ghost btn-sm" id="btnGcal" href="${gcalHref()}" target="_blank" rel="noopener">${icon('cal', 16)} Google Lịch</a>
        </div>
        <div class="evd-cta">
          <div class="evd-price"><span>Giá từ</span> <b>${from === null ? '—' : money(from)}</b><i class="chev">${icon('chev', 18)}</i></div>
          <a class="evd-buy" id="heroBuy" href="${bookHref()}" ${allSold() ? 'aria-disabled="true"' : ''}>${allSold() ? 'Hết vé' : 'Mua vé ngay'}</a>
        </div>
      </div>
      <div class="evd-art"><div class="evd-poster poster">${posterArt(ev.name, ev.posterUrl)}</div></div>
    </section>`;
  }

  const ticketRows = () => ev.ticketTypes.map(t => `<li class="evd-tk ${t.soldOut ? 'out' : ''}"><div><b>${esc(t.name)}</b><span class="muted">${esc(t.zone)}${t.seatMode === 2 ? ' · chọn ghế khi mua' : t.seatMode === 1 ? ' · có số ghế' : ''}</span></div><div class="evd-tk-r"><b>${money(t.price)}</b><span class="${t.soldOut ? 'out' : (t.availableQuantity <= 20 ? 'low' : '')}">${stockText(t)}</span></div></li>`).join('');

  function mainHTML() {
    const desc = (ev.description || '').trim();
    return `${desc ? `<section class="evd-card" aria-labelledby="abT"><h2 id="abT">Giới thiệu</h2><div class="evd-card-b about-text">${esc(desc)}</div></section>` : ''}
      <section class="evd-card" aria-labelledby="tkT"><h2 id="tkT">Thông tin vé</h2><div class="evd-card-b"><ul class="evd-tks" id="tkList">${ticketRows() || '<li class="muted">Chưa mở bán vé.</li>'}</ul>
        <a class="evd-buy evd-buy-inline" href="${bookHref()}" ${allSold() ? 'aria-disabled="true"' : ''}>${allSold() ? 'Hết vé' : 'Chọn vé & xem sơ đồ'}</a></div></section>
      <section class="evd-card notes" aria-labelledby="ntT"><h2 id="ntT">Trước khi mua</h2><div class="evd-card-b"><ul>
        <li>Mỗi lần đặt tối đa 6 vé.</li>
        <li>Vé được giữ trong 10 phút. Quá hạn mà chưa thanh toán, vé tự trả lại kho.</li>
        <li>Thanh toán xong, mã QR được gửi qua email và lưu trong mục Vé của tôi.</li></ul></div></section>`;
  }

  /* ----- sự kiện tương tự: cùng loại trước, thiếu thì lấy sự kiện khác sắp diễn ra ----- */
  const simCard = e => { const d = new Date(e.startsAt);
    return `<a class="ev" href="/Event/Detail/${e.id}"><div class="poster">${posterArt(e.name, e.posterUrl)}<div class="date-tag"><b>${p2(d.getDate())}</b><span>Th${d.getMonth() + 1}</span></div></div>
      <div class="ev-body"><h3>${esc(e.name)}</h3><div class="ev-meta">${icon('pin', 16)}<span>${esc(e.venue)}</span></div></div><div class="ev-perf" aria-hidden="true"></div>
      <div class="ev-stub"><span class="ev-price">${e.minPrice != null ? `<small>Từ</small>${money(e.minPrice)}` : ''}</span><span class="muted">${e.available === 0 ? 'Hết vé' : `Còn ${e.available} vé`}</span></div></a>`; };
  async function loadSimilar() {
    try {
      const all = await API.call('/api/events'), me = all.find(e => e.id === id), cat = me && me.category;
      const rows = all.filter(e => e.id !== id)
        .sort((a, b) => (b.category === cat && cat ? 1 : 0) - (a.category === cat && cat ? 1 : 0) || new Date(a.startsAt) - new Date(b.startsAt)).slice(0, 3);
      const sec = document.getElementById('simSec'); if (!rows.length || !sec) return;
      document.getElementById('simList').innerHTML = rows.map(simCard).join(''); sec.hidden = false;
    } catch { /* không tải được gợi ý thì thôi, không ảnh hưởng trang */ }
  }

  async function refreshStock() {
    try {
      const fresh = await API.call(`/api/events/${id}`); ev.ticketTypes = fresh.ticketTypes;
      const l = document.getElementById('tkList'); if (l && ev.ticketTypes.length) l.innerHTML = ticketRows();
    } catch { /* bỏ qua lỗi polling */ }
  }

  (async () => {
    try { ev = await API.call(`/api/events/${id}`); }
    catch (err) { root.innerHTML = `<div class="empty">${esc(err.message)}. <a href="/">Về trang sự kiện</a></div>`; return; }
    document.title = ev.name + ' – TicketGo';
    root.innerHTML = `${heroHTML()}<div class="evd-body evd-one"><div class="evd-main">${mainHTML()}</div></div>
      <section class="evd-sim" id="simSec" aria-labelledby="simT" hidden><h2 id="simT">Sự kiện tương tự</h2><div class="grid-events" id="simList"></div></section>`;
    document.querySelectorAll('[aria-disabled=true]').forEach(a => a.addEventListener('click', e => e.preventDefault()));
    document.getElementById('btnShare').addEventListener('click', shareEvent);
    tick(); setInterval(tick, 1000); rememberView(); loadSimilar();
    setInterval(refreshStock, 10000);
  })();
})();
