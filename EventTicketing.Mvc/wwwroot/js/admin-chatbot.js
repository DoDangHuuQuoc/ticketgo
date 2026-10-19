(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$;
  let faqs = [], stats = null;
  const INTENT = { faq: 'Từ FAQ của bạn', rule: 'Bot tự trả lời', ai: 'Do AI trả lời', unknown: 'Chưa trả lời được' };
  const short = (t, n = 110) => t.length > n ? t.slice(0, n) + '…' : t;

  function drawStats() {
    const d = stats, n = k => (d.byIntent.find(x => x.intent === k) || {}).n || 0;
    const unk = n('unknown'), ok = d.total - unk;
    $('logInfo').textContent = `Đang lưu ${d.totalAll} câu hỏi`;
    $('kpi').innerHTML = [
      ['Lượt hỏi', String(d.total), `${d.days} ngày gần nhất`],
      ['Bot trả lời được', d.total ? Math.round(100 * ok / d.total) + '%' : '—', `${ok} câu`],
      ['Chưa trả lời được', String(unk), unk ? 'Nên soạn FAQ bổ sung' : 'Rất tốt'],
      ['Từ FAQ của bạn', String(n('faq')), `${n('rule')} câu bot tự trả lời · ${n('ai')} do AI`]
    ].map(([l, v, s]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${esc(v)}</div><div class="sub">${esc(s)}</div></div>`).join('');
    $('unRows').innerHTML = d.unanswered.map((u, i) => `<tr><td style="white-space:pre-wrap">${esc(short(u.message, 200))}</td><td class="r num">${u.n}</td>
      <td><div class="acts"><button class="btn btn-ghost btn-sm" data-a="mk" data-i="${i}">Tạo FAQ</button></div></td></tr>`).join('')
      || '<tr><td colspan="3" class="empty">Chưa có câu nào bot không trả lời được.</td></tr>';
    const max = Math.max(1, ...d.daily.map(x => x.n));
    $('chart').innerHTML = d.daily.map(x => `<div class="c" title="${x.date}: ${x.n} lượt hỏi"><i style="height:${Math.max(3, Math.round(100 * x.n / max))}%"></i></div>`).join('') || '<p class="muted">Chưa có dữ liệu.</p>';
    const f = d.daily[0], l = d.daily[d.daily.length - 1];
    $('axis').innerHTML = f ? `<span>${f.date.split('-').reverse().join('/')}</span><span>Cao nhất ${max} lượt/ngày</span><span>${l.date.split('-').reverse().join('/')}</span>` : '';
  }

  function drawFaqs() {
    $('faqRows').innerHTML = faqs.map(f => `<tr class="${f.isActive ? '' : 'dim'}">
      <td><b>${esc(f.question)}</b></td><td><small>${esc(f.keywords)}</small></td><td style="white-space:pre-wrap;min-width:220px">${esc(short(f.answer, 140))}</td>
      <td class="r num">${f.hits}</td><td><span class="badge ${f.isActive ? 'b-go' : 'b-off'}">${f.isActive ? 'Đang bật' : 'Đã tắt'}</span></td>
      <td><div class="acts"><button class="btn btn-ghost btn-sm" data-a="edit" data-id="${f.id}">Sửa</button>
        <button class="btn btn-ghost btn-sm" data-a="tog" data-id="${f.id}">${f.isActive ? 'Tắt' : 'Bật'}</button>
        <button class="btn btn-ghost btn-sm danger" data-a="del" data-id="${f.id}">Xoá</button></div></td></tr>`).join('')
      || '<tr><td colspan="6" class="empty">Chưa có câu hỏi–đáp nào. Bấm “+ Thêm câu hỏi–đáp” để bắt đầu.</td></tr>';
  }

  async function load() {
    try {
      [faqs, stats] = await Promise.all([API.call('/api/admin/chatbot/faqs'), API.call('/api/admin/chatbot/stats?days=' + $('days').value)]);
      drawFaqs(); drawStats();
    } catch (err) { toast(err.message, 'error'); }
  }

  function form(f, preset) {
    const isNew = !f, v = f || { question: preset || '', keywords: '', answer: '', isActive: true };
    const m = ADM.modal({
      title: isNew ? 'Thêm câu hỏi–đáp' : 'Sửa câu hỏi–đáp', wide: true,
      body: `<form class="dlg-form" novalidate>
        <div class="field"><label for="fQ">Câu hỏi (để bạn dễ quản lý)</label><input id="fQ" maxlength="200" placeholder="VD: Trẻ em có cần mua vé không?" /></div>
        <div class="field"><label for="fK">Từ khoá (cách nhau bằng dấu phẩy)</label><input id="fK" maxlength="300" placeholder="VD: tre em, be, tre con, tre nho" /></div>
        <div class="field"><label for="fA">Câu trả lời bot sẽ gửi</label><textarea id="fA" rows="5" maxlength="1500" placeholder="Nội dung trả lời…"></textarea></div>
        <div class="field"><label class="check"><input type="checkbox" id="fOn" /> Đang bật</label></div>
        <p class="hint">Bot trả lời khi câu khách có chứa <b>nguyên cụm</b> một trong các từ khoá. Từ khoá dài/cụ thể được ưu tiên. Tránh từ quá chung như “vé” để khỏi trả lời nhầm.</p>
        <div class="dlg-f"><button type="button" class="btn btn-ghost" data-x>Huỷ</button><button type="submit" class="btn btn-primary">${isNew ? 'Thêm' : 'Lưu'}</button></div></form>`
    });
    $('fQ').value = v.question; $('fK').value = v.keywords; $('fA').value = v.answer; $('fOn').checked = v.isActive;
    m.body.querySelector('[data-x]').addEventListener('click', () => m.close());
    m.body.querySelector('form').addEventListener('submit', async e => {
      e.preventDefault();
      const body = { question: $('fQ').value, keywords: $('fK').value, answer: $('fA').value, isActive: $('fOn').checked };
      const ok = await ADM.act(e.currentTarget.querySelector('[type=submit]'), () => API.call(isNew ? '/api/admin/chatbot/faqs' : '/api/admin/chatbot/faqs/' + f.id, { method: isNew ? 'POST' : 'PUT', body }));
      if (ok) { m.close(); load(); }
    });
    setTimeout(() => $(preset ? 'fK' : 'fQ').focus(), 50);
  }

  $('faqRows').addEventListener('click', async e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const f = faqs.find(x => x.id === b.dataset.id); if (!f) return;
    if (b.dataset.a === 'edit') return form(f);
    if (b.dataset.a === 'tog') { if (await ADM.act(b, () => API.call(`/api/admin/chatbot/faqs/${f.id}/active`, { method: 'PUT', body: { isActive: !f.isActive } }))) load(); }
    else if (b.dataset.a === 'del') {
      if (await ADM.ask({ title: 'Xoá câu hỏi–đáp', message: 'Xoá hẳn câu hỏi này?', confirmText: 'Xoá', danger: true }) === null) return;
      if (await ADM.act(b, () => API.call('/api/admin/chatbot/faqs/' + f.id, { method: 'DELETE' }))) load();
    }
  });
  $('unRows').addEventListener('click', e => {
    const b = e.target.closest('[data-a="mk"]'); if (!b) return;
    form(null, stats.unanswered[+b.dataset.i].message);
  });
  $('newFaq').addEventListener('click', () => form(null));
  $('days').addEventListener('change', load);
  $('purge').addEventListener('click', async () => {
    const r = await ADM.ask({ title: 'Xoá nhật ký cũ', message: 'Xoá các câu hỏi chatbot cũ hơn số ngày dưới đây?', label: 'Số ngày (mặc định 30)', placeholder: '30', confirmText: 'Xoá', danger: true });
    if (r === null) return;
    const n = parseInt(r || '30', 10);
    if (!(n >= 1)) return toast('Số ngày không hợp lệ', 'error');
    if (await ADM.act($('purge'), () => API.call('/api/admin/chatbot/logs?olderThanDays=' + n, { method: 'DELETE' }))) load();
  });
  load();
})();
