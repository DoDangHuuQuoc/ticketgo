using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record CheckInDto(Guid EventId, string QrCode);

[ApiController, Route("api/admin")]
public class AdminController(AppDbContext db) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics([FromQuery] Guid? eventId)
    {
        var paid = db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Paid);
        if (eventId is not null) paid = paid.Where(b => b.EventId == eventId);

        var revenue = await paid.SumAsync(b => (decimal?)b.TotalAmount) ?? 0;
        var ticketsSold = await paid.SumAsync(b => (int?)b.Quantity) ?? 0;
        var byDay = await paid.GroupBy(b => b.CreatedAt.Date)
            .Select(g => new { date = g.Key, revenue = g.Sum(x => x.TotalAmount), tickets = g.Sum(x => x.Quantity) })
            .OrderBy(x => x.date).ToListAsync();
        var checkedIn = await db.Tickets.CountAsync(t => t.Status == TicketStatus.CheckedIn && (eventId == null || t.EventId == eventId));

        // Mở rộng: trạng thái đơn, doanh thu theo loại vé, top sự kiện, tiền đã hoàn
        var all = db.Bookings.AsNoTracking().AsQueryable();
        if (eventId is not null) all = all.Where(b => b.EventId == eventId);
        var byStatus = await all.GroupBy(b => b.Status).Select(g => new { status = g.Key.ToString(), count = g.Count(), amount = g.Sum(x => x.TotalAmount) }).ToListAsync();
        var byType = await paid.GroupBy(b => new { b.TicketType!.Name, b.TicketType.Zone })
            .Select(g => new { name = g.Key.Name, zone = g.Key.Zone, tickets = g.Sum(x => x.Quantity), revenue = g.Sum(x => x.TotalAmount) })
            .OrderByDescending(x => x.revenue).Take(8).ToListAsync();
        var topEvents = eventId is not null ? null : await paid.GroupBy(b => b.TicketType!.Event!.Name)
            .Select(g => new { name = g.Key, tickets = g.Sum(x => x.Quantity), revenue = g.Sum(x => x.TotalAmount) })
            .OrderByDescending(x => x.revenue).Take(5).ToListAsync();
        var refunded = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Refunded
            && (eventId == null || db.Bookings.Any(b => b.Id == p.BookingId && b.EventId == eventId))).SumAsync(p => (decimal?)p.Amount) ?? 0;
        var pendingNow = await db.Bookings.CountAsync(b => b.Status == BookingStatus.Pending && b.ExpiresAt > DateTime.UtcNow && (eventId == null || b.EventId == eventId));

        // Theo giờ Việt Nam (UTC+7): doanh thu hôm nay chia theo từng giờ
        var vn = TimeSpan.FromHours(7);
        var todayStartUtc = (DateTime.UtcNow + vn).Date - vn;
        var todayRows = await paid.Where(b => b.CreatedAt >= todayStartUtc).Select(b => new { b.CreatedAt, b.TotalAmount, b.Quantity }).ToListAsync();
        var byHour = Enumerable.Range(0, 24).Select(h =>
        {
            var rows = todayRows.Where(r => (r.CreatedAt + vn).Hour == h).ToList();
            return new { hour = h, revenue = rows.Sum(r => r.TotalAmount), tickets = rows.Sum(r => r.Quantity) };
        }).ToList();
        var todayRevenue = todayRows.Sum(r => r.TotalAmount);
        var ticketsToday = todayRows.Sum(r => r.Quantity);
        var customers = await db.Users.AsNoTracking().CountAsync(u => u.Role == "User");
        var eventCount = await db.Events.AsNoTracking().CountAsync();

        return Ok(new { revenue, ticketsSold, checkedIn, byDay, byHour, byStatus, byType, topEvents, refunded, pendingNow, todayRevenue, ticketsToday, customers, eventCount });
    }

    /// Danh sách sự kiện cho màn soát vé: gồm cả sự kiện đã bắt đầu (trong 24h qua) — khác với /api/events chỉ trả sự kiện chưa diễn ra.
    [Authorize(Roles = "Admin,Staff")]
    [HttpGet("checkin/events")]
    public async Task<IActionResult> CheckInEvents() => Ok(await db.Events.AsNoTracking()
        .Where(e => e.IsPublished && e.StartsAt > DateTime.UtcNow.AddHours(-24)).OrderBy(e => e.StartsAt)
        .Select(e => new { e.Id, e.Name, e.StartsAt }).ToListAsync());

    /// Check-in nguyên tử: chỉ 1 request thắng dù 2 nhân viên quét cùng lúc.
    /// Nhận cả mã QR đầy đủ lẫn "Mã vé" 8 ký tự in trên vé/email (nhân viên gõ tay).
    [Authorize(Roles = "Admin,Staff")]
    [HttpPost("tickets/check-in")]
    public async Task<IActionResult> CheckIn(CheckInDto dto)
    {
        var code = (dto.QrCode ?? "").Trim();
        if (code.Length == 0) throw new AppException(400, "EMPTY", "Chưa có mã vé");

        // "Mã vé" 8 ký tự hex (đầu của Id vé) -> đổi sang mã QR thật của vé thuộc sự kiện đang soát
        if (code.Length == 8 && code.All(Uri.IsHexDigit))
        {
            var cands = await db.Tickets.AsNoTracking().Where(t => t.EventId == dto.EventId).Select(t => new { t.Id, t.QrCode }).ToListAsync();
            var hit = cands.Where(t => t.Id.ToString("N")[..8].Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 1) code = hit[0].QrCode;
            else if (hit.Count > 1) throw new AppException(409, "TICKET_AMBIGUOUS", "Mã ngắn trùng nhiều vé, hãy quét QR hoặc dán mã QR đầy đủ");
        }

        var n = await db.Tickets
            .Where(t => t.QrCode == code && t.EventId == dto.EventId && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.CheckedIn).SetProperty(t => t.CheckedInAt, DateTime.UtcNow));

        if (n == 1)
        {
            var info = await (from t in db.Tickets.AsNoTracking()
                              where t.QrCode == code
                              join b in db.Bookings.AsNoTracking() on t.BookingId equals b.Id
                              join tt in db.TicketTypes.AsNoTracking() on b.TicketTypeId equals tt.Id
                              join u in db.Users.AsNoTracking() on t.UserId equals u.Id
                              select new { t.Id, t.SeatInfo, t.CheckedInAt, TypeName = tt.Name, tt.Zone, u.FullName }).FirstAsync();
            // Trả "Mã vé" (đúng mã in trên vé/email) + hạng vé + tên khách để nhân viên đối chiếu với vé khách đưa
            return Ok(new { message = "Check-in thành công", info.SeatInfo, info.CheckedInAt, code = info.Id.ToString()[..8].ToUpperInvariant(), info.TypeName, info.Zone, customer = info.FullName });
        }

        var existing = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.QrCode == code);
        if (existing is null)
            throw new AppException(404, "TICKET_INVALID", "Không tìm thấy vé với mã này");
        if (existing.EventId != dto.EventId)
        {
            var evName = await db.Events.AsNoTracking().Where(e => e.Id == existing.EventId).Select(e => e.Name).FirstOrDefaultAsync();
            throw new AppException(404, "TICKET_OTHER_EVENT", $"Vé này thuộc sự kiện «{evName}». Hãy chọn đúng sự kiện ở ô phía trên rồi quét lại");
        }
        if (existing.Status == TicketStatus.CheckedIn)
        {
            var at = existing.CheckedInAt is { } d ? $" lúc {d.ToLocalTime():HH:mm dd/MM}" : "";
            throw new AppException(409, "TICKET_USED", $"Vé đã được check-in{at}");
        }
        throw new AppException(409, "TICKET_CANCELLED", "Vé đã bị huỷ/thu hồi, không dùng được");
    }
}
