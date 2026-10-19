/* Đánh giá & bình luận sản phẩm (kiểu Shopee) + báo cáo bình luận. Dùng chung cho /Shop (cửa sổ sản phẩm) và /Shop/Order/{id}. */
const ShopRev = (() => {
  'use strict';
  const REASONS = [['Spam', 'Spam / quảng cáo'], ['Offensive', 'Ngôn từ xúc phạm, thô tục'], ['Fake', 'Đánh giá giả mạo / sai sự thật'], ['Personal', 'Lộ thông tin cá nhân'], ['Other', 'Lý do khác']];
  const LABEL = ['', 'Rất tệ', 'Tệ', 'Bình thường', 'Tốt', 'Tuyệt vời'];
  const iso = s => (/Z|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z');
  const day = s => new Date(iso(s)).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
  const stars = (n, cls = '') => `<span class="rv-st ${cls}" role="img" aria-label="${n} trên 5 sao">${[1, 2, 3, 4, 5].map(i => `<i class="${n >= i - 0.25 ? 'on' : n >= i - 0.75 ? 'half' : ''}">★</i>`).join('')}</span>`;

  /* ---------- Chọn sao (form) ---------- */
  const picker = (name, val) => `<div class="rv-pick" role="radiogroup" aria-label="Số sao" data-pick="${name}">${[1, 2, 3, 4, 5].map(i => `<button type="button" role="radio" aria-checked="${val === i}" aria-label="${i} sao" class="${val >= i ? 'on' : ''}" data-star="${i}">★</button>`).join('')}<span class="rv-pl">${LABEL[val] || 'Chọn số sao'}</span></div>`;
  function setPick(box, v) {
    box.dataset.val = v;
    box.querySelectorAll('[data-star]').forEach(b => { const i = +b.dataset.star; b.classList.toggle('on', v >= i); b.setAttribute('aria-checked', v === i); });
    box.querySelector('.rv-pl').textContent = LABEL[v] || 'Chọn số sao';
  }

  /* ---------- Danh sách đánh giá công khai của một sản phẩm ---------- */
  const S = new Map();   // productId -> { data, items, page, filter }
  async function fetchPage(st, append) {
    const q = `?page=${st.page}${st.filter ? '&stars=' + st.filter : ''}`;
    const d = await API.call(`/api/shop/products/${st.pid}/reviews${q}`);
    st.data = d; st.items = append ? st.items.concat(d.items) : d.items;
  }
  function item(r) {
    return `<article class="rv-it" data-rid="${r.id}"><div class="rv-av" aria-hidden="true">${esc(r.name.charAt(0).toUpperCase())}</div>
      <div class="rv-bd"><div class="rv-hd"><b>${esc(r.name)}</b>${r.mine ? '<span class="rv-me">Của bạn</span>' : ''}</div>
        ${stars(r.stars)}<div class="rv-meta">${day(r.createdAt)}${r.variantName ? ` · Phân loại: ${esc(r.variantName)}` : ''}${r.edited ? ' · đã chỉnh sửa' : ''}</div>
        ${r.content ? `<p class="rv-tx">${esc(r.content)}</p>` : ''}
        ${r.adminReply ? `<div class="rv-reply"><b>Phản hồi của cửa hàng</b><p>${esc(r.adminReply)}</p></div>` : ''}
        ${r.mine ? '' : r.reportedByMe ? '<span class="rv-done">Đã báo cáo</span>' : `<button type="button" class="rv-rp" data-report="${r.id}">${icon('flag', 14)} Báo cáo</button>`}</div></article>`;
  }
  function paint(box, st) {
    const d = st.data, max = Math.max(1, ...d.distribution.map(x => x.count));
    box.innerHTML = `<h3>Đánh giá sản phẩm</h3>` + (d.count === 0 && !st.filter
      ? '<p class="muted rv-empty">Chưa có đánh giá nào. Khách đã nhận hàng có thể đánh giá ở trang chi tiết đơn hàng.</p>'
      : `<div class="rv-sum"><div class="rv-big"><b>${d.average.toFixed(1)}</b><span>/ 5</span>${stars(d.average, 'lg')}<small>${d.count} đánh giá</small></div>
        <div class="rv-dist">${[5, 4, 3, 2, 1].map(s => { const c = d.distribution.find(x => x.stars === s).count; return `<button type="button" class="rv-bar ${st.filter === s ? 'sel' : ''}" data-filter="${s}" aria-pressed="${st.filter === s}"><span>${s} ★</span><i><u style="width:${Math.round(c / max * 100)}%"></u></i><em>${c}</em></button>`; }).join('')}</div></div>
        ${st.filter ? `<p class="rv-flt">Đang lọc ${st.filter} sao · <button type="button" class="rv-lk" data-filter="0">Bỏ lọc</button></p>` : ''}
        <div class="rv-list">${st.items.map(item).join('') || '<p class="muted rv-empty">Không có đánh giá nào ở mức sao này.</p>'}</div>
        ${d.hasMore ? '<button type="button" class="btn btn-ghost btn-sm rv-more" data-more>Xem thêm đánh giá</button>' : ''}`);
  }
  async function mount(box, pid) {
    if (!box) return;
    box.classList.add('sh-rv'); box.dataset.pid = pid;
    let st = S.get(pid);
    if (st) { paint(box, st); return; }
    box.innerHTML = '<div class="skel" style="height:90px;border-radius:12px"></div>';
    st = { pid, page: 1, filter: 0, items: [], data: null };
    try { await fetchPage(st, false); S.set(pid, st); if (box.isConnected) paint(box, st); }
    catch { if (box.isConnected) box.innerHTML = '<p class="muted rv-empty">Không tải được đánh giá lúc này.</p>'; }
  }
  const invalidate = pid => (pid ? S.delete(pid) : S.clear());

  /* ---------- Hộp báo cáo bình luận ---------- */
  function openReport(rid, pid) {
    if (!API.token()) { toast('Vui lòng đăng nhập để báo cáo bình luận', 'info'); setTimeout(() => location.href = '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname + location.search), 700); return; }
    document.getElementById('rvRp')?.remove();
    document.body.insertAdjacentHTML('beforeend', `<div class="rv-rpbg" id="rvRp"><div class="rv-rpc" role="dialog" aria-modal="true" aria-labelledby="rvRpT">
      <h3 id="rvRpT">Báo cáo bình luận</h3><p class="muted">Chọn lý do. Cửa hàng sẽ xem xét và ẩn bình luận nếu vi phạm.</p>
      <div class="rv-rs">${REASONS.map(([k, l], i) => `<label><input type="radio" name="rvReason" value="${k}" ${i === 0 ? 'checked' : ''}> ${esc(l)}</label>`).join('')}</div>
      <textarea id="rvDetail" maxlength="300" rows="3" placeholder="Mô tả thêm (không bắt buộc, bắt buộc nếu chọn “Lý do khác”)"></textarea>
      <div class="rv-rpa"><button type="button" class="btn btn-ghost" data-rpx>Huỷ</button><button type="button" class="btn btn-primary" data-rpsend>Gửi báo cáo</button></div></div></div>`);
    const bg = document.getElementById('rvRp'); bg.dataset.rid = rid; bg.dataset.pid = pid || '';
    bg.querySelector('input')?.focus();
  }
  async function sendReport(btn) {
    const bg = document.getElementById('rvRp'); if (!bg) return;
    const reason = bg.querySelector('input[name=rvReason]:checked')?.value, detail = bg.querySelector('#rvDetail').value.trim();
    busy(btn, true, 'Đang gửi…');
    try {
      const r = await API.call(`/api/shop/reviews/${bg.dataset.rid}/report`, { method: 'POST', body: { reason, detail } });
      toast(r.message, 'ok');
      const st = S.get(bg.dataset.pid); if (st) st.items.forEach(x => { if (x.id === bg.dataset.rid) x.reportedByMe = true; });
      document.querySelectorAll(`.sh-rv[data-pid="${bg.dataset.pid}"]`).forEach(b => { const s = S.get(bg.dataset.pid); if (s) paint(b, s); });
      bg.remove();
    } catch (err) { toast(err.message, 'error'); busy(btn, false); }
  }

  document.addEventListener('click', async e => {
    const t = e.target;
    const rp = t.closest('[data-report]'); if (rp) { openReport(rp.dataset.report, rp.closest('.sh-rv')?.dataset.pid); return; }
    if (t.closest('[data-rpx]') || t.id === 'rvRp') { document.getElementById('rvRp')?.remove(); return; }
    const sd = t.closest('[data-rpsend]'); if (sd) { sendReport(sd); return; }
    const box = t.closest('.sh-rv'); if (!box) return;
    const st = S.get(box.dataset.pid); if (!st) return;
    const f = t.closest('[data-filter]');
    if (f) { const v = +f.dataset.filter; st.filter = st.filter === v ? 0 : v; st.page = 1; try { await fetchPage(st, false); paint(box, st); } catch (err) { toast(err.message, 'error'); } return; }
    if (t.closest('[data-more]')) { st.page++; try { await fetchPage(st, true); paint(box, st); } catch (err) { st.page--; toast(err.message, 'error'); } }
  });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') document.getElementById('rvRp')?.remove(); });

  return { stars, picker, setPick, mount, invalidate, day, LABEL };
})();
