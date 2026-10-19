using System.Net;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record ConfirmPaymentDto(string? Reference);
public record InviteDto(Guid TicketTypeId, string Email, int Quantity, string? Note, string[]? Seats = null);
public record NotifyDto(string? Subject, string? Message);

public record RptDay(string Date, int Orders, int Tickets, decimal Revenue);
public record RptEvent(Guid Id, string Name, int Orders, int Tickets, decimal Revenue, decimal Discount, int Capacity, int Remaining);
public record RptType(Guid Id, string Name, string Zone, string EventName, int Orders, int Tickets, decimal Revenue, int Capacity, int Remaining);
public record RptCoupon(string Code, int Orders, int Tickets, decimal Discount, decimal Revenue);
public record RptData(
    DateTime From, DateTime To, decimal Revenue, int Orders, int Tickets, decimal Discount, decimal Refunded,
    int Invited, decimal InvitedValue, int CheckedIn, int TicketsIssued, decimal AvgOrder,
    Dictionary<string, int> OrdersByStatus,
    IReadOnlyList<RptDay> ByDay, IReadOnlyList<RptEvent> ByEvent, IReadOnlyList<RptType> ByType, IReadOnlyList<RptCoupon> ByCoupon);

/// Tài chính & vận hành bổ sung cho admin: xác nhận chuyển khoản thủ công, vé mời, sổ giao dịch, báo cáo, thông báo cho khách. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin")]
public class AdminFinanceController(AppDbContext db, AuditService audit, TicketService tickets, IServiceScopeFactory scopes,
    ILogger<AdminFinanceController> log) : ControllerBase
{
    private const int MaxExport = 50_000;
    private const int MaxRecipients = 3000;
    private const int MaxInvite = 50;
    /// Đơn phát vé mời được đánh dấu bằng CouponCode này (CouponId = null nên không ảnh hưởng đếm lượt dùng mã giảm giá).
    public const string InviteTag = "VÉ MỜI";

    private static DateTime? Utc(DateTime? d) =>
        d is null ? null : d.Value.Kind == DateTimeKind.Local ? d.Value.ToUniversalTime() : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);

    // =====================================================================
    // XÁC NHẬN THANH TOÁN THỦ CÔNG (khách đã chuyển khoản qua VietQR nhưng chưa có webhook)
    // =====================================================================
    /// Đơn Pending -> Paid. Đơn Expired (khách chuyển khoản trễ) -> lấy lại tồn kho nếu còn, hết vé thì từ chối để admin hoàn tiền.
    [HttpPost("bookings/{id:guid}/confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(Guid id, ConfirmPaymentDto dto)
    {
        var reference = (dto.Reference ?? "").Trim();
        if (reference.Length > 60) throw new AppException(400, "REF_TOO_LONG", "Mã giao dịch tối đa 60 ký tự");
        var txRef = reference.Length > 0 ? $"MAN-{reference}" : $"MAN-{Guid.NewGuid():N}";
        if (await db.Payments.AnyAsync(p => p.TransactionRef == txRef))
            throw new AppException(409, "DUP_REF", "Mã giao dịch này đã được ghi nhận cho một đơn khác");

        decimal amount;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var b = await LockBooking(id);
            if (b.Status == BookingStatus.Paid) throw new AppException(409, "ALREADY_PAID", "Đơn này đã được thanh toán");
            if (b.Status is not (BookingStatus.Pending or BookingStatus.Expired))
                throw new AppException(409, "NOT_PAYABLE", $"Không thể xác nhận thanh toán cho đơn ở trạng thái {b.Status}");

            if (b.Status == BookingStatus.Expired)
            {
                // Worker đã trả vé về kho khi đơn hết hạn -> lấy lại nguyên tử; hết vé thì không thể phát hành
                var qty = b.Quantity;
                var n = await db.TicketTypes.Where(t => t.Id == b.TicketTypeId && t.AvailableQuantity >= qty)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.AvailableQuantity, t => t.AvailableQuantity - qty));
                if (n == 0) throw new AppException(409, "SOLD_OUT", "Đơn đã hết hạn và số vé này đã được bán cho người khác. Hãy hoàn tiền cho khách bằng chuyển khoản.");
            }

            b.Status = BookingStatus.Paid;
            amount = b.TotalAmount;
            db.Payments.Add(new Payment { BookingId = b.Id, Amount = b.TotalAmount, Status = PaymentStatus.Paid, TransactionRef = txRef });
            await tickets.CreateTicketsAsync(b);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await audit.LogAsync(User, "booking.confirm-payment", "Booking", id, $"{amount:N0} đ — {txRef}");
        var mailOk = await TrySendTicketsAsync(id);
        return Ok(new { message = mailOk ? "Đã xác nhận thanh toán và gửi vé qua email" : "Đã xác nhận thanh toán, nhưng chưa gửi được email vé (dùng “Gửi lại email vé” sau khi kiểm tra SMTP)" });
    }

    // =====================================================================
    // VÉ MỜI (miễn phí, do admin phát)
    // =====================================================================
    [HttpPost("invitations")]
    public async Task<IActionResult> Invite(InviteDto dto)
    {
        var email = (dto.Email ?? "").Trim();
        var note = (dto.Note ?? "").Trim();
        if (dto.Quantity < 1 || dto.Quantity > MaxInvite) throw new AppException(400, "BAD_QUANTITY", $"Số lượng từ 1 đến {MaxInvite}");
        if (email.Length == 0) throw new AppException(400, "EMAIL_REQUIRED", "Vui lòng nhập email người nhận");
        if (note.Length > 200) throw new AppException(400, "NOTE_TOO_LONG", "Ghi chú tối đa 200 ký tự");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email)
                   ?? throw new AppException(404, "USER_NOT_FOUND", "Chưa có tài khoản với email này. Hãy để người nhận đăng ký trước (hoặc tạo ở trang Người dùng).");
        if (user.IsLocked) throw new AppException(403, "ACCOUNT_LOCKED", "Tài khoản người nhận đang bị khoá");

        Guid bookingId, eventId; string eventName;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var tt = (await db.TicketTypes.FromSqlInterpolated($"SELECT * FROM TicketTypes WITH (UPDLOCK, ROWLOCK) WHERE Id = {dto.TicketTypeId}").ToListAsync()).SingleOrDefault()
                     ?? throw new AppException(404, "TICKET_TYPE_NOT_FOUND", "Loại vé không tồn tại");
            if (tt.AvailableQuantity < dto.Quantity)
                throw new AppException(409, "SOLD_OUT", $"Chỉ còn {tt.AvailableQuantity} vé");

            // Vé có số ghế do khách tự chọn (vd. VIP): admin chọn ghế cho khách mời (kiểm tra ghế trống ngay trong lúc giữ khoá loại vé)
            List<string>? invSeats = null;
            if (tt.SeatMode == SeatMode.Choose) invSeats = await new SeatService(db).NormalizeChosenAsync(tt, dto.Seats, dto.Quantity);

            tt.AvailableQuantity -= dto.Quantity;
            var b = new Booking
            {
                UserId = user.Id, EventId = tt.EventId, TicketTypeId = tt.Id, Quantity = dto.Quantity,
                Seats = invSeats is null ? null : string.Join(",", invSeats),
                TotalAmount = 0, DiscountAmount = tt.Price * dto.Quantity, CouponCode = InviteTag,
                Status = BookingStatus.Paid, ExpiresAt = DateTime.UtcNow
            };
            db.Bookings.Add(b);
            db.Payments.Add(new Payment { BookingId = b.Id, Amount = 0, Status = PaymentStatus.Paid, TransactionRef = $"INV-{Guid.NewGuid():N}" });
            await tickets.CreateTicketsAsync(b);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            bookingId = b.Id; eventId = tt.EventId;
            eventName = await db.Events.AsNoTracking().Where(e => e.Id == tt.EventId).Select(e => e.Name).FirstAsync();
        }

        await audit.LogAsync(User, "booking.invite", "Booking", bookingId, $"{dto.Quantity} vé · {eventName} → {user.Email}{(note.Length > 0 ? " — " + note : "")}");
        var mailOk = await TrySendTicketsAsync(bookingId);
        return Ok(new { bookingId, eventId, message = mailOk ? $"Đã phát {dto.Quantity} vé mời và gửi email cho {user.Email}" : $"Đã phát {dto.Quantity} vé mời, nhưng chưa gửi được email (kiểm tra SMTP rồi dùng “Gửi lại email vé”)" });
    }

    // =====================================================================
    // SỔ GIAO DỊCH
    // =====================================================================
    private sealed class TxnRow
    {
        public Guid Id { get; init; }
        public Guid BookingId { get; init; }
        public decimal Amount { get; init; }
        public PaymentStatus Status { get; init; }
        public string TransactionRef { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public Guid EventId { get; init; }
        public string EventName { get; init; } = "";
        public string UserName { get; init; } = "";
        public string UserEmail { get; init; } = "";
    }

    private IQueryable<TxnRow> TxnBase(Guid? eventId, string? q, DateTime? fromUtc, DateTime? toUtc)
    {
        var query =
            from p in db.Payments.AsNoTracking()
            join b in db.Bookings on p.BookingId equals b.Id
            join tt in db.TicketTypes on b.TicketTypeId equals tt.Id
            join e in db.Events on tt.EventId equals e.Id
            join u in db.Users on b.UserId equals u.Id
            where (eventId == null || e.Id == eventId) && (fromUtc == null || p.CreatedAt >= fromUtc) && (toUtc == null || p.CreatedAt < toUtc)
            select new TxnRow
            {
                Id = p.Id, BookingId = b.Id, Amount = p.Amount, Status = p.Status, TransactionRef = p.TransactionRef, CreatedAt = p.CreatedAt,
                EventId = e.Id, EventName = e.Name, UserName = u.FullName, UserEmail = u.Email
            };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim(); var idPrefix = k.ToUpperInvariant();
            query = query.Where(r => r.UserEmail.Contains(k) || r.UserName.Contains(k) || r.TransactionRef.Contains(k)
                || EF.Functions.Like(r.BookingId.ToString(), idPrefix + "%"));
        }
        return query;
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions([FromQuery] string? status, [FromQuery] Guid? eventId, [FromQuery] string? q,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var baseQ = TxnBase(eventId, q, Utc(from), Utc(to));

        var sums = await baseQ.GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(x => x.Amount) }).ToListAsync();
        var filtered = Enum.TryParse<PaymentStatus>(status, true, out var s) ? baseQ.Where(r => r.Status == s) : baseQ;
        var total = await filtered.CountAsync();
        var items = await filtered.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync();

        return Ok(new
        {
            items = items.Select(r => new { r.Id, r.BookingId, r.Amount, r.Status, r.TransactionRef, r.CreatedAt, r.EventId, r.EventName, r.UserName, r.UserEmail }),
            total, page, size,
            sums = sums.Select(c => new { status = c.Status.ToString(), c.Count, c.Amount })
        });
    }

    [HttpGet("transactions/export")]
    public async Task<IActionResult> ExportTransactions([FromQuery] string? status, [FromQuery] Guid? eventId, [FromQuery] string? q,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var baseQ = TxnBase(eventId, q, Utc(from), Utc(to));
        if (Enum.TryParse<PaymentStatus>(status, true, out var s)) baseQ = baseQ.Where(r => r.Status == s);
        var rows = await baseQ.OrderByDescending(r => r.CreatedAt).Take(MaxExport).ToListAsync();

        var bytes = Csv.Build(
            ["Thời gian", "Loại", "Số tiền (đ)", "Mã giao dịch", "Mã đơn", "Sự kiện", "Khách hàng", "Email"],
            rows.Select(r => new object?[] { r.CreatedAt, r.Status, r.Amount, r.TransactionRef, r.BookingId, r.EventName, r.UserName, r.UserEmail }));
        await audit.LogAsync(User, "export.transactions", "Payment", null, $"{rows.Count} dòng");
        return File(bytes, "text/csv; charset=utf-8", $"giao-dich-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    // =====================================================================
    // BÁO CÁO
    // =====================================================================
    private async Task<RptData> BuildReport(DateTime? from, DateTime? to, Guid? eventId)
    {
        var vn = TimeSpan.FromHours(7);
        var end = Utc(to) ?? (DateTime.UtcNow + vn).Date.AddDays(1) - vn;     // hết hôm nay (giờ VN)
        end = DateTime.SpecifyKind(end, DateTimeKind.Utc);
        var start = Utc(from) ?? end.AddDays(-30);
        if (end <= start) throw new AppException(400, "BAD_RANGE", "Khoảng thời gian không hợp lệ");
        if ((end - start).TotalDays > 366) throw new AppException(400, "RANGE_TOO_LONG", "Báo cáo tối đa 366 ngày");

        var inRange = db.Bookings.AsNoTracking()
            .Where(b => b.CreatedAt >= start && b.CreatedAt < end && (eventId == null || b.EventId == eventId));
        var paid = inRange.Where(b => b.Status == BookingStatus.Paid);

        var rows = await paid.Select(b => new { b.CreatedAt, b.TotalAmount, b.DiscountAmount, b.Quantity, b.EventId, b.TicketTypeId, b.CouponCode })
            .Take(200_000).ToListAsync();

        var statusCounts = await inRange.GroupBy(b => b.Status).Select(g => new { Status = g.Key, C = g.Count() }).ToListAsync();
        var refunded = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Refunded && p.CreatedAt >= start && p.CreatedAt < end
            && (eventId == null || db.Bookings.Any(b => b.Id == p.BookingId && b.EventId == eventId))).SumAsync(p => (decimal?)p.Amount) ?? 0;

        var tix = await db.Tickets.AsNoTracking().Where(t => db.Bookings.Any(b => b.Id == t.BookingId && b.Status == BookingStatus.Paid
                && b.CreatedAt >= start && b.CreatedAt < end && (eventId == null || b.EventId == eventId)))
            .GroupBy(t => t.Status).Select(g => new { Status = g.Key, C = g.Count() }).ToListAsync();
        var checkedIn = tix.Where(x => x.Status == TicketStatus.CheckedIn).Sum(x => x.C);
        var issued = tix.Where(x => x.Status != TicketStatus.Cancelled).Sum(x => x.C);

        var evs = await db.Events.AsNoTracking().Where(e => eventId == null || e.Id == eventId).Select(e => new { e.Id, e.Name }).ToListAsync();
        var types = await db.TicketTypes.AsNoTracking().Where(t => eventId == null || t.EventId == eventId)
            .Select(t => new { t.Id, t.Name, t.Zone, t.EventId, t.TotalQuantity, t.AvailableQuantity }).ToListAsync();
        var evName = evs.ToDictionary(e => e.Id, e => e.Name);

        // Theo ngày (giờ VN), lấp đủ các ngày trong kỳ để biểu đồ liền mạch
        var byDayMap = rows.GroupBy(r => (r.CreatedAt + vn).Date).ToDictionary(g => g.Key, g => g.ToList());
        var days = new List<RptDay>();
        var firstDay = (start + vn).Date; var lastDay = (end + vn).AddTicks(-1).Date;
        for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
        {
            byDayMap.TryGetValue(d, out var g);
            days.Add(new RptDay(d.ToString("yyyy-MM-dd"), g?.Count ?? 0, g?.Sum(x => x.Quantity) ?? 0, g?.Sum(x => x.TotalAmount) ?? 0m));
        }

        var paying = rows.Where(r => r.CouponCode != InviteTag).ToList();   // vé mời không tính vào doanh thu/giảm giá
        var invited = rows.Where(r => r.CouponCode == InviteTag).ToList();

        var byEvent = rows.GroupBy(r => r.EventId).Select(g =>
        {
            var ts = types.Where(t => t.EventId == g.Key).ToList();
            return new RptEvent(g.Key, evName.GetValueOrDefault(g.Key, "(đã xoá)"), g.Count(), g.Sum(x => x.Quantity), g.Sum(x => x.TotalAmount),
                g.Where(x => x.CouponCode != InviteTag).Sum(x => x.DiscountAmount), ts.Sum(t => t.TotalQuantity), ts.Sum(t => t.AvailableQuantity));
        }).OrderByDescending(x => x.Revenue).ToList();

        var byType = rows.GroupBy(r => r.TicketTypeId).Select(g =>
        {
            var t = types.FirstOrDefault(x => x.Id == g.Key);
            return new RptType(g.Key, t?.Name ?? "(đã xoá)", t?.Zone ?? "", t is null ? "" : evName.GetValueOrDefault(t.EventId, ""),
                g.Count(), g.Sum(x => x.Quantity), g.Sum(x => x.TotalAmount), t?.TotalQuantity ?? 0, t?.AvailableQuantity ?? 0);
        }).OrderByDescending(x => x.Revenue).ToList();

        var byCoupon = paying.Where(r => r.CouponCode != null).GroupBy(r => r.CouponCode!.StartsWith(WelcomeCouponService.Prefix) ? WelcomeCouponService.Prefix + "* (mã chào mừng)" : r.CouponCode!).Select(g =>
            new RptCoupon(g.Key, g.Count(), g.Sum(x => x.Quantity), g.Sum(x => x.DiscountAmount), g.Sum(x => x.TotalAmount)))
            .OrderByDescending(x => x.Discount).ToList();

        var revenue = rows.Sum(r => r.TotalAmount);
        var orders = paying.Count;
        return new RptData(start, end, revenue, orders, paying.Sum(r => r.Quantity), paying.Sum(r => r.DiscountAmount), refunded,
            invited.Sum(r => r.Quantity), invited.Sum(r => r.DiscountAmount), checkedIn, issued, orders == 0 ? 0 : Math.Round(paying.Sum(r => r.TotalAmount) / orders),
            statusCounts.ToDictionary(x => x.Status.ToString(), x => x.C), days, byEvent, byType, byCoupon);
    }

    [HttpGet("reports")]
    public async Task<IActionResult> Report([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] Guid? eventId)
        => Ok(await BuildReport(from, to, eventId));

    [HttpGet("reports/export")]
    public async Task<IActionResult> ExportReport([FromQuery] string? kind, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] Guid? eventId)
    {
        var r = await BuildReport(from, to, eventId);
        byte[] bytes; string name;
        switch (kind)
        {
            case "event":
                name = "bao-cao-su-kien";
                bytes = Csv.Build(["Sự kiện", "Số đơn", "Vé bán", "Doanh thu (đ)", "Giảm giá (đ)", "Tổng vé", "Còn lại"],
                    r.ByEvent.Select(x => new object?[] { x.Name, x.Orders, x.Tickets, x.Revenue, x.Discount, x.Capacity, x.Remaining }));
                break;
            case "type":
                name = "bao-cao-loai-ve";
                bytes = Csv.Build(["Sự kiện", "Loại vé", "Khu", "Số đơn", "Vé bán", "Doanh thu (đ)", "Tổng vé", "Còn lại"],
                    r.ByType.Select(x => new object?[] { x.EventName, x.Name, x.Zone, x.Orders, x.Tickets, x.Revenue, x.Capacity, x.Remaining }));
                break;
            case "coupon":
                name = "bao-cao-ma-giam-gia";
                bytes = Csv.Build(["Mã", "Số đơn", "Vé", "Tiền đã giảm (đ)", "Doanh thu (đ)"],
                    r.ByCoupon.Select(x => new object?[] { x.Code, x.Orders, x.Tickets, x.Discount, x.Revenue }));
                break;
            default:
                name = "bao-cao-theo-ngay";
                bytes = Csv.Build(["Ngày", "Số đơn", "Vé bán", "Doanh thu (đ)"],
                    r.ByDay.Select(x => new object?[] { x.Date, x.Orders, x.Tickets, x.Revenue }));
                break;
        }
        await audit.LogAsync(User, "export.report", "Report", null, $"{name} · {r.From:yyyy-MM-dd} → {r.To:yyyy-MM-dd}");
        return File(bytes, "text/csv; charset=utf-8", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    // =====================================================================
    // THÔNG BÁO EMAIL CHO KHÁCH CỦA SỰ KIỆN (đổi giờ, đổi địa điểm, nhắc lịch…)
    // =====================================================================
    [HttpGet("events/{id:guid}/recipients")]
    public async Task<IActionResult> Recipients(Guid id)
    {
        var n = await RecipientsQuery(id).CountAsync();
        return Ok(new { count = n, max = MaxRecipients });
    }

    private IQueryable<string> RecipientsQuery(Guid eventId) =>
        db.Bookings.AsNoTracking().Where(b => b.EventId == eventId && b.Status == BookingStatus.Paid).Select(b => b.User!.Email).Distinct();

    [HttpPost("events/{id:guid}/notify")]
    public async Task<IActionResult> Notify(Guid id, NotifyDto dto)
    {
        var subject = (dto.Subject ?? "").Trim(); var message = (dto.Message ?? "").Trim();
        if (subject.Length is < 3 or > 150) throw new AppException(400, "BAD_SUBJECT", "Tiêu đề từ 3 đến 150 ký tự");
        if (message.Length is < 5 or > 3000) throw new AppException(400, "BAD_MESSAGE", "Nội dung từ 5 đến 3000 ký tự");

        var ev = await db.Events.AsNoTracking().Where(e => e.Id == id).Select(e => new { e.Name, e.Venue, e.StartsAt }).FirstOrDefaultAsync()
                 ?? throw new AppException(404, "NOT_FOUND", "Sự kiện không tồn tại");
        var emails = await RecipientsQuery(id).Take(MaxRecipients + 1).ToListAsync();
        if (emails.Count == 0) throw new AppException(409, "NO_RECIPIENTS", "Sự kiện này chưa có khách đã thanh toán");
        if (emails.Count > MaxRecipients) throw new AppException(409, "TOO_MANY", $"Quá {MaxRecipients} người nhận, hãy chia nhỏ hoặc dùng dịch vụ gửi email hàng loạt");

        var html = $"<div style='font-family:Arial;max-width:600px;margin:auto'><h2>{WebUtility.HtmlEncode(ev.Name)}</h2>" +
                   $"<p style='color:#64748b'>📍 {WebUtility.HtmlEncode(ev.Venue)} · 🕒 {ev.StartsAt.ToLocalTime():HH:mm dd/MM/yyyy}</p>" +
                   $"<div style='white-space:pre-wrap;line-height:1.6'>{WebUtility.HtmlEncode(message)}</div>" +
                   "<hr style='border:0;border-top:1px solid #e2e8f0;margin:24px 0'/><p style='color:#94a3b8;font-size:12px'>Bạn nhận email này vì đã mua vé sự kiện trên TicketGo.</p></div>";

        await audit.LogAsync(User, "event.notify", "Event", id, $"{emails.Count} người · {subject}");

        // Gửi nền: SMTP mở kết nối cho từng thư nên có thể mất vài phút; không giữ request của admin
        var list = emails.ToList();
        _ = Task.Run(async () =>
        {
            int ok = 0, fail = 0;
            using var scope = scopes.CreateScope();
            var mail = scope.ServiceProvider.GetRequiredService<IEmailService>();
            foreach (var to in list)
            {
                try { await mail.SendAsync(to, subject, html); ok++; }
                catch (Exception ex) { fail++; log.LogWarning(ex, "Notify email failed for event {Event}", id); }
            }
            log.LogInformation("Notify event {Event}: sent {Ok}, failed {Fail}", id, ok, fail);
        });

        return Accepted(new { count = emails.Count, message = $"Đang gửi thông báo cho {emails.Count} khách. Việc gửi chạy nền, có thể mất vài phút." });
    }

    // =====================================================================
    private async Task<bool> TrySendTicketsAsync(Guid bookingId)
    {
        try { await tickets.SendTicketEmailAsync(bookingId); return true; }
        catch (Exception ex) { log.LogError(ex, "Send ticket email failed for booking {Id}", bookingId); return false; }
    }

    private async Task<Booking> LockBooking(Guid id) =>
        (await db.Bookings.FromSqlInterpolated($"SELECT * FROM Bookings WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}").ToListAsync()).SingleOrDefault()
        ?? throw new AppException(404, "NOT_FOUND", "Đơn hàng không tồn tại");
}
