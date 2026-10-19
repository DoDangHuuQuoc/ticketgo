'use strict';

/* ---------- API ---------- */
const API = {
  token: () => localStorage.getItem('jwt'),
  // Địa chỉ Web API (project EventTicketing.Api). Layout gán window.API_BASE; để trống = cùng máy chủ với trang.
  url: p => (typeof p === 'string' && p.startsWith('/api/') ? (window.API_BASE || '') + p : p),
  // Thông báo dễ hiểu khi máy chủ không trả JSON {message} (mất mạng, 502/503 của hosting, lỗi kiểm tra dữ liệu...)
  explain(status, data) {
    if (data && data.message) return data.message;
    if (data && data.errors) { const f = Object.values(data.errors).flat()[0]; if (f) return f; }
    if (data && data.title && status !== 500) return data.title;
    if (status === 400) return 'Dữ liệu chưa hợp lệ. Vui lòng kiểm tra lại.';
    if (status === 401) return 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.';
    if (status === 403) return 'Bạn không có quyền thực hiện thao tác này.';
    if (status === 404) return 'Không tìm thấy dữ liệu yêu cầu.';
    if (status === 429) return 'Bạn thao tác quá nhanh. Vui lòng thử lại sau ít phút.';
    if (status >= 500) return 'Máy chủ đang bận hoặc gặp sự cố. Vui lòng thử lại sau ít phút.';
    return 'Có lỗi xảy ra (mã ' + status + ').';
  },
  async call(path, { method = 'GET', body, headers = {} } = {}) {
    const h = { ...(method === 'GET' ? {} : { 'Content-Type': 'application/json' }), ...headers };   // GET không có body: bỏ Content-Type để khỏi bị preflight CORS
    if (API.token()) h.Authorization = 'Bearer ' + API.token();
    let r;
    try { r = await fetch(API.url(path), { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body) }); }
    catch { throw Object.assign(new Error('Không kết nối được máy chủ. Hãy kiểm tra mạng rồi thử lại.'), { status: 0, data: {} }); }
    const data = await r.json().catch(() => ({}));
    if (!r.ok) {
      if (r.status === 401 && API.token()) { localStorage.removeItem('jwt'); }
      throw Object.assign(new Error(API.explain(r.status, data)), { status: r.status, data });
    }
    return data;
  },
  async blobUrl(path) {
    const r = await fetch(API.url(path), { headers: { Authorization: 'Bearer ' + API.token() } });
    if (!r.ok) throw new Error('Không tải được ảnh');
    return URL.createObjectURL(await r.blob());
  }
};

/* ---------- Helpers ---------- */
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const money = n => Number(n || 0).toLocaleString('vi-VN') + ' đ';
const fold = s => String(s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
const mmss = ms => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`;
const compact = n => n >= 1e9 ? (n / 1e9).toFixed(1).replace('.0', '') + ' tỷ' : n >= 1e6 ? (n / 1e6).toFixed(n >= 1e7 ? 0 : 1).replace('.0', '') + ' tr' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(Math.round(n));

function dateParts(iso) {
  const d = new Date(iso);
  return {
    day: d.getDate(), mon: 'thg ' + (d.getMonth() + 1),
    wd: ['Chủ nhật', 'Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7'][d.getDay()],
    time: d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }),
    full: d.toLocaleDateString('vi-VN', { weekday: 'long', day: '2-digit', month: '2-digit', year: 'numeric' })
  };
}

function toast(msg, type = 'info') {
  const box = document.getElementById('toast'); if (!box) return;
  const el = document.createElement('div');
  el.className = 'toast ' + type; el.textContent = msg;
  box.appendChild(el); setTimeout(() => el.remove(), 4500);
  // Popover nằm ở "top layer": hiện trên cả hộp thoại <dialog>. Ẩn rồi hiện lại để luôn nằm trên cùng.
  try { box.hidePopover(); box.showPopover(); } catch { /* trình duyệt cũ: dùng z-index */ }
}

function busy(btn, on, text) {
  if (!btn) return;
  if (on) { btn.dataset.t = btn.textContent; btn.textContent = text || 'Đang xử lý…'; btn.disabled = true; }
  else { btn.textContent = btn.dataset.t || btn.textContent; btn.disabled = false; }
}

function requireLogin() {
  if (!API.token()) { location.href = '/Account/Login' + '?returnUrl=' + encodeURIComponent(location.pathname); return false; }
  return true;
}

// Thanh toán giả lập: xin payload + chữ ký (chỉ Development) rồi gọi webhook như cổng thanh toán thật
async function payBooking(id) {
  const s = await API.call(`/api/payments/simulate/${id}`, { method: 'POST' });
  const r = await fetch(API.url('/api/payments/webhook'), { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Signature': s.signature }, body: s.payload });
  const d = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(d.message || d.code || 'Thanh toán thất bại');
  return d;
}

/* ---------- Voucher (dùng chung trang chủ, Khuyến mãi, mua vé, thanh toán) ---------- */
const VC = {
  value: c => c.type === 'Percent' ? `Giảm ${c.value}%${c.maxDiscount ? ` <small>tối đa ${money(c.maxDiscount)}</small>` : ''}` : `Giảm ${money(c.value)}`,
  short: c => c.type === 'Percent' ? `Giảm ${c.value}%` : `Giảm ${money(c.value)}`,
  day: iso => { const d = new Date(iso), p = n => String(n).padStart(2, '0'); return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()}`; },
  meta: c => [c.minOrder > 0 ? `Đơn từ ${money(c.minOrder)}` : 'Mọi giá trị đơn', c.eventName ? `Chỉ: ${esc(c.eventName)}` : 'Mọi sự kiện',
    c.endsAt ? `HSD ${VC.day(c.endsAt)}` : 'Không hết hạn', c.left != null ? `Còn ${c.left} lượt` : ''].filter(Boolean),
  // Lưu voucher vào ví. Chưa đăng nhập thì chuyển sang trang đăng nhập rồi quay lại đúng trang này.
  async save(code, btn) {
    if (!API.token()) { location.href = '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname + location.search); return false; }
    busy(btn, true, 'Đang lưu…');
    try { const r = await API.call(`/api/coupons/${encodeURIComponent(code)}/save`, { method: 'POST' }); toast(r.message, 'ok'); busy(btn, false); return true; }
    catch (err) { busy(btn, false); toast(err.message, 'error'); return false; }
  },
  async unsave(code) {
    try { await API.call(`/api/coupons/saved/${encodeURIComponent(code)}`, { method: 'DELETE' }); return true; }
    catch (err) { toast(err.message, 'error'); return false; }
  },
  copy(code, btn) {
    const done = () => { btn.textContent = 'Đã chép'; setTimeout(() => { btn.textContent = 'Sao chép'; }, 1500); };
    if (navigator.clipboard?.writeText) navigator.clipboard.writeText(code).then(done, () => toast('Không sao chép được, hãy chép thủ công', 'error'));
    else toast('Trình duyệt không hỗ trợ sao chép, hãy chép thủ công', 'error');
  }
};

/* ---------- Thêm vào Google Lịch, dùng chung trang sự kiện / đơn hàng / vé ---------- */
// startsAt hiển thị đúng như admin nhập (giờ địa phương) nên lịch cũng dùng giờ "treo" tương ứng, không đổi múi giờ.
// Chưa có giờ kết thúc: mặc định kéo dài 3 giờ.
const CAL = {
  stamp: d => { const p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}T${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`; },
  end: d => new Date(d.getTime() + 3 * 36e5),
  gcal({ name, venue, startsAt, url }) {
    const s = new Date(startsAt);
    return 'https://calendar.google.com/calendar/render?action=TEMPLATE&text=' + encodeURIComponent(name)
      + '&dates=' + CAL.stamp(s) + '/' + CAL.stamp(CAL.end(s)) + '&ctz=Asia%2FHo_Chi_Minh'
      + '&location=' + encodeURIComponent(venue || '') + '&details=' + encodeURIComponent('Chi tiết và đặt vé: ' + url);
  }
};

/* ---------- Icons ---------- */
const ICON = {
  pin: '<path d="M12 21s7-6.2 7-11a7 7 0 1 0-14 0c0 4.8 7 11 7 11Z"/><circle cx="12" cy="10" r="2.5"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  ticket: '<path d="M3 8a2 2 0 0 0 0 4v0a2 2 0 0 1 0 4v1a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1v-1a2 2 0 0 1 0-4v0a2 2 0 0 0 0-4V7a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1Z"/><path d="M14 6v12" stroke-dasharray="2 3"/>',
  cal: '<rect x="3" y="5" width="18" height="16" rx="2.5"/><path d="M3 10h18M8 3v4M16 3v4"/>',
  chev: '<path d="m9 5 7 7-7 7"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  minus: '<path d="M5 12h14"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  flag: '<path d="M5 21V4M5 4h11l-2 4 2 4H5"/>',
  share: '<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="m8.6 13.5 6.8 4M15.4 6.5l-6.8 4"/>'
};
const icon = (n, s = 18) => `<svg width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON[n]}</svg>`;

/* ---------- Poster art: sinh hình từ tên sự kiện (không cần ảnh) ---------- */
function hash(s) { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }
function rng(seed) {
  let a = seed >>> 0;
  return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const PALETTES = [
  ['#14264a', '#1f3a6b', '#d9a866', '#eef2f9'],
  ['#0f1e3d', '#2c4d86', '#e6c598', '#f4f7fc'],
  ['#14264a', '#b9884a', '#f3dcb8', '#fbf3e6'],
  ['#1a2f57', '#3d5f9b', '#d9a866', '#eef2f9'],
  ['#0f1e3d', '#c8975f', '#ffffff', '#fbf3e6']
];
function posterArt(name, posterUrl) {
  if (posterUrl && !/picsum\.photos/.test(posterUrl)) return `<img src="${esc(posterUrl)}" alt="" loading="lazy">`;
  const h = hash(String(name || 'x')), r = rng(h), p = PALETTES[h % PALETTES.length], kind = (h >>> 3) % 3;
  let bg = p[0], s = '';
  if (kind === 0) { // đèn sân khấu: các vòng tròn đồng tâm
    const cx = 110 + r() * 180, cy = 70 + r() * 110;
    for (let i = 6; i >= 1; i--) s += `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="${i * 34}" fill="none" stroke="${i % 2 ? p[1] : p[2]}" stroke-width="${10 + i}" opacity="${(0.3 + (6 - i) * 0.12).toFixed(2)}"/>`;
    s += `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="34" fill="${p[2]}"/>`;
  } else if (kind === 1) { // sân cỏ / băng rôn: sọc chéo + quả bóng
    bg = p[1];
    for (let i = -2; i < 9; i++) s += `<polygon points="${i * 60},250 ${i * 60 + 40},250 ${i * 60 + 140},0 ${i * 60 + 100},0" fill="${i % 2 ? p[0] : p[3]}" opacity="${i % 2 ? 0.9 : 0.25}"/>`;
    const cx = 60 + r() * 280, cy = 50 + r() * 100;
    s += `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="64" fill="${p[2]}"/><circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="22" fill="${p[0]}"/>`;
  } else { // hoàng hôn phát sáng: tia toả từ đáy
    for (let i = 0; i < 18; i += 2) {
      const a1 = Math.PI + i * Math.PI / 18, a2 = a1 + Math.PI / 18;
      s += `<polygon points="200,250 ${(200 + 520 * Math.cos(a1)).toFixed(1)},${(250 + 520 * Math.sin(a1)).toFixed(1)} ${(200 + 520 * Math.cos(a2)).toFixed(1)},${(250 + 520 * Math.sin(a2)).toFixed(1)}" fill="${p[1]}" opacity=".9"/>`;
    }
    s += `<path d="M125 250A75 75 0 0 1 275 250Z" fill="${p[2]}"/>`;
    for (let i = 0; i < 14; i++) s += `<circle cx="${(r() * 400).toFixed(0)}" cy="${(r() * 120).toFixed(0)}" r="${(2 + r() * 3).toFixed(1)}" fill="${p[3]}" opacity=".8"/>`;
  }
  return `<svg viewBox="0 0 400 250" preserveAspectRatio="xMidYMid slice" aria-hidden="true"><rect width="400" height="250" fill="${bg}"/>${s}</svg>`;
}

/* ---------- Nav ---------- */
(function renderNav() {
  const nav = document.getElementById('nav'); if (!nav) return;
  const role = localStorage.getItem('role'), name = localStorage.getItem('name'), path = location.pathname.toLowerCase();
  if (document.body.dataset.shell === 'staff') {   // khu nhân viên: chỉ tên + đăng xuất
    let sh = '';
    if (API.token()) sh = `<a class="who" href="/Staff/Profile" title="Hồ sơ &amp; đổi mật khẩu"><i aria-hidden="true">${esc((name || '?').trim().charAt(0).toUpperCase())}</i>${esc(name)}<small class="who-role">Nhân viên</small></a><button type="button" id="logout">Đăng xuất</button>`;
    nav.innerHTML = sh;
    document.getElementById('logout')?.addEventListener('click', () => { localStorage.clear(); location.href = '/'; });
    return;
  }
  if (document.body.dataset.shell === 'admin') {   // khu quản trị: thanh trên cùng riêng, không có menu của khách
    let ah = '';
    if (API.token()) ah += `<div class="bell-wrap" id="bellWrap"><button type="button" class="bell" id="bell" aria-label="Thông báo" aria-haspopup="true" aria-expanded="false"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.9 1.9 0 0 0 3.4 0"/></svg><span class="bell-n" id="bellN" hidden>0</span></button><div class="bell-pop" id="bellPop" hidden role="region" aria-label="Danh sách thông báo"></div></div><a class="who" href="/Manage/Profile" title="Hồ sơ &amp; đổi mật khẩu"><i aria-hidden="true">${esc((name || '?').trim().charAt(0).toUpperCase())}</i>${esc(name)}<small class="who-role">${role === 'Staff' ? 'Nhân viên' : 'Quản trị viên'}</small></a><button type="button" id="logout">Đăng xuất</button>`;
    nav.innerHTML = ah;
    document.getElementById('logout')?.addEventListener('click', () => { localStorage.clear(); location.href = '/'; });
    if (API.token()) initBell();
    return;
  }
  const link = (href, label) => `<a href="${href}" ${path === href.toLowerCase() || (href !== '/' && path.startsWith(href.toLowerCase())) ? 'aria-current="page"' : ''}>${label}</a>`;
  // Menu chính (Sự kiện, Vé của tôi...) nằm chung một hàng với menu danh mục bên dưới; ở đây chỉ còn phần tài khoản
  let h = '';
  if (API.token()) {
    h += `<div class="bell-wrap" id="bellWrap"><button type="button" class="bell" id="bell" aria-label="Thông báo" aria-haspopup="true" aria-expanded="false"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.9 1.9 0 0 0 3.4 0"/></svg><span class="bell-n" id="bellN" hidden>0</span></button><div class="bell-pop" id="bellPop" hidden role="region" aria-label="Danh sách thông báo"></div></div>`;
    h += `<a class="who" href="/Account/Profile" title="Thông tin cá nhân" ${path.startsWith('/account/profile') ? 'aria-current="page"' : ''}><i aria-hidden="true">${esc((name || '?').trim().charAt(0).toUpperCase())}</i>${esc(name)}</a><button type="button" id="logout">Đăng xuất</button>`;
  } else {
    h += link('/Account/Login', 'Đăng nhập') + '<a class="nav-cta" href="/Account/Register">Đăng ký</a>';
  }
  nav.innerHTML = h;
  document.getElementById('logout')?.addEventListener('click', () => { localStorage.clear(); location.href = '/'; });
  if (API.token()) initBell();

  const cats = document.getElementById('cats');
  if (cats) {
    const ic = d => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${d}</svg>`;
    const me = cats.querySelector('.cats-me');
    if (me && path.startsWith('/account/mybookings')) me.setAttribute('aria-current', 'page');
    const fav = cats.querySelector('.cats-fav'); if (fav && path.startsWith('/favorites')) fav.setAttribute('aria-current', 'page');
    const sh = cats.querySelector('.cats-shop'); if (sh && path.startsWith('/shop')) sh.setAttribute('aria-current', 'page');
    const vc = cats.querySelector('.cats-vc'); if (vc && path.startsWith('/promotions')) vc.setAttribute('aria-current', 'page');
  }
})();

/* ---------- Chuông thông báo ---------- */
function initBell() {
  const wrap = document.getElementById('bellWrap'), btn = document.getElementById('bell'), pop = document.getElementById('bellPop'), badge = document.getElementById('bellN');
  if (!wrap || !btn || !pop) return;
  const ago = iso => {
    const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
    if (m < 1) return 'Vừa xong'; if (m < 60) return m + ' phút trước';
    const h = Math.round(m / 60); if (h < 24) return h + ' giờ trước';
    return Math.round(h / 24) + ' ngày trước';
  };
  let data = { unread: 0, items: [] };
  const drawBadge = () => { badge.hidden = data.unread <= 0; badge.textContent = data.unread > 99 ? '99+' : data.unread; };
  const drawPop = () => {
    pop.innerHTML = `<div class="bell-h"><b>Thông báo</b>${data.unread ? '<button type="button" class="bell-all" id="bellAll">Đánh dấu đã đọc</button>' : ''}</div>`
      + (data.items.length
        ? `<div class="bell-list">${data.items.map(n => `<div class="bell-it${n.isRead ? '' : ' unread'}" role="link" tabindex="0" data-id="${n.id}" data-href="${esc(n.link || '')}"><b>${esc(n.title)}</b>${n.message ? `<span>${esc(n.message)}</span>` : ''}<small>${ago(n.createdAt)}</small></div>`).join('')}</div>`
        : '<p class="bell-empty">Chưa có thông báo nào.</p>');
  };
  async function load() {
    try { data = await API.call('/api/notifications'); drawBadge(); if (!pop.hidden) drawPop(); } catch { /* im lặng: không làm phiền khi mạng chập chờn */ }
  }
  const setOpen = on => { pop.hidden = !on; btn.setAttribute('aria-expanded', String(on)); if (on) { drawPop(); load(); } };
  btn.addEventListener('click', e => { e.stopPropagation(); setOpen(pop.hidden); });
  document.addEventListener('click', e => { if (!pop.hidden && !wrap.contains(e.target)) setOpen(false); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && !pop.hidden) { setOpen(false); btn.focus(); } });
  const open = async el => {
    const id = el.dataset.id, href = el.dataset.href;
    try { await API.call(`/api/notifications/${id}/read`, { method: 'POST' }); } catch { /* vẫn cho chuyển trang */ }
    if (href) location.href = href; else load();
  };
  pop.addEventListener('click', async e => {
    if (e.target.closest('#bellAll')) {
      try { await API.call('/api/notifications/read-all', { method: 'POST' }); } catch { return; }
      data.unread = 0; data.items.forEach(n => { n.isRead = true; }); drawBadge(); drawPop(); return;
    }
    const it = e.target.closest('.bell-it'); if (it) open(it);
  });
  pop.addEventListener('keydown', e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('bell-it')) { e.preventDefault(); open(e.target); } });
  load();
  setInterval(() => { if (!document.hidden) load(); }, 60000);
}

/* ---------- Trang trí QR (trang đăng nhập/đăng ký) ---------- */
(function decoQr() {
  const el = document.getElementById('decoQr'); if (!el) return;
  const r = rng(20260930); let h = '';
  const finder = (x, y) => (x < 4 && y < 4) || (x > 6 && y < 4) || (x < 4 && y > 6);
  for (let y = 0; y < 11; y++) for (let x = 0; x < 11; x++) {
    let on;
    if (finder(x, y)) { const lx = x % 7, ly = y % 7; on = lx === 0 || ly === 0 || lx === 3 || ly === 3; } // khung vuông định vị
    else on = r() > 0.5;
    h += `<i class="${on ? '' : 'o'}"></i>`;
  }
  el.innerHTML = h;
})();

/* ---------- Burger (mobile) ---------- */
document.getElementById('burger')?.addEventListener('click', e => {
  const on = document.body.classList.toggle('menu-open');
  e.currentTarget.setAttribute('aria-expanded', on);
});

/* ---------- Chiều cao header dính (để thanh bên quản trị bám đúng dưới header) ---------- */
(function siteHeight() {
  const h = document.querySelector('.site-header'); if (!h) return;
  const set = () => document.documentElement.style.setProperty('--site-h', h.offsetHeight + 'px');
  set(); window.addEventListener('resize', set); window.addEventListener('load', set);
})();
