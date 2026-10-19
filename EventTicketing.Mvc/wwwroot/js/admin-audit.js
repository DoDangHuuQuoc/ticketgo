(() => {
  'use strict';
  if (!ADM.guard(['Admin'])) return;
  const $ = ADM.$, S = { page: 1, size: 30 };
  const ACTION = {
    'event.create': 'Tạo sự kiện', 'event.update': 'Sửa sự kiện', 'event.publish': 'Công khai sự kiện', 'event.unpublish': 'Ẩn sự kiện',
    'event.duplicate': 'Nhân bản sự kiện', 'event.delete': 'Xoá sự kiện', 'queue.config': 'Cấu hình phòng chờ', 'seatmap.update': 'Lưu sơ đồ chỗ ngồi', 'seatmap.delete': 'Xoá sơ đồ chỗ ngồi',
    'ticketType.create': 'Thêm loại vé', 'ticketType.update': 'Sửa loại vé', 'ticketType.delete': 'Xoá loại vé',
    'booking.cancel': 'Huỷ đơn', 'booking.refund': 'Hoàn tiền', 'booking.resend': 'Gửi lại email vé',
    'ticket.checkin': 'Check-in thủ công', 'ticket.undo-checkin': 'Hoàn tác check-in', 'ticket.cancel': 'Thu hồi vé',
    'coupon.create': 'Tạo mã giảm giá', 'coupon.update': 'Sửa mã giảm giá', 'coupon.enable': 'Bật mã giảm giá', 'coupon.disable': 'Tắt mã giảm giá', 'coupon.delete': 'Xoá mã giảm giá',
    'site.maintenance.on': 'Bật chế độ bảo trì', 'site.maintenance.off': 'Tắt chế độ bảo trì',
    'user.create': 'Tạo tài khoản', 'user.role': 'Đổi vai trò', 'user.lock': 'Khoá tài khoản', 'user.unlock': 'Mở khoá tài khoản',
    'user.reset-password': 'Đặt lại mật khẩu', 'user.verify': 'Xác thực email thủ công',
    'booking.confirm-payment': 'Xác nhận đã nhận tiền', 'booking.invite': 'Phát vé mời', 'event.notify': 'Gửi thông báo cho khách',
    'chatbot.faq.create': 'Thêm FAQ chatbot', 'chatbot.faq.update': 'Sửa FAQ chatbot', 'chatbot.faq.toggle': 'Bật/tắt FAQ chatbot', 'chatbot.faq.delete': 'Xoá FAQ chatbot', 'chatbot.logs.purge': 'Xoá nhật ký chatbot',
    'site.settings': 'Lưu cài đặt chung', 'broadcast.send': 'Gửi email hàng loạt', 'export.insights': 'Xuất CSV phân tích',
    'feedback.reply': 'Trả lời phản hồi', 'feedback.status': 'Đổi trạng thái phản hồi', 'feedback.delete': 'Xoá phản hồi',
    'export.bookings': 'Xuất CSV đơn hàng', 'export.attendees': 'Xuất danh sách khách', 'export.transactions': 'Xuất CSV giao dịch', 'export.report': 'Xuất báo cáo'
  };
  const tone = a => /refund|delete|lock$|cancel|reset|unpublish/.test(a) ? 'b-bad' : /create|publish|unlock|duplicate|confirm|invite/.test(a) ? 'b-go' : 'b-wait';
  async function load() {
    try {
      const d = await API.call('/api/admin/audit?' + ADM.qs({ q: $('q').value.trim(), type: $('type').value, page: S.page, size: S.size }));
      $('rows').innerHTML = d.items.map(l => `<tr><td class="num">${ADM.dt(l.createdAt)}</td><td>${esc(l.actorEmail || '—')}</td>
        <td><span class="badge ${tone(l.action)}">${esc(ACTION[l.action] || l.action)}</span></td>
        <td>${esc(l.detail || '')}${l.entityId ? `<small class="mono">${esc(l.entityType)} ${esc(String(l.entityId).slice(0, 8).toUpperCase())}</small>` : ''}</td></tr>`).join('')
        || '<tr><td colspan="4" class="empty">Chưa có thao tác nào được ghi lại.</td></tr>';
      ADM.pager($('pager'), d, p => { S.page = p; load(); });
    } catch (err) { toast(err.message, 'error'); }
  }
  $('type').addEventListener('change', () => { S.page = 1; load(); });
  $('q').addEventListener('input', ADM.debounce(() => { S.page = 1; load(); }));
  load();
})();
