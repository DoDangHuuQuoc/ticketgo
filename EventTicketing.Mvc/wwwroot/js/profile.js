(() => {
  'use strict';
  if (!requireLogin()) return;

  const $ = id => document.getElementById(id);
  const side = document.querySelector('.pf-side');
  const GENDER = { Male: 'Nam', Female: 'Nữ', Other: 'Khác' };
  const ROLE = { Admin: 'Quản trị viên', Staff: 'Nhân viên', User: 'Khách hàng' };
  const PHONE = /^(\+84|84|0)\d{9}$/;
  let saved = null;      // bản đã lưu trên server, dùng để biết form có thay đổi hay không
  let gender = '';

  /* ---------- Hiện / ẩn mật khẩu ---------- */
  const EYE = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg>';
  const EYE_OFF = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.4 0 10 7 10 7a17 17 0 0 1-3.2 4M6.5 6.5C3.8 8.3 2 12 2 12s3.6 7 10 7c1.6 0 3-.4 4.3-1M9.9 9.9a3 3 0 0 0 4.2 4.2"/></svg>';
  document.querySelectorAll('.pw-toggle').forEach(b => {
    b.innerHTML = EYE;
    b.addEventListener('click', () => {
      const i = b.previousElementSibling, show = i.type === 'password';
      i.type = show ? 'text' : 'password'; b.innerHTML = show ? EYE_OFF : EYE; b.setAttribute('aria-label', show ? 'Ẩn mật khẩu' : 'Hiện mật khẩu');
    });
  });

  /* ---------- Lỗi từng ô ---------- */
  const setErr = (name, msg) => {
    const f = document.querySelector(`[data-field="${name}"]`); if (!f) return;
    f.classList.toggle('invalid', !!msg); $(name + '-err').textContent = msg || '';
    const i = f.querySelector('input,textarea'); if (i) i.setAttribute('aria-invalid', msg ? 'true' : 'false');
  };
  const formErr = (id, msg) => { const e = $(id); e.textContent = msg || ''; e.classList.toggle('on', !!msg); };

  /* ---------- Cột trái: tóm tắt ---------- */
  const fmtDate = iso => { const d = new Date(iso); return `${String(d.getDate()).padStart(2, '0')}/${String(d.getMonth() + 1).padStart(2, '0')}/${d.getFullYear()}`; };
  // Ưu đãi VIP: khách VIP thấy mức giảm đang hưởng; khách thường (khi admin bật tự lên VIP theo chi tiêu) thấy còn thiếu bao nhiêu
  const vipNote = v => {
    if (!v || !v.enabled || !v.percent) return '';
    if (v.isVip) return `<p class="pf-since muted">Ưu đãi VIP: giảm ${v.percent}% giá vé mỗi đơn${v.maxDiscount > 0 ? ` (tối đa ${money(v.maxDiscount)})` : ''}.</p>`;
    return v.remaining != null ? `<p class="pf-since muted">Chi tiêu thêm ${money(v.remaining)} để trở thành khách VIP và được giảm ${v.percent}% giá vé.</p>` : '';
  };
  function renderSide(p) {
    const initial = (p.fullName || p.email || '?').trim().charAt(0).toUpperCase();
    side.innerHTML = `<div class="pf-avatar" aria-hidden="true">${esc(initial)}</div>
      <h2 class="pf-name">${esc(p.fullName)}</h2>
      <p class="pf-mail">${esc(p.email)}</p>
      <div class="pf-badges"><span class="badge b-cat">${esc(ROLE[p.role] || p.role)}</span>
        ${p.isVerified ? '<span class="badge b-go">Đã xác thực email</span>' : '<a class="badge b-wait" href="/Account/Verify">Chưa xác thực email</a>'}
        ${p.vip?.isVip ? '<span class="badge b-wait">★ Khách VIP</span>' : ''}</div>
      <dl class="pf-stats">
        <div><dt>Vé đang có</dt><dd>${p.stats.tickets}</dd></div>
        <div><dt>Đơn đã thanh toán</dt><dd>${p.stats.orders}</dd></div>
        <div class="wide"><dt>Tổng đã chi</dt><dd>${money(p.stats.spent)}</dd></div>
      </dl>
      ${vipNote(p.vip)}
      <p class="pf-since muted">Thành viên từ ${fmtDate(p.createdAt)}</p>`;
  }

  /* ---------- Form hồ sơ ---------- */
  const readForm = () => ({
    fullName: $('fullName').value.trim(), phone: $('phone').value.trim(),
    dateOfBirth: $('dateOfBirth').value, gender, address: $('address').value.trim()
  });
  const snapshot = p => ({ fullName: p.fullName || '', phone: p.phone || '', dateOfBirth: p.dateOfBirth || '', gender: p.gender || '', address: p.address || '' });
  const dirty = () => saved && JSON.stringify(readForm()) !== JSON.stringify(saved);
  const syncButtons = () => { const d = !!dirty(); $('infoSave').disabled = !d; $('infoReset').disabled = !d; };

  function fillForm(s) {
    $('fullName').value = s.fullName; $('phone').value = s.phone; $('dateOfBirth').value = s.dateOfBirth; $('address').value = s.address;
    setGender(s.gender); ['fullName', 'phone', 'dateOfBirth', 'address'].forEach(n => setErr(n, '')); formErr('infoErr', '');
    syncButtons();
  }
  function setGender(g) {
    gender = g || '';
    $('genderChips').querySelectorAll('button').forEach(b => b.setAttribute('aria-checked', b.dataset.g === gender));
  }
  $('genderChips').addEventListener('click', e => {
    const b = e.target.closest('button[data-g]'); if (!b) return;
    setGender(gender === b.dataset.g ? '' : b.dataset.g);   // bấm lại để bỏ chọn
    syncButtons();
  });

  const validators = {
    fullName: v => !v ? 'Nhập họ và tên của bạn.' : v.length < 2 ? 'Họ và tên cần ít nhất 2 ký tự.' : '',
    phone: v => { if (!v) return ''; return PHONE.test(v.replace(/[\s.\-()]/g, '')) ? '' : 'Số điện thoại chưa đúng, ví dụ 0912 345 678.'; },
    dateOfBirth: v => { if (!v) return ''; const d = new Date(v + 'T00:00:00'); return isNaN(d) || d > new Date() || d.getFullYear() < 1900 ? 'Ngày sinh không hợp lệ.' : ''; },
    address: v => v.length > 300 ? 'Địa chỉ tối đa 300 ký tự.' : ''
  };
  Object.keys(validators).forEach(n => {
    const el = $(n);
    el.addEventListener('input', () => { syncButtons(); if (document.querySelector(`[data-field="${n}"]`).classList.contains('invalid')) setErr(n, validators[n](readForm()[n])); });
    el.addEventListener('blur', () => setErr(n, validators[n](readForm()[n])));
  });

  $('infoReset').addEventListener('click', () => fillForm(saved));
  $('infoForm').addEventListener('submit', async e => {
    e.preventDefault();
    const v = readForm(); let bad = null;
    for (const n of Object.keys(validators)) { const m = validators[n](v[n]); setErr(n, m); if (m && !bad) bad = n; }
    if (bad) { $(bad).focus(); return; }
    formErr('infoErr', '');
    const btn = $('infoSave'); busy(btn, true, 'Đang lưu…');
    try {
      const r = await API.call('/api/account/profile', { method: 'PUT', body: { fullName: v.fullName, phone: v.phone || null, dateOfBirth: v.dateOfBirth || null, gender: v.gender || null, address: v.address || null } });
      localStorage.setItem('name', r.fullName);
      const who = document.querySelector('.who'); if (who) who.innerHTML = `<i aria-hidden="true">${esc(r.fullName.trim().charAt(0).toUpperCase())}</i>${esc(r.fullName)}`;
      const p = await API.call('/api/account/profile');   // lấy lại bản đã chuẩn hoá phía server (vd. số điện thoại bỏ dấu cách)
      saved = snapshot(p); renderSide(p); fillForm(saved);
      toast(r.message, 'ok');
    } catch (err) { formErr('infoErr', err.message); }
    finally { busy(btn, false); syncButtons(); }
  });

  /* ---------- Form đổi mật khẩu ---------- */
  const pwVal = {
    currentPassword: v => v ? '' : 'Nhập mật khẩu hiện tại.',
    newPassword: v => !v ? 'Nhập mật khẩu mới.' : v.length < 8 ? 'Mật khẩu mới cần ít nhất 8 ký tự.' : v === $('currentPassword').value ? 'Mật khẩu mới phải khác mật khẩu hiện tại.' : '',
    confirmPassword: v => v !== $('newPassword').value ? 'Mật khẩu nhập lại chưa khớp.' : ''
  };
  Object.keys(pwVal).forEach(n => $(n).addEventListener('blur', () => setErr(n, pwVal[n]($(n).value))));
  $('pwForm').addEventListener('submit', async e => {
    e.preventDefault(); formErr('pwErr', '');
    let bad = null;
    for (const n of Object.keys(pwVal)) { const m = pwVal[n]($(n).value); setErr(n, m); if (m && !bad) bad = n; }
    if (bad) { $(bad).focus(); return; }
    const btn = $('pwSave'); busy(btn, true, 'Đang cập nhật…');
    try {
      const r = await API.call('/api/account/change-password', { method: 'POST', body: { currentPassword: $('currentPassword').value, newPassword: $('newPassword').value } });
      $('pwForm').reset(); Object.keys(pwVal).forEach(n => setErr(n, ''));
      toast(r.message, 'ok');
    } catch (err) {
      if (err.data?.code === 'BAD_CURRENT_PASSWORD') { setErr('currentPassword', err.message); $('currentPassword').focus(); }
      else formErr('pwErr', err.message);
    } finally { busy(btn, false); }
  });

  /* ---------- Khởi tạo ---------- */
  (async () => {
    try {
      const p = await API.call('/api/account/profile');
      $('email').value = p.email;
      saved = snapshot(p); renderSide(p); fillForm(saved);
    } catch (err) {
      if (err.status === 401) { requireLogin(); return; }
      side.innerHTML = '';
      $('pf').querySelector('.pf-main').innerHTML = `<div class="empty">Không tải được thông tin: ${esc(err.message)}</div>`;
    }
  })();
})();
