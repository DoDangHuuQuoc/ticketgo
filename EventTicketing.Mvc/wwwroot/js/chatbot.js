'use strict';
/* Widget chatbot hỗ trợ khách hàng. Dùng API/esc từ app.js. Lịch sử lưu trong sessionStorage (mất khi đóng tab). */
(() => {
  const KEY = 'cb_hist', OPEN = 'cb_open';
  const GREET = 'Xin chào! Mình là trợ lý TicketGo. Bạn cần hỗ trợ gì?';
  const START = ['Sự kiện đang mở bán', 'Cách mua vé', 'Thanh toán', 'Mã giảm giá', 'Đồ lưu niệm', 'Chat với nhân viên'];
  const svgChat = '<svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12Z"/><path d="M8.5 11h.01M12 11h.01M15.5 11h.01"/></svg>';
  const svgSend = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M22 2 11 13M22 2l-7 20-4-9-9-4 20-7Z"/></svg>';
  const svgX = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>';

  const ss = { get: k => { try { return sessionStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { sessionStorage.setItem(k, v); } catch { } } };
  let hist = []; try { hist = JSON.parse(ss.get(KEY) || '[]'); } catch { hist = []; }
  let busyNow = false;

  const root = document.createElement('div');
  root.innerHTML = `
    <button class="cb-fab" type="button" aria-label="Mở trợ lý hỗ trợ" aria-expanded="false" aria-controls="cb-panel">${svgChat}<span class="cb-dot"></span></button>
    <section class="cb-panel" id="cb-panel" role="dialog" aria-label="Trợ lý TicketGo">
      <header class="cb-head"><span class="cb-avatar">${svgChat.replace('26', '20').replace('26', '20')}</span>
        <div><b id="cb-title">Trợ lý TicketGo</b><small id="cb-sub">Trả lời tự động</small></div>
        <button class="cb-x" type="button" aria-label="Đóng">${svgX}</button></header>
      <div class="cb-body" id="cb-body" aria-live="polite"></div>
      <div class="cb-live-bar" id="cb-bar" hidden></div>
      <form class="cb-form" autocomplete="off">
        <input type="text" maxlength="500" placeholder="Nhập câu hỏi của bạn…" aria-label="Câu hỏi" />
        <button type="submit" aria-label="Gửi">${svgSend}</button>
      </form>
      <div class="cb-foot" id="cb-foot">${document.body.dataset.hotline ? 'Cần gấp: gọi ' + esc(document.body.dataset.hotline) : ''}</div>
    </section>`;
  document.body.appendChild(root);

  const fab = root.querySelector('.cb-fab'), panel = root.querySelector('.cb-panel'), body = root.querySelector('.cb-body');
  const form = root.querySelector('.cb-form'), input = form.querySelector('input'), sendBtn = form.querySelector('button');

  // **đậm** + xuống dòng; mọi thứ khác được escape nên không thể chèn HTML
  const fmt = t => esc(t).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');
  const scroll = () => { body.scrollTop = body.scrollHeight; };
  const clearExtras = () => body.querySelectorAll('.cb-chips,.cb-links').forEach(n => n.remove());

  function add(role, text, extra) {
    clearExtras();
    const m = document.createElement('div');
    m.className = 'cb-msg ' + (role === 'me' ? 'me' : 'bot') + (extra && extra.err ? ' err' : '');
    m.innerHTML = fmt(text); body.appendChild(m);
    if (extra && extra.links && extra.links.length) {
      const l = document.createElement('div'); l.className = 'cb-links';
      extra.links.forEach(x => { const a = document.createElement('a'); a.href = x.url; a.textContent = x.label; l.appendChild(a); });
      body.appendChild(l);
    }
    if (extra && extra.suggestions && extra.suggestions.length) {
      const c = document.createElement('div'); c.className = 'cb-chips';
      extra.suggestions.forEach(s => { const b = document.createElement('button'); b.type = 'button'; b.textContent = s; b.onclick = () => send(s); c.appendChild(b); });
      body.appendChild(c);
    }
    scroll();
  }

  function save() { ss.set(KEY, JSON.stringify(hist.slice(-30))); }

  // Form gửi phản hồi cho nhân viên (hiện trong khung chat khi bot trả action = "feedback")
  function feedbackForm() {
    body.querySelectorAll('.cb-fb').forEach(n => n.remove());
    const logged = !!API.token();
    const f = document.createElement('form'); f.className = 'cb-fb'; f.noValidate = true;
    f.innerHTML = `
      ${logged ? '' : '<input name="name" maxlength="200" placeholder="Họ tên" aria-label="Họ tên" required /><input name="email" type="email" maxlength="256" placeholder="Email nhận phản hồi" aria-label="Email" required />'}
      <select name="category" aria-label="Chủ đề"><option value="Order">Đơn hàng</option><option value="Payment">Thanh toán</option><option value="Ticket">Vé / email vé</option><option value="Refund">Hoàn / đổi vé</option><option value="Event">Sự kiện</option><option value="Other" selected>Khác</option></select>
      <textarea name="message" rows="3" maxlength="2000" placeholder="Mô tả vấn đề (kèm mã đơn nếu có)" aria-label="Nội dung" required></textarea>
      <div class="cb-fb-b"><button type="button" class="cb-fb-x">Huỷ</button><button type="submit" class="cb-fb-ok">Gửi nhân viên</button></div>`;
    body.appendChild(f); scroll(); f.querySelector('input,textarea').focus();
    f.querySelector('.cb-fb-x').onclick = () => f.remove();
    f.onsubmit = async e => {
      e.preventDefault();
      const v = Object.fromEntries(new FormData(f));
      if (!logged && (!(v.name || '').trim() || !/^\S+@\S+\.\S+$/.test((v.email || '').trim()))) return toast('Vui lòng nhập họ tên và email hợp lệ', 'error');
      if ((v.message || '').trim().length < 5) return toast('Vui lòng mô tả vấn đề (ít nhất 5 ký tự)', 'error');
      const ok = f.querySelector('.cb-fb-ok'); ok.disabled = true; ok.textContent = 'Đang gửi…';
      try {
        const r = await API.call('/api/feedback', { method: 'POST', body: v });
        f.remove(); add('bot', '✅ ' + r.message, { suggestions: logged ? ['Phản hồi của tôi', 'Cách mua vé'] : ['Cách mua vé'] });
        hist.push({ role: 'bot', text: '✅ ' + r.message }); save();
      } catch (err) { ok.disabled = false; ok.textContent = 'Gửi nhân viên'; toast(err.message, 'error'); }
    };
  }


  /* ───────── Chat trực tiếp với nhân viên ───────── */
  const LKEY = 'cb_live';
  const ls = { get: k => { try { return localStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { } }, del: k => { try { localStorage.removeItem(k); } catch { } } };
  let live = null; try { live = JSON.parse(ls.get(LKEY) || 'null'); } catch { live = null; }   // { id, key }
  let mode = 'bot', liveMsgs = [], liveStatus = 'Open', online = false, lastSeenStaff = '', pollT = null, sending = false;
  const utc = iso => new Date(/Z|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
  const clock = iso => utc(iso).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
  const isOpen = () => panel.classList.contains('open');
  const titleEl = root.querySelector('#cb-title'), subEl = root.querySelector('#cb-sub'), bar = root.querySelector('#cb-bar');

  function dot(on) {
    const d = fab.querySelector('.cb-dot');
    if (on && !d) { const x = document.createElement('span'); x.className = 'cb-dot'; fab.appendChild(x); }
    if (!on && d) d.remove();
  }
  function head() {
    const lv = mode === 'live';
    titleEl.textContent = lv ? 'Nhân viên TicketGo' : 'Trợ lý TicketGo';
    subEl.textContent = lv ? (liveStatus !== 'Open' ? 'Đã kết thúc' : online ? 'Đang trực tuyến' : 'Chưa có nhân viên trực tuyến') : 'Trả lời tự động';
    input.placeholder = lv ? 'Nhập tin nhắn…' : 'Nhập câu hỏi của bạn…';
    bar.hidden = !(lv || (live && liveStatus === 'Open'));
    if (lv) bar.innerHTML = liveStatus === 'Open' ? '<button type="button" data-b="back">← Trợ lý</button><button type="button" data-b="end">Kết thúc chat</button>' : '<button type="button" data-b="back">← Trợ lý</button><button type="button" data-b="new">Chat mới</button>';
    else if (live && liveStatus === 'Open') bar.innerHTML = '<button type="button" data-b="resume">Tiếp tục chat với nhân viên</button>';
  }
  bar.onclick = e => {
    const b = e.target.closest('[data-b]'); if (!b) return;
    if (b.dataset.b === 'back') { mode = 'bot'; head(); render(); }
    else if (b.dataset.b === 'resume') { mode = 'live'; head(); renderLive(); schedule(); poll(); }
    else if (b.dataset.b === 'end') endLive();
    else if (b.dataset.b === 'new') { clearLive(); startLive(); }
  };

  function renderLive() {
    body.innerHTML = '';
    const note = document.createElement('div'); note.className = 'cb-note';
    note.textContent = liveStatus !== 'Open' ? 'Cuộc chat đã kết thúc.' : online ? 'Nhân viên đang trực tuyến.' : 'Chưa có nhân viên trực tuyến. Bạn cứ nhắn, nhân viên sẽ trả lời sớm nhất có thể.';
    body.appendChild(note);
    liveMsgs.forEach(m => {
      const d = document.createElement('div'); d.className = 'cb-msg ' + (m.fromStaff ? 'bot staff' : 'me');
      d.innerHTML = fmt(m.text) + `<small class="cb-time">${m.fromStaff ? esc(m.sender || 'Nhân viên') + ' · ' : ''}${clock(m.createdAt)}</small>`; body.appendChild(d);
    });
    scroll();
  }

  function apply(st) {
    online = !!st.online; liveStatus = st.status || 'Open';
    const known = new Set(liveMsgs.map(m => m.id));
    liveMsgs = liveMsgs.filter(m => !String(m.id).startsWith('tmp'));   // tin tạm đã có bản thật trên server
    let newStaff = false;
    (st.messages || []).forEach(m => { if (!liveMsgs.some(x => x.id === m.id)) { liveMsgs.push(m); if (m.fromStaff && !known.has(m.id)) newStaff = true; } });
    liveMsgs.sort((a, b) => utc(a.createdAt) - utc(b.createdAt));
    head();
    if (mode === 'live' && isOpen()) renderLive();
    if (newStaff && !(mode === 'live' && isOpen())) dot(true);
  }

  async function poll() {
    if (!live) return;
    const since = liveMsgs.filter(m => !String(m.id).startsWith('tmp')).slice(-1)[0]?.createdAt;
    try { apply(await API.call(`/api/livechat/${live.id}?key=${encodeURIComponent(live.key || '')}${since ? '&since=' + encodeURIComponent(since) : ''}`)); }
    catch (e) { if (/không tìm thấy/i.test(e.message || '')) clearLive(); }
  }
  function schedule() {
    clearTimeout(pollT);
    if (!live || liveStatus !== 'Open') return;
    pollT = setTimeout(async () => { await poll(); schedule(); }, isOpen() && mode === 'live' ? 3000 : 12000);
  }
  function clearLive() { clearTimeout(pollT); live = null; liveMsgs = []; liveStatus = 'Open'; ls.del(LKEY); mode = 'bot'; head(); if (isOpen()) render(); }

  async function endLive() {
    const l = live; clearLive();
    try { await API.call(`/api/livechat/${l.id}/close`, { method: 'POST', body: { key: l.key || '' } }); } catch { }
    add('bot', 'Đã kết thúc chat với nhân viên. Cần gì thêm cứ hỏi mình nhé.', { suggestions: START });
  }

  function nameForm() {
    body.innerHTML = '';
    const f = document.createElement('form'); f.className = 'cb-fb';
    f.innerHTML = `<b>Chat với nhân viên</b><input name="name" maxlength="100" placeholder="Tên của bạn" aria-label="Tên của bạn" required value="${esc(localStorage.getItem('name') || '')}" />
      <textarea name="text" rows="3" maxlength="1000" placeholder="Bạn cần hỗ trợ gì? (không bắt buộc)" aria-label="Nội dung"></textarea>
      <div class="cb-fb-b"><button type="button" class="cb-fb-x">Huỷ</button><button type="submit" class="cb-fb-ok">Bắt đầu chat</button></div>`;
    body.appendChild(f); f.querySelector('input').focus();
    f.querySelector('.cb-fb-x').onclick = () => { render(); };
    f.onsubmit = e => { e.preventDefault(); const v = Object.fromEntries(new FormData(f)); if ((v.name || '').trim().length < 2) return toast('Vui lòng nhập tên của bạn', 'error'); begin(v.name.trim(), (v.text || '').trim(), f.querySelector('.cb-fb-ok')); };
  }
  async function begin(name, text, btn) {
    if (btn) { btn.disabled = true; btn.textContent = 'Đang kết nối…'; }
    try {
      const r = await API.call('/api/livechat/start', { method: 'POST', body: { name, text, key: live?.key || ls.get('cb_guest') || '' } });
      if (r.key) ls.set('cb_guest', r.key);
      live = { id: r.chat.id, key: r.key || '' }; ls.set(LKEY, JSON.stringify(live));
      liveMsgs = []; mode = 'live'; apply(r.chat); head(); renderLive(); schedule(); input.focus();
    } catch (e) { if (btn) { btn.disabled = false; btn.textContent = 'Bắt đầu chat'; } toast(e.message, 'error'); }
  }
  function startLive() {
    if (live) { mode = 'live'; head(); renderLive(); schedule(); poll(); return; }
    if (API.token()) begin('', '', null); else nameForm();
  }

  async function sendLive(text) {
    text = (text || '').trim(); if (!text || sending || !live) return;
    sending = true; input.value = '';
    liveMsgs.push({ id: 'tmp' + Date.now(), fromStaff: false, text, createdAt: new Date().toISOString() }); renderLive();
    try { await API.call(`/api/livechat/${live.id}/send`, { method: 'POST', body: { text, key: live.key || '' } }); liveStatus = 'Open'; head(); await poll(); schedule(); }
    catch (e) { liveMsgs = liveMsgs.filter(m => !String(m.id).startsWith('tmp')); renderLive(); toast(e.message, 'error'); input.value = text; }
    finally { sending = false; input.focus(); }
  }

  // Nối lại cuộc chat đang mở sau khi tải lại trang (khách có tài khoản: hỏi server; khách vãng lai: dùng khoá đã lưu)
  (async () => {
    try {
      if (!live && API.token()) { const c = await API.call('/api/livechat/current'); if (c.id) { live = { id: c.id, key: '' }; ls.set(LKEY, JSON.stringify(live)); apply(c); } }
      else if (live) await poll();
      if (live && liveStatus === 'Open') { head(); schedule(); } else if (live) clearLive();
    } catch { /* không có cuộc chat để nối lại */ }
  })();

  async function send(text) {
    text = (text || '').trim(); if (!text || busyNow) return;
    busyNow = true; sendBtn.disabled = true; input.value = '';
    add('me', text);
    const prev = hist.slice(-8).map(h => ({ role: h.role, text: h.text }));
    hist.push({ role: 'user', text }); save();
    clearExtras();
    const t = document.createElement('div'); t.className = 'cb-msg bot cb-typing'; t.innerHTML = '<i></i><i></i><i></i>'; body.appendChild(t); scroll();
    try {
      const r = await API.call('/api/chatbot', { method: 'POST', body: { message: text, history: prev } });
      t.remove(); add('bot', r.reply, { links: r.links, suggestions: r.suggestions });
      if (r.action === 'feedback') feedbackForm();
      if (r.action === 'live') startLive();
      hist.push({ role: 'bot', text: r.reply }); save();
    } catch (e) {
      t.remove(); add('bot', e.message || 'Không gửi được câu hỏi. Vui lòng thử lại.', { err: true });
    } finally { busyNow = false; sendBtn.disabled = false; input.focus(); }
  }

  function render() {
    if (mode === 'live') { renderLive(); return; }
    body.innerHTML = '';
    if (!hist.length) { add('bot', GREET, { suggestions: START }); return; }
    hist.forEach(h => add(h.role === 'user' ? 'me' : 'bot', h.text));
    clearExtras(); scroll();
  }

  function setOpen(o) {
    panel.classList.toggle('open', o); fab.setAttribute('aria-expanded', o);
    ss.set(OPEN, o ? '1' : '0');
    if (o) { dot(false); if (!body.childElementCount) render(); else if (mode === 'live') renderLive(); scroll(); setTimeout(() => input.focus(), 50); if (live) { poll(); schedule(); } }
  }

  fab.onclick = () => setOpen(!panel.classList.contains('open'));
  root.querySelector('.cb-x').onclick = () => { setOpen(false); fab.focus(); };
  panel.addEventListener('keydown', e => { if (e.key === 'Escape') { setOpen(false); fab.focus(); } });
  form.onsubmit = e => { e.preventDefault(); if (mode === 'live') { if (liveStatus !== 'Open') return toast('Cuộc chat đã kết thúc, bấm “Chat mới” để chat lại.', 'error'); sendLive(input.value); } else send(input.value); };
  head();
  if (ss.get(OPEN) === '1') setOpen(true);
})();
