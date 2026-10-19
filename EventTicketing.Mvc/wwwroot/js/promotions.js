(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const tabs = { all: ['tab-all', 'pane-all'], mine: ['tab-mine', 'pane-mine'] };
  const logged = () => !!API.token();
  const STATUS = {
    expired: 'Đã hết hạn', soon: 'Chưa tới ngày áp dụng', off: 'Đã ngừng áp dụng', out: 'Đã hết lượt', used: 'Bạn đã dùng hết lượt'
  };
  let mineLoaded = false, mine = [];

  document.querySelector('#cats a[data-m="promo"]')?.setAttribute('aria-current', 'page');

  function show(k, push) {
    for (const [key, [t, p]] of Object.entries(tabs)) { $(t).setAttribute('aria-selected', key === k); $(p).hidden = key !== k; }
    if (push) history.replaceState(null, '', k === 'mine' ? '/Promotions?tab=mine' : '/Promotions');
    if (k === 'mine') loadMine();
  }
  Object.keys(tabs).forEach(k => $(tabs[k][0]).addEventListener('click', () => show(k, true)));

  const empty = (title, sub, extra = '') => `<div class="empty"><p class="strong" style="color:var(--ink)">${title}</p><p>${sub}</p>${extra}</div>`;

  /* ---------- Tất cả khuyến mãi ---------- */
  function card(c, mode) {
    const bad = mode === 'mine' && c.status !== 'ok';
    const action = mode === 'all'
      ? (c.saved ? '<span class="cp-saved">Đã lưu</span>' : `<button type="button" class="ck-copy cp-save" data-save="${esc(c.code)}">Lưu</button>`)
      : (bad ? `<span class="badge b-off">${STATUS[c.status] || ''}</span>`
             : `<a class="ck-copy cp-use" href="${c.eventId ? '/Event/Book/' + c.eventId : '/'}">Dùng ngay</a>`);
    const meta = mode === 'all' ? VC.meta(c)
      : [c.minOrder > 0 ? `Đơn từ ${money(c.minOrder)}` : 'Mọi giá trị đơn', c.eventName ? `Chỉ: ${esc(c.eventName)}` : 'Mọi sự kiện', c.endsAt ? `HSD ${VC.day(c.endsAt)}` : 'Không hết hạn'];
    return `<article class="cp-card ${bad ? 'dim' : ''}"><div class="cp-val">${VC.value(c)}</div>
      ${c.description ? `<p class="cp-desc">${esc(c.description)}</p>` : ''}
      <ul class="cp-meta">${meta.map(m => `<li>${m}</li>`).join('')}</ul>
      <div class="cp-row"><code>${esc(c.code)}</code><span class="cp-btns"><button type="button" class="ck-copy" data-copy="${esc(c.code)}">Sao chép</button>${action}</span></div>
      ${mode === 'mine' ? `<button type="button" class="cp-del" data-unsave="${esc(c.code)}">Bỏ lưu</button>` : ''}</article>`;
  }

  async function loadAll() {
    const el = $('pane-all'); el.innerHTML = '<div class="cp-grid"><div class="skel" style="height:190px;border-radius:20px"></div><div class="skel" style="height:190px;border-radius:20px"></div></div>';
    try {
      const list = await API.call('/api/coupons/public');
      el.innerHTML = list.length ? `<div class="cp-grid">${list.map(c => card(c, 'all')).join('')}</div>`
        : empty('Hiện chưa có khuyến mãi nào.', 'Hãy quay lại sau nhé, voucher mới sẽ xuất hiện ở đây.');
    } catch (err) { el.innerHTML = empty('Không tải được khuyến mãi', esc(err.message)); }
  }

  /* ---------- Voucher của tôi ---------- */
  function paintMine() {
    const el = $('pane-mine'), usable = mine.filter(c => c.status === 'ok').length;
    $('mineN').hidden = !mine.length; $('mineN').textContent = mine.length;
    if (!mine.length) { el.innerHTML = empty('Ví voucher đang trống.', 'Vào tab Khuyến mãi và bấm “Lưu” để cất voucher vào đây.', '<button type="button" class="btn btn-primary" id="goAll" style="margin-top:14px">Xem khuyến mãi</button>'); return; }
    el.innerHTML = `<p class="muted" style="margin:0 0 14px">${usable} voucher dùng được trong ${mine.length} voucher đã lưu.</p><div class="cp-grid">${mine.map(c => card(c, 'mine')).join('')}</div>`;
  }
  async function loadMine() {
    const el = $('pane-mine');
    if (!logged()) { el.innerHTML = empty('Đăng nhập để xem ví voucher', 'Voucher bạn lưu sẽ được giữ trong tài khoản.', `<a class="btn btn-primary" style="margin-top:14px" href="/Account/Login?returnUrl=${encodeURIComponent('/Promotions?tab=mine')}">Đăng nhập</a>`); return; }
    if (mineLoaded) { paintMine(); return; }
    el.innerHTML = '<div class="skel" style="height:190px;border-radius:20px"></div>';
    try { mine = await API.call('/api/coupons/mine'); mineLoaded = true; paintMine(); }
    catch (err) { el.innerHTML = empty('Không tải được ví voucher', esc(err.message)); }
  }

  /* ---------- Thao tác ---------- */
  document.addEventListener('click', async e => {
    const cp = e.target.closest('[data-copy]'); if (cp) { VC.copy(cp.dataset.copy, cp); return; }
    if (e.target.closest('#goAll')) { show('all', true); return; }
    const sv = e.target.closest('[data-save]');
    if (sv) { if (await VC.save(sv.dataset.save, sv)) { sv.outerHTML = '<span class="cp-saved">Đã lưu</span>'; mineLoaded = false; refreshCount(); } return; }
    const un = e.target.closest('[data-unsave]');
    if (un && await VC.unsave(un.dataset.unsave)) {
      mine = mine.filter(c => c.code !== un.dataset.unsave); paintMine(); toast('Đã bỏ lưu voucher');
      loadAll();   // tab Khuyến mãi hiện lại nút "Lưu" cho mã vừa bỏ
    }
  });
  async function refreshCount() {
    if (!logged()) return;
    try { const m = await API.call('/api/coupons/mine'); $('mineN').hidden = !m.length; $('mineN').textContent = m.length; } catch { /* phụ */ }
  }

  loadAll();
  if (logged()) refreshCount();
  if (new URLSearchParams(location.search).get('tab') === 'mine') show('mine', false);
})();
