using System.Security.Claims;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Bookings;
using EventTicketing.Modules.Promo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Coupons;

public record CouponCheckDto(string? Code, Guid TicketTypeId, int Quantity);

/// Khách bấm "Áp dụng" ở trang mua vé: chỉ tính thử, chưa trừ lượt. Lượt chỉ được giữ khi tạo đơn.
[ApiController, Authorize, Route("api/coupons")]
public class CouponsController(AppDbContext db, CouponService svc, VipService vip) : ControllerBase
{
    [HttpPost("validate")]
    public async Task<IActionResult> Validate(CouponCheckDto dto)
    {
        if (dto.Quantity < 1 || dto.Quantity > BookingService.MaxPerBooking)
            throw new AppException(400, "BAD_QUANTITY", $"Số lượng từ 1 đến {BookingService.MaxPerBooking}");
        var code = CouponService.Normalize(dto.Code);
        if (code.Length == 0) throw new AppException(400, "COUPON_EMPTY", "Hãy nhập mã giảm giá");

        var tt = await db.TicketTypes.AsNoTracking().Where(t => t.Id == dto.TicketTypeId)
            .Select(t => new { t.EventId, t.Price }).FirstOrDefaultAsync()
            ?? throw new AppException(404, "TICKET_TYPE_NOT_FOUND", "Loại vé không tồn tại");

        var uid = User.UserId();
        var coupon = await svc.FindAsync(code);
        var subtotal = tt.Price * dto.Quantity;
        var (promo, isVip) = await vip.ResolveAsync(uid);
        var vipDiscount = isVip ? VipService.Compute(promo, subtotal, CouponService.MinPayable) : 0m;
        var discount = await svc.EvaluateAsync(coupon, uid, tt.EventId, subtotal, null, vipDiscount);
        return Ok(new { code = coupon!.Code, description = coupon.Description, subtotal, vipDiscount, discount, total = subtotal - vipDiscount - discount });
    }

    /// Mục "Mã giảm giá đang có" ở trang chủ: chỉ mã đang bật, được đánh dấu công khai, trong thời hạn và còn lượt.
    [HttpGet("public"), AllowAnonymous]
    public async Task<IActionResult> Public()
    {
        var now = DateTime.UtcNow;
        var rows = await db.Coupons.AsNoTracking()
            .Where(c => c.IsActive && c.IsPublic && (c.StartsAt == null || c.StartsAt <= now) && (c.EndsAt == null || c.EndsAt > now))
            .Select(c => new
            {
                c.Id, c.Code, c.Description, c.Type, c.Value, c.MaxDiscount, c.MinOrder, c.MaxUses, c.EndsAt, c.EventId, c.AppliesToShop,
                EventName = c.EventId == null ? null : db.Events.Where(e => e.Id == c.EventId).Select(e => e.Name).FirstOrDefault(),
                Live = db.Bookings.Count(b => b.CouponId == c.Id && (b.Status == BookingStatus.Paid || (b.Status == BookingStatus.Pending && b.ExpiresAt > now)))
                     + db.ShopOrders.Count(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled)
            }).ToListAsync();
        // Khách đã đăng nhập (token gửi kèm) thì đánh dấu mã nào đã lưu; khách vãng lai thì saved = false
        var savedIds = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
            ? await db.UserCoupons.AsNoTracking().Where(x => x.UserId == uid).Select(x => x.CouponId).ToListAsync()
            : new List<Guid>();
        return Ok(rows.Where(c => c.MaxUses == null || c.Live < c.MaxUses)
            .OrderBy(c => c.EndsAt ?? DateTime.MaxValue)
            .Select(c => new
            {
                c.Code, c.Description, type = c.Type.ToString(), c.Value, c.MaxDiscount, c.MinOrder, c.EndsAt, c.EventId, c.EventName, c.AppliesToShop,
                saved = savedIds.Contains(c.Id),
                left = c.MaxUses == null ? (int?)null : c.MaxUses - c.Live
            }));
    }
    /// Lưu voucher vào ví của khách. Idempotent: lưu lần nữa vẫn trả OK.
    [HttpPost("{code}/save")]
    public async Task<IActionResult> Save(string code)
    {
        var uid = User.UserId();
        var c = await svc.FindAsync(CouponService.Normalize(code));
        if (c is null || !c.IsActive || (c.EndsAt is DateTime e && e < DateTime.UtcNow) || (c.OwnerUserId is Guid owner && owner != uid))
            throw new AppException(404, "COUPON_INVALID", "Mã giảm giá không tồn tại hoặc đã hết hạn");

        if (!await db.UserCoupons.AnyAsync(x => x.UserId == uid && x.CouponId == c.Id))
        {
            db.UserCoupons.Add(new UserCoupon { UserId = uid, CouponId = c.Id });
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { /* bấm lưu hai lần cùng lúc: bản ghi đã có nhờ chỉ mục duy nhất */ }
        }
        return Ok(new { message = $"Đã lưu voucher {c.Code}", code = c.Code });
    }

    [HttpDelete("saved/{code}")]
    public async Task<IActionResult> Unsave(string code)
    {
        var uid = User.UserId();
        var c = await svc.FindAsync(CouponService.Normalize(code));
        if (c is not null) await db.UserCoupons.Where(x => x.UserId == uid && x.CouponId == c.Id).ExecuteDeleteAsync();
        return Ok(new { message = "Đã bỏ lưu voucher" });
    }

    /// Ví voucher của tôi. status: ok (dùng được) | expired | soon (chưa tới ngày) | off (admin đã tắt) | out (hết lượt chung) | used (bạn dùng hết lượt).
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = User.UserId();
        var now = DateTime.UtcNow;
        var rows = await (from s in db.UserCoupons.AsNoTracking()
                          where s.UserId == uid
                          join c in db.Coupons.AsNoTracking() on s.CouponId equals c.Id
                          orderby s.SavedAt descending
                          select new { Coupon = c, s.SavedAt }).ToListAsync();
        var ids = rows.Select(r => r.Coupon.Id).ToList();
        var live = await db.Bookings.AsNoTracking()
            .Where(b => b.CouponId != null && ids.Contains(b.CouponId.Value)
                && (b.Status == BookingStatus.Paid || (b.Status == BookingStatus.Pending && b.ExpiresAt > now)))
            .Select(b => new { CouponId = b.CouponId!.Value, b.UserId }).ToListAsync();
        live.AddRange(await db.ShopOrders.AsNoTracking()
            .Where(o => o.CouponId != null && ids.Contains(o.CouponId.Value) && o.Status != ShopOrderStatus.Cancelled)
            .Select(o => new { CouponId = o.CouponId!.Value, o.UserId }).ToListAsync());
        var evIds = rows.Where(r => r.Coupon.EventId != null).Select(r => r.Coupon.EventId!.Value).Distinct().ToList();
        var evNames = await db.Events.AsNoTracking().Where(e => evIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name);

        return Ok(rows.Select(r =>
        {
            var c = r.Coupon;
            var all = live.Count(x => x.CouponId == c.Id);
            var mine = live.Count(x => x.CouponId == c.Id && x.UserId == uid);
            var status = !c.IsActive ? "off"
                : c.EndsAt is DateTime e && e < now ? "expired"
                : c.StartsAt is DateTime st && st > now ? "soon"
                : c.MaxUses is int max && all >= max ? "out"
                : mine >= Math.Max(1, c.PerUserLimit) ? "used" : "ok";
            return new
            {
                c.Code, c.Description, type = c.Type.ToString(), c.Value, c.MaxDiscount, c.MinOrder, c.StartsAt, c.EndsAt, c.EventId, c.AppliesToShop,
                EventName = c.EventId is Guid g && evNames.TryGetValue(g, out var n) ? n : null,
                status, r.SavedAt
            };
        }));
    }
}
