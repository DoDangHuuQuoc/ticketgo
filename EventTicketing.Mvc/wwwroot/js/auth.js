(() => {
  'use strict';
  const f = document.getElementById('f'), mode = f.dataset.mode;
  const val = n => f.elements[n]?.value.trim();
  const safeBack = () => { const b = new URLSearchParams(location.search).get('returnUrl'); return b && b.startsWith('/') && !b.startsWith('//') ? b : '/'; };

  /* ---------- Hiện / ẩn mật khẩu ---------- */
  const EYE = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg>';
  const EYE_OFF = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.4 0 10 7 10 7a17 17 0 0 1-3.2 4M6.5 6.5C3.8 8.3 2 12 2 12s3.6 7 10 7c1.6 0 3-.4 4.3-1M9.9 9.9a3 3 0 0 0 4.2 4.2"/></svg>';
  f.querySelectorAll('.pw-toggle').forEach(b => {
    b.innerHTML = EYE;
    b.addEventListener('click', () => {
      const i = b.previousElementSibling, show = i.type === 'password';
      i.type = show ? 'text' : 'password'; b.innerHTML = show ? EYE_OFF : EYE; b.setAttribute('aria-label', show ? 'Ẩn mật khẩu' : 'Hiện mật khẩu');
    });
  });

  /* ---------- Kiểm tra từng ô (báo lỗi ngay dưới ô) ---------- */
  const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;
  const rules = {
    fullName: () => { const v = val('fullName'); return !v ? 'Nhập họ và tên của bạn.' : v.length < 2 ? 'Họ và tên cần ít nhất 2 ký tự.' : ''; },
    email: () => { const v = val('email'); return !v ? 'Nhập email của bạn.' : !EMAIL.test(v) ? 'Email chưa đúng dạng, ví dụ ten@email.com.' : ''; },
    password: () => {
      const v = f.elements['password'].value;
      if (!v) return 'Nhập mật khẩu.';
      return (mode === 'register' || mode === 'forgot') && v.length < 8 ? 'Mật khẩu cần ít nhất 8 ký tự.' : '';
    },
    confirm: () => { const v = f.elements['confirm'].value; return !v ? 'Nhập lại mật khẩu.' : v !== f.elements['password'].value ? 'Hai mật khẩu chưa giống nhau.' : ''; },
    agree: () => f.elements['agree'].checked ? '' : 'Bạn cần đồng ý với điều khoản để tiếp tục.'
  };
  const wrapOf = n => f.querySelector(`[data-field="${n}"]`);
  function check(n, show = true) {
    if (!rules[n] || !f.elements[n]) return '';
    const msg = rules[n](), w = wrapOf(n); if (!w) return msg;
    if (show) {
      w.classList.toggle('invalid', !!msg);
      w.classList.toggle('valid', !msg && n !== 'agree' && (f.elements[n].value || '').length > 0 && mode === 'register');
      const e = w.querySelector('.err'); if (e) e.textContent = msg;
      f.elements[n].setAttribute('aria-invalid', !!msg);
    }
    return msg;
  }
  const names = [...f.querySelectorAll('[data-field]')].map(w => w.dataset.field).filter(n => rules[n] && f.elements[n]);
  names.forEach(n => {
    const el = f.elements[n];
    el.addEventListener('blur', () => { if (el.type === 'checkbox' || el.value) check(n); });
    el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', () => { if (wrapOf(n).classList.contains('invalid') || n === 'confirm' && f.elements['confirm'].value) check(n); });
  });

  /* ---------- Độ mạnh mật khẩu ---------- */
  if (mode === 'register') {
    const pw = f.elements['password'], meter = document.getElementById('pwMeter'), label = document.getElementById('pwLabel'), list = document.getElementById('pwRules');
    const LV = ['Nhập mật khẩu để kiểm tra độ mạnh', 'Yếu', 'Trung bình', 'Khá', 'Mạnh'];
    pw.addEventListener('input', () => {
      const v = pw.value, r = { len: v.length >= 8, case: /[a-z]/.test(v) && /[A-Z]/.test(v), num: /\d/.test(v), sym: /[^A-Za-z0-9]/.test(v) };
      let lv = Object.values(r).filter(Boolean).length; if (!r.len) lv = Math.min(lv, 1); if (!v) lv = 0;
      meter.dataset.lv = lv; label.innerHTML = lv ? `Độ mạnh: <b>${LV[lv]}</b>` : LV[0];
      list.querySelectorAll('li').forEach(li => li.classList.toggle('ok', !!r[li.dataset.rule]));
      if (f.elements['confirm'].value) check('confirm');
    });
  }

  /* ---------- Gửi form ---------- */
  const submit = (fn) => f.addEventListener('submit', async e => {
    e.preventDefault();
    const bad = names.filter(n => check(n));
    if (bad.length) { f.elements[bad[0]].focus(); return; }
    const btn = f.querySelector('button[type=submit]'); busy(btn, true);
    const fe = document.getElementById('formErr'); if (fe) { fe.textContent = ''; fe.classList.remove('on'); }
    try { await fn(); }
    catch (err) {
      if (fe) { fe.textContent = err.message; fe.classList.add('on'); fe.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
      toast(err.message, 'error'); busy(btn, false);
    }
  });

  if (mode === 'login') {
    f.elements['email'].focus();
    submit(async () => {
      const r = await API.call('/api/auth/login', { method: 'POST', body: { email: val('email'), password: f.elements['password'].value } });
      localStorage.setItem('jwt', r.accessToken); localStorage.setItem('role', r.role); localStorage.setItem('name', r.fullName);
      if (!r.isVerified) { toast('Tài khoản chưa xác thực email. Hãy nhập mã OTP.', 'error'); setTimeout(() => location.href = '/Account/Verify?email=' + encodeURIComponent(val('email')), 900); return; }
      // Admin/Staff luôn vào thẳng khu quản trị (chỉ giữ returnUrl nếu nó cũng thuộc khu quản trị); khách thì về trang họ đang xem
      const back = safeBack(), staff = r.role === 'Admin' || r.role === 'Staff';
      location.href = !staff ? back : r.role === 'Staff' ? (back.toLowerCase().startsWith('/staff') ? back : '/Staff/CheckIn') : (back.toLowerCase().startsWith('/manage') ? back : '/Manage/Dashboard');
    });
  } else if (mode === 'register') {
    f.elements['fullName'].focus();
    submit(async () => {
      await API.call('/api/auth/register', { method: 'POST', body: { fullName: val('fullName'), email: val('email'), password: f.elements['password'].value } });
      location.href = '/Account/Verify?email=' + encodeURIComponent(val('email'));
    });
  } else if (mode === 'forgot') {
    const st2 = document.getElementById('stage2'), btn = document.getElementById('fgBtn'), rs = document.getElementById('resend'), fe = document.getElementById('formErr');
    const box = document.getElementById('otp');
    box.innerHTML = Array.from({ length: 6 }, (_, i) => `<input inputmode="numeric" maxlength="1" placeholder=" " autocomplete="${i ? 'off' : 'one-time-code'}" aria-label="Chữ số ${i + 1}">`).join('');
    const ins = [...box.children];
    ins.forEach((el, i) => {
      el.addEventListener('input', () => { el.value = el.value.replace(/\D/g, '').slice(-1); if (el.value && i < 5) ins[i + 1].focus(); });
      el.addEventListener('keydown', e => { if (e.key === 'Backspace' && !el.value && i > 0) ins[i - 1].focus(); });
      el.addEventListener('paste', e => {
        const t = (e.clipboardData.getData('text') || '').replace(/\D/g, '').slice(0, 6); if (!t) return;
        e.preventDefault(); [...t].forEach((c, k) => ins[k].value = c); ins[Math.min(t.length, 5)].focus();
      });
    });
    let stage = 1, cd = 0, timer;
    const showErr = m => { fe.textContent = m; fe.classList.toggle('on', !!m); };
    const cool = () => {
      cd = 45; rs.disabled = true; rs.textContent = `Gửi lại mã (${cd}s)`; clearInterval(timer);
      timer = setInterval(() => { cd--; if (cd <= 0) { clearInterval(timer); rs.disabled = false; rs.textContent = 'Gửi lại mã'; } else rs.textContent = `Gửi lại mã (${cd}s)`; }, 1000);
    };
    const goStage2 = () => {
      stage = 2; st2.hidden = false; rs.hidden = false; btn.textContent = 'Đặt lại mật khẩu'; f.elements['email'].readOnly = true;
      document.getElementById('st1').classList.add('done'); document.getElementById('st1').removeAttribute('aria-current'); document.getElementById('st2').setAttribute('aria-current', 'step');
      document.getElementById('fgTitle').textContent = 'Nhập mã & mật khẩu mới';
      document.getElementById('fgSub').textContent = 'Nhập mã OTP 6 số trong email, rồi chọn mật khẩu mới cho tài khoản.';
      cool(); ins[0].focus();
    };
    const sendCode = async () => { await API.call('/api/auth/forgot-password', { method: 'POST', body: { email: val('email') } }); };
    const em = new URLSearchParams(location.search).get('email'); if (em) f.elements['email'].value = em;
    f.elements['email'].focus();

    rs.addEventListener('click', async () => {
      showErr(''); rs.disabled = true;
      try { await sendCode(); toast('Đã gửi lại mã OTP', 'ok'); cool(); }
      catch (err) { rs.disabled = false; showErr(err.message); toast(err.message, 'error'); }
    });

    f.addEventListener('submit', async e => {
      e.preventDefault(); showErr('');
      const need = stage === 1 ? ['email'] : ['password', 'confirm'];
      const bad = need.filter(n => check(n));
      if (bad.length) { f.elements[bad[0]].focus(); return; }
      busy(btn, true);
      let sent = false;
      try {
        if (stage === 1) { await sendCode(); sent = true; }
        else {
          const otp = ins.map(i => i.value).join('');
          if (otp.length < 6) throw new Error('Nhập đủ 6 chữ số của mã OTP');
          await API.call('/api/auth/reset-password', { method: 'POST', body: { email: val('email'), otp, newPassword: f.elements['password'].value } });
          toast('Đặt lại mật khẩu thành công. Hãy đăng nhập.', 'ok');
          setTimeout(() => location.href = '/Account/Login', 1000); return;   // giữ nút khoá cho tới khi chuyển trang
        }
      } catch (err) { showErr(err.message); toast(err.message, 'error'); }
      busy(btn, false);
      if (sent) { toast('Đã gửi mã OTP tới email của bạn', 'ok'); goStage2(); }
    });
  } else if (mode === 'verify') {
    const box = document.getElementById('otp');
    box.innerHTML = Array.from({ length: 6 }, (_, i) => `<input inputmode="numeric" maxlength="1" placeholder=" " autocomplete="${i ? 'off' : 'one-time-code'}" aria-label="Chữ số ${i + 1}">`).join('');
    const ins = [...box.children];
    ins.forEach((el, i) => {
      el.addEventListener('input', () => { el.value = el.value.replace(/\D/g, '').slice(-1); if (el.value && i < 5) ins[i + 1].focus(); });
      el.addEventListener('keydown', e => { if (e.key === 'Backspace' && !el.value && i > 0) ins[i - 1].focus(); });
      el.addEventListener('paste', e => {
        const t = (e.clipboardData.getData('text') || '').replace(/\D/g, '').slice(0, 6); if (!t) return;
        e.preventDefault(); [...t].forEach((c, k) => ins[k].value = c); ins[Math.min(t.length, 5)].focus();
      });
    });
    const em = new URLSearchParams(location.search).get('email') || '';
    f.elements['email'].value = em; (em ? ins[0] : f.elements['email']).focus();

    const rs = document.getElementById('resend'); let cd = 0, timer;
    const cool = () => {
      cd = 45; rs.disabled = true; rs.textContent = `Gửi lại mã (${cd}s)`; clearInterval(timer);
      timer = setInterval(() => { cd--; if (cd <= 0) { clearInterval(timer); rs.disabled = false; rs.textContent = 'Gửi lại mã'; } else rs.textContent = `Gửi lại mã (${cd}s)`; }, 1000);
    };
    if (em) cool();
    rs.addEventListener('click', async () => {
      if (check('email')) return f.elements['email'].focus();
      try { await API.call('/api/auth/resend-otp', { method: 'POST', body: val('email') }); toast('Đã gửi lại mã OTP', 'ok'); cool(); }
      catch (err) { toast(err.message, 'error'); }
    });
    submit(async () => {
      const otp = ins.map(i => i.value).join('');
      if (otp.length < 6) throw new Error('Nhập đủ 6 chữ số của mã OTP');
      await API.call('/api/auth/verify-email', { method: 'POST', body: { email: val('email'), otp } });
      toast('Xác thực thành công. Hãy đăng nhập.', 'ok');
      setTimeout(() => location.href = '/Account/Login', 900);
    });
  }
})();
