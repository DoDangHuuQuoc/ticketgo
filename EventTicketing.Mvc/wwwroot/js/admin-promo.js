/* Trang Mã giảm giá: cấu hình ưu đãi VIP + mã chào mừng, và danh sách mã chào mừng đã cấp */
(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, wrows = $('wRows');
  const W = { page: 1, size: 10 };
  let items = [];

  function syncType() {
    const pct = $('wType').value === 'Percent';
    $('wValL').textContent = pct ? 'Phần trăm giảm (1–100)' : 'Số tiền giảm (đ)';
    $('wVal').max = pct ? 100 : 100000000; $('wMaxW').hidden = !pct;
  }

  async function loadSettings() {
    try {
      const s = await API.call('/api/admin/promo/settings');
      $('vOn').checked = s.vipEnabled; $('vPct').value = s.vipDiscountPercent; $('vMax').value = s.vipMaxDiscount ?? ''; $('vAuto').value = s.vipAutoSpend || 0;
      $('vipCount').textContent = s.vipCount + ' khách được gán VIP';
      $('wOn').checked = s.welcomeEnabled; $('wType').value = s.welcomeType; $('wVal').value = s.welcomeValue; $('wMax').value = s.welcomeMaxDiscount ?? '';
      $('wMin').value = s.welcomeMinOrder || 0; $('wDays').value = s.welcomeValidDays; $('wShop').checked = s.welcomeAppliesToShop;
      syncType();
    } catch (err) { toast(err.message, 'error'); }
  }

  $('wType').addEventListener('change', syncType);
  $('promoForm').addEventListener('submit', async e => {
    e.preventDefault();
    const num = id => $(id).value === '' ? null : Number($(id).value);
    const pct = $('wType').value === 'Percent';
    const body = {
      vipEnabled: $('vOn').checked, vipDiscountPercent: num('vPct') ?? 0, vipMaxDiscount: num('vMax') || null, vipAutoSpend: num('vAuto') ?? 0,
      welcomeEnabled: $('wOn').checked, welcomeType: $('wType').value, welcomeValue: num('wVal') ?? 0, welcomeMaxDiscount: pct ? (num('wMax') || null) : null,
      welcomeMinOrder: num('wMin') ?? 0, welcomeValidDays: num('wDays') ?? 0, welcomeAppliesToShop: $('wShop').checked
    };
    if (await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call('/api/admin/promo/settings', { method: 'PUT', body }))) loadSettings();
  });

  /* ----- Mã chào mừng đã cấp ----- */
  function status(c) {
    if (!c.isActive) return ['Đã thu hồi', 'b-off'];
    if (c.used > 0) return ['Đã dùng', 'b-go'];
    if (c.endsAt && new Date(c.endsAt).getTime() < Date.now()) return ['Hết hạn', 'b-bad'];
    if (c.held > 0) return ['Đang giữ chỗ', 'b-wait'];
    return ['Chưa dùng', 'b-queue'];
  }
  const offText = c => (c.type === 'Percent' ? `${c.value}%${c.maxDiscount ? ` <small>tối đa ${money(c.maxDiscount)}</small>` : ''}` : money(c.value))
    + (c.minOrder > 0 ? `<small>Đơn từ ${money(c.minOrder)}</small>` : '');

  async function loadWelcome() {
    try {
      const d = await API.call('/api/admin/promo/welcome?' + ADM.qs({ q: $('wq').value.trim(), page: W.page, size: W.size }));
      items = d.items;
      $('wStats').textContent = `${d.issued} mã đã cấp · ${d.used} đã dùng`;
      wrows.innerHTML = items.map(c => {
        const [label, cls] = status(c), usedUp = c.used > 0 || c.held > 0;
        return `<tr class="${c.isActive ? '' : 'dim'}">
          <td><b class="cp-code">${esc(c.code)}</b><small>Cấp ${ADM.dt(c.createdAt)}</small></td>
          <td>${esc(c.ownerName || '—')}<small>${esc(c.ownerEmail || '')}</small></td>
          <td class="num">${offText(c)}${c.saved ? `<small>Đã giảm ${money(c.saved)}</small>` : ''}</td>
          <td class="nw">${c.endsAt ? ADM.dt(c.endsAt) : 'Không giới hạn'}</td>
          <td><span class="badge ${cls}">${label}</span></td>
          <td><div class="acts">
            <button class="btn btn-ghost btn-sm" data-a="toggle" data-id="${c.id}">${c.isActive ? 'Thu hồi' : 'Bật lại'}</button>
            ${usedUp ? '' : `<button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${c.id}">Xoá</button>`}
          </div></td></tr>`;
      }).join('') || `<tr><td colspan="6" class="empty">${$('wq').value.trim() ? 'Không có mã phù hợp.' : 'Chưa cấp mã chào mừng nào. Mã sẽ tự cấp khi khách mới xác thực email xong.'}</td></tr>`;
      ADM.pager($('wPager'), d, p => { W.page = p; loadWelcome(); });
    } catch (err) { wrows.innerHTML = '<tr><td colspan="6" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  wrows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const c = items.find(x => x.id === b.dataset.id); if (!c) return;
    if (b.dataset.a === 'toggle') {
      if (await ADM.act(b, () => API.call(`/api/admin/promo/welcome/${c.id}/active`, { method: 'PUT', body: { isActive: !c.isActive } }))) loadWelcome();
    } else if (b.dataset.a === 'del') {
      const r = await ADM.ask({ title: 'Xoá mã ' + c.code, message: `Xoá hẳn mã này khỏi ví của <b>${esc(c.ownerEmail || 'khách')}</b>? Mã đã dùng trong đơn thì không xoá được (hãy thu hồi).`, confirmText: 'Xoá', danger: true });
      if (r === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/promo/welcome/' + c.id, { method: 'DELETE' }))) loadWelcome();
    }
  });

  $('wIssue').addEventListener('click', async () => {
    const mail = await ADM.ask({ title: 'Cấp mã chào mừng', message: 'Dành cho khách đã có tài khoản (vd. đăng ký trước khi bật tính năng). Mỗi khách chỉ có một mã chào mừng, theo cấu hình hiện tại.',
      label: 'Email của khách', required: true, confirmText: 'Cấp mã', placeholder: 'khach@example.com' });
    if (!mail) return;
    if (await ADM.act($('wIssue'), () => API.call('/api/admin/promo/welcome/issue', { method: 'POST', body: { email: mail } }))) { W.page = 1; loadWelcome(); }
  });
  $('wq').addEventListener('input', ADM.debounce(() => { W.page = 1; loadWelcome(); }, 250));

  loadSettings(); loadWelcome();
})();
