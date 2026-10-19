using System.Net;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record ReasonDto(string? Reason);

/// Quản lý đơn hàng (booking) và vé đã phát hành. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin")]
public class AdminOrdersController(AppDbContext db, AuditService audit, TicketService tickets, IEmailService email,
    ILogger<AdminOrdersController> log) : ControllerBase
{
    private const int MaxExport = 50_000;

    // =====================================================================
    // ĐƠN HÀNG
    // =====================================================================
    private IQueryable<Booking> BookingBase(Guid? eventId, string? q, DateTime? from, DateTime? to)
    {
        var query = db.Bookings.AsNoTracking().AsQueryable();
        if (eventId is not null) query = query.Where(b => b.EventId == eventId);
        if (from is not null) query = query.Where(b => b.CreatedAt >= from);
        if (to is not null) query = query.Where(b => b.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim(); var idPrefix = k.ToUpperInvariant();
            query = query.Where(b => b.User!.Email.Contains(k) || b.User.FullName.Contains(k) || EF.Functions.Like(b.Id.ToString(), idPrefix + "%"));
        }
        return query;
    }

    private static IQueryable<Booking> ByStatus(IQueryable<Booking> q, string? status) =>
        Enum.TryParse<BookingStatus>(status, true, out var s) ? q.Where(b => b.Status == s) : q;

    [HttpGet("bookings")]
    public async Task<IActionResult> Bookings([FromQuery] string? status, [FromQuery] Guid? eventId, [FromQuery] string? q,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var baseQ = BookingBase(eventId, q, from, to);

        var counts = await baseQ.GroupBy(b => b.Status).Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(x => x.TotalAmount) }).ToListAsync();
        var filtered = ByStatus(baseQ, status);
        var total = await filtered.CountAsync();

        var items = await filtered.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * size).Take(size).Select(b => new
        {
            b.Id, b.Quantity, b.TotalAmount, b.Status, b.CreatedAt, b.ExpiresAt, b.EventId,
            EventName = b.TicketType!.Event!.Name, TypeName = b.TicketType.Name, b.TicketType.Zone,
            UserEmail = b.User!.Email, UserName = b.User.FullName
        }).ToListAsync();

        return Ok(new
        {
            items, total, page, size,
            counts = counts.Select(c => new { status = c.Status.ToString(), c.Count, c.Amount })
        });
    }

    [HttpGet("bookings/{id:guid}")]
    public async Task<IActionResult> BookingDetail(Guid id)
    {
        var b = await db.Bookings.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Quantity, x.TotalAmount, x.Status, x.CreatedAt, x.ExpiresAt, x.EventId,
            EventName = x.TicketType!.Event!.Name, EventStartsAt = x.TicketType.Event.StartsAt, x.TicketType.Event.Venue,
            TypeName = x.TicketType.Name, x.TicketType.Zone, UnitPrice = x.TicketType.Price,
            User = new { x.User!.Id, x.User.Email, x.User.FullName }
        }).FirstOrDefaultAsync();
        if (b is null) return NotFound();

        var payments = await db.Payments.AsNoTracking().Where(p => p.BookingId == id).OrderBy(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Amount, p.Status, p.TransactionRef, p.CreatedAt }).ToListAsync();
        var list = await db.Tickets.AsNoTracking().Where(t => t.BookingId == id).OrderBy(t => t.SeatInfo)
            .Select(t => new { t.Id, t.SeatInfo, t.Status, t.CheckedInAt }).ToListAsync();
        return Ok(new { booking = b, payments, tickets = list });
    }

    /// Huỷ đơn đang giữ chỗ (chưa thanh toán) và trả lại tồn kho ngay.
    [HttpPost("bookings/{id:guid}/cancel")]
    public async Task<IActionResult> CancelBooking(Guid id, ReasonDto dto)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var b = await LockBooking(id);
        if (b.Status != BookingStatus.Pending)
            throw new AppException(409, "NOT_PENDING", $"Chỉ huỷ được đơn đang giữ chỗ (hiện tại: {b.Status}). Đơn đã thanh toán hãy dùng Hoàn tiền.");

        b.Status = BookingStatus.Cancelled;
        await db.SaveChangesAsync();
        await Restock(b.TicketTypeId, b.Quantity);
        await tx.CommitAsync();

        await audit.LogAsync(User, "booking.cancel", "Booking", id, dto.Reason);
        return Ok(new { message = "Đã huỷ đơn và trả lại vé vào kho" });
    }

    /// Hoàn tiền toàn phần: đơn -> Cancelled, vé -> Cancelled, ghi nhận Payment kiểu Refunded, trả tồn kho.
    /// Từ chối nếu đã có vé được soát vào cổng.
    [HttpPost("bookings/{id:guid}/refund")]
    public async Task<IActionResult> Refund(Guid id, ReasonDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason)) throw new AppException(400, "REASON_REQUIRED", "Vui lòng nhập lý do hoàn tiền");

        string userMail, userName, eventName; decimal amount;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var b = await LockBooking(id);
            if (b.Status != BookingStatus.Paid)
                throw new AppException(409, "NOT_PAID", $"Chỉ hoàn tiền được đơn đã thanh toán (hiện tại: {b.Status})");

            var ts = await db.Tickets.Where(t => t.BookingId == id).ToListAsync();
            if (ts.Any(t => t.Status == TicketStatus.CheckedIn))
                throw new AppException(409, "TICKET_USED", "Đã có vé của đơn này được soát vào cổng, không thể hoàn tiền tự động");

            foreach (var t in ts) t.Status = TicketStatus.Cancelled;
            b.Status = BookingStatus.Cancelled;
            db.Payments.Add(new Payment { BookingId = id, Amount = b.TotalAmount, Status = PaymentStatus.Refunded, TransactionRef = $"RF-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
            await Restock(b.TicketTypeId, b.Quantity);
            await tx.CommitAsync();

            var info = await db.Bookings.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.User!.Email, x.User.FullName, Ev = x.TicketType!.Event!.Name }).FirstAsync();
            userMail = info.Email; userName = info.FullName; eventName = info.Ev; amount = b.TotalAmount;
        }

        await audit.LogAsync(User, "booking.refund", "Booking", id, $"{amount:N0} đ — {dto.Reason}");

        // Email thông báo: lỗi gửi mail không được làm hỏng giao dịch đã hoàn
        try
        {
            await email.SendAsync(userMail, $"Hoàn tiền đơn vé – {eventName}",
                $"<div style='font-family:Arial'><h2>Đơn của bạn đã được hoàn tiền</h2><p>Xin chào {WebUtility.HtmlEncode(userName)},</p>" +
                $"<p>Đơn vé <b>{WebUtility.HtmlEncode(eventName)}</b> đã được hoàn <b>{amount:N0} đ</b>. Các mã QR của đơn này không còn hiệu lực.</p>" +
                $"<p>Lý do: {WebUtility.HtmlEncode(dto.Reason)}</p></div>");
        }
        catch (Exception ex) { log.LogError(ex, "Send refund email failed for booking {Id}", id); }

        return Ok(new { message = "Đã hoàn tiền, thu hồi vé và trả lại kho" });
    }

    [HttpPost("bookings/{id:guid}/resend-email")]
    public async Task<IActionResult> ResendEmail(Guid id)
    {
        var st = await db.Bookings.AsNoTracking().Where(b => b.Id == id).Select(b => (BookingStatus?)b.Status).FirstOrDefaultAsync();
        if (st is null) return NotFound();
        if (st != BookingStatus.Paid) throw new AppException(409, "NOT_PAID", "Chỉ gửi lại vé cho đơn đã thanh toán");
        try { await tickets.SendTicketEmailAsync(id); }
        catch (Exception ex) { log.LogError(ex, "Resend ticket email failed for booking {Id}", id); throw new AppException(502, "MAIL_FAILED", "Không gửi được email. Kiểm tra cấu hình SMTP."); }
        await audit.LogAsync(User, "booking.resend", "Booking", id);
        return Ok(new { message = "Đã gửi lại email vé" });
    }

    [HttpGet("bookings/export")]
    public async Task<IActionResult> ExportBookings([FromQuery] string? status, [FromQuery] Guid? eventId, [FromQuery] string? q,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var rows = await ByStatus(BookingBase(eventId, q, from, to), status).OrderByDescending(b => b.CreatedAt).Take(MaxExport).Select(b => new
        {
            b.Id, b.CreatedAt, b.Status, Ev = b.TicketType!.Event!.Name, Type = b.TicketType.Name, b.TicketType.Zone,
            b.Quantity, b.TotalAmount, b.User!.FullName, b.User.Email
        }).ToListAsync();

        var bytes = Csv.Build(
            ["Mã đơn", "Ngày tạo", "Trạng thái", "Sự kiện", "Loại vé", "Khu", "Số lượng", "Tổng tiền (đ)", "Khách hàng", "Email"],
            rows.Select(r => new object?[] { r.Id, r.CreatedAt, r.Status, r.Ev, r.Type, r.Zone, r.Quantity, r.TotalAmount, r.FullName, r.Email }));
        await audit.LogAsync(User, "export.bookings", "Booking", null, $"{rows.Count} dòng");
        return File(bytes, "text/csv; charset=utf-8", $"don-hang-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    // =====================================================================
    // VÉ
    // =====================================================================
    private sealed class TicketRow
    {
        public Guid Id { get; init; }
        public string SeatInfo { get; init; } = "";
        public TicketStatus Status { get; init; }
        public DateTime? CheckedInAt { get; init; }
        public Guid BookingId { get; init; }
        public Guid EventId { get; init; }
        public string EventName { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string Zone { get; init; } = "";
        public string UserName { get; init; } = "";
        public string UserEmail { get; init; } = "";
    }

    private IQueryable<TicketRow> TicketBase(Guid? eventId, string? q)
    {
        var query =
            from t in db.Tickets.AsNoTracking()
            join b in db.Bookings on t.BookingId equals b.Id
            join tt in db.TicketTypes on b.TicketTypeId equals tt.Id
            join e in db.Events on t.EventId equals e.Id
            join u in db.Users on t.UserId equals u.Id
            where eventId == null || t.EventId == eventId
            select new TicketRow
            {
                Id = t.Id, SeatInfo = t.SeatInfo, Status = t.Status, CheckedInAt = t.CheckedInAt, BookingId = b.Id, EventId = e.Id,
                EventName = e.Name, TypeName = tt.Name, Zone = tt.Zone, UserName = u.FullName, UserEmail = u.Email
            };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim(); var idPrefix = k.ToUpperInvariant();
            query = query.Where(r => r.UserEmail.Contains(k) || r.UserName.Contains(k) || EF.Functions.Like(r.Id.ToString(), idPrefix + "%"));
        }
        return query;
    }

    [HttpGet("tickets")]
    public async Task<IActionResult> Tickets([FromQuery] Guid? eventId, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var baseQ = TicketBase(eventId, q);
        var counts = await baseQ.GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();

        var filtered = Enum.TryParse<TicketStatus>(status, true, out var s) ? baseQ.Where(r => r.Status == s) : baseQ;
        var total = await filtered.CountAsync();
        var items = await filtered.OrderBy(r => r.EventName).ThenBy(r => r.UserEmail).ThenBy(r => r.SeatInfo)
            .Skip((page - 1) * size).Take(size).ToListAsync();

        return Ok(new
        {
            items = items.Select(r => new
            {
                r.Id, Code = r.Id.ToString()[..8].ToUpper(), r.SeatInfo, r.Status, r.CheckedInAt, r.BookingId, r.EventId,
                r.EventName, r.TypeName, r.Zone, r.UserName, r.UserEmail
            }),
            total, page, size,
            counts = counts.Select(c => new { status = c.Status.ToString(), c.Count })
        });
    }

    /// Check-in thủ công (khi khách mất mạng/không quét được QR). Nguyên tử như check-in bằng QR.
    [HttpPost("tickets/{id:guid}/check-in")]
    public async Task<IActionResult> ManualCheckIn(Guid id)
    {
        var n = await db.Tickets.Where(t => t.Id == id && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.CheckedIn).SetProperty(t => t.CheckedInAt, DateTime.UtcNow));
        if (n == 0) throw await TicketStateError(id, "check-in");
        await audit.LogAsync(User, "ticket.checkin", "Ticket", id, "thủ công");
        return Ok(new { message = "Đã check-in vé" });
    }

    /// Hoàn tác check-in (quét nhầm). Bắt buộc có lý do để truy vết.
    [HttpPost("tickets/{id:guid}/undo-check-in")]
    public async Task<IActionResult> UndoCheckIn(Guid id, ReasonDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason)) throw new AppException(400, "REASON_REQUIRED", "Vui lòng nhập lý do hoàn tác");
        var n = await db.Tickets.Where(t => t.Id == id && t.Status == TicketStatus.CheckedIn)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Valid).SetProperty(t => t.CheckedInAt, (DateTime?)null));
        if (n == 0) throw await TicketStateError(id, "hoàn tác");
        await audit.LogAsync(User, "ticket.undo-checkin", "Ticket", id, dto.Reason);
        return Ok(new { message = "Đã hoàn tác check-in" });
    }

    /// Thu hồi một vé (mã QR không dùng được nữa). Không hoàn tiền/tồn kho — dùng Hoàn tiền ở cấp đơn nếu cần.
    [HttpPost("tickets/{id:guid}/cancel")]
    public async Task<IActionResult> CancelTicket(Guid id, ReasonDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason)) throw new AppException(400, "REASON_REQUIRED", "Vui lòng nhập lý do thu hồi");
        var n = await db.Tickets.Where(t => t.Id == id && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Cancelled));
        if (n == 0) throw await TicketStateError(id, "thu hồi");
        await audit.LogAsync(User, "ticket.cancel", "Ticket", id, dto.Reason);
        return Ok(new { message = "Đã thu hồi vé" });
    }

    /// Danh sách khách tham dự của một sự kiện (không xuất mã QR).
    [HttpGet("tickets/export")]
    public async Task<IActionResult> ExportAttendees([FromQuery] Guid eventId, [FromQuery] string? status)
    {
        var baseQ = TicketBase(eventId, null);
        if (Enum.TryParse<TicketStatus>(status, true, out var s)) baseQ = baseQ.Where(r => r.Status == s);
        var rows = await baseQ.OrderBy(r => r.Zone).ThenBy(r => r.UserName).ThenBy(r => r.SeatInfo).Take(MaxExport).ToListAsync();

        var bytes = Csv.Build(
            ["Mã vé", "Sự kiện", "Khu", "Loại vé", "Ghế/Số", "Trạng thái", "Giờ vào cổng", "Khách hàng", "Email", "Mã đơn"],
            rows.Select(r => new object?[] { r.Id.ToString()[..8].ToUpper(), r.EventName, r.Zone, r.TypeName, r.SeatInfo, r.Status, r.CheckedInAt, r.UserName, r.UserEmail, r.BookingId }));
        await audit.LogAsync(User, "export.attendees", "Event", eventId, $"{rows.Count} dòng");
        return File(bytes, "text/csv; charset=utf-8", $"khach-tham-du-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    // =====================================================================
    private async Task<Booking> LockBooking(Guid id) =>
        (await db.Bookings.FromSqlInterpolated($"SELECT * FROM Bookings WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}").ToListAsync()).SingleOrDefault()
        ?? throw new AppException(404, "NOT_FOUND", "Đơn hàng không tồn tại");

    private Task<int> Restock(Guid ticketTypeId, int qty) =>
        db.TicketTypes.Where(t => t.Id == ticketTypeId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AvailableQuantity, t => t.AvailableQuantity + qty));

    private async Task<AppException> TicketStateError(Guid id, string action)
    {
        var st = await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => (TicketStatus?)t.Status).FirstOrDefaultAsync();
        return st is null
            ? new AppException(404, "NOT_FOUND", "Vé không tồn tại")
            : new AppException(409, "BAD_STATE", $"Không thể {action}: vé đang ở trạng thái {st}");
    }
}
