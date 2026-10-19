/* Bộ chọn ghế dạng lưới hàng × ghế: khách chọn ghế khi mua vé, admin chọn ghế cho khách mời.
   SeatPicker.mount(el, { typeId, max: () => số ghế tối đa, onChange: list => {}, initial: [] }) -> { get, reload, trim } */
window.SeatPicker = (() => {
  async function mount(el, opt) {
    let data = null, picked = [...(opt.initial || [])];
    const maxN = () => Math.max(1, Number(opt.max()) || 1);
    const fire = () => opt.onChange && opt.onChange(picked.slice());

    function sum() {
      const s = el.querySelector('.sp-sum'); if (!s) return;
      s.textContent = picked.length ? `Đã chọn ${picked.length}/${maxN()} ghế: ${picked.join(', ')}` : `Chọn ${maxN()} ghế trên sơ đồ.`;
    }
    function mark(label, on) {
      const b = el.querySelector(`[data-s="${label}"]`); if (!b) return;
      b.classList.toggle('on', on); b.setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    function paint() {
      const taken = new Set(data.taken), sel = new Set(picked);
      el.innerHTML = `<div class="sp">
        <div class="sp-legend"><span><i class="sp-seat"></i>Trống</span><span><i class="sp-seat on"></i>Bạn chọn</span><span><i class="sp-seat off"></i>Đã có người</span></div>
        <div class="sp-stage">Sân khấu / sàn đấu</div>
        <div class="sp-scroll"><div class="sp-grid" role="group" aria-label="Chọn ghế ${esc(data.zone || '')}">${data.rowLabels.map(r => `<div class="sp-row"><span class="sp-rl">${r}</span>${Array.from({ length: data.perRow }, (_, i) => {
          const l = r + (i + 1), off = taken.has(l), on = sel.has(l);
          return `<button type="button" class="sp-seat${off ? ' off' : on ? ' on' : ''}" data-s="${l}" ${off ? 'disabled' : ''} aria-pressed="${on}" aria-label="Ghế ${l}${off ? ', đã có người' : ''}" title="${l}">${i + 1}</button>`;
        }).join('')}</div>`).join('')}</div></div>
        <p class="sp-sum" aria-live="polite"></p></div>`;
      el.querySelector('.sp-grid').addEventListener('click', e => {
        const b = e.target.closest('[data-s]'); if (!b || b.disabled) return;
        const l = b.dataset.s, i = picked.indexOf(l);
        if (i >= 0) { picked.splice(i, 1); mark(l, false); }
        else {
          if (picked.length >= maxN()) {
            if (maxN() === 1) { mark(picked[0], false); picked = []; }
            else { if (window.toast) toast(`Bạn chỉ chọn tối đa ${maxN()} ghế. Bỏ bớt một ghế để đổi.`, 'error'); return; }
          }
          picked.push(l); mark(l, true);
        }
        sum(); fire();
      });
      sum();
    }

    async function load() {
      el.innerHTML = '<p class="muted sp-load">Đang tải sơ đồ ghế…</p>';
      try { data = await API.call(`/api/ticket-types/${opt.typeId}/seats`); }
      catch (err) { data = null; el.innerHTML = `<p class="sp-err">${esc(err.message)}</p>`; return; }
      picked = picked.filter(s => !data.taken.includes(s));   // ghế vừa bị người khác lấy thì bỏ chọn
      paint(); fire();
    }

    await load();
    return {
      get: () => picked.slice(),
      reload: load,
      trim: () => { while (picked.length > maxN()) mark(picked.pop(), false); sum(); fire(); }   // gọi khi giảm số lượng
    };
  }
  return { mount };
})();
