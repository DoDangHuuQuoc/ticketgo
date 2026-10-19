using System.Security.Cryptography;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Auth;
using EventTicketing.Modules.Tickets;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Promo;

/// Tự cấp mã chào mừng (mã cá nhân, dùng 1 lần) cho khách khi tạo tài khoản thành công (xác thực email xong).
/// Mức giảm, hạn dùng... do admin cấu hình ở /Manage/Coupons; mã đã cấp được quản lý (tắt / xoá / cấp thêm) tại đó.
public class WelcomeCouponService(AppDbContext db, IEmailService email, ILogger<WelcomeCouponService> log)
{
    public const string SourceName = "Welcome";
    public const string Prefix = "WELCOME-";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // bỏ I, O, 0, 1 cho khỏi nhầm

    public static string OffText(PromoSetting c) => c.WelcomeType == CouponType.Percent
        ? $"Giảm {c.WelcomeValue:0.##}%" + (c.WelcomeMaxDiscount is decimal m && m > 0 ? $" (tối đa {m:N0} đ)" : "")
        : $"Giảm {c.WelcomeValue:N0} đ";

    /// Dùng ở luồng đăng ký/xác thực: lỗi cấp mã không bao giờ được làm hỏng việc tạo tài khoản.
    public async Task TryIssueAsync(Guid userId)
    {
        try { await IssueAsync(userId, false); }
        catch (Exception ex) { log.LogWarning(ex, "Không cấp được mã chào mừng cho tài khoản {UserId}", userId); }
    }

    /// Cấp mã cho khách (mỗi khách chỉ một mã chào mừng; gọi lại trả mã đã có). ignoreSwitch = admin cấp tay kể cả khi tính năng đang tắt.
    public async Task<Coupon?> IssueAsync(Guid userId, bool ignoreSwitch)
    {
        var cfg = await db.PromoSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1) ?? new PromoSetting();
        if (!cfg.WelcomeEnabled && !ignoreSwitch) return null;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.Role != "User") return null;

        var existing = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Source == SourceName);
        if (existing is not null) return existing;

        string? code = null;
        for (var i = 0; i < 6 && code is null; i++)
        {
            var tryCode = Prefix + new string(Enumerable.Range(0, 6).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            if (!await db.Coupons.AnyAsync(c => c.Code == tryCode)) code = tryCode;
        }
        if (code is null) return null;

        var now = DateTime.UtcNow;
        var days = Math.Clamp(cfg.WelcomeValidDays, 1, 365);
        var coupon = new Coupon
        {
            Code = code, Description = "Quà chào mừng thành viên mới", Type = cfg.WelcomeType, Value = cfg.WelcomeValue,
            MaxDiscount = cfg.WelcomeType == CouponType.Percent ? cfg.WelcomeMaxDiscount : null,
            MinOrder = cfg.WelcomeMinOrder, MaxUses = 1, PerUserLimit = 1, StartsAt = null, EndsAt = now.AddDays(days),
            IsActive = true, AppliesToShop = cfg.WelcomeAppliesToShop, IsPublic = false, EventId = null,
            OwnerUserId = userId, Source = SourceName
        };
        db.Coupons.Add(coupon);
        db.UserCoupons.Add(new UserCoupon { UserId = userId, CouponId = coupon.Id });   // vào thẳng Ví voucher của khách
        db.Notifications.Add(new Notification
        {
            UserId = userId, Title = "🎁 Quà chào mừng dành cho bạn",
            Message = $"Mã {code}: {OffText(cfg)}, dùng được đến {coupon.EndsAt:dd/MM/yyyy}. Mã đã nằm trong Ví voucher của bạn.",
            Link = "/Promotions"
        });
        await db.SaveChangesAsync();

        // Gửi email ở nền (SMTP chậm/bị chặn không được làm khách chờ lâu ở bước xác thực). Mã đã ở trong ví nên email lỗi cũng không sao.
        var to = user.Email;
        var html = EmailTemplates.WelcomeCoupon(user.FullName, code, OffText(cfg),
            (cfg.WelcomeMinOrder > 0 ? $"Áp dụng cho đơn từ {cfg.WelcomeMinOrder:N0} đ. " : "") + $"Dùng được 1 lần, hạn đến {coupon.EndsAt:dd/MM/yyyy}.");
        _ = Task.Run(async () =>
        {
            try { await email.SendAsync(to, "🎁 Quà chào mừng từ TicketGo dành cho bạn", html); }
            catch (Exception ex) { log.LogWarning(ex, "Không gửi được email mã chào mừng tới {UserId}", userId); }
        });
        return coupon;
    }
}
