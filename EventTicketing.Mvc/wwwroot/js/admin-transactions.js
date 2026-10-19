(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, rows = $('rows');
  const S = { status: '', page: 1, size: 20 };
  const LABEL = { '': 'Tất cả', Paid: 'Thu tiền', Refunded: 'Hoàn tiền', Failed: 'Thất bại' };
  const KIND = { Paid: ['Thu tiền', 'b-go'], Refunded: ['Hoàn tiền', 'b-bad'], Failed: ['Thất bại', 'b-off'] };

  /* Nguồn giao dịch suy ra từ tiền tố mã (không cần đổi schema) */
  function source(ref) {
    if (ref.startsWith('MAN-')) return 'Admin xác nhận tay';
    if (ref.startsWith('INV-')) return 'Vé mời (0 đ)';
    if (ref.startsWith('RF-')) return 'Hoàn tiền';
    if (ref.startsWith('SIM-')) return 'Giả lập';
    return 'Cổng / ngân hàng';
  }

  const filters = () => ({ status: S.status, eventId: $('ev').value, q: $('q').value.trim(),
    from: $('from').value ? new Date($('from').value).toISOString() : '',
    to: $('to').value ? new Date(new Date($('to').value).getTime() + 864e5).toISOString() : '' });

  async function load() {
    try {
      const d = await API.call('/api/admin/transactions?' + ADM.qs({ ...filters(), page: S.page, size: S.size }));
      const by = Object.fromEntries(d.sums.map(c => [c.status, c]));
      const paid = by.Paid || { count: 0, amount: 0 }, ref = by.Refunded || { count: 0, amount: 0 };
      $('kpi').innerHTML = [
        ['Tổng thu', money(paid.amount), `${paid.count} giao dịch`],
        ['Đã hoàn', money(ref.amount), `${ref.count} giao dịch`],
        ['Thực thu (thu − hoàn)', money(paid.amount - ref.amount), 'Theo bộ lọc hiện tại']
      ].map(([l, v, s]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${esc(v)}</div><div class="sub">${esc(s)}</div></div>`).join('');
      const all = d.sums.reduce((a, c) => a + c.count, 0);
      $('chips').innerHTML = Object.keys(LABEL).map(k => `<button type="button" data-s="${k}" aria-pressed="${S.status === k}">${LABEL[k]}<span class="n">${k === '' ? all : (by[k]?.count || 0)}</span></button>`).join('');
      rows.innerHTML = d.items.map(t => {
        const [kt, kc] = KIND[t.status] || [t.status, ''];
        return `<tr><td class="num">${ADM.dt(t.createdAt)}</td>
          <td><span class="badge ${kc}">${kt}</span><small>${esc(source(t.transactionRef))}</small></td>
          <td class="r num"><b>${t.status === 'Refunded' ? '−' : ''}${money(t.amount)}</b></td>
          <td class="mono">${esc(t.transactionRef)}<small>Đơn ${ADM.shortId(t.bookingId)}</small></td>
          <td><b>${esc(t.userName)}</b><small>${esc(t.userEmail)}</small></td><td>${esc(t.eventName)}</td></tr>`;
      }).join('') || '<tr><td colspan="6" class="empty">Không có giao dịch phù hợp.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { toast(err.message, 'error'); }
  }

  $('chips').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.status = b.dataset.s; S.page = 1; load(); } });
  ['ev', 'from', 'to'].forEach(i => $(i).addEventListener('change', () => { S.page = 1; load(); }));
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  $('export').addEventListener('click', async e => {
    busy(e.currentTarget, true, 'Đang xuất…');
    try { await ADM.download('/api/admin/transactions/export', filters()); } catch (err) { toast(err.message, 'error'); }
    busy(e.currentTarget, false);
  });
  (async () => {
    try { const evs = await API.call('/api/admin/events'); $('ev').insertAdjacentHTML('beforeend', evs.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('')); } catch { /* bỏ qua */ }
    load();
  })();
})();
