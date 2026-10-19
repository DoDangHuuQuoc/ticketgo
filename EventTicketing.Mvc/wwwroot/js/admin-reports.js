(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  const pad = n => String(n).padStart(2, '0');
  const ymd = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const pct = (a, b) => b > 0 ? Math.round(100 * a / b) : 0;

  const range = () => ({
    from: $('from').value ? new Date($('from').value).toISOString() : '',
    to: $('to').value ? new Date(new Date($('to').value).getTime() + 864e5).toISOString() : '',
    eventId: $('ev').value
  });

  function setPreset(k) {
    const to = new Date(), from = new Date();
    if (k === 'month') from.setDate(1); else from.setDate(from.getDate() - (+k - 1));
    $('from').value = ymd(from); $('to').value = ymd(to);
    document.querySelectorAll('#preset button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.d === k)));
  }

  const fill = (sold, cap) => {
    const p = pct(sold, sold + cap);   // cap = số còn lại; sold + cap xấp xỉ tổng vé nếu chưa huỷ/hoàn
    return `<div class="hbar"><span class="t">${p}%</span><span class="v">còn ${cap}</span><span class="bar-bg"><i style="width:${p}%"></i></span></div>`;
  };

  function draw(d) {
    const stat = d.ordersByStatus || {};
    const checkRate = pct(d.checkedIn, d.ticketsIssued);
    $('kpi').innerHTML = [
      ['Doanh thu', money(d.revenue), `${d.orders} đơn · TB ${money(d.avgOrder)}/đơn`],
      ['Vé đã bán', String(d.tickets), d.invited ? `+ ${d.invited} vé mời` : 'Không tính vé mời'],
      ['Đã giảm giá', money(d.discount), d.invitedValue ? `Vé mời trị giá ${money(d.invitedValue)}` : 'Từ mã giảm giá'],
      ['Tiền đã hoàn', money(d.refunded), `${stat.Cancelled || 0} đơn huỷ/hoàn · ${stat.Expired || 0} hết hạn`],
      ['Đã vào cổng', `${d.checkedIn}/${d.ticketsIssued}`, `${checkRate}% số vé phát hành`]
    ].map(([l, v, s]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${esc(v)}</div><div class="sub">${esc(s)}</div></div>`).join('');

    const max = Math.max(1, ...d.byDay.map(x => x.revenue));
    $('chart').innerHTML = d.byDay.map(x => `<div class="c ${x.revenue ? '' : 'z'}" title="${x.date}: ${money(x.revenue)} · ${x.tickets} vé"><i style="height:${Math.max(2, Math.round(100 * x.revenue / max))}%"></i></div>`).join('');
    const f = d.byDay[0], l = d.byDay[d.byDay.length - 1];
    $('axis').innerHTML = f ? `<span>${f.date.split('-').reverse().join('/')}</span><span>Cao nhất ${money(max)}</span><span>${l.date.split('-').reverse().join('/')}</span>` : '';

    $('tEvent').innerHTML = d.byEvent.map(x => `<tr><td><b>${esc(x.name)}</b></td><td class="r num">${x.orders}</td><td class="r num">${x.tickets}</td><td class="r num"><b>${money(x.revenue)}</b></td><td class="r num">${money(x.discount)}</td><td>${fill(x.tickets, x.remaining)}</td></tr>`).join('')
      || '<tr><td colspan="6" class="empty">Chưa có đơn thanh toán trong kỳ này.</td></tr>';
    $('tType').innerHTML = d.byType.map(x => `<tr><td><b>${esc(x.name)}</b><small>${esc(x.zone)}</small></td><td>${esc(x.eventName)}</td><td class="r num">${x.orders}</td><td class="r num">${x.tickets}</td><td class="r num"><b>${money(x.revenue)}</b></td><td>${fill(x.capacity - x.remaining, x.remaining)}</td></tr>`).join('')
      || '<tr><td colspan="6" class="empty">Chưa có đơn thanh toán trong kỳ này.</td></tr>';
    $('tCoupon').innerHTML = d.byCoupon.map(x => `<tr><td><b class="cp-code">${esc(x.code)}</b></td><td class="r num">${x.orders}</td><td class="r num">${x.tickets}</td><td class="r num">${money(x.discount)}</td><td class="r num">${money(x.revenue)}</td></tr>`).join('')
      || '<tr><td colspan="5" class="empty">Không có đơn nào dùng mã giảm giá trong kỳ này.</td></tr>';
  }

  async function load() {
    try { draw(await API.call('/api/admin/reports?' + ADM.qs(range()))); }
    catch (err) { toast(err.message, 'error'); }
  }

  document.querySelectorAll('#preset button').forEach(b => b.addEventListener('click', () => { setPreset(b.dataset.d); load(); }));
  ['from', 'to'].forEach(i => $(i).addEventListener('change', () => { document.querySelectorAll('#preset button').forEach(b => b.setAttribute('aria-pressed', 'false')); load(); }));
  $('ev').addEventListener('change', load);
  document.querySelectorAll('[data-exp]').forEach(b => b.addEventListener('click', async () => {
    busy(b, true, 'Đang xuất…');
    try { await ADM.download('/api/admin/reports/export', { ...range(), kind: b.dataset.exp }); } catch (err) { toast(err.message, 'error'); }
    busy(b, false);
  }));

  setPreset('30');
  (async () => {
    try { const evs = await API.call('/api/admin/events'); $('ev').insertAdjacentHTML('beforeend', evs.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')); } catch { /* bỏ qua */ }
    load();
  })();
})();
