/* Tiện ích dùng chung cho các trang quản trị (nạp sau app.js, trước js của từng trang) */
const ADM = (() => {
  'use strict';
  const $ = id => document.getElementById(id);

  function guard(roles) {
    if (!requireLogin()) return false;
    const role = localStorage.getItem('role');
    if (roles.includes(role)) return true;
    const c = $('content'), g = $('guard');
    if (c) c.hidden = true;
    if (g) { g.hidden = false; g.innerHTML = '<h2>Cần quyền quản trị</h2><p class="muted">Đăng nhập bằng tài khoản Admin để xem trang này.</p>'; }
    if (role === 'Staff') { document.querySelectorAll('#admTabs a:not([data-staff])').forEach(a => a.remove()); document.querySelectorAll('.adm-grp').forEach(g => g.querySelector('a') || g.remove()); }
    return false;
  }

  function meId() {
    try {
      const p = JSON.parse(atob(API.token().split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
      return p.nameid || p['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'] || '';
    } catch { return ''; }
  }

  const qs = o => { const u = new URLSearchParams(); Object.entries(o).forEach(([k, v]) => { if (v !== '' && v != null) u.set(k, v); }); return u.toString(); };
  const debounce = (fn, ms = 300) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
  const dt = iso => iso ? new Date(iso).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' }) : '—';
  const shortId = id => String(id).slice(0, 8).toUpperCase();
  const toLocalInput = iso => { const d = new Date(iso); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 16); };
  const initials = n => String(n || '?').trim().split(/\s+/).slice(-2).map(w => w[0]).join('').toUpperCase();
  const AV = ['#3050e0', '#e5484d', '#0e9f6e', '#7c4dff', '#c23bd6', '#d98400'];
  const avatar = (name, key) => `<span class="av" style="background:${AV[hash(String(key)) % AV.length]}">${esc(initials(name))}</span>`;

  const BOOKING = { Pending: ['Đang giữ chỗ', 'b-wait'], Paid: ['Đã thanh toán', 'b-go'], Cancelled: ['Đã huỷ / hoàn', 'b-bad'], Expired: ['Hết hạn', 'b-off'] };
  const TICKET = { Valid: ['Hợp lệ', 'b-go'], CheckedIn: ['Đã vào cổng', 'b-queue'], Cancelled: ['Đã thu hồi', 'b-bad'] };
  const badge = (map, k) => { const [t, c] = map[k] || [k, '']; return `<span class="badge ${c}">${esc(t)}</span>`; };

  /* ---------- Modal (thẻ <dialog> gốc: có sẵn Esc + khoá focus) ---------- */
  function modal({ title, body, wide = false }) {
    const d = document.createElement('dialog');
    d.className = 'dlg' + (wide ? ' wide' : '');
    d.innerHTML = `<div class="dlg-h"><h2>${esc(title)}</h2><button type="button" class="dlg-x" aria-label="Đóng">×</button></div><div class="dlg-b"></div>`;
    const b = d.querySelector('.dlg-b');
    if (typeof body === 'string') b.innerHTML = body; else if (body) b.append(body);
    d.querySelector('.dlg-x').addEventListener('click', () => d.close());
    d.addEventListener('click', e => { if (e.target === d) d.close(); });
    d.addEventListener('close', () => d.remove());
    document.body.append(d); d.showModal();
    return { el: d, body: b, close: () => d.close() };
  }

  /* Hộp xác nhận (có thể bắt nhập lý do). Trả về: null = huỷ; chuỗi = lý do ('' nếu không hỏi lý do) */
  function ask({ title, message, label, required = false, confirmText = 'Xác nhận', danger = false, placeholder = '' }) {
    return new Promise(resolve => {
      let done = false;
      const m = modal({
        title, body: `<form class="dlg-form" novalidate>
          <p>${message || ''}</p>
          ${label ? `<div class="field"><label for="askText">${esc(label)}</label><textarea id="askText" rows="3" maxlength="500" placeholder="${esc(placeholder)}"></textarea></div>` : ''}
          <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn ${danger ? 'btn-danger' : 'btn-primary'}">${esc(confirmText)}</button></div></form>`
      });
      const f = m.body.querySelector('form'), t = m.body.querySelector('#askText');
      t?.focus();
      f.querySelector('[data-x]').addEventListener('click', () => m.close());
      f.addEventListener('submit', e => {
        e.preventDefault();
        const v = (t?.value || '').trim();
        if (required && !v) { toast('Vui lòng nhập lý do', 'error'); t?.focus(); return; }
        done = true; resolve(v); m.close();
      });
      m.el.addEventListener('close', () => { if (!done) resolve(null); });
    });
  }

  function pager(el, { page, size, total }, go) {
    const pages = Math.max(1, Math.ceil(total / size));
    el.innerHTML = total <= size ? `<span class="muted">${total} kết quả</span>` :
      `<span class="muted">${(page - 1) * size + 1}–${Math.min(page * size, total)} / ${total}</span>
       <div><button class="btn btn-ghost btn-sm" type="button" data-p="${page - 1}" ${page <= 1 ? 'disabled' : ''}>‹ Trước</button>
       <span class="pg">Trang ${page}/${pages}</span>
       <button class="btn btn-ghost btn-sm" type="button" data-p="${page + 1}" ${page >= pages ? 'disabled' : ''}>Sau ›</button></div>`;
    el.querySelectorAll('[data-p]').forEach(b => b.addEventListener('click', () => go(+b.dataset.p)));
  }

  /* Tải file CSV có kèm JWT (thẻ <a href> không gửi được header Authorization) */
  async function download(path, params = {}) {
    const r = await fetch(API.url(path) + '?' + qs(params), { headers: { Authorization: 'Bearer ' + API.token() } });
    if (!r.ok) { const d = await r.json().catch(() => ({})); throw new Error(d.message || 'Xuất file thất bại'); }
    const cd = r.headers.get('Content-Disposition') || '', m = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(cd);
    const url = URL.createObjectURL(await r.blob()), a = document.createElement('a');
    a.href = url; a.download = m ? decodeURIComponent(m[1]) : 'export.csv'; document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
  }

  /* Chạy hành động có nút bận + toast + tải lại */
  async function act(btn, fn, okMsg) {
    busy(btn, true, 'Đang xử lý…');
    try { const r = await fn(); toast(okMsg || r?.message || 'Thành công', 'ok'); busy(btn, false); return true; }
    catch (err) { toast(err.message, 'error'); busy(btn, false); return false; }
  }

  return { $, guard, meId, qs, debounce, dt, shortId, toLocalInput, avatar, badge, BOOKING, TICKET, modal, ask, pager, download, act };
})();
