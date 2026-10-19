using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record PromoSettingsDto(
    bool VipEnabled, int VipDiscountPercent, decimal? VipMaxDiscount, decimal VipAutoSpend,
    bool WelcomeEnabled, string? WelcomeType, decimal WelcomeValue, decimal? WelcomeMaxDiscount, decimal WelcomeMinOrder, int WelcomeValidDays, bool WelcomeAppliesToShop);
public record IssueWelcomeDto(string? Email);
public record VipFlagDto(bool IsVip);

/// Ưu đãi thành viên do admin quản lý: (1) giảm giá cho khách VIP, (2) mã chào mừng tự cấp khi tạo tài khoản thành công. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/promo")]
public class AdminPromoController(AppDbContext db, AuditService audit, WelcomeCouponService welcome) : ControllerBase
{
    // ---------- Cấu hình ----------
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var s = await db.PromoSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1) ?? new PromoSetting();
        var vipCount = await db.Users.CountAsync(u => u.IsVip);
        return Ok(new
        {
            s.VipEnabled, s.VipDiscountPercent, s.VipMaxDiscount, s.VipAutoSpend,
            s.WelcomeEnabled, WelcomeType = s.WelcomeType.ToString(), s.WelcomeValue, s.WelcomeMaxDiscount, s.WelcomeMinOrder, s.WelcomeValidDays, s.WelcomeAppliesToShop,
            vipCount
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SetSettings(PromoSettingsDto d)
    {
        if (d.VipDiscountPercent < 1 || d.VipDiscountPercent > 50) throw new AppException(400, "BAD_VIP_PERCENT", "Ưu đãi VIP giảm từ 1% đến 50%");
        if (d.VipMaxDiscount is decimal vm && (vm < 1000 || vm > 100_000_000)) throw new AppException(400, "BAD_VIP_MAX", "Mức giảm VIP tối đa mỗi đơn từ 1.000 đ đến 100.000.000 đ (để trống = không giới hạn)");
        if (d.VipAutoSpend < 0 || d.VipAutoSpend > 10_000_000_000m) throw new AppException(400, "BAD_VIP_SPEND", "Ngưỡng chi tiêu tự lên VIP không hợp lệ (0 = tắt)");

        if (!Enum.TryParse<CouponType>(d.WelcomeType, true, out var type)) throw new AppException(400, "BAD_TYPE", "Loại giảm giá không hợp lệ");
        if (type == CouponType.Percent && (d.WelcomeValue < 1 || d.WelcomeValue > 100)) throw new AppException(400, "BAD_VALUE", "Giảm theo % phải từ 1 đến 100");
        if (type == CouponType.Amount && (d.WelcomeValue < 1000 || d.WelcomeValue > 100_000_000)) throw new AppException(400, "BAD_VALUE", "Số tiền giảm từ 1.000 đ đến 100.000.000 đ");
        if (d.WelcomeMaxDiscount is < 0) throw new AppException(400, "BAD_MAX", "Mức giảm tối đa không được âm");
        if (d.WelcomeMinOrder < 0) throw new AppException(400, "BAD_MIN", "Đơn tối thiểu không được âm");
        if (d.WelcomeValidDays < 1 || d.WelcomeValidDays > 365) throw new AppException(400, "BAD_DAYS", "Hạn dùng mã chào mừng từ 1 đến 365 ngày");

        var s = await db.PromoSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null) { s = new PromoSetting { Id = 1 }; db.PromoSettings.Add(s); }
        s.VipEnabled = d.VipEnabled; s.VipDiscountPercent = d.VipDiscountPercent; s.VipMaxDiscount = d.VipMaxDiscount; s.VipAutoSpend = d.VipAutoSpend;
        s.WelcomeEnabled = d.WelcomeEnabled; s.WelcomeType = type; s.WelcomeValue = d.WelcomeValue;
        s.WelcomeMaxDiscount = type == CouponType.Percent && d.WelcomeMaxDiscount is > 0 ? d.WelcomeMaxDiscount : null;
        s.WelcomeMinOrder = d.WelcomeMinOrder; s.WelcomeValidDays = d.WelcomeValidDays; s.WelcomeAppliesToShop = d.WelcomeAppliesToShop;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "promo.settings", "Promo", 1,
            $"VIP {(d.VipEnabled ? "bật" : "tắt")} {d.VipDiscountPercent}%, chào mừng {(d.WelcomeEnabled ? "bật" : "tắt")} ({type} {d.WelcomeValue:0.##}, {d.WelcomeValidDays} ngày)");
        return Ok(new { message = "Đã lưu cấu hình ưu đãi. Mã chào mừng đã cấp trước đó giữ nguyên điều kiện cũ." });
    }

    // ---------- Mã chào mừng đã cấp ----------
    [HttpGet("welcome")]
    public async Task<IActionResult> ListWelcome([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int size = 15)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var now = DateTime.UtcNow;
        var all = db.Coupons.AsNoTracking().Where(c => c.Source == WelcomeCouponService.SourceName);
        var issued = await all.CountAsync();
        var used = await all.CountAsync(c => db.Bookings.Any(b => b.CouponId == c.Id && b.Status == BookingStatus.Paid)
                                          || db.ShopOrders.Any(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled));

        var query = all;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim();
            query = query.Where(c => c.Code.Contains(k) || db.Users.Any(u => u.Id == c.OwnerUserId && (u.Email.Contains(k) || u.FullName.Contains(k))));
        }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(c => c.CreatedAt).Skip((page - 1) * size).Take(size).Select(c => new
        {
            c.Id, c.Code, c.Type, c.Value, c.MaxDiscount, c.MinOrder, c.EndsAt, c.IsActive, c.CreatedAt,
            ownerEmail = db.Users.Where(u => u.Id == c.OwnerUserId).Select(u => u.Email).FirstOrDefault(),
            ownerName = db.Users.Where(u => u.Id == c.OwnerUserId).Select(u => u.FullName).FirstOrDefault(),
            used = db.Bookings.Count(b => b.CouponId == c.Id && b.Status == BookingStatus.Paid) + db.ShopOrders.Count(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled),
            held = db.Bookings.Count(b => b.CouponId == c.Id && b.Status == BookingStatus.Pending && b.ExpiresAt > now),
            saved = (db.Bookings.Where(b => b.CouponId == c.Id && b.Status == BookingStatus.Paid).Sum(b => (decimal?)b.DiscountAmount) ?? 0)
                  + (db.ShopOrders.Where(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled).Sum(o => (decimal?)o.DiscountAmount) ?? 0)
        }).ToListAsync();
        return Ok(new { items, total, page, size, issued, used });
    }

    /// Cấp mã chào mừng cho một tài khoản có sẵn (vd. khách đăng ký trước khi bật tính năng). Mỗi khách chỉ có một mã chào mừng.
    [HttpPost("welcome/issue")]
    public async Task<IActionResult> Issue(IssueWelcomeDto dto)
    {
        var mail = (dto.Email ?? "").Trim().ToLowerInvariant();
        if (mail.Length == 0) throw new AppException(400, "BAD_EMAIL", "Hãy nhập email khách");
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == mail) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy tài khoản với email này");
        if (u.Role != "User") throw new AppException(400, "NOT_CUSTOMER", "Chỉ cấp mã chào mừng cho tài khoản khách");
        if (!u.IsVerified) throw new AppException(409, "NOT_VERIFIED", "Tài khoản chưa xác thực email. Mã sẽ tự cấp khi khách xác thực xong.");

        var had = await db.Coupons.AsNoTracking().AnyAsync(x => x.OwnerUserId == u.Id && x.Source == WelcomeCouponService.SourceName);
        var coupon = await welcome.IssueAsync(u.Id, true) ?? throw new AppException(500, "ISSUE_FAILED", "Không tạo được mã, hãy thử lại");
        if (had) throw new AppException(409, "ALREADY", $"Khách này đã có mã chào mừng {coupon.Code}");
        await audit.LogAsync(User, "promo.welcome-issue", "Coupon", coupon.Id, $"{coupon.Code} → {mail}");
        return Ok(new { coupon.Id, coupon.Code, message = $"Đã cấp mã {coupon.Code} cho {mail}" });
    }

    [HttpPut("welcome/{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, CouponActiveDto dto)
    {
        var n = await db.Coupons.Where(x => x.Id == id && x.Source == WelcomeCouponService.SourceName).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, dto.IsActive));
        if (n == 0) throw new AppException(404, "NOT_FOUND", "Không tìm thấy mã");
        await audit.LogAsync(User, dto.IsActive ? "coupon.enable" : "coupon.disable", "Coupon", id, "mã chào mừng");
        return Ok(new { message = dto.IsActive ? "Đã bật lại mã" : "Đã thu hồi mã (khách không dùng được nữa)" });
    }

    [HttpDelete("welcome/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var c = await db.Coupons.FirstOrDefaultAsync(x => x.Id == id && x.Source == WelcomeCouponService.SourceName) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy mã");
        if (await db.Bookings.AnyAsync(b => b.CouponId == id) || await db.ShopOrders.AnyAsync(o => o.CouponId == id))
            throw new AppException(409, "IN_USE", "Mã đã được dùng trong đơn hàng nên không xoá được. Hãy thu hồi (tắt) mã.");
        db.UserCoupons.RemoveRange(db.UserCoupons.Where(u => u.CouponId == id));
        db.Coupons.Remove(c);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "coupon.delete", "Coupon", id, c.Code);
        return Ok(new { message = "Đã xoá mã" });
    }
}
