using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Modules.WaitingRoom;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Bookings;

public record ApplyCouponDto(string? Code);

[ApiController, Authorize, Route("api/bookings")]
public class BookingsController(BookingService svc, AppDbContext db, WaitingRoomService queue) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateBookingDto dto)
    {
        var uid = User.UserId();
        // Hàng chờ mua vé: khi quá đông, chỉ người đã tới lượt mới giữ vé được (admin chỉ được bỏ qua khi bật Queue:BypassAdmin)
        var eventId = queue.BypassAdmin && User.IsInRole("Admin") ? null
            : await db.TicketTypes.AsNoTracking().Where(t => t.Id == dto.TicketTypeId).Select(t => (Guid?)t.EventId).FirstOrDefaultAsync();
        if (eventId.HasValue) queue.RequireAdmitted(eventId.Value, uid);

        var result = await svc.CreateAsync(uid, dto);

        if (eventId.HasValue) queue.Release(eventId.Value, uid);   // đã giữ vé xong: trả suất cho người kế tiếp
        return Ok(result);
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = User.UserId();
        return Ok(await db.Bookings.AsNoTracking().Where(b => b.UserId == uid).OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                b.Id, b.Quantity, b.TotalAmount, b.Status, b.ExpiresAt, b.CreatedAt, b.EventId,
                EventName = b.TicketType!.Event!.Name, TypeName = b.TicketType.Name, b.TicketType.Zone
            })
            .ToListAsync());
    }

    // Chi tiết một đơn (chỉ chủ đơn) – dùng cho trang thanh toán và trang đơn hàng
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> One(Guid id)
    {
        var uid = User.UserId();
        var b = await db.Bookings.AsNoTracking().Where(x => x.Id == id && x.UserId == uid)
            .Select(x => new
            {
                x.Id, x.Quantity, x.TotalAmount, x.Status, x.ExpiresAt, x.CreatedAt, x.EventId, x.CouponCode, x.DiscountAmount, x.VipDiscount, x.Seats,
                EventName = x.TicketType!.Event!.Name, x.TicketType.Event.Venue, x.TicketType.Event.StartsAt, x.TicketType.Event.PosterUrl,
                TypeName = x.TicketType.Name, x.TicketType.Zone, UnitPrice = x.TicketType.Price
            })
            .FirstOrDefaultAsync();
        return b is null ? NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy đơn hàng" }) : Ok(b);
    }

    // Lưu / gỡ mã giảm giá cho đơn đang chờ thanh toán (trang thanh toán, trước khi hiện QR)
    [HttpPut("{id:guid}/coupon")]
    public async Task<IActionResult> ApplyCoupon(Guid id, ApplyCouponDto dto) => Ok(await svc.ApplyCouponAsync(User.UserId(), id, dto.Code));

    [HttpDelete("{id:guid}/coupon")]
    public async Task<IActionResult> RemoveCoupon(Guid id) => Ok(await svc.RemoveCouponAsync(User.UserId(), id));
}
