(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  let status = '', list = [];

  const stateBadges = e => [
    e.isPublished ? '' : '<span class="badge b-wait">Bản nháp</span>',
    e.isPast ? '<span class="badge b-off">Đã diễn ra</span>' : (e.isPublished ? '<span class="badge b-go">Đang mở bán</span>' : ''),
  ].join(' ');

  async function load() {
    try {
      list = await API.call('/api/admin/events?' + ADM.qs({ q: $('q').value, status }));
      rows.innerHTML = list.map(e => {
        const pct = Math.round((e.sold + e.holding) * 100 / Math.max(1, e.total));
        return `<tr class="${e.isPast ? 'dim' : ''}">
          <td><div class="cell-ev"><span class="thumb">${posterArt(e.name, e.posterUrl)}</span><div><button class="link" data-a="types" data-id="${e.id}">${esc(e.name)}</button><small>${esc(e.venue)}</small></div></div></td>
          <td class="num">${ADM.dt(e.startsAt)}</td>
          <td><b class="num">${e.sold}</b> / ${e.total}${e.holding ? ` <small>+${e.holding} đang giữ</small>` : ''}
            <div class="meter-sm" title="${pct}% đã bán hoặc đang giữ"><i style="width:${Math.min(100, pct)}%"></i></div><small>${e.typeCount} loại vé · ${e.orders} đơn</small></td>
          <td class="r num"><b>${money(e.revenue)}</b></td>
          <td>${stateBadges(e)}</td>
          <td><div class="acts">
            <button class="btn btn-ghost btn-sm" data-a="types" data-id="${e.id}">Loại vé</button>
            <button class="btn btn-ghost btn-sm" data-a="edit" data-id="${e.id}">Sửa</button>
            <button class="btn btn-ghost btn-sm" data-a="pub" data-id="${e.id}">${e.isPublished ? 'Ẩn' : 'Công khai'}</button>
            <button class="btn btn-ghost btn-sm" data-a="dup" data-id="${e.id}">Nhân bản</button>
            <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${e.id}">Xoá</button>
          </div></td></tr>`;
      }).join('') || '<tr><td colspan="6" class="empty">Chưa có sự kiện nào. Bấm “Tạo sự kiện” để bắt đầu.</td></tr>';
    } catch (err) { toast(err.message, 'error'); }
  }

  /* ---------- Form tạo / sửa sự kiện ---------- */
  async function openEditor(id) {
    let ev = { name: '', venue: '', description: '', posterUrl: '', seatMapUrl: '', isPublished: false, category: 'Concert', startsAt: new Date(Date.now() + 30 * 864e5).toISOString() };
    if (id) { try { ev = (await API.call('/api/admin/events/' + id)).ev; } catch (err) { return toast(err.message, 'error'); } }
    const m = ADM.modal({
      title: id ? 'Sửa sự kiện' : 'Tạo sự kiện mới', wide: true, body: `<form class="dlg-form" novalidate>
        ${id ? '' : '<p class="hint">Sau khi tạo, bạn sẽ thêm các loại vé (giá, số lượng). Sự kiện chỉ công khai được khi có ít nhất một loại vé.</p>'}
        <div class="grid2">
          <div class="field full"><label for="fName">Tên sự kiện *</label><input id="fName" maxlength="300" required value="${esc(ev.name)}" /></div>
          <div class="field"><label for="fVenue">Địa điểm *</label><input id="fVenue" maxlength="300" required value="${esc(ev.venue)}" /></div>
          <div class="field"><label for="fCat">Loại sự kiện</label><select id="fCat" class="select" style="width:100%">${[['Concert', 'Ca nhạc'], ['Sport', 'Thể thao'], ['Theatre', 'Sân khấu']].map(([k, n]) => `<option value="${k}" ${(ev.category || 'Concert') === k ? 'selected' : ''}>${n}</option>`).join('')}</select></div>
          <div class="field"><label for="fStart">Thời gian bắt đầu *</label><input id="fStart" type="datetime-local" required value="${ADM.toLocalInput(ev.startsAt)}" /></div>
          <div class="field full"><label for="fPoster">Ảnh poster (đường dẫn https)</label><input id="fPoster" type="url" maxlength="1000" placeholder="Để trống để dùng ảnh tự sinh" value="${esc(ev.posterUrl || '')}" /></div>
          <div class="field full"><label for="fSeatMap">Link ảnh sơ đồ chỗ ngồi (https)</label><input id="fSeatMap" type="url" maxlength="1000" placeholder="Dán link ảnh sơ đồ, để trống nếu không có" value="${esc(ev.seatMapUrl || '')}" /><small class="muted">Chỉ cần dán một đường link ảnh. Khách sẽ xem ảnh này ở trang mua vé.</small></div>
          <div class="field full"><label for="fDesc">Mô tả</label><textarea id="fDesc" rows="4" maxlength="2000">${esc(ev.description || '')}</textarea></div>
        </div>
        <div class="sw-row"><label for="fPub"><b>Công khai</b><small class="muted" style="display:block">Tắt = bản nháp, khách không thấy và không mua được.</small></label><span class="switch"><input type="checkbox" id="fPub" role="switch" ${ev.isPublished ? 'checked' : ''}></span></div>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">${id ? 'Lưu thay đổi' : 'Tạo sự kiện'}</button></div></form>`
    });
    const f = m.body.querySelector('form');
    f.querySelector('[data-x]').addEventListener('click', () => m.close());
    f.addEventListener('submit', async e => {
      e.preventDefault();
      const name = $('fName').value.trim(), venue = $('fVenue').value.trim();
      if (!name || !venue || !$('fStart').value) return toast('Nhập đủ tên, địa điểm và thời gian', 'error');
      const body = {
        name, venue, description: $('fDesc').value, posterUrl: $('fPoster').value.trim() || null, seatMapUrl: $('fSeatMap').value.trim() || null,
        startsAt: new Date($('fStart').value).toISOString(), isPublished: $('fPub').checked,
        category: $('fCat').value
      };
      const btn = f.querySelector('[type=submit]');
      busy(btn, true, 'Đang lưu…');
      try {
        const r = await API.call(id ? '/api/admin/events/' + id : '/api/admin/events', { method: id ? 'PUT' : 'POST', body });
        toast(r.message, 'ok'); m.close(); await load();
        if (!id) openTypes(r.id);   // tạo xong -> thêm loại vé ngay
      } catch (err) { toast(err.message, 'error'); busy(btn, false); }
    });
  }

  /* ---------- Quản lý loại vé ---------- */
  async function openTypes(eventId) {
    const m = ADM.modal({ title: 'Loại vé', wide: true, body: '<p class="muted">Đang tải…</p>' });
    let editing = null;

    async function render() {
      let d;
      try { d = await API.call('/api/admin/events/' + eventId); } catch (err) { m.body.innerHTML = `<p>${esc(err.message)}</p>`; return; }
      m.el.querySelector('.dlg-h h2').textContent = 'Loại vé – ' + d.ev.name;
      const cur = d.ticketTypes.find(t => t.id === editing);
      const seatLocked = !!cur && cur.orders > 0;   // đã có đơn: không đổi được kiểu ghế / số hàng / số ghế
      m.body.innerHTML = `
        <div class="table-wrap"><table class="tbl" style="min-width:640px"><thead><tr><th>Loại vé</th><th class="r">Giá</th><th class="r">Tổng</th><th class="r">Đã bán</th><th class="r">Đang giữ</th><th class="r">Còn lại</th><th></th></tr></thead><tbody>
        ${d.ticketTypes.map(t => `<tr><td><b>${esc(t.name)}</b><small>${esc(t.zone)}${t.seatMode ? ` · ${t.seatRows} hàng × ${t.seatsPerRow} ghế · ${t.seatMode === 2 ? 'khách tự chọn ghế' : 'ghế ngẫu nhiên'}` : ''}</small></td><td class="r num">${money(t.price)}</td><td class="r num">${t.totalQuantity}</td>
          <td class="r num">${t.sold}</td><td class="r num">${t.holding}</td><td class="r num"><b>${t.availableQuantity}</b></td>
          <td><div class="acts"><button class="btn btn-ghost btn-sm" data-e="${t.id}">Sửa</button><button class="btn btn-ghost btn-sm danger" data-d="${t.id}">Xoá</button></div></td></tr>`).join('')
          || '<tr><td colspan="7" class="empty">Chưa có loại vé nào.</td></tr>'}
        </tbody></table></div>
        <form class="sect" id="tf" novalidate>
          <h3>${cur ? 'Sửa loại vé' : 'Thêm loại vé'}</h3>
          ${cur ? `<p class="hint warn">Tổng số vé không được nhỏ hơn số đã bán + đang giữ (${cur.sold + cur.holding}). Hệ thống tự cộng/trừ phần chênh vào số vé còn lại.</p>` : ''}
          <div class="grid2">
            <div class="field"><label for="tName">Tên loại vé *</label><input id="tName" maxlength="100" required placeholder="VIP, Thường…" value="${esc(cur?.name || '')}" /></div>
            <div class="field"><label for="tZone">Khu vực *</label><input id="tZone" maxlength="100" required placeholder="Khu A" value="${esc(cur?.zone || '')}" /></div>
            <div class="field"><label for="tPrice">Giá (đ) *</label><input id="tPrice" type="number" min="0" step="1000" required value="${cur?.price ?? ''}" /></div>
            <div class="field"><label for="tQty">Tổng số vé *</label><input id="tQty" type="number" min="0" max="1000000" required value="${cur?.totalQuantity ?? ''}" /></div>
            <div class="field full"><label for="tMode">Kiểu ghế (sự kiện nhà thi đấu, sân vận động…)</label><select id="tMode" class="select" style="width:100%" ${seatLocked ? 'disabled' : ''}>
              <option value="0">Không đánh số ghế (vé #1, #2…)</option>
              <option value="1">Có số ghế — hệ thống xếp ngẫu nhiên khi thanh toán xong</option>
              <option value="2">Có số ghế — khách tự chọn trên sơ đồ (vé VIP) / admin chọn cho khách mời</option></select></div>
            <div class="field" data-seat><label for="tRows">Số hàng (A, B, C…)</label><input id="tRows" type="number" min="1" max="702" step="1" value="${cur?.seatRows || ''}" ${seatLocked ? 'disabled' : ''} /></div>
            <div class="field" data-seat><label for="tPer">Số ghế mỗi hàng</label><input id="tPer" type="number" min="1" max="1000" step="1" value="${cur?.seatsPerRow || ''}" ${seatLocked ? 'disabled' : ''} /></div>
            <p class="hint ${seatLocked ? 'warn' : ''}" id="tSeatHint" data-seat style="grid-column:1/-1;margin:0"></p>
          </div>
          <div class="dlg-f">${cur ? '<button type="button" class="btn btn-ghost" id="tCancel">Bỏ chỉnh sửa</button>' : ''}<button type="submit" class="btn btn-primary">${cur ? 'Lưu loại vé' : '+ Thêm loại vé'}</button></div>
        </form>`;
      // Kiểu ghế: hiện/ẩn ô hàng×ghế, tự điền tổng số vé = số ghế của sơ đồ
      const seatSync = fromInput => {
        const mode = +$('tMode').value, rows = +$('tRows').value || 0, per = +$('tPer').value || 0;
        m.body.querySelectorAll('[data-seat]').forEach(x => { x.hidden = mode === 0; });
        if (mode === 0) return;
        const cap = rows * per, last = rows > 0 && per > 0 ? `${rows > 0 ? rowLabel(rows - 1) : ''}${per}` : '';
        $('tSeatHint').textContent = cap > 0
          ? (seatLocked ? 'Loại vé đã có đơn nên không đổi được kiểu ghế. ' : '') + `Sơ đồ có ${cap.toLocaleString('vi-VN')} ghế (A1 … ${last}). Tổng số vé không được vượt số ghế.${mode === 2 ? ' Tối đa 3.000 ghế cho loại khách tự chọn.' : ''}`
          : 'Nhập số hàng và số ghế mỗi hàng.';
        if (fromInput && cap > 0 && !seatLocked) $('tQty').value = cap;
      };
      const rowLabel = i => { let s = '', n = i + 1; while (n > 0) { n--; s = String.fromCharCode(65 + n % 26) + s; n = Math.floor(n / 26); } return s; };
      $('tMode').value = String(cur?.seatMode || 0);
      $('tMode').addEventListener('change', () => seatSync(true));
      ['tRows', 'tPer'].forEach(i => $(i).addEventListener('input', () => seatSync(true)));
      seatSync(false);
      m.body.querySelectorAll('[data-e]').forEach(b => b.addEventListener('click', () => { editing = b.dataset.e; render(); }));
      m.body.querySelector('#tCancel')?.addEventListener('click', () => { editing = null; render(); });
      m.body.querySelectorAll('[data-d]').forEach(b => b.addEventListener('click', async () => {
        const t = d.ticketTypes.find(x => x.id === b.dataset.d);
        const ok = await ADM.ask({ title: 'Xoá loại vé', message: `Xoá loại vé <b>${esc(t.name)}</b>? Chỉ xoá được khi chưa có đơn hàng.`, confirmText: 'Xoá', danger: true });
        if (ok === null) return;
        try { const r = await API.call('/api/admin/ticket-types/' + t.id, { method: 'DELETE' }); toast(r.message, 'ok'); editing = null; render(); load(); }
        catch (err) { toast(err.message, 'error'); }
      }));
      m.body.querySelector('#tf').addEventListener('submit', async e => {
        e.preventDefault();
        const body = { name: $('tName').value.trim(), zone: $('tZone').value.trim(), price: +$('tPrice').value, totalQuantity: parseInt($('tQty').value, 10),
          seatMode: +$('tMode').value, seatRows: +$('tRows').value || 0, seatsPerRow: +$('tPer').value || 0 };
        if (!body.name || !body.zone || $('tPrice').value === '' || isNaN(body.totalQuantity)) return toast('Nhập đủ thông tin loại vé', 'error');
        if (body.seatMode !== 0) {
          if (body.seatRows < 1 || body.seatsPerRow < 1) return toast('Nhập số hàng và số ghế mỗi hàng', 'error');
          if (body.totalQuantity > body.seatRows * body.seatsPerRow) return toast(`Tổng số vé không được vượt số ghế (${body.seatRows * body.seatsPerRow})`, 'error');
        }
        const btn = e.currentTarget.querySelector('[type=submit]'); busy(btn, true, 'Đang lưu…');
        try {
          const r = await API.call(cur ? '/api/admin/ticket-types/' + cur.id : `/api/admin/events/${eventId}/ticket-types`, { method: cur ? 'PUT' : 'POST', body });
          toast(r.message, 'ok'); editing = null; await render(); load();
        } catch (err) { toast(err.message, 'error'); busy(btn, false); }
      });
    }
    render();
  }

  /* ---------- Hành động trên dòng ---------- */
  rows.addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const ev = list.find(x => x.id === b.dataset.id); if (!ev) return;
    switch (b.dataset.a) {
      case 'edit': return openEditor(ev.id);
      case 'types': return openTypes(ev.id);
      case 'pub': {
        const next = !ev.isPublished;
        if (!next && ev.sold + ev.holding > 0 && await ADM.ask({ title: 'Ẩn sự kiện', message: `Sự kiện đã có <b>${ev.sold}</b> vé bán và <b>${ev.holding}</b> đang giữ chỗ. Ẩn chỉ ngăn khách mới mua, vé đã bán vẫn hợp lệ.`, confirmText: 'Vẫn ẩn' }) === null) return;
        if (await ADM.act(b, () => API.call(`/api/admin/events/${ev.id}/publish`, { method: 'POST', body: { isPublished: next } }))) load();
        return;
      }
      case 'dup':
        if (await ADM.act(b, () => API.call(`/api/admin/events/${ev.id}/duplicate`, { method: 'POST' }))) load();
        return;
      case 'del': {
        const ok = await ADM.ask({ title: 'Xoá sự kiện', message: `Xoá vĩnh viễn <b>${esc(ev.name)}</b> cùng các loại vé? Sự kiện đã có đơn hàng sẽ không xoá được, hãy dùng “Ẩn”.`, confirmText: 'Xoá sự kiện', danger: true });
        if (ok === null) return;
        if (await ADM.act(b, () => API.call('/api/admin/events/' + ev.id, { method: 'DELETE' }))) load();
      }
    }
  });

  $('newEv').addEventListener('click', () => openEditor(null));
  $('q').addEventListener('input', ADM.debounce(load));
  $('chips').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    status = b.dataset.s; $('chips').querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', x === b)); load();
  });
  load();
})();
