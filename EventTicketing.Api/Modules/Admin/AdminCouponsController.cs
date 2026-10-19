using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Coupons;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record CouponInput(string? Code, string? Description, string? Type, decimal Value, decimal? MaxDiscount, decimal MinOrder,
    int? MaxUses, int PerUserLimit, DateTime? StartsAt, DateTime? EndsAt, bool IsActive, Guid? EventId, bool IsPublic = true, bool AppliesToShop = false);
public record CouponActiveDto(bool IsActive);

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/coupons")]
public class AdminCouponsController(AppDbContext db, AuditService audit) : ControllerBase
{
    private static readonly Regex CodeRx = new("^[A-Z0-9_-]{3,40}$", RegexOptions.Compiled);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var now = DateTime.UtcNow;
        var items = await db.Coupons.AsNoTracking().Where(c => c.OwnerUserId == null).OrderByDescending(c => c.CreatedAt).Select(c => new
        {
            c.Id, c.Code, c.Description, c.Type, c.Value, c.MaxDiscount, c.MinOrder, c.MaxUses, c.PerUserLimit,
            c.StartsAt, c.EndsAt, c.IsActive, c.IsPublic, c.EventId, c.AppliesToShop,
            eventName = c.EventId == null ? null : db.Events.Where(e => e.Id == c.EventId).Select(e => e.Name).FirstOrDefault(),
            used = db.Bookings.Count(b => b.CouponId == c.Id && b.Status == BookingStatus.Paid) + db.ShopOrders.Count(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled),
            held = db.Bookings.Count(b => b.CouponId == c.Id && b.Status == BookingStatus.Pending && b.ExpiresAt > now),
            saved = (db.Bookings.Where(b => b.CouponId == c.Id && b.Status == BookingStatus.Paid).Sum(b => (decimal?)b.DiscountAmount) ?? 0)
                  + (db.ShopOrders.Where(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled).Sum(o => (decimal?)o.DiscountAmount) ?? 0)
        }).ToListAsync();
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CouponInput dto)
    {
        var c = new Coupon();
        await Apply(c, dto);
        if (await db.Coupons.AnyAsync(x => x.Code == c.Code)) throw new AppException(409, "DUP_CODE", "Mã này đã tồn tại");
        db.Coupons.Add(c);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "coupon.create", "Coupon", c.Id, c.Code);
        return Ok(new { c.Id, message = "Đã tạo mã giảm giá" });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, CouponInput dto)
    {
        var c = await db.Coupons.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy mã");
        await Apply(c, dto);
        if (await db.Coupons.AnyAsync(x => x.Code == c.Code && x.Id != id)) throw new AppException(409, "DUP_CODE", "Mã này đã tồn tại");
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "coupon.update", "Coupon", c.Id, c.Code);
        return Ok(new { message = "Đã lưu mã giảm giá" });
    }

    [HttpPut("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, CouponActiveDto dto)
    {
        var n = await db.Coupons.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, dto.IsActive));
        if (n == 0) throw new AppException(404, "NOT_FOUND", "Không tìm thấy mã");
        await audit.LogAsync(User, dto.IsActive ? "coupon.enable" : "coupon.disable", "Coupon", id);
        return Ok(new { message = dto.IsActive ? "Đã bật mã" : "Đã tắt mã" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var c = await db.Coupons.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy mã");
        if (await db.Bookings.AnyAsync(b => b.CouponId == id) || await db.ShopOrders.AnyAsync(o => o.CouponId == id))
            throw new AppException(409, "IN_USE", "Mã đã được dùng trong đơn hàng nên không xoá được. Hãy tắt mã thay vì xoá.");
        db.UserCoupons.RemoveRange(db.UserCoupons.Where(u => u.CouponId == id));   // dọn khỏi ví của khách
        db.Coupons.Remove(c);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "coupon.delete", "Coupon", id, c.Code);
        return Ok(new { message = "Đã xoá mã" });
    }

    private async Task Apply(Coupon c, CouponInput d)
    {
        var code = CouponService.Normalize(d.Code);
        if (!CodeRx.IsMatch(code)) throw new AppException(400, "BAD_CODE", "Mã gồm 3–40 ký tự: chữ A–Z, số, dấu - hoặc _ (không dấu, không khoảng trắng)");
        if (!Enum.TryParse<CouponType>(d.Type, true, out var type)) throw new AppException(400, "BAD_TYPE", "Loại giảm giá không hợp lệ");
        if (type == CouponType.Percent && (d.Value < 1 || d.Value > 100)) throw new AppException(400, "BAD_VALUE", "Giảm theo % phải từ 1 đến 100");
        if (type == CouponType.Amount && (d.Value < 1000 || d.Value > 100_000_000)) throw new AppException(400, "BAD_VALUE", "Số tiền giảm từ 1.000 đ đến 100.000.000 đ");
        if (d.MaxDiscount is < 0) throw new AppException(400, "BAD_MAX", "Mức giảm tối đa không được âm");
        if (d.MinOrder < 0) throw new AppException(400, "BAD_MIN", "Đơn tối thiểu không được âm");
        if (d.MaxUses is < 1) throw new AppException(400, "BAD_USES", "Số lượt dùng tối đa phải từ 1 trở lên");
        if (d.PerUserLimit < 1 || d.PerUserLimit > 100) throw new AppException(400, "BAD_LIMIT", "Số lần mỗi khách được dùng từ 1 đến 100");
        if (d.StartsAt is DateTime s && d.EndsAt is DateTime e && e <= s) throw new AppException(400, "BAD_RANGE", "Ngày kết thúc phải sau ngày bắt đầu");
        var desc = (d.Description ?? "").Trim();
        if (desc.Length > 200) throw new AppException(400, "DESC_LONG", "Mô tả tối đa 200 ký tự");
        if (d.EventId is Guid ev && !await db.Events.AnyAsync(x => x.Id == ev)) throw new AppException(400, "BAD_EVENT", "Sự kiện không tồn tại");

        c.Code = code; c.Description = desc.Length == 0 ? null : desc; c.Type = type; c.Value = d.Value;
        c.MaxDiscount = type == CouponType.Percent ? d.MaxDiscount : null;
        c.MinOrder = d.MinOrder; c.MaxUses = d.MaxUses; c.PerUserLimit = d.PerUserLimit;
        c.StartsAt = d.StartsAt; c.EndsAt = d.EndsAt; c.IsActive = d.IsActive; c.IsPublic = d.IsPublic; c.EventId = d.EventId;
        c.AppliesToShop = d.AppliesToShop && d.EventId is null;   // mã gắn với một sự kiện thì không dùng cho đơn lưu niệm
    }
}
