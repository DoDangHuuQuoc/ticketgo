(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  let items = [], events = [];

  /* Trạng thái thực tế của mã (tính ở client từ dữ liệu server) */
  function status(c) {
    const now = Date.now();
    if (!c.isActive) return ['Đã tắt', 'b-off', 'off'];
    if (c.endsAt && new Date(c.endsAt).getTime() < now) return ['Hết hạn', 'b-bad', 'end'];
    if (c.maxUses && c.used + c.held >= c.maxUses) return ['Hết lượt', 'b-bad', 'end'];
    if (c.startsAt && new Date(c.startsAt).getTime() > now) return ['Sắp bắt đầu', 'b-wait', 'on'];
    return ['Đang bật', 'b-go', 'on'];
  }
  const valueText = c => c.type === 'Percent'
    ? `${c.value}%${c.maxDiscount ? ` <small>tối đa ${money(c.maxDiscount)}</small>` : ''}` : money(c.value);

  function draw() {
    const q = $('q').value.trim().toLowerCase(), st = $('state').value;
    const list = items.filter(c => (!q || (c.code + ' ' + (c.description || '')).toLowerCase().includes(q)) && (!st || status(c)[2] === st));
    rows.innerHTML = list.map(c => {
      const [label, cls] = status(c);
      const cond = [c.isPublic === false ? 'Mã riêng' : 'Công khai', c.minOrder > 0 ? 'Đơn từ ' + money(c.minOrder) : 'Mọi giá trị đơn', c.eventName ? 'Chỉ: ' + esc(c.eventName) : 'Mọi sự kiện', ...(c.appliesToShop ? ['Cả đơn lưu niệm'] : []), c.perUserLimit + ' lần/khách'];
      return `<tr class="${c.isActive ? '' : 'dim'}">
        <td><b class="cp-code">${esc(c.code)}</b>${c.description ? `<small>${esc(c.description)}</small>` : ''}</td>
        <td class="num">${valueText(c)}</td>
        <td><div class="cp-cond">${cond.map(x => `<span>${x}</span>`).join('')}</div></td>
        <td class="nw">${c.startsAt ? ADM.dt(c.startsAt) : 'Ngay'}<small>→ ${c.endsAt ? ADM.dt(c.endsAt) : 'Không giới hạn'}</small></td>
        <td class="r num">${c.used}${c.maxUses ? ' / ' + c.maxUses : ''}${c.held ? `<small>+${c.held} đang giữ chỗ</small>` : ''}${c.saved ? `<small>Đã giảm ${money(c.saved)}</small>` : ''}</td>
        <td><span class="badge ${cls}">${label}</span></td>
        <td><div class="acts">
          <button class="btn btn-ghost btn-sm" data-a="edit" data-id="${c.id}">Sửa</button>
          <button class="btn btn-ghost btn-sm" data-a="toggle" data-id="${c.id}">${c.isActive ? 'Tắt' : 'Bật'}</button>
          <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${c.id}">Xoá</button>
        </div></td></tr>`;
    }).join('') || `<tr><td colspan="7" class="empty">${items.length ? 'Không có mã phù hợp.' : 'Chưa có mã giảm giá nào. Bấm “+ Tạo mã” để bắt đầu.'}</td></tr>`;
  }

  async function load() {
    try {
      [items, events] = await Promise.all([API.call('/api/admin/coupons'), events.length ? events : API.call('/api/admin/events')]);
      draw();
    } catch (err) { rows.innerHTML = '<tr><td colspan="7" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  function form(c) {
    const isNew = !c, v = c || { type: 'Percent', value: 10, minOrder: 0, perUserLimit: 1, isActive: true };
    const m = ADM.modal({
      title: isNew ? 'Tạo mã giảm giá' : 'Sửa mã ' + c.code, wide: true,
      body: `<form class="dlg-form" novalidate>
        <div class="grid2">
          <div class="field"><label for="cCode">Mã (chữ hoa, số, - hoặc _)</label><input id="cCode" maxlength="40" autocomplete="off" spellcheck="false" placeholder="VD: GIAM10" /></div>
          <div class="field"><label for="cDesc">Mô tả ngắn (hiện cho khách)</label><input id="cDesc" maxlength="200" placeholder="VD: Giảm 10% mùa lễ hội" /></div>
          <div class="field"><label for="cType">Kiểu giảm</label><select id="cType" class="select" style="width:100%"><option value="Percent">Theo phần trăm (%)</option><option value="Amount">Theo số tiền (đ)</option></select></div>
          <div class="field"><label for="cVal" id="cValL">Giá trị</label><input id="cVal" type="number" min="1" step="1" inputmode="numeric" /></div>
          <div class="field" id="cMaxW"><label for="cMax">Giảm tối đa (đ, để trống = không giới hạn)</label><input id="cMax" type="number" min="0" step="1000" inputmode="numeric" /></div>
          <div class="field"><label for="cMin">Đơn tối thiểu (đ)</label><input id="cMin" type="number" min="0" step="1000" inputmode="numeric" /></div>
          <div class="field"><label for="cUses">Tổng lượt dùng tối đa (trống = không giới hạn)</label><input id="cUses" type="number" min="1" step="1" inputmode="numeric" /></div>
          <div class="field"><label for="cPer">Số lần mỗi khách được dùng</label><input id="cPer" type="number" min="1" max="100" step="1" inputmode="numeric" /></div>
          <div class="field"><label for="cFrom">Bắt đầu (trống = ngay)</label><input id="cFrom" type="datetime-local" /></div>
          <div class="field"><label for="cTo">Kết thúc (trống = không giới hạn)</label><input id="cTo" type="datetime-local" /></div>
          <div class="field full"><label for="cEv">Áp dụng cho</label><select id="cEv" class="select" style="width:100%"><option value="">Mọi sự kiện</option>${events.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')}</select></div>
          <div class="field full"><label class="check"><input type="checkbox" id="cOn" /> Đang bật (khách dùng được ngay khi lưu)</label></div>
          <div class="field full"><label class="check"><input type="checkbox" id="cShop" /> Áp dụng cả cho đơn hàng lưu niệm (chỉ khi chọn “Mọi sự kiện”)</label></div>
          <div class="field full"><label class="check"><input type="checkbox" id="cPub" /> Hiện công khai ở trang chủ (bỏ tick nếu là mã riêng, chỉ gửi cho từng khách)</label></div>
        </div>
        <p class="hint">Mỗi đơn luôn phải thanh toán tối thiểu ${money(1000)} (mã QR ngân hàng không tạo được số tiền 0 đ).</p>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">${isNew ? 'Tạo mã' : 'Lưu'}</button></div></form>`
    });
    $('cCode').value = v.code || ''; $('cDesc').value = v.description || ''; $('cType').value = v.type; $('cVal').value = v.value;
    $('cMax').value = v.maxDiscount ?? ''; $('cMin').value = v.minOrder || 0; $('cUses').value = v.maxUses ?? ''; $('cPer').value = v.perUserLimit;
    $('cFrom').value = v.startsAt ? ADM.toLocalInput(v.startsAt) : ''; $('cTo').value = v.endsAt ? ADM.toLocalInput(v.endsAt) : '';
    $('cEv').value = v.eventId || ''; $('cOn').checked = v.isActive; $('cPub').checked = v.isPublic !== false; $('cShop').checked = !!v.appliesToShop;
    const sync = () => {
      const pct = $('cType').value === 'Percent';
      $('cValL').textContent = pct ? 'Phần trăm giảm (1–100)' : 'Số tiền giảm (đ)';
      $('cVal').max = pct ? 100 : 100000000; $('cMaxW').hidden = !pct;
    };
    $('cType').addEventListener('change', sync); sync();
    const syncShop = () => { $('cShop').disabled = !!$('cEv').value; if ($('cEv').value) $('cShop').checked = false; };
    $('cEv').addEventListener('change', syncShop); syncShop();
    $('cCode').addEventListener('input', e => { e.target.value = e.target.value.toUpperCase().replace(/[^A-Z0-9_-]/g, ''); });
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const num = id => $(id).value === '' ? null : Number($(id).value);
      const iso = id => $(id).value ? new Date($(id).value).toISOString() : null;
      const body = {
        code: $('cCode').value, description: $('cDesc').value, type: $('cType').value, value: Number($('cVal').value) || 0,
        maxDiscount: $('cType').value === 'Percent' ? num('cMax') : null, minOrder: num('cMin') ?? 0, maxUses: num('cUses'),
        perUserLimit: num('cPer') ?? 1, startsAt: iso('cFrom'), endsAt: iso('cTo'), isActive: $('cOn').checked, isPublic: $('cPub').checked, eventId: $('cEv').value || null, appliesToShop: $('cShop').checked && !$('cEv').value
      };
      if (!/^[A-Z0-9_-]{3,40}$/.test(body.code)) return toast('Mã gồm 3–40 ký tự: chữ A–Z, số, - hoặc _', 'error');
      const ok = await ADM.act(e.currentTarget.querySelector('[type=submit]'),
        () => API.call(isNew ? '/api/admin/coupons' : '/api/admin/coupons/' + c.id, { method: isNew ? 'POST' : 'PUT', body }));
      if (ok) { m.close(); load(); }
    });
    setTimeout(() => $('cCode').focus(), 50);
  }

  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const c = items.find(x => x.id === b.dataset.id); if (!c) return;
    if (b.dataset.a === 'edit') return form(c);
    if (b.dataset.a === 'toggle') {
      if (await ADM.act(b, () => API.call(`/api/admin/coupons/${c.id}/active`, { method: 'PUT', body: { isActive: !c.isActive } }))) load();
    } else if (b.dataset.a === 'del') {
      const r = await ADM.ask({ title: 'Xoá mã ' + c.code, message: 'Xoá hẳn mã này? Nếu mã đã được dùng trong đơn, hệ thống sẽ không cho xoá (hãy tắt mã).', confirmText: 'Xoá', danger: true });
      if (r === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/coupons/' + c.id, { method: 'DELETE' }))) load();
    }
  });
  $('newCp').addEventListener('click', () => form(null));
  $('q').addEventListener('input', ADM.debounce(draw, 200)); $('state').addEventListener('change', draw);
  load();
})();
