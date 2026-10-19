(async () => {
  'use strict';
  const $ = id => document.getElementById(id);
  const listEl = $('list'), heroEl = $('hero'), qEl = $('q'), menu = $('cats');   // #cats = menu đầu trang (_Layout)
  const own = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
  const p2 = n => String(n).padStart(2, '0');

  const CATS = { Concert: 'Ca nhạc', Sport: 'Thể thao', Theatre: 'Sân khấu' };
  const WHEN = { all: 'Mọi lúc', today: 'Hôm nay', weekend: 'Cuối tuần này', '7d': '7 ngày tới', '30d': '30 ngày tới', custom: 'Chọn ngày' };
  const PRICES = { '': 'Mọi mức giá', '0-500000': 'Dưới 500.000 đ', '500000-1000000': '500.000 – 1.000.000 đ', '1000000-2000000': '1.000.000 – 2.000.000 đ', '2000000-': 'Trên 2.000.000 đ' };
  const STS = { all: 'Tất cả', avail: 'Còn vé', low: 'Sắp hết vé' };
  const LOW = 0.2;   // "Sắp hết vé" = còn dưới 20% tổng số vé (dùng chung cho nhãn, mục nổi bật và bộ lọc)
  const isLow = e => e.total > 0 && e.available > 0 && e.available / e.total < LOW;

  /* ---------- Trạng thái bộ lọc: nguồn duy nhất, đồng bộ với URL (chia sẻ / nút Back đều đúng) ---------- */
  const blank = () => ({ q: '', cats: new Set(), when: 'all', from: '', to: '', price: '', st: 'all', sort: 'date' });
  const SORTS = {
    date: (a, b) => new Date(a.startsAt) - new Date(b.startsAt),
    priceAsc: (a, b) => (a.minPrice ?? Infinity) - (b.minPrice ?? Infinity),
    priceDesc: (a, b) => (b.minPrice ?? -1) - (a.minPrice ?? -1),
    avail: (a, b) => b.available - a.available
  };
  const ISO = /^\d{4}-\d{2}-\d{2}$/;

  function fromUrl() {
    const p = new URLSearchParams(location.search), s = blank();
    s.q = (p.get('q') || '').trim();
    (p.get('cat') || '').split(',').forEach(c => { if (own(CATS, c)) s.cats.add(c); });
    const w = p.get('when'); if (w && own(WHEN, w)) s.when = w;
    if (ISO.test(p.get('from') || '')) s.from = p.get('from');
    if (ISO.test(p.get('to') || '')) s.to = p.get('to');
    if (s.from || s.to) s.when = 'custom';
    if (s.when !== 'custom') { s.from = ''; s.to = ''; }
    const pr = p.get('price'); if (pr !== null && own(PRICES, pr)) s.price = pr;
    const st = p.get('st') || p.get('f');   // ?f=avail là liên kết cũ
    if (st === 'avail' || st === 'low') s.st = st;
    const so = p.get('sort'); if (so && own(SORTS, so)) s.sort = so;
    return s;
  }

  let all = [], loaded = false, S = fromUrl();
  qEl.value = S.q;

  const whenOn = () => S.when !== 'all' && (S.when !== 'custom' || !!(S.from || S.to));   // "Chọn ngày" mà chưa chọn ngày nào thì chưa lọc gì
  const groupsOn = () => (S.cats.size ? 1 : 0) + (whenOn() ? 1 : 0) + (S.price ? 1 : 0) + (S.st !== 'all' ? 1 : 0);
  const filtering = () => !!S.q || groupsOn() > 0 || S.sort !== 'date';

  function toUrl() {
    const p = new URLSearchParams();
    if (S.q.trim()) p.set('q', S.q.trim());
    if (S.cats.size) p.set('cat', Object.keys(CATS).filter(c => S.cats.has(c)).join(','));
    if (S.when !== 'all') p.set('when', S.when);
    if (S.when === 'custom') { if (S.from) p.set('from', S.from); if (S.to) p.set('to', S.to); }
    if (S.price) p.set('price', S.price);
    if (S.st !== 'all') p.set('st', S.st);
    if (S.sort !== 'date') p.set('sort', S.sort);
    return p.toString() ? '/?' + p : '/';
  }
  function setUrl(push) {
    const u = toUrl(); if (u !== location.pathname + location.search) history[push ? 'pushState' : 'replaceState'](null, '', u);
  }

  /* ---------- Khoảng thời gian theo giờ máy khách ---------- */
  const addDays = (d, n) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
  const endOf = d => new Date(d.getFullYear(), d.getMonth(), d.getDate(), 23, 59, 59, 999).getTime();
  const dayStart = v => new Date(v + 'T00:00:00').getTime(), dayEnd = v => new Date(v + 'T23:59:59.999').getTime();
  function range() {
    const n = new Date(), today = new Date(n.getFullYear(), n.getMonth(), n.getDate());
    switch (S.when) {
      case 'today': return [today.getTime(), endOf(today)];
      case 'weekend': {
        const dow = today.getDay();
        const start = dow === 0 ? today : dow === 6 ? today : addDays(today, 6 - dow);
        const end = dow === 0 ? today : addDays(start, 1);
        return [start.getTime(), endOf(end)];
      }
      case '7d': return [today.getTime(), endOf(addDays(today, 7))];
      case '30d': return [today.getTime(), endOf(addDays(today, 30))];
      case 'custom': {
        let a = S.from ? dayStart(S.from) : null, b = S.to ? dayEnd(S.to) : null;
        if (a !== null && b !== null && a > b) { const f = S.from; a = dayStart(S.to); b = dayEnd(f); }   // nhập ngược thì tự đảo
        return [a, b];
      }
      default: return [null, null];
    }
  }
  const inPrice = e => {
    if (!S.price) return true;
    if (e.minPrice == null) return false;
    const [lo, hi] = S.price.split('-'); return e.minPrice >= Number(lo || 0) && (hi === '' || e.minPrice < Number(hi));
  };
  const fmtDay = v => { const [y, m, d] = v.split('-'); return `${d}/${m}/${y}`; };

  /* ---------- Đồng bộ giao diện ---------- */
  let panelOpen = groupsOn() > 0;
  const mark = (box, attr, test, prop) => $(box).querySelectorAll('button').forEach(b => b.setAttribute(prop, test(b.dataset[attr])));
  const hl = { low: false, soon: false, art: false, recent: false };
  const applyHl = () => {   // đang lọc thì ẩn các mục gợi ý để kết quả hiện ngay dưới bộ lọc
    const f = filtering();
    $('lowSec').hidden = f || !hl.low; $('soonSec').hidden = f || !hl.soon; $('artSec').hidden = f || !hl.art; $('recentSec').hidden = f || !hl.recent;
  };
  const menuKey = () => S.cats.size === 1 ? 'c:' + [...S.cats][0] : !filtering() ? 'all' : '';

  function activeChips() {
    const a = [];
    if (S.q.trim()) a.push(['q', `Từ khoá: “${S.q.trim()}”`]);
    Object.keys(CATS).filter(c => S.cats.has(c)).forEach(c => a.push(['cat:' + c, CATS[c]]));
    if (S.when === 'custom') { if (S.from || S.to) a.push(['when', `${S.from ? fmtDay(S.from) : '…'} → ${S.to ? fmtDay(S.to) : '…'}`]); }
    else if (S.when !== 'all') a.push(['when', WHEN[S.when]]);
    if (S.price) a.push(['price', PRICES[S.price]]);
    if (S.st !== 'all') a.push(['st', STS[S.st]]);
    return a;
  }

  function syncUI() {
    mark('catChips', 'c', v => String(S.cats.has(v)), 'aria-pressed');
    mark('whenChips', 'w', v => String(v === S.when), 'aria-checked');
    mark('priceChips', 'p', v => String(v === S.price), 'aria-checked');
    mark('stChips', 's', v => String(v === S.st), 'aria-checked');
    $('fbDates').hidden = S.when !== 'custom';
    if ($('dFrom').value !== S.from) $('dFrom').value = S.from;
    if ($('dTo').value !== S.to) $('dTo').value = S.to;
    if (qEl.value !== S.q) qEl.value = S.q;
    $('fSort').value = S.sort;

    const n = groupsOn(), badge = $('fbN');
    badge.hidden = n === 0; badge.textContent = n;
    $('fbToggle').setAttribute('aria-expanded', String(panelOpen)); $('fbPanel').classList.toggle('open', panelOpen);

    const chips = activeChips();
    $('fbBar').hidden = !filtering();
    $('fbActive').innerHTML = chips.map(([k, l]) => `<button type="button" class="fb-chip" data-rm="${esc(k)}" aria-label="Bỏ lọc ${esc(l)}">${esc(l)}<span aria-hidden="true">×</span></button>`).join('');

    const k = menuKey();
    menu?.querySelectorAll('a[data-m]').forEach(a => { if (k && a.dataset.m === k) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
  }
  function apply(push) { syncUI(); setUrl(push); renderList(); }
  const reset = () => { S = blank(); };

  /* ---------- Sự kiện điều khiển ---------- */
  $('catChips').addEventListener('click', e => {
    const b = e.target.closest('button[data-c]'); if (!b) return;
    S.cats.has(b.dataset.c) ? S.cats.delete(b.dataset.c) : S.cats.add(b.dataset.c); apply(true);
  });
  $('whenChips').addEventListener('click', e => {
    const b = e.target.closest('button[data-w]'); if (!b) return;
    S.when = b.dataset.w; if (S.when !== 'custom') { S.from = ''; S.to = ''; } apply(true);
    if (S.when === 'custom') $('dFrom').focus();
  });
  $('priceChips').addEventListener('click', e => { const b = e.target.closest('button[data-p]'); if (b) { S.price = b.dataset.p; apply(true); } });
  $('stChips').addEventListener('click', e => { const b = e.target.closest('button[data-s]'); if (b) { S.st = b.dataset.s; apply(true); } });
  qEl.addEventListener('input', () => { S.q = qEl.value; apply(false); });
  $('dFrom').addEventListener('change', e => { S.from = e.target.value; apply(false); });
  $('dTo').addEventListener('change', e => { S.to = e.target.value; apply(false); });
  $('fSort').addEventListener('change', e => { S.sort = e.target.value; apply(true); });
  $('fbToggle').addEventListener('click', () => { panelOpen = !panelOpen; syncUI(); });
  $('advReset').addEventListener('click', () => { reset(); apply(true); });
  $('fbActive').addEventListener('click', e => {
    const b = e.target.closest('[data-rm]'); if (!b) return;
    const k = b.dataset.rm;
    if (k === 'q') S.q = ''; else if (k.startsWith('cat:')) S.cats.delete(k.slice(4));
    else if (k === 'when') { S.when = 'all'; S.from = ''; S.to = ''; } else if (k === 'price') S.price = ''; else if (k === 'st') S.st = 'all';
    apply(true);
  });
  listEl.addEventListener('click', e => { if (e.target.closest('#emptyReset')) { reset(); apply(true); } });

  // Menu đầu trang (lối tắt tới loại sự kiện): giữ các bộ lọc khác, chỉ đổi loại
  menu?.addEventListener('click', e => {
    const a = e.target.closest('a[data-m]'); if (!a || a.dataset.m === 'promo' || e.metaKey || e.ctrlKey || e.shiftKey || e.button) return;   // 'promo' là trang khác: để trình duyệt tự chuyển trang
    e.preventDefault();
    const k = a.dataset.m;
    if (k === 'all') reset(); else if (k.startsWith('c:')) S.cats = new Set([k.slice(2)]);
    apply(true);
    $('filter').scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
  window.addEventListener('popstate', () => { S = fromUrl(); panelOpen = groupsOn() > 0 || panelOpen; syncUI(); renderList(); });

  listEl.innerHTML = Array.from({ length: 3 }, () => '<div class="skel" style="height:360px;border-radius:20px"></div>').join('');

  const badges = e => {
    const b = [];
    if (CATS[e.category]) b.push(`<span class="badge b-cat">${CATS[e.category]}</span>`);
    if (e.total > 0) {
      if (e.available === 0) b.push('<span class="badge b-out">Hết vé</span>');
      else if (isLow(e)) b.push('<span class="badge b-low">Sắp hết vé</span>');
    }
    return b.join('');
  };

  /* Mã công khai: chọn mã giảm nhiều nhất cho giá vé thấp nhất của sự kiện (cùng quy tắc với CouponService) */
  let coupons = [];
  const deal = e => {
    if (e.minPrice == null || !coupons.length) return null;
    let best = null;
    for (const c of coupons) {
      if (c.eventId && c.eventId !== e.id) continue;
      if (e.minPrice < (c.minOrder || 0)) continue;
      let off = c.type === 'Percent' ? Math.round(e.minPrice * c.value / 100) : c.value;
      if (c.maxDiscount > 0 && off > c.maxDiscount) off = c.maxDiscount;
      off = Math.max(0, Math.min(off, e.minPrice - 1000));
      if (off > 0 && (!best || off > best.off)) best = { code: c.code, off, price: e.minPrice - off };
    }
    return best;
  };

  const card = e => {
    const d = dateParts(e.startsAt);
    return `<a class="ev" href="/Event/Detail/${e.id}">
      <div class="poster">${posterArt(e.name, e.posterUrl)}
        <div class="date-tag"><b>${d.day}</b><span>${d.mon}</span></div>
        <div class="badges">${badges(e)}</div></div>
      <div class="ev-body"><h3>${esc(e.name)}</h3>
        <div class="ev-meta">${icon('pin', 16)}<span>${esc(e.venue)}</span></div>
        <div class="ev-meta">${icon('clock', 16)}<span>${d.wd}, ${d.time}</span></div></div>
      <div class="ev-perf" aria-hidden="true"></div>
      <div class="ev-stub"><span class="ev-price">${e.minPrice != null ? (dl => dl ? `<small>Từ <s>${money(e.minPrice)}</s></small>${money(dl.price)}` : `<small>Từ</small>${money(e.minPrice)}`)(deal(e)) : ''}</span>
        <span class="muted">${e.available === 0 ? 'Hết vé' : `Còn ${e.available} vé`}</span></div></a>`;
  };

  function renderHero() {
    const e = all.find(x => x.available > 0) || all[0];
    if (!e) { heroEl.innerHTML = ''; return; }
    const d = dateParts(e.startsAt);
    heroEl.innerHTML = `<div class="hx-wrap"><a class="hx" href="/Event/Detail/${e.id}" aria-label="${esc(e.name)}">
      <div class="hx-main"><div class="poster">${posterArt(e.name, e.posterUrl)}</div><div class="hx-shade"></div>
        <div class="hx-top"><span class="hx-kicker">Sự kiện nổi bật</span><div class="hx-badges">${badges(e)}</div></div>
        <div class="hx-copy"><h2>${esc(e.name)}</h2>
          <div class="hx-facts"><span>${icon('pin', 16)}${esc(e.venue)}</span><span>${icon('clock', 16)}${esc(d.wd)}, ${d.time}</span></div></div></div>
      <div class="hx-stub"><div class="hx-date"><b>${d.day}</b><span>${d.mon}</span></div>
        <div class="hx-price">${e.minPrice != null ? (dl => dl
          ? `<small>Giá từ <s>${money(e.minPrice)}</s></small><b>${money(dl.price)}</b><span class="hx-deal" title="Mã ${esc(dl.code)}">Mã ${esc(dl.code)} · -${money(dl.off)}</span>`
          : `<small>Giá từ</small><b>${money(e.minPrice)}</b>`)(deal(e)) : ''}</div>
        <span class="hx-go">Xem vé và giá ${icon('chev', 16)}</span>
        <div class="hx-bar" aria-hidden="true"></div><div class="hx-code" aria-hidden="true">TG-${esc(String(e.id).slice(0, 6).toUpperCase())}</div></div>
    </a></div>`;
  }

  function renderList() {
    if (!loaded) return;   // chưa tải xong: giữ khung chờ
    const q = fold(S.q), [t0, t1] = range();
    const rows = all.filter(e => {
      if (S.cats.size && !S.cats.has(e.category)) return false;
      if (q && !fold(e.name).includes(q) && !fold(e.venue).includes(q)) return false;
      const t = new Date(e.startsAt).getTime();
      if ((t0 !== null && t < t0) || (t1 !== null && t > t1)) return false;
      if (S.st === 'avail' && e.available <= 0) return false;
      if (S.st === 'low' && !isLow(e)) return false;
      return inPrice(e);
    }).sort(SORTS[S.sort]);
    $('count').textContent = all.length ? `${rows.length} / ${all.length} sự kiện` : '';
    applyHl(); syncUI();
    listEl.innerHTML = rows.length ? rows.map(card).join('')
      : `<div class="empty"><p class="strong" style="color:var(--ink)">Không có sự kiện nào khớp</p><p>Thử bỏ bớt điều kiện lọc hoặc đổi từ khoá.</p>${filtering() ? '<button type="button" class="btn btn-primary" id="emptyReset" style="margin-top:14px">Xoá bộ lọc</button>' : ''}</div>`;
  }

  /* Mục nổi bật: Sắp hết vé (còn dưới 20% và còn ít nhất 1 vé) và Sắp diễn ra (14 ngày tới) */
  function renderHighlights() {
    const low = all.filter(isLow).sort((a, b) => a.available / a.total - b.available / b.total).slice(0, 3);
    const soon = all.filter(e => new Date(e.startsAt).getTime() - Date.now() <= 14 * 864e5)
      .sort(SORTS.date).slice(0, 3);
    hl.low = low.length > 0; $('lowList').innerHTML = low.map(card).join('');
    hl.soon = soon.length > 0; $('soonList').innerHTML = soon.map(card).join('');
    // Đã xem gần đây: id lưu ở trang chi tiết; chỉ hiện sự kiện còn mở bán (có trong danh sách), tối đa 3
    let ids = []; try { ids = JSON.parse(localStorage.getItem('tg_recent') || '[]'); } catch { ids = []; }
    const recent = ids.map(rid => all.find(e => e.id === rid)).filter(Boolean).slice(0, 3);
    hl.recent = recent.length > 0; $('recentList').innerHTML = recent.map(card).join('');
  }

  /* Nghệ sĩ yêu thích: tải riêng, lỗi hay chưa có nghệ sĩ thì chỉ ẩn mục này, không ảnh hưởng danh sách sự kiện */
  async function loadArtists() {
    try {
      const list = (await API.call('/api/favorites?kind=Artist')).slice(0, 6);   // API xếp nhiều đánh giá nhất lên đầu
      hl.art = list.length > 0;
      $('artList').innerHTML = list.map(it => `<a class="fv-card fv-mini" href="/Favorites" aria-label="${esc(it.name)} – xem và chấm sao">${FV.avatar(it)}<h3>${esc(it.name)}</h3>${FV.score(it)}</a>`).join('');
      applyHl();
    } catch { hl.art = false; }
  }
  loadArtists();
  $('recentClear').addEventListener('click', () => { try { localStorage.removeItem('tg_recent'); } catch { /* bỏ qua */ } hl.recent = false; applyHl(); });

  try {
    all = await API.call('/api/events'); loaded = true;
    try { coupons = await API.call('/api/coupons/public'); } catch { coupons = []; }   // lỗi mã giảm giá không được làm hỏng trang chủ
    renderHero();
    if (!all.length) { listEl.innerHTML = '<div class="empty">Chưa có sự kiện nào sắp diễn ra.</div>'; syncUI(); return; }
    renderHighlights();
    renderList();   // renderList cũng đồng bộ menu + bộ lọc theo tham số URL
  } catch (err) {
    heroEl.innerHTML = '';
    listEl.innerHTML = `<div class="empty">Không tải được danh sách sự kiện: ${esc(err.message)}</div>`;
  }
})();
