(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows'), me = ADM.meId();
  const S = { page: 1, size: 20 }; let items = [], promo = { vipEnabled: false, vipAutoSpend: 0 };
  // VIP hiệu lực = admin gán tay, hoặc tự lên VIP khi tổng chi tiêu đạt ngưỡng (nếu admin bật); chỉ có tác dụng khi ưu đãi VIP đang bật
  const vipBadge = u => {
    if (u.role !== 'User') return '';
    const auto = promo.vipAutoSpend > 0 && u.spent >= promo.vipAutoSpend;
    if (!u.isVip && !auto) return '';
    return ` <span class="badge b-wait" title="${u.isVip ? 'Admin gán VIP' : 'Tự động lên VIP theo mức chi tiêu'}${promo.vipEnabled ? '' : ' (ưu đãi VIP đang tắt)'}">VIP${u.isVip ? '' : ' tự động'}</span>`;
  };
  const ROLE = { Admin: ['Admin', 'b-queue'], Staff: ['Nhân viên', 'b-wait'], User: ['Khách', ''] };

  async function load() {
    try {
      const d = await API.call('/api/admin/users?' + ADM.qs({ q: $('q').value.trim(), role: $('role').value, state: $('state').value, page: S.page, size: S.size }));
      items = d.items; promo = { vipEnabled: d.vipEnabled, vipAutoSpend: d.vipAutoSpend };
      rows.innerHTML = items.map(u => `<tr class="${u.isLocked ? 'dim' : ''}">
        <td><div class="u">${ADM.avatar(u.fullName || u.email, u.email)}<div><button class="link" data-a="detail" data-id="${u.id}">${esc(u.fullName)}${u.id === me ? ' (bạn)' : ''}</button>${vipBadge(u)}<small>${esc(u.email)}</small></div></div></td>
        <td>${ADM.badge(ROLE, u.role)}</td>
        <td>${u.isLocked ? '<span class="badge b-bad">Bị khoá</span>' : u.isVerified ? '<span class="badge b-go">Đã xác thực</span>' : '<span class="badge b-off">Chưa xác thực</span>'}</td>
        <td class="r num">${u.orders}</td><td class="r num">${money(u.spent)}</td>
        <td><div class="acts">
          ${u.role === 'User' ? `<button class="btn btn-ghost btn-sm" data-a="vip" data-id="${u.id}">${u.isVip ? 'Bỏ VIP' : 'Gán VIP'}</button>` : ''}
          <button class="btn btn-ghost btn-sm" data-a="role" data-id="${u.id}" ${u.id === me ? 'disabled' : ''}>Vai trò</button>
          <button class="btn btn-ghost btn-sm ${u.isLocked ? '' : 'danger'}" data-a="lock" data-id="${u.id}" ${u.id === me ? 'disabled' : ''}>${u.isLocked ? 'Mở khoá' : 'Khoá'}</button>
          <button class="btn btn-ghost btn-sm" data-a="pw" data-id="${u.id}">Đặt lại MK</button>
        </div></td></tr>`).join('') || '<tr><td colspan="6" class="empty">Không có người dùng phù hợp.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { toast(err.message, 'error'); }
  }

  function roleDialog(u) {
    const m = ADM.modal({ title: 'Đổi vai trò', body: `<form class="dlg-form"><p><b>${esc(u.fullName)}</b> · ${esc(u.email)}</p>
      <div class="field"><label for="rSel">Vai trò mới</label><select id="rSel" class="select" style="width:100%">
        <option value="User">Khách — mua vé</option><option value="Staff">Nhân viên — soát vé, chat (chuyển sang trang Nhân viên)</option><option value="Admin">Admin — toàn quyền quản trị</option></select></div>
      <p class="hint warn">Người dùng cần đăng nhập lại để quyền mới có hiệu lực.</p>
      <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Lưu</button></div></form>` });
    $('rSel').value = u.role;
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call(`/api/admin/users/${u.id}/role`, { method: 'PUT', body: { role: $('rSel').value } }))) { m.close(); load(); }
    });
  }

  function passwordDialog(u) {
    const m = ADM.modal({ title: 'Đặt lại mật khẩu', body: `<form class="dlg-form"><p><b>${esc(u.fullName)}</b> · ${esc(u.email)}</p>
      <div class="field"><label for="pNew">Mật khẩu mới (tối thiểu 8 ký tự)</label><input id="pNew" type="text" minlength="8" autocomplete="off" /></div>
      <p class="hint">Hãy gửi mật khẩu này cho người dùng qua kênh an toàn và nhắc họ đổi sau khi đăng nhập.</p>
      <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Đặt lại</button></div></form>` });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      if ($('pNew').value.length < 8) return toast('Mật khẩu tối thiểu 8 ký tự', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call(`/api/admin/users/${u.id}/reset-password`, { method: 'POST', body: { password: $('pNew').value } }))) m.close();
    });
  }

  async function detail(id) {
    const m = ADM.modal({ title: 'Hồ sơ người dùng', wide: true, body: '<p class="muted">Đang tải…</p>' });
    try {
      const d = await API.call('/api/admin/users/' + id), u = d.user;
      m.body.innerHTML = `<dl class="kv"><dt>Họ tên</dt><dd>${esc(u.fullName)}</dd><dt>Email</dt><dd>${esc(u.email)}</dd>
        <dt>Vai trò</dt><dd>${ADM.badge(ROLE, u.role)}</dd><dt>Trạng thái</dt><dd>${u.isLocked ? 'Bị khoá' : u.isVerified ? 'Đã xác thực' : 'Chưa xác thực email'}</dd>${u.role === 'User' ? `<dt>Khách VIP</dt><dd>${u.isVip ? 'Có (admin gán)' : 'Không'}</dd>` : ''}<dt>Tham gia</dt><dd>${ADM.dt(u.createdAt)}</dd></dl>
        ${!u.isVerified ? '<button class="btn btn-ghost btn-sm" id="forceVerify">Xác thực email thủ công</button>' : ''}
        <div class="sect"><h3>Đơn gần đây</h3><ul class="mini-list">${d.bookings.map(b => `<li><span><b>${esc(b.eventName)}</b><br><small class="muted">${esc(b.typeName)} ×${b.quantity} · ${ADM.dt(b.createdAt)}</small></span><span>${money(b.totalAmount)} ${ADM.badge(ADM.BOOKING, b.status)}</span></li>`).join('') || '<li class="muted">Chưa có đơn hàng.</li>'}</ul></div>
        <div class="sect"><h3>Lịch sử quản trị</h3><ul class="mini-list">${d.logs.map(l => `<li><span>${esc(l.action)} <small class="muted">bởi ${esc(l.actorEmail)}</small>${l.detail ? `<br><small class="muted">${esc(l.detail)}</small>` : ''}</span><small class="muted">${ADM.dt(l.createdAt)}</small></li>`).join('') || '<li class="muted">Chưa có.</li>'}</ul></div>`;
      $('forceVerify')?.addEventListener('click', async e => { if (await ADM.act(e.currentTarget, () => API.call(`/api/admin/users/${id}/verify`, { method: 'POST' }))) { m.close(); load(); } });
    } catch (err) { m.body.innerHTML = esc(err.message); }
  }

  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const u = items.find(x => x.id === b.dataset.id); if (!u) return;
    if (b.dataset.a === 'detail') return detail(u.id);
    if (b.dataset.a === 'role') return roleDialog(u);
    if (b.dataset.a === 'pw') return passwordDialog(u);
    if (b.dataset.a === 'vip') { if (await ADM.act(b, () => API.call(`/api/admin/users/${u.id}/vip`, { method: 'PUT', body: { isVip: !u.isVip } }))) load(); return; }
    const lock = !u.isLocked;
    const reason = await ADM.ask({ title: lock ? 'Khoá tài khoản' : 'Mở khoá tài khoản', message: lock ? `<b>${esc(u.email)}</b> sẽ không đăng nhập hay mua vé được nữa (hiệu lực trong vài giây).` : `Cho phép <b>${esc(u.email)}</b> đăng nhập trở lại.`, label: lock ? 'Lý do (không bắt buộc)' : '', confirmText: lock ? 'Khoá' : 'Mở khoá', danger: lock });
    if (reason === null) return;
    if (await ADM.act(b, () => API.call(`/api/admin/users/${u.id}/lock`, { method: 'POST', body: { isLocked: lock, reason } }))) load();
  });

  $('newUser').addEventListener('click', () => {
    const m = ADM.modal({ title: 'Tạo tài khoản', body: `<form class="dlg-form" novalidate><div class="grid2">
      <div class="field full"><label for="nName">Họ tên *</label><input id="nName" maxlength="200" /></div>
      <div class="field full"><label for="nMail">Email *</label><input id="nMail" type="email" maxlength="256" /></div>
      <div class="field"><label for="nRole">Vai trò</label><select id="nRole" class="select" style="width:100%"><option value="User">Khách</option><option value="Admin">Admin</option></select></div>
      <div class="field"><label for="nPw">Mật khẩu tạm * (≥ 8 ký tự)</label><input id="nPw" type="text" autocomplete="off" /></div></div>
      <p class="hint">Tài khoản tạo tại đây được xác thực sẵn, không cần OTP email. Nhân viên soát vé thì tạo ở trang “Nhân viên”.</p>
      <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Tạo tài khoản</button></div></form>` });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const body = { fullName: $('nName').value.trim(), email: $('nMail').value.trim(), role: $('nRole').value, password: $('nPw').value };
      if (!body.fullName || !body.email || body.password.length < 8) return toast('Nhập đủ họ tên, email và mật khẩu ≥ 8 ký tự', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call('/api/admin/users', { method: 'POST', body }))) { m.close(); load(); }
    });
  });

  ['role', 'state'].forEach(i => $(i).addEventListener('change', () => { S.page = 1; load(); }));
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  load();
})();
