(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  let configured = false;

  const digits = v => String(v || '').replace(/\s+/g, '');
  const rows = d => `<dl class="kv pay-kv"><dt>Ngân hàng</dt><dd>${esc(d.bankName)}</dd><dt>Số tài khoản</dt><dd class="mono">${esc(d.accountNo)}</dd>
    <dt>Số tiền</dt><dd>${money(d.amount)}</dd><dt>Nội dung</dt><dd class="mono">${esc(d.content)}</dd></dl>`;

  async function preview() {
    const bin = $('bank').value, acc = digits($('acc').value), amount = Number($('amt').value);
    if (!bin || acc.length < 4) { $('prev').innerHTML = '<p class="muted">Chọn ngân hàng và nhập số tài khoản để xem thử.</p>'; return; }
    try {
      const d = await API.call('/api/admin/payment-settings/preview', { method: 'POST', body: { bankBin: bin, accountNo: acc, amount } });
      $('prev').innerHTML = `<img class="pay-qr" src="${d.image}" alt="Mã QR thử" width="240" height="240" />${rows(d)}`;
    } catch (err) { $('prev').innerHTML = `<p class="muted">${esc(err.message)}</p>`; }
  }

  function notice() {
    $('notice').innerHTML = configured ? '' : '<div class="hint warn">Chưa có tài khoản nhận tiền — khách sẽ không thấy mã QR ở trang thanh toán. Hãy thiết lập bên dưới.</div>';
  }

  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    const btn = $('save');
    busy(btn, true, 'Đang lưu…');
    try {
      const r = await API.call('/api/admin/payment-settings', { method: 'PUT', body: { bankBin: $('bank').value, accountNo: digits($('acc').value), accountName: $('name').value } });
      toast(r.message, 'ok'); configured = true; notice(); $('name').value = $('name').value.toUpperCase();
    } catch (err) { toast(err.message, 'error'); }
    busy(btn, false);
  });

  const again = ADM.debounce(preview, 350);
  ['bank'].forEach(i => $(i).addEventListener('change', preview));
  ['acc', 'amt'].forEach(i => $(i).addEventListener('input', again));

  (async () => {
    try {
      const s = await API.call('/api/admin/payment-settings');
      $('bank').insertAdjacentHTML('beforeend', s.banks.map(b => `<option value="${b.bin}">${esc(b.name)}</option>`).join(''));
      $('bank').value = s.bankBin; $('acc').value = s.accountNo; $('name').value = s.accountName;
      configured = s.configured; notice(); preview();
    } catch (err) { toast(err.message, 'error'); }
  })();
})();
