(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  let items = [], hotMin = 5;
  const KIND = { Artist: 'Nghệ sĩ', Program: 'Chương trình' };

  function draw() {
    const q = fold($('q').value.trim()), k = $('kind').value;
    const list = items.filter(i => (!k || i.kind === k) && (!q || fold(i.name).includes(q)));
    rows.innerHTML = list.map(i => `<tr class="${i.isActive ? '' : 'dim'}">
      <td><div class="fv-name">${FV.avatar(i, true)}<div><b>${esc(i.name)}</b>${i.description ? `<small>${esc(i.description.length > 80 ? i.description.slice(0, 80) + '…' : i.description)}</small>` : ''}</div></div></td>
      <td>${KIND[i.kind] || esc(i.kind)}</td>
      <td class="nw">${i.count ? `${FV.stars(i.avg)} <b>${Number(i.avg).toFixed(1)}</b><small>${i.count} lượt đánh giá${i.hot ? ' · đang có dấu sao' : ''}</small>` : '<span class="muted">Chưa có</span>'}</td>
      <td><span class="badge ${i.isActive ? 'b-go' : 'b-off'}">${i.isActive ? 'Đang hiện' : 'Đã ẩn'}</span></td>
      <td><div class="acts">
        <button class="btn btn-ghost btn-sm" data-a="edit" data-id="${i.id}">Sửa</button>
        <button class="btn btn-ghost btn-sm" data-a="toggle" data-id="${i.id}">${i.isActive ? 'Ẩn' : 'Hiện'}</button>
        <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${i.id}">Xoá</button>
      </div></td></tr>`).join('')
      || `<tr><td colspan="5" class="empty">${items.length ? 'Không có mục phù hợp.' : 'Chưa có nghệ sĩ / chương trình nào. Bấm “+ Thêm mới” để bắt đầu.'}</td></tr>`;
  }

  async function load() {
    try {
      const r = await API.call('/api/admin/favorites');
      items = r.items; hotMin = r.hotMin;
      $('legend').innerHTML = `Mục có từ <b>${hotMin}</b> lượt đánh giá trở lên sẽ hiện dấu sao vàng cạnh ảnh đại diện (ở trang khách và ở bảng này).`;
      draw();
    } catch (err) { rows.innerHTML = '<tr><td colspan="5" class="empty">Không tải được dữ liệu.</td></tr>'; toast(err.message, 'error'); }
  }

  function form(it) {
    const isNew = !it, v = it || { kind: 'Artist', isActive: true };
    const m = ADM.modal({
      title: isNew ? 'Thêm nghệ sĩ / chương trình' : 'Sửa: ' + it.name,
      body: `<form class="dlg-form" novalidate>
        <div class="grid2">
          <div class="field"><label for="fKind">Loại</label><select id="fKind" class="select" style="width:100%"><option value="Artist">Nghệ sĩ</option><option value="Program">Chương trình</option></select></div>
          <div class="field"><label for="fName">Tên</label><input id="fName" maxlength="200" autocomplete="off" placeholder="VD: Sơn Tùng M-TP" /></div>
          <div class="field full"><label for="fImg">Ảnh đại diện (đường dẫn ảnh, để trống = dùng chữ cái đầu)</label><input id="fImg" maxlength="1000" inputmode="url" autocomplete="off" placeholder="https://... hoặc /img/ten-anh.png" /></div>
          <div class="field full"><label for="fDesc">Giới thiệu ngắn</label><textarea id="fDesc" rows="3" maxlength="1000" placeholder="Vài dòng giới thiệu cho khách"></textarea></div>
          <div class="field full"><label class="check"><input type="checkbox" id="fOn" /> Hiện cho khách xem và đánh giá</label></div>
          ${isNew ? '<div class="field full"><label class="check"><input type="checkbox" id="fNotify" checked /> Gửi thông báo (chuông) cho khách</label></div>' : ''}
        </div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">${isNew ? 'Thêm' : 'Lưu'}</button></div></form>`
    });
    $('fKind').value = v.kind; $('fName').value = v.name || ''; $('fImg').value = v.imageUrl || ''; $('fDesc').value = v.description || ''; $('fOn').checked = v.isActive;
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const body = { kind: $('fKind').value, name: $('fName').value.trim(), imageUrl: $('fImg').value.trim(), description: $('fDesc').value.trim(), isActive: $('fOn').checked, notify: isNew ? $('fNotify').checked : false };
      if (body.name.length < 2) return toast('Vui lòng nhập tên (tối thiểu 2 ký tự)', 'error');
      const ok = await ADM.act(e.currentTarget.querySelector('[type=submit]'),
        () => API.call(isNew ? '/api/admin/favorites' : '/api/admin/favorites/' + it.id, { method: isNew ? 'POST' : 'PUT', body }));
      if (ok) { m.close(); load(); }
    });
    setTimeout(() => $('fName').focus(), 50);
  }

  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const it = items.find(x => x.id === b.dataset.id); if (!it) return;
    if (b.dataset.a === 'edit') return form(it);
    if (b.dataset.a === 'toggle') {
      if (await ADM.act(b, () => API.call(`/api/admin/favorites/${it.id}/active`, { method: 'PUT', body: { isActive: !it.isActive } }))) load();
    } else if (b.dataset.a === 'del') {
      const r = await ADM.ask({ title: 'Xoá ' + it.name, message: 'Xoá hẳn mục này cùng toàn bộ đánh giá sao của khách? Nếu chỉ muốn tạm giấu, hãy dùng nút “Ẩn”.', confirmText: 'Xoá', danger: true });
      if (r === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/favorites/' + it.id, { method: 'DELETE' }))) load();
    }
  });
  $('newFv').addEventListener('click', () => form(null));
  $('q').addEventListener('input', ADM.debounce(draw, 200)); $('kind').addEventListener('change', draw);
  load();
})();
