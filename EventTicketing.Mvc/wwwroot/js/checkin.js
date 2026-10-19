(() => {
  'use strict';
  const seatWord = s => String(s).startsWith('#') ? 'Vé' : 'Ghế';   // ghế thật (A5) thì nhân viên đọc để chỉ chỗ ngồi
  // Camera chỉ chạy trên HTTPS. KHÔNG tự chuyển trang sang https:// nữa: nếu máy chủ không phục vụ https thì khách bị ERR_CONNECTION_RESET và không vào được trang soát vé.
  // Thay vào đó chỉ hiện gợi ý ở dưới nút Bật camera; vẫn dùng được "Chụp / chọn ảnh mã QR" và ô nhập mã.
  const insecure = location.protocol === 'http:' && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1';
  if (!requireLogin()) return;
  const $ = id => document.getElementById(id);
  const role = localStorage.getItem('role');
  if (role !== 'Staff') {
    $('content').hidden = true; $('guard').hidden = false;
    $('guard').innerHTML = '<h2>Cần quyền soát vé</h2><p class="muted">Đăng nhập bằng tài khoản nhân viên để dùng trang này.</p>';
    return;
  }
  const sel = $('ev'), res = $('result'), logEl = $('log');
  if (insecure) {
    const tip = document.createElement('p'); tip.className = 'muted'; tip.style.cssText = 'margin:8px 0 0;font-size:.85rem';
    tip.innerHTML = 'Trang đang mở bằng http nên trình duyệt chặn camera. Dùng nút “Chụp / chọn ảnh mã QR” hoặc nhập mã vé. Nếu máy chủ có https, mở <a href="https://' + location.host + '/Staff/CheckIn">bản https</a> để bật camera trực tiếp.';
    $('start').after(tip);
  }
  /* Dòng chi tiết kỹ thuật dưới nút Bật camera (toast biến mất sau vài giây nên trước đây không kịp đọc lý do) */
  const camErr = document.createElement('p'); camErr.className = 'muted'; camErr.style.cssText = 'margin:8px 0 0;font-size:.82rem;word-break:break-word'; camErr.hidden = true;
  $('start').after(camErr);
  function showCamErr(err) {
    const names = err ? (err.all && err.all.length ? err.all.join(' → ') : (err.name || String(err.message || err))) : 'trình duyệt không có getUserMedia';
    camErr.textContent = 'Chi tiết kỹ thuật: ' + names + ' · ' + location.protocol + '//' + location.host + ' · ' + (window.isSecureContext ? 'kết nối an toàn' : 'KHÔNG an toàn (cần HTTPS hoặc localhost)');
    camErr.hidden = false;
  }
  let scanner = null, running = false, last = '', lastAt = 0, n = 0, okN = 0, noN = 0, ctx = null;

  function beep(ok) {
    try {
      ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.type = ok ? 'sine' : 'square'; o.frequency.value = ok ? 880 : 220; g.gain.value = 0.08;
      o.connect(g); g.connect(ctx.destination); o.start(); o.stop(ctx.currentTime + (ok ? 0.12 : 0.35));
    } catch { /* trình duyệt chặn âm thanh */ }
    navigator.vibrate?.(ok ? 60 : [120, 60, 120]);
  }

  function verdict(ok, title, sub) {
    res.className = 'verdict ' + (ok ? 'ok' : 'bad');
    res.innerHTML = `<div><div class="mark">${icon(ok ? 'check' : 'x', 40)}</div><div class="big">${esc(title)}</div><p>${esc(sub || '')}</p></div>`;
    beep(ok);
  }
  function addLog(ok, text) {
    n++; if (n === 1) logEl.innerHTML = '';
    const li = document.createElement('li'), t = new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    li.innerHTML = `<span>${esc(text)}</span><span class="badge ${ok ? 'b-go' : 'b-bad'}">${ok ? 'Hợp lệ' : 'Từ chối'} ${t}</span>`;
    logEl.prepend(li); while (logEl.children.length > 8) logEl.lastChild.remove();
    $('count').textContent = n + ' lượt';
    ok ? okN++ : noN++;
    $('stAll').textContent = n; $('stOk').textContent = okN; $('stNo').textContent = noN;
  }

  async function checkIn(code) {
    const now = Date.now();
    if (!code || (code === last && now - lastAt < 3000)) return; // chống quét lặp liên tục
    last = code; lastAt = now;
    try {
      const r = await API.call('/api/admin/tickets/check-in', { method: 'POST', body: { eventId: sel.value, qrCode: code } });
      const detail = [r.typeName, r.zone, r.customer].filter(Boolean).join(' · ');
      verdict(true, 'Hợp lệ, mời vào', `Mã vé ${r.code} · ${seatWord(r.seatInfo)} ${r.seatInfo}${detail ? ' · ' + detail : ''}`); addLog(true, `${r.code} · ${seatWord(r.seatInfo)} ${r.seatInfo}`);
    } catch (err) {
      const msg = err.message;   // server trả lý do cụ thể: sai sự kiện / đã check-in lúc nào / vé bị huỷ / không tìm thấy
      verdict(false, msg, 'Không cho vào'); addLog(false, (code.includes('.') ? code.split('.')[0] : code.slice(0, 8)) + '…');
    }
  }

  /* Lý do camera không mở được -> câu hướng dẫn cụ thể (trước đây mọi lỗi đều báo chung một câu) */
  function camMsg(err) {
    const n = err?.name || '', m = String(err?.message || err || '');
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia)
      return 'Camera chỉ mở được trên HTTPS. Hãy mở trang bằng địa chỉ https://' + location.host + '/Staff/CheckIn (không dùng http:// hoặc địa chỉ IP). Trong lúc đó bạn vẫn dùng được nút "Chụp / chọn ảnh mã QR".';
    if (n === 'NotAllowedError' || /permission|denied/i.test(m)) return 'Bạn đã chặn quyền camera. Bấm biểu tượng ổ khoá cạnh thanh địa chỉ, cho phép Camera rồi bấm Bật camera lại.';
    if (n === 'NotFoundError' || /no camera|not found|requested device/i.test(m)) return 'Không tìm thấy camera trên thiết bị này.';
    if (n === 'NotReadableError' || /in use|could not start|not readable/i.test(m)) return 'Camera đang được ứng dụng hoặc tab khác sử dụng. Hãy đóng chúng rồi thử lại.';
    return 'Không mở được camera: ' + (m || n || 'lỗi không xác định') + '. Bạn có thể dùng ô nhập mã bên dưới.';
  }

  /* Thử lần lượt: camera sau -> camera bất kỳ trong danh sách -> camera trước (laptop thường chỉ có camera trước) */
  async function startScanner() {
    /* Quét TOÀN KHUNG HÌNH (không đặt qrbox): trước đây qrbox 80% bị lệch so với video đã cắt object-fit:cover nên mã nằm trong khung vẫn không đọc được.
       fps 10 + khung hình vuông (aspectRatio 1) khớp với ô xem; thiết bị không hỗ trợ aspectRatio thì tự dùng tỉ lệ gốc. */
    const mkCfg = square => ({ fps: 10, disableFlip: false, ...(square ? { aspectRatio: 1 } : {}) }), onScan = txt => checkIn(txt.trim());
    let lastErr = null; const errs = [];
    const fail = err => { try { err.all = errs.map(x => x?.name || String(x?.message || x).slice(0, 60)); } catch { /* bỏ qua */ } return err; };
    const attempts = [{ facingMode: 'environment' }];
    try {
      const cams = await Html5Qrcode.getCameras();
      const back = cams.find(c => /back|rear|environment|sau/i.test(c.label)) || cams[cams.length - 1];
      if (back) attempts.push(back.id);
    } catch (err) { lastErr = err; errs.push(err); }
    attempts.push({ facingMode: 'user' });
    for (const a of attempts) {
      for (const square of [true, false]) {   // một số máy báo lỗi khi ép tỉ lệ 1:1 -> thử lại không ép
        try {
          scanner = new Html5Qrcode('reader', { formatsToSupport: [Html5QrcodeSupportedFormats.QR_CODE], experimentalFeatures: { useBarCodeDetectorIfSupported: true }, verbose: false });
          await scanner.start(a, mkCfg(square), onScan, () => { /* khung hình không có mã: bỏ qua */ });
          return;
        } catch (err) {
          lastErr = err; errs.push(err);
          try { await scanner.stop(); } catch { /* chưa chạy */ }
          try { scanner.clear(); } catch { /* bỏ qua */ }
          if (err?.name === 'NotAllowedError' || /permission|denied/i.test(String(err?.message || err))) throw fail(err);   // bị chặn quyền thì thử tiếp cũng vô ích
        }
      }
    }
    throw fail(lastErr || new Error('Không mở được camera'));
  }

  /* Ảnh chụp từ điện thoại rất lớn (12MP+) nên thư viện hay không đọc được mã: thu nhỏ về tối đa 1400px rồi giải mã (ưu tiên bộ đọc có sẵn của trình duyệt) */
  async function decodePhoto(file) {
    let canvas = null;
    try {
      const bmp = await createImageBitmap(file);
      const k = Math.min(1, 1400 / Math.max(bmp.width, bmp.height));
      canvas = document.createElement('canvas'); canvas.width = Math.round(bmp.width * k); canvas.height = Math.round(bmp.height * k);
      canvas.getContext('2d').drawImage(bmp, 0, 0, canvas.width, canvas.height); bmp.close?.();
    } catch { /* trình duyệt cũ: dùng file gốc */ }
    if (canvas && 'BarcodeDetector' in window) {
      try { const r = await new BarcodeDetector({ formats: ['qr_code'] }).detect(canvas); if (r[0]?.rawValue) return r[0].rawValue; } catch { /* thử thư viện */ }
    }
    const src = canvas ? new File([await new Promise(ok => canvas.toBlob(ok, 'image/png'))], 'qr.png', { type: 'image/png' }) : file;
    const r = new Html5Qrcode('fileReader');
    try { return await r.scanFile(src, false); } finally { try { r.clear(); } catch { /* bỏ qua */ } }
  }

  $('start').addEventListener('click', async e => {
    const b = e.currentTarget;
    if (running) { await scanner.stop().catch(() => { }); try { scanner.clear(); } catch { /* bỏ qua */ } running = false; $('vfIdle').hidden = false; b.textContent = 'Bật camera'; return; }
    if (typeof Html5Qrcode === 'undefined') return toast('Không tải được thư viện quét mã. Hãy tải lại trang (Ctrl+F5) hoặc dùng ô nhập mã bên dưới.', 'error');
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) { toast(camMsg(null), 'error'); showCamErr(null); $('shot').click(); return; }   // http: trình duyệt cấm camera trực tiếp -> mở luôn app chụp ảnh để vẫn quét được
    b.disabled = true; b.textContent = 'Đang mở camera…';
    try {
      await startScanner();
      running = true; camErr.hidden = true; $('vfIdle').hidden = true; b.textContent = 'Tắt camera';
    } catch (err) { b.textContent = 'Bật camera'; toast(camMsg(err), 'error'); showCamErr(err); }
    finally { b.disabled = false; }
  });

  /* Không cần camera trực tiếp (không đòi HTTPS): trên điện thoại nút này mở app chụp ảnh, trên máy tính cho chọn file ảnh */
  $('shot').addEventListener('change', async e => {
    const f = e.target.files?.[0]; e.target.value = '';
    if (!f) return;
    if (typeof Html5Qrcode === 'undefined') return toast('Không tải được thư viện quét mã. Hãy tải lại trang (Ctrl+F5).', 'error');
    try {
      const txt = await decodePhoto(f);
      last = ''; checkIn(txt.trim());
    } catch { toast('Không đọc được mã QR trong ảnh. Hãy chụp gần hơn, đủ sáng và thấy rõ cả mã.', 'error'); }
  });

  $('manualForm').addEventListener('submit', e => { e.preventDefault(); const v = $('manual').value.trim(); if (v) { last = ''; checkIn(v); $('manual').value = ''; } });

  (async () => {
    try {
      const events = await API.call('/api/admin/checkin/events');
      sel.innerHTML = events.map(e => `<option value="${e.id}">${esc(e.name)}</option>`).join('') || '<option value="">Chưa có sự kiện</option>';
    } catch (err) { toast(err.message, 'error'); }
  })();
})();
