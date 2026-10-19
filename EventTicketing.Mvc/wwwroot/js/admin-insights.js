(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  const pad = n => String(n).padStart(2, '0');
  const ymd = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const pct = (a, b) => b > 0 ? Math.round(100 * a / b) : 0;
  const DOW = ['Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7', 'Chủ nhật'];
  const CAT = { Order: 'Đơn hàng', Payment: 'Thanh toán', Ticket: 'Vé / email vé', Refund: 'Hoàn / đổi vé', Event: 'Sự kiện', Other: 'Khác' };

  const range = () => ({
    from: $('from').value ? new Date($('from').value).toISOString() : '',
    to: $('to').value ? new Date(new Date($('to').value).getTime() + 864e5).toISOString() : ''
  });
  function setPreset(k) {
    const to = new Date(), from = new Date();
    if (k === 'month') from.setDate(1); else from.setDate(from.getDate() - (+k - 1));
    $('from').value = ymd(from); $('to').value = ymd(to);
    document.querySelectorAll('#preset button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.d === k)));
  }
  const bar = (label, val, max, text, cls = '') => `<div class="hbar ${cls}"><span class="t">${esc(label)}</span><span class="v">${esc(text)}</span><span class="bar-bg"><i style="width:${max ? Math.round(100 * val / max) : 0}%"></i></span></div>`;

  function draw(d) {
    const done = d.paid + d.cancelled + d.expired;
    $('kpi').innerHTML = [
      ['Khách mua hàng', String(d.buyers), `${d.newUsers} tài khoản mới trong kỳ`],
      ['Khách quay lại', pct(d.repeatBuyers, d.buyers) + '%', `${d.repeatBuyers}/${d.buyers} đã mua từ 2 đơn`],
      ['Giá trị đơn trung bình', money(d.avgOrder), `${d.paid} đơn đã thanh toán`],
      ['Tỷ lệ thanh toán', pct(d.paid, done) + '%', `${d.expired} hết hạn · ${d.cancelled} huỷ/hoàn`]
    ].map(([l, v, s]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${esc(v)}</div><div class="sub">${esc(s)}</div></div>`).join('');

    const maxH = Math.max(1, ...d.byHour.map(x => x.orders));
    $('cHour').innerHTML = d.byHour.map(x => `<div class="c ${x.orders ? '' : 'z'}" title="${pad(x.key)}:00 — ${x.orders} đơn · ${x.tickets} vé · ${money(x.revenue)}"><i style="height:${Math.max(2, Math.round(100 * x.orders / maxH))}%"></i></div>`).join('');
    const peak = d.byHour.reduce((a, b) => b.orders > a.orders ? b : a, d.byHour[0]);
    $('aHour').innerHTML = `<span>00h</span><span>${peak.orders ? `Cao điểm ${pad(peak.key)}:00 (${peak.orders} đơn)` : 'Chưa có đơn'}</span><span>23h</span>`;

    const maxD = Math.max(1, ...d.byWeekday.map(x => x.orders));
    $('dow').innerHTML = d.byWeekday.map(x => bar(DOW[x.key], x.orders, maxD, `${x.orders} đơn · ${money(x.revenue)}`)).join('');

    $('top').innerHTML = d.topCustomers.map(c => `<tr><td><b>${esc(c.name)}</b><small>${esc(c.email)}</small></td><td class="r num">${c.orders}</td><td class="r num">${c.tickets}</td><td class="r num"><b>${money(c.spent)}</b></td><td class="nw">${ADM.dt(c.last)}</td></tr>`).join('')
      || '<tr><td colspan="5" class="empty">Chưa có đơn thanh toán trong kỳ này.</td></tr>';

    $('funnel').innerHTML = d.orders ? [
      bar('Đã thanh toán', d.paid, d.orders, `${d.paid} (${pct(d.paid, d.orders)}%)`),
      bar('Đang giữ chỗ', d.pending, d.orders, `${d.pending} (${pct(d.pending, d.orders)}%)`, 'warn'),
      bar('Hết hạn giữ chỗ', d.expired, d.orders, `${d.expired} (${pct(d.expired, d.orders)}%)`, 'off'),
      bar('Huỷ / hoàn tiền', d.cancelled, d.orders, `${d.cancelled} (${pct(d.cancelled, d.orders)}%)`, 'bad')
    ].join('') : '<p class="muted">Chưa có đơn nào trong kỳ này.</p>';

    $('fb').innerHTML = d.fbTotal ? `<p>${d.fbTotal} phản hồi: <b>${d.fbNew}</b> mới · <b>${d.fbInProgress}</b> đang xử lý · <b>${d.fbResolved}</b> đã xong.
      ${d.fbAvgReplyHours != null ? `Thời gian trả lời trung bình <b>${d.fbAvgReplyHours} giờ</b>.` : 'Chưa có phản hồi nào được trả lời.'}</p>` +
      Object.entries(d.fbByCategory).sort((a, b) => b[1] - a[1]).map(([k, n]) => bar(CAT[k] || k, n, d.fbTotal, String(n))).join('')
      : '<p class="muted">Chưa có phản hồi nào trong kỳ này.</p>';
  }

  async function load() {
    try { draw(await API.call('/api/admin/insights?' + ADM.qs(range()))); }
    catch (err) { toast(err.message, 'error'); }
  }
  document.querySelectorAll('#preset button').forEach(b => b.addEventListener('click', () => { setPreset(b.dataset.d); load(); }));
  ['from', 'to'].forEach(id => $(id).addEventListener('change', () => { document.querySelectorAll('#preset button').forEach(b => b.setAttribute('aria-pressed', 'false')); load(); }));
  document.querySelectorAll('[data-exp]').forEach(b => b.addEventListener('click', () =>
    ADM.act(b, () => ADM.download('/api/admin/insights/export', { kind: b.dataset.exp, ...range() }), 'Đã xuất file')));
  setPreset('30'); load();
})();
