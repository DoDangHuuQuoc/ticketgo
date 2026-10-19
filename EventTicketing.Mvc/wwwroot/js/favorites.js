/* Nghệ sĩ & chương trình yêu thích: phần dùng chung (avatar, sao) + trang khách /Favorites */
const FV = (() => {
  'use strict';
  const STAR = '<path d="m12 2.8 2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.7l-5.9 3.1 1.2-6.5L2.5 9.7l6.6-.9L12 2.8Z"/>';
  const starSvg = '<svg viewBox="0 0 24 24" aria-hidden="true">' + STAR + '</svg>';

  /* Avatar tròn (nghệ sĩ) / bo góc (chương trình). Có dấu sao vàng ở góc khi mục được đánh giá nhiều. */
  const avatar = (it, small) => {
    const ini = esc(String(it.name || '?').trim().split(/\s+/).slice(-2).map(w => w[0]).join('').toUpperCase());
    const pic = it.imageUrl ? `<img src="${esc(it.imageUrl)}" alt="" loading="lazy" onerror="this.remove()">` : '';
    return `<span class="fv-av${small ? ' sm' : ''}${it.kind === 'Program' ? ' prog' : ''}"><span class="pic">${pic || ini}</span>`
      + (it.hot ? `<span class="fv-hot" title="Được nhiều người đánh giá" role="img" aria-label="Được nhiều người đánh giá"><svg viewBox="0 0 24 24" aria-hidden="true">${STAR}</svg></span>` : '') + '</span>';
  };

  /* Hàng sao chỉ để xem (làm tròn tới nửa gần nhất không cần thiết: tô sao đủ theo điểm làm tròn) */
  const stars = avg => `<span class="stars" aria-hidden="true">${[1, 2, 3, 4, 5].map(i => `<span class="${avg >= i - 0.25 ? 'on' : ''}">${starSvg}</span>`).join('')}</span>`;
  const score = it => it.count
    ? `<div class="fv-score">${stars(it.avg)}<b>${Number(it.avg).toFixed(1)}</b><span>(${it.count} đánh giá)</span></div>`
    : '<div class="fv-score"><span>Chưa có đánh giá</span></div>';

  return { avatar, stars, score, starSvg };
})();

(() => {
  'use strict';
  const grid = { Artist: document.getElementById('gridArtist'), Program: document.getElementById('gridProgram') };
  if (!grid.Artist) return;   // file này cũng được nạp ở trang admin (chỉ dùng FV); các trang đó không có lưới khách
  const sec = { Artist: document.getElementById('secArtist'), Program: document.getElementById('secProgram') };
  const tabs = document.getElementById('tabs');
  let items = [], kind = '';

  const card = it => `<article class="fv-card" data-id="${it.id}">
    ${FV.avatar(it)}
    <h3>${esc(it.name)}</h3>
    <span class="fv-kind">${it.kind === 'Artist' ? 'Nghệ sĩ' : 'Chương trình'}</span>
    ${FV.score(it)}
    ${it.description ? `<p>${esc(it.description)}</p>` : ''}
    <div class="rate"><span class="rate-lab">${it.myStars ? 'Bạn đã chấm ' + it.myStars + ' sao – bấm để đổi' : 'Chấm sao cho ' + esc(it.name)}</span>
      <div class="rate-stars" role="group" aria-label="Chấm sao cho ${esc(it.name)}">${[1, 2, 3, 4, 5].map(n =>
        `<button type="button" data-n="${n}" class="${n <= it.myStars ? 'on' : ''}" aria-label="${n} sao" aria-pressed="${n === it.myStars}">${FV.starSvg}</button>`).join('')}</div></div>
  </article>`;

  function draw() {
    for (const k of ['Artist', 'Program']) {
      const list = items.filter(i => i.kind === k);
      sec[k].hidden = !!kind && kind !== k;
      grid[k].innerHTML = list.map(card).join('') || `<div class="empty">Chưa có ${k === 'Artist' ? 'nghệ sĩ' : 'chương trình'} nào.</div>`;
    }
  }

  async function load() {
    try { items = await API.call('/api/favorites'); draw(); }
    catch (err) { Object.values(grid).forEach(g => { g.innerHTML = '<div class="empty">Không tải được dữ liệu.</div>'; }); toast(err.message, 'error'); }
  }

  tabs.addEventListener('click', e => {
    const b = e.target.closest('button[data-k]'); if (!b) return;
    kind = b.dataset.k;
    tabs.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
    draw();
  });

  document.querySelector('main').addEventListener('click', async e => {
    const b = e.target.closest('.rate-stars button'); if (!b) return;
    if (!requireLogin()) return;   // chưa đăng nhập -> sang trang đăng nhập rồi quay lại
    const id = b.closest('.fv-card').dataset.id, it = items.find(x => x.id === id); if (!it) return;
    const btns = b.parentElement.querySelectorAll('button'); btns.forEach(x => { x.disabled = true; });
    try {
      const r = await API.call(`/api/favorites/${id}/rate`, { method: 'POST', body: { stars: Number(b.dataset.n) } });
      Object.assign(it, { myStars: r.myStars, count: r.count, avg: r.avg, hot: r.hot });
      toast(r.message, 'ok');
      // Vẽ lại đúng một thẻ để không làm nhảy trang
      const el = b.closest('.fv-card'); el.outerHTML = card(it);
    } catch (err) { btns.forEach(x => { x.disabled = false; }); toast(err.message, 'error'); }
  });

  load();
})();
