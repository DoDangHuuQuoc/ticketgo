using EventTicketing.Common;
using EventTicketing.Data;
using System.Text.Json;
using EventTicketing.Models;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record EventUpsertDto(string Name, string? Description, string Venue, DateTime StartsAt, string? PosterUrl,
    bool IsPublished, string? Category = null, string? SeatMapUrl = null);
public record PublishDto(bool IsPublished);
public record TicketTypeUpsertDto(string Name, string Zone, decimal Price, int TotalQuantity, int SeatMode = 0, int SeatRows = 0, int SeatsPerRow = 0);

/// Quản lý sự kiện + loại vé. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin")]
public class AdminEventsController(AppDbContext db, AuditService audit) : ControllerBase
{
    // ---------- Danh sách (kèm số liệu bán hàng) ----------
    [HttpGet("events")]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status)
    {
        var now = DateTime.UtcNow;
        var query = db.Events.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim();
            query = query.Where(e => e.Name.Contains(k) || e.Venue.Contains(k));
        }
        query = status switch
        {
            "draft" => query.Where(e => !e.IsPublished),
            "upcoming" => query.Where(e => e.IsPublished && e.StartsAt > now),
            "past" => query.Where(e => e.StartsAt <= now),
            _ => query
        };

        var events = await query.OrderByDescending(e => e.StartsAt).Select(e => new
        {
            e.Id, e.Name, e.PosterUrl, e.Venue, e.StartsAt, e.IsPublished, e.CreatedAt,
            HasSeatMap = e.SeatMapUrl != null,
            TypeCount = e.TicketTypes.Count,
            Total = e.TicketTypes.Sum(t => (int?)t.TotalQuantity) ?? 0,
            Available = e.TicketTypes.Sum(t => (int?)t.AvailableQuantity) ?? 0
        }).ToListAsync();

        var ids = events.Select(e => e.Id).ToList();
        var sales = await db.Bookings.AsNoTracking().Where(b => ids.Contains(b.EventId))
            .GroupBy(b => new { b.EventId, b.Status })
            .Select(g => new { g.Key.EventId, g.Key.Status, Qty = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.TotalAmount), Orders = g.Count() })
            .ToListAsync();

        return Ok(events.Select(e =>
        {
            var mine = sales.Where(s => s.EventId == e.Id).ToList();
            var paid = mine.FirstOrDefault(s => s.Status == BookingStatus.Paid);
            var pending = mine.FirstOrDefault(s => s.Status == BookingStatus.Pending);
            return new
            {
                e.Id, e.Name, e.PosterUrl, e.Venue, e.StartsAt, e.IsPublished, e.CreatedAt, e.HasSeatMap,
                e.TypeCount, e.Total, e.Available,
                Sold = paid?.Qty ?? 0, Holding = pending?.Qty ?? 0, Revenue = paid?.Amount ?? 0m,
                Orders = mine.Sum(s => s.Orders),
                IsPast = e.StartsAt <= now
            };
        }));
    }

    // ---------- Chi tiết + loại vé ----------
    [HttpGet("events/{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var ev = await db.Events.AsNoTracking().Where(e => e.Id == id).Select(e => new
        {
            e.Id, e.Name, e.Description, e.PosterUrl, e.SeatMapUrl, e.Venue, e.StartsAt, e.IsPublished, e.Category
        }).FirstOrDefaultAsync();
        if (ev is null) return NotFound();

        var types = await db.TicketTypes.AsNoTracking().Where(t => t.EventId == id).OrderByDescending(t => t.Price)
            .Select(t => new { t.Id, t.Name, t.Zone, t.Price, t.TotalQuantity, t.AvailableQuantity, SeatMode = (int)t.SeatMode, t.SeatRows, t.SeatsPerRow }).ToListAsync();
        var byType = await db.Bookings.AsNoTracking().Where(b => b.EventId == id && (b.Status == BookingStatus.Paid || b.Status == BookingStatus.Pending))
            .GroupBy(b => new { b.TicketTypeId, b.Status })
            .Select(g => new { g.Key.TicketTypeId, g.Key.Status, Qty = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.TotalAmount), Orders = g.Count() }).ToListAsync();

        return Ok(new
        {
            ev,
            ticketTypes = types.Select(t =>
            {
                var paid = byType.FirstOrDefault(x => x.TicketTypeId == t.Id && x.Status == BookingStatus.Paid);
                var pend = byType.FirstOrDefault(x => x.TicketTypeId == t.Id && x.Status == BookingStatus.Pending);
                return new
                {
                    t.Id, t.Name, t.Zone, t.Price, t.TotalQuantity, t.AvailableQuantity, t.SeatMode, t.SeatRows, t.SeatsPerRow,
                    Sold = paid?.Qty ?? 0, Holding = pend?.Qty ?? 0, Revenue = paid?.Amount ?? 0m,
                    Orders = (paid?.Orders ?? 0) + (pend?.Orders ?? 0)
                };
            })
        });
    }

    // ---------- Tạo / sửa / xoá sự kiện ----------
    [HttpPost("events")]
    public async Task<IActionResult> Create(EventUpsertDto dto)
    {
        Validate(dto);
        var ev = new Event();
        Apply(ev, dto);
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "event.create", "Event", ev.Id, ev.Name);
        return Ok(new { ev.Id, message = "Đã tạo sự kiện" });
    }

    [HttpPut("events/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, EventUpsertDto dto)
    {
        Validate(dto);
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id) ?? throw NotFoundEx("Sự kiện không tồn tại");
        var before = $"{ev.Name} | {ev.Venue} | {ev.StartsAt:u} | publish={ev.IsPublished}";
        Apply(ev, dto);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "event.update", "Event", ev.Id, $"{before} -> {ev.Name} | {ev.Venue} | {ev.StartsAt:u} | publish={ev.IsPublished}");
        return Ok(new { message = "Đã cập nhật sự kiện" });
    }

    [HttpPost("events/{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, PublishDto dto)
    {
        if (dto.IsPublished && !await db.TicketTypes.AnyAsync(t => t.EventId == id))
            throw new AppException(409, "NO_TICKET_TYPE", "Cần có ít nhất một loại vé trước khi công khai sự kiện");
        var n = await db.Events.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsPublished, dto.IsPublished));
        if (n == 0) throw NotFoundEx("Sự kiện không tồn tại");
        await audit.LogAsync(User, dto.IsPublished ? "event.publish" : "event.unpublish", "Event", id);
        return Ok(new { message = dto.IsPublished ? "Đã công khai sự kiện" : "Đã chuyển về bản nháp" });
    }

    [HttpPost("events/{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id)
    {
        var src = await db.Events.AsNoTracking().Include(e => e.TicketTypes).FirstOrDefaultAsync(e => e.Id == id)
                  ?? throw NotFoundEx("Sự kiện không tồn tại");
        var copy = new Event
        {
            Name = (src.Name + " (bản sao)").Length > 300 ? src.Name : src.Name + " (bản sao)",
            Description = src.Description, PosterUrl = src.PosterUrl, SeatMapUrl = src.SeatMapUrl, Venue = src.Venue,
            StartsAt = src.StartsAt > DateTime.UtcNow ? src.StartsAt : DateTime.UtcNow.AddDays(30),
            Category = src.Category,
            IsPublished = false   // bản sao luôn là nháp để admin kiểm tra trước khi mở bán
        };
        foreach (var t in src.TicketTypes)
        {
            var nt = new TicketType { Name = t.Name, Zone = t.Zone, Price = t.Price, TotalQuantity = t.TotalQuantity, AvailableQuantity = t.TotalQuantity, SeatMode = t.SeatMode, SeatRows = t.SeatRows, SeatsPerRow = t.SeatsPerRow };
            copy.TicketTypes.Add(nt);
        }
        db.Events.Add(copy);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "event.duplicate", "Event", copy.Id, $"từ {src.Id}");
        return Ok(new { copy.Id, message = "Đã nhân bản (bản nháp)" });
    }

    [HttpDelete("events/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var ev = await db.Events.Include(e => e.TicketTypes).FirstOrDefaultAsync(e => e.Id == id) ?? throw NotFoundEx("Sự kiện không tồn tại");
        if (await db.Bookings.AnyAsync(b => b.EventId == id))
            throw new AppException(409, "HAS_BOOKINGS", "Sự kiện đã phát sinh đơn hàng nên không thể xoá. Hãy chuyển về bản nháp để ẩn khỏi khách.");
        db.Events.Remove(ev);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "event.delete", "Event", id, ev.Name);
        return Ok(new { message = "Đã xoá sự kiện" });
    }

    // ---------- Loại vé ----------
    [HttpPost("events/{eventId:guid}/ticket-types")]
    public async Task<IActionResult> AddType(Guid eventId, TicketTypeUpsertDto dto)
    {
        ValidateType(dto);
        if (!await db.Events.AnyAsync(e => e.Id == eventId)) throw NotFoundEx("Sự kiện không tồn tại");
        var t = new TicketType
        {
            EventId = eventId, Name = dto.Name.Trim(), Zone = dto.Zone.Trim(), Price = dto.Price,
            TotalQuantity = dto.TotalQuantity, AvailableQuantity = dto.TotalQuantity,
            SeatMode = (SeatMode)dto.SeatMode, SeatRows = dto.SeatMode == 0 ? 0 : dto.SeatRows, SeatsPerRow = dto.SeatMode == 0 ? 0 : dto.SeatsPerRow
        };
        db.TicketTypes.Add(t);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "ticketType.create", "TicketType", t.Id, $"{t.Name}/{t.Zone} x{t.TotalQuantity} @ {t.Price:N0}");
        return Ok(new { t.Id, message = "Đã thêm loại vé" });
    }

    /// Đổi tổng số vé một cách nguyên tử: tồn kho được cộng/trừ đúng phần chênh, và không cho giảm dưới số vé đã bán + đang giữ.
    [HttpPut("ticket-types/{id:guid}")]
    public async Task<IActionResult> UpdateType(Guid id, TicketTypeUpsertDto dto)
    {
        ValidateType(dto);
        var name = dto.Name.Trim(); var zone = dto.Zone.Trim(); var total = dto.TotalQuantity; var price = dto.Price;
        var mode = (SeatMode)dto.SeatMode; var rows = mode == SeatMode.None ? 0 : dto.SeatRows; var perRow = mode == SeatMode.None ? 0 : dto.SeatsPerRow;

        // Sơ đồ ghế không đổi được khi loại vé đã có đơn (vé đã phát/đang giữ gắn với ghế cũ)
        var curType = await db.TicketTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) ?? throw NotFoundEx("Loại vé không tồn tại");
        if ((curType.SeatMode != mode || curType.SeatRows != rows || curType.SeatsPerRow != perRow) && await db.Bookings.AnyAsync(b => b.TicketTypeId == id))
            throw new AppException(409, "SEAT_LAYOUT_LOCKED", "Loại vé đã có đơn hàng nên không đổi được kiểu ghế / số hàng / số ghế mỗi hàng. Hãy tạo loại vé mới.");

        var n = await db.TicketTypes
            .Where(t => t.Id == id && t.TotalQuantity - t.AvailableQuantity <= total)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Name, name).SetProperty(t => t.Zone, zone).SetProperty(t => t.Price, price)
                .SetProperty(t => t.AvailableQuantity, t => t.AvailableQuantity + (total - t.TotalQuantity))
                .SetProperty(t => t.SeatMode, mode).SetProperty(t => t.SeatRows, rows).SetProperty(t => t.SeatsPerRow, perRow)
                .SetProperty(t => t.TotalQuantity, total));
        if (n == 0)
        {
            var cur = await db.TicketTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) ?? throw NotFoundEx("Loại vé không tồn tại");
            throw new AppException(409, "BELOW_SOLD", $"Không thể giảm xuống {total}: đã bán/đang giữ {cur.TotalQuantity - cur.AvailableQuantity} vé");
        }
        await audit.LogAsync(User, "ticketType.update", "TicketType", id, $"{name}/{zone} tổng {total} @ {price:N0}");
        return Ok(new { message = "Đã cập nhật loại vé" });
    }

    [HttpDelete("ticket-types/{id:guid}")]
    public async Task<IActionResult> DeleteType(Guid id)
    {
        var t = await db.TicketTypes.FirstOrDefaultAsync(x => x.Id == id) ?? throw NotFoundEx("Loại vé không tồn tại");
        if (await db.Bookings.AnyAsync(b => b.TicketTypeId == id))
            throw new AppException(409, "HAS_BOOKINGS", "Loại vé đã có đơn hàng nên không thể xoá. Hãy đặt số lượng bằng số đã bán để ngừng bán.");
        db.TicketTypes.Remove(t);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "ticketType.delete", "TicketType", id, t.Name);
        return Ok(new { message = "Đã xoá loại vé" });
    }

    // ---------- Helpers ----------
    private static AppException NotFoundEx(string msg) => new(404, "NOT_FOUND", msg);

    private static void Apply(Event ev, EventUpsertDto d)
    {
        ev.Name = d.Name.Trim();
        ev.Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim();
        ev.Venue = d.Venue.Trim();
        ev.StartsAt = d.StartsAt.Kind == DateTimeKind.Local ? d.StartsAt.ToUniversalTime() : DateTime.SpecifyKind(d.StartsAt, DateTimeKind.Utc);
        ev.PosterUrl = string.IsNullOrWhiteSpace(d.PosterUrl) ? null : d.PosterUrl.Trim();
        ev.SeatMapUrl = string.IsNullOrWhiteSpace(d.SeatMapUrl) ? null : d.SeatMapUrl.Trim();
        ev.IsPublished = d.IsPublished;
        // Không gửi Category (client cũ) thì giữ nguyên loại hiện có
        if (!string.IsNullOrWhiteSpace(d.Category)) ev.Category = Categories.First(x => x.Equals(d.Category.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static readonly string[] Categories = { "Concert", "Sport", "Theatre" };

    private static void Validate(EventUpsertDto d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 300) throw new AppException(400, "BAD_NAME", "Tên sự kiện bắt buộc, tối đa 300 ký tự");
        if (string.IsNullOrWhiteSpace(d.Venue) || d.Venue.Trim().Length > 300) throw new AppException(400, "BAD_VENUE", "Địa điểm bắt buộc, tối đa 300 ký tự");
        if (d.Description is { Length: > 2000 }) throw new AppException(400, "BAD_DESC", "Mô tả tối đa 2000 ký tự");
        if (!string.IsNullOrWhiteSpace(d.Category) && !Categories.Any(x => x.Equals(d.Category.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new AppException(400, "BAD_CATEGORY", "Loại sự kiện phải là Concert, Sport hoặc Theatre");
        if (!string.IsNullOrWhiteSpace(d.PosterUrl))
        {
            if (d.PosterUrl.Length > 1000 || !Uri.TryCreate(d.PosterUrl.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                throw new AppException(400, "BAD_POSTER", "Ảnh poster phải là đường dẫn http(s) hợp lệ");
        }
        if (!string.IsNullOrWhiteSpace(d.SeatMapUrl))
        {
            var u = d.SeatMapUrl.Trim();
            var ok = u.Length <= 1000 && (u.StartsWith("/img/", StringComparison.Ordinal)
                || (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")));
            if (!ok) throw new AppException(400, "BAD_SEATMAP", "Ảnh sơ đồ phải là link http(s):// hợp lệ (hoặc đường dẫn /img/...)");
        }
    }

    private static void ValidateType(TicketTypeUpsertDto d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 100) throw new AppException(400, "BAD_NAME", "Tên loại vé bắt buộc, tối đa 100 ký tự");
        if (string.IsNullOrWhiteSpace(d.Zone) || d.Zone.Trim().Length > 100) throw new AppException(400, "BAD_ZONE", "Khu vực bắt buộc, tối đa 100 ký tự");
        if (d.Price < 0 || d.Price > 1_000_000_000) throw new AppException(400, "BAD_PRICE", "Giá vé không hợp lệ");
        if (d.TotalQuantity < 0 || d.TotalQuantity > 1_000_000) throw new AppException(400, "BAD_QTY", "Số lượng vé từ 0 đến 1.000.000");
        if (!Enum.IsDefined(typeof(SeatMode), d.SeatMode)) throw new AppException(400, "BAD_SEAT_MODE", "Kiểu ghế không hợp lệ");
        SeatLayout.Validate((SeatMode)d.SeatMode, d.SeatRows, d.SeatsPerRow, d.TotalQuantity);
    }
}
