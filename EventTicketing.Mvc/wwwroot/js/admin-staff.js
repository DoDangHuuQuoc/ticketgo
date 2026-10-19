/* Trang Nhân viên: danh sách + tạo + khoá + đặt lại mật khẩu + gỡ quyền (dùng chung API người dùng với role=Staff) */
(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  const S = { page: 1, size: 20 }; let items = [];

  async function load() {
    try {
      const d = await API.call('/api/admin/users?' + ADM.qs({ q: $('q').value.trim(), role: 'Staff', state: $('state').value, page: S.page, size: S.size }));
      items = d.items;
      const all = (d.counts || []).find(c => c.role === 'Staff');
      $('stCount').textContent = (all ? all.count : 0) + ' nhân viên';
      rows.innerHTML = items.map(u => `<tr class="${u.isLocked ? 'dim' : ''}">
        <td><div class="u">${ADM.avatar(u.fullName || u.email, u.email)}<div><b>${esc(u.fullName)}</b><small>${esc(u.email)}</small></div></div></td>
        <td>${u.isLocked ? '<span class="badge b-bad">Bị khoá</span>' : '<span class="badge b-go">Đang hoạt động</span>'}</td>
        <td class="nw">${ADM.dt(u.createdAt)}</td>
        <td><div class="acts">
          <button class="btn btn-ghost btn-sm ${u.isLocked ? '' : 'danger'}" data-a="lock" data-id="${u.id}">${u.isLocked ? 'Mở khoá' : 'Khoá'}</button>
          <button class="btn btn-ghost btn-sm" data-a="pw" data-id="${u.id}">Đặt lại MK</button>
          <button class="btn btn-ghost btn-sm danger" data-a="demote" data-id="${u.id}">Gỡ quyền</button>
        </div></td></tr>`).join('') || `<tr><td colspan="4" class="empty">${$('q').value.trim() || $('state').value ? 'Không có nhân viên phù hợp.' : 'Chưa có nhân viên nào. Bấm “+ Thêm nhân viên” để tạo tài khoản soát vé.'}</td></tr>`;
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { rows.innerHTML = '<tr><td colspan="4" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  function passwordDialog(u) {
    const m = ADM.modal({ title: 'Đặt lại mật khẩu', body: `<form class="dlg-form"><p><b>${esc(u.fullName)}</b> · ${esc(u.email)}</p>
      <div class="field"><label for="pNew">Mật khẩu mới (tối thiểu 8 ký tự)</label><input id="pNew" type="text" minlength="8" autocomplete="off" /></div>
      <p class="hint">Gửi mật khẩu này cho nhân viên qua kênh an toàn và nhắc họ đổi sau khi đăng nhập.</p>
      <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Đặt lại</button></div></form>` });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      if ($('pNew').value.length < 8) return toast('Mật khẩu tối thiểu 8 ký tự', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call(`/api/admin/users/${u.id}/reset-password`, { method: 'POST', body: { password: $('pNew').value } }))) m.close();
    });
  }

  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const u = items.find(x => x.id === b.dataset.id); if (!u) return;
    if (b.dataset.a === 'pw') return passwordDialog(u);
    if (b.dataset.a === 'demote') {
      const r = await ADM.ask({ title: 'Gỡ quyền nhân viên', message: `<b>${esc(u.email)}</b> sẽ trở thành tài khoản khách thường và không vào được trang soát vé nữa (cần đăng nhập lại).`, confirmText: 'Gỡ quyền', danger: true });
      if (r === null) return;
      if (await ADM.act(b, () => API.call(`/api/admin/users/${u.id}/role`, { method: 'PUT', body: { role: 'User' } }))) load();
      return;
    }
    const lock = !u.isLocked;
    const reason = await ADM.ask({ title: lock ? 'Khoá nhân viên' : 'Mở khoá nhân viên', message: lock ? `<b>${esc(u.email)}</b> sẽ không đăng nhập được nữa (hiệu lực trong vài giây).` : `Cho phép <b>${esc(u.email)}</b> đăng nhập trở lại.`, label: lock ? 'Lý do (không bắt buộc)' : '', confirmText: lock ? 'Khoá' : 'Mở khoá', danger: lock });
    if (reason === null) return;
    if (await ADM.act(b, () => API.call(`/api/admin/users/${u.id}/lock`, { method: 'POST', body: { isLocked: lock, reason } }))) load();
  });

  $('newStaff').addEventListener('click', () => {
    const m = ADM.modal({ title: 'Thêm nhân viên', body: `<form class="dlg-form" novalidate><div class="grid2">
      <div class="field full"><label for="nName">Họ tên *</label><input id="nName" maxlength="200" /></div>
      <div class="field full"><label for="nMail">Email đăng nhập *</label><input id="nMail" type="email" maxlength="256" /></div>
      <div class="field full"><label for="nPw">Mật khẩu tạm * (≥ 8 ký tự)</label><input id="nPw" type="text" autocomplete="off" /></div></div>
      <p class="hint">Tài khoản được xác thực sẵn, không cần OTP email. Nhân viên đăng nhập ở trang chính, hệ thống tự đưa vào khu soát vé.</p>
      <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">Tạo nhân viên</button></div></form>` });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const body = { fullName: $('nName').value.trim(), email: $('nMail').value.trim(), role: 'Staff', password: $('nPw').value };
      if (!body.fullName || !body.email || body.password.length < 8) return toast('Nhập đủ họ tên, email và mật khẩu ≥ 8 ký tự', 'error');
      if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call('/api/admin/users', { method: 'POST', body }))) { m.close(); S.page = 1; load(); }
    });
  });

  $('state').addEventListener('change', () => { S.page = 1; load(); });
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  load();
})();
