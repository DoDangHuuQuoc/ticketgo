(() => {
  'use strict';
  if (!requireLogin()) return;
  const $ = id => document.getElementById(id);
  const guard = $('guard'), content = $('content');
  if (localStorage.getItem('role') !== 'Admin') {
    content.hidden = true; guard.hidden = false; $('updated').textContent = '';
    guard.innerHTML = '<h2>Cần quyền quản trị</h2><p class="muted">Đăng nhập bằng tài khoản Admin để xem trang này.</p>';
    return;
  }
  const sel = $('ev'); let events = [], timer = null;
  Object.assign(ICON, {
    wallet: '<path d="M3 7a2 2 0 0 1 2-2h13v4"/><path d="M3 7v10a2 2 0 0 0 2 2h14a1 1 0 0 0 1-1v-9a1 1 0 0 0-1-1H5a2 2 0 0 1-2-2Z"/><circle cx="16" cy="13.5" r="1"/>',
    ticket: '<path d="M3 9a2 2 0 0 0 0 6v3h18v-3a2 2 0 0 1 0-6V6H3Z"/><path d="M13 6v12" stroke-dasharray="2 3"/>',
    door: '<path d="M14 3h5v18h-5"/><path d="M4 12h10m0 0-3-3m3 3-3 3"/>',
    bolt: '<path d="M13 3 5 14h6l-1 7 8-11h-6Z"/>',
    film: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 4v16M17 4v16M3 9h4M3 15h4M17 9h4M17 15h4"/>',
    users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6.5 6.5 0 0 1 3.5 6"/>'
  });
  function niceMax(v) { if (!v) return 1; const e = Math.pow(10, Math.floor(Math.log10(v))), m = v / e; return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * e; }

  function barChart(data, title) {
    if (!data.length) return '<p class="muted">Chưa có đơn thanh toán nào.</p>';
    const W = 640, H = 260, L = 58, B = 30, T = 10, R = 8, iw = W - L - R, ih = H - T - B;
    const top = Math.max(...data.map(d => d.revenue)), max = niceMax(top), bw = Math.min(56, iw / data.length * 0.62);
    let g = '';
    for (let i = 0; i <= 4; i++) {
      const y = T + ih - ih * i / 4, lab = top ? compact(max * i / 4) : (i ? '' : '0');
      g += `<line x1="${L}" x2="${W - R}" y1="${y}" y2="${y}" stroke="#e2e7f1" ${i ? 'stroke-dasharray="3 4"' : ''}/><text x="${L - 8}" y="${y + 4}" text-anchor="end" font-size="11" fill="#64708c">${lab}</text>`;
    }
    data.forEach((d, i) => {
      const x = L + iw / data.length * (i + 0.5) - bw / 2, h = d.revenue ? Math.max(3, ih * d.revenue / max) : 2;
      g += `<rect x="${x.toFixed(1)}" y="${(T + ih - h).toFixed(1)}" width="${bw.toFixed(1)}" height="${h.toFixed(1)}" rx="${Math.min(8, bw / 2)}" fill="${d.revenue ? 'url(#bg)' : '#e2e7f1'}"><title>${esc(d.label)}: ${money(d.revenue)}, ${d.tickets} vé</title></rect>`;
      if (data.length <= 12 || i % Math.ceil(data.length / 12) === 0) g += `<text x="${(x + bw / 2).toFixed(1)}" y="${H - 8}" text-anchor="middle" font-size="11" fill="#64708c">${esc(d.label)}</text>`;
    });
    return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(title)}"><defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6d86f5"/><stop offset="1" stop-color="#3050e0"/></linearGradient></defs>${g}</svg>`;
  }

  let last = null, mode = 'hour';
  function drawChart() {
    if (!last) return;
    const box = $('chart'), sub = $('chartSub');
    if (mode === 'hour') {
      const data = last.byHour.map(x => ({ label: String(x.hour).padStart(2, '0') + 'h', revenue: x.revenue, tickets: x.tickets }));
      box.innerHTML = barChart(data, 'Biểu đồ doanh thu hôm nay theo giờ');
      sub.textContent = last.todayRevenue ? `Hôm nay: ${money(last.todayRevenue)} · ${last.ticketsToday} vé` : 'Hôm nay chưa có đơn thanh toán.';
    } else {
      const data = last.byDay.map(d => ({ label: new Date(d.date).toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit' }), revenue: d.revenue, tickets: d.tickets }));
      box.innerHTML = barChart(data, 'Biểu đồ doanh thu theo ngày');
      sub.textContent = data.length ? data.length + ' ngày có doanh thu' : '';
    }
  }

  async function analytics() {
    try {
      const a = await API.call('/api/admin/analytics' + (sel.value ? `?eventId=${sel.value}` : ''));
      last = a;
      const k = [
        ['Doanh thu', money(a.revenue), 'wallet', 'Đơn đã thanh toán'],
        ['Vé đã bán', a.ticketsSold, 'ticket', 'Tổng số vé'],
        ['Đã vào cổng', a.checkedIn, 'door', a.ticketsSold ? Math.round(a.checkedIn * 100 / a.ticketsSold) + '% số vé đã bán' : 'Chưa có vé'],
        ['Doanh thu hôm nay', money(a.todayRevenue), 'bolt', a.ticketsToday + ' vé bán hôm nay'],
        ['Khách hàng', a.customers, 'users', 'Tài khoản khách'],
        ['Sự kiện', a.eventCount, 'film', 'Tổng số sự kiện']
      ];
      $('kpi').innerHTML = k.map(([l, v, ic, sub]) => `<div class="kpi"><span class="ic">${icon(ic, 22)}</span><div class="l">${l}</div><div class="v">${esc(v)}</div><div class="sub">${esc(sub)}</div></div>`).join('');
      drawChart(); breakdowns(a);
      $('updated').textContent = 'Cập nhật lúc ' + new Date().toLocaleTimeString('vi-VN');
    } catch (err) { $('updated').textContent = ''; toast(err.status === 403 ? 'Bạn không có quyền Admin' : err.message, 'error'); }
  }

  const hbar = (label, v, max, text, cls = '') => `<div class="hbar ${cls}"><span class="t" title="${esc(label)}">${esc(label)}</span><span class="bar-bg"><i style="width:${max ? Math.max(2, Math.round(v * 100 / max)) : 0}%"></i></span><span class="v">${esc(text)}</span></div>`;
  const STATUS = { Paid: ['Đã thanh toán', ''], Pending: ['Đang giữ chỗ', 'warn'], Cancelled: ['Huỷ / hoàn', 'bad'], Expired: ['Hết hạn', 'off'] };
  const ACT = { 'booking.refund': 'Hoàn tiền đơn', 'coupon.create': 'Tạo mã giảm giá', 'coupon.update': 'Sửa mã giảm giá', 'coupon.enable': 'Bật mã giảm giá', 'coupon.disable': 'Tắt mã giảm giá', 'coupon.delete': 'Xoá mã giảm giá', 'site.maintenance.on': 'Bật bảo trì', 'site.maintenance.off': 'Tắt bảo trì', 'booking.cancel': 'Huỷ đơn', 'event.create': 'Tạo sự kiện', 'event.update': 'Sửa sự kiện', 'event.delete': 'Xoá sự kiện', 'event.publish': 'Công khai sự kiện', 'event.unpublish': 'Ẩn sự kiện', 'user.lock': 'Khoá tài khoản', 'user.unlock': 'Mở khoá tài khoản', 'user.role': 'Đổi vai trò' };

  function breakdowns(a) {
    const order = ['Paid', 'Pending', 'Cancelled', 'Expired'], map = Object.fromEntries(a.byStatus.map(x => [x.status, x])), max = Math.max(1, ...a.byStatus.map(x => x.count));
    $('byStatus').innerHTML = order.map(k => hbar(STATUS[k][0], (map[k]?.count || 0), max, `${map[k]?.count || 0} đơn`, STATUS[k][1])).join('')
      + `<p class="muted" style="margin-top:10px">Đã hoàn: <b>${money(a.refunded)}</b> · Đang giữ chỗ (còn hạn): <b>${a.pendingNow}</b> đơn</p>`;
    const tmax = Math.max(1, ...a.byType.map(x => x.revenue));
    $('byType').innerHTML = a.byType.map(x => hbar(`${x.name} · ${x.zone}`, x.revenue, tmax, `${compact(x.revenue)} (${x.tickets} vé)`)).join('') || '<p class="muted">Chưa có doanh thu.</p>';
    if (a.topEvents) {
      const emax = Math.max(1, ...a.topEvents.map(x => x.revenue));
      $('topEvents').innerHTML = a.topEvents.map(x => hbar(x.name, x.revenue, emax, `${compact(x.revenue)} (${x.tickets} vé)`)).join('') || '<p class="muted">Chưa có doanh thu.</p>';
    } else $('topEvents').innerHTML = '<p class="muted">Đang xem một sự kiện cụ thể. Chọn “Tất cả sự kiện” để xem xếp hạng.</p>';
  }

  async function feed() {
    try {
      const d = await API.call('/api/admin/audit?size=8');
      $('feed').innerHTML = d.items.map(l => `<li><span><b>${esc(ACT[l.action] || l.action)}</b><br><small>${esc(l.actorEmail)}${l.detail ? ' · ' + esc(l.detail.slice(0, 70)) : ''}</small></span><small>${new Date(l.createdAt).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' })}</small></li>`).join('') || '<li class="muted">Chưa có hoạt động nào.</li>';
    } catch { $('feed').innerHTML = '<li class="muted">Không tải được nhật ký.</li>'; }
  }

  /* ---------- Chế độ bảo trì ---------- */
  const mt = { enabled: false };
  function drawMaint(d) {
    mt.enabled = !!d.enabled;
    const st = $('mtState'), btn = $('mtToggle');
    st.innerHTML = d.enabled
      ? '<b class="mt-on">ĐANG BẬT</b> — khách chỉ thấy trang bảo trì. Khu quản trị và đăng nhập vẫn dùng được.'
      : '<b class="mt-off">ĐANG TẮT</b> — website hoạt động bình thường cho mọi khách truy cập.';
    btn.textContent = d.enabled ? 'Tắt bảo trì' : 'Bật bảo trì';
    btn.className = 'btn ' + (d.enabled ? 'btn-primary' : 'btn-danger');
    btn.disabled = false;
    $('mtText').value = d.message || '';
  }
  async function loadMaint() {
    try { drawMaint(await API.call('/api/admin/site/maintenance')); }
    catch (err) { $('mtState').textContent = 'Không tải được trạng thái bảo trì.'; }
  }
  async function saveMaint(enabled, btn, ok) {
    const message = $('mtText').value.trim();
    busy(btn, true, 'Đang lưu…');
    try { drawMaint(await API.call('/api/admin/site/maintenance', { method: 'PUT', body: { enabled, message } })); toast(ok, 'ok'); }
    catch (err) { toast(err.message, 'error'); }
    busy(btn, false);
  }
  $('mtToggle').addEventListener('click', async e => {
    const turnOn = !mt.enabled;
    if (turnOn && !confirm('Bật bảo trì? Khách sẽ không đặt vé được cho tới khi bạn tắt.')) return;
    await saveMaint(turnOn, e.currentTarget, turnOn ? 'Đã bật chế độ bảo trì' : 'Đã tắt chế độ bảo trì');
    const b = $('mtToggle'); b.textContent = mt.enabled ? 'Tắt bảo trì' : 'Bật bảo trì';
  });
  $('mtForm').addEventListener('submit', e => { e.preventDefault(); saveMaint(mt.enabled, $('mtSave'), 'Đã lưu lời nhắn'); });
  document.querySelectorAll('#chartMode button').forEach(b => b.addEventListener('click', () => {
    mode = b.dataset.m; document.querySelectorAll('#chartMode button').forEach(x => x.setAttribute('aria-pressed', x === b)); drawChart();
  }));

  function arm() { clearInterval(timer); if ($('auto').checked) timer = setInterval(() => { if (!document.hidden) analytics(); }, 5000); }

  (async () => {
    try {
      events = await API.call('/api/admin/events');
      sel.innerHTML = '<option value="">Tất cả sự kiện</option>' + events.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('');
    } catch (err) { return toast(err.status === 403 ? 'Bạn không có quyền Admin' : err.message, 'error'); }
    sel.addEventListener('change', analytics);
    $('auto').addEventListener('change', arm);
    arm(); analytics(); feed(); loadMaint();
  })();
})();
