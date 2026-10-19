using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Auth;
using EventTicketing.Modules.Tickets;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Promo;

/// Chúc mừng khách khi lên VIP (admin gán tay hoặc tự lên nhờ chi tiêu): một thông báo trên chuông + một email, chỉ gửi một lần
/// (User.VipNotified). Lỗi ở đây không bao giờ được làm hỏng việc gọi nó (thanh toán, gán VIP...).
public class VipNotifier(AppDbContext db, VipService vip, IEmailService email, ILogger<VipNotifier> log)
{
    public static string Benefit(PromoSetting c) =>
        $"Giảm {c.VipDiscountPercent}% giá vé và đơn hàng lưu niệm" + (c.VipMaxDiscount is decimal m && m > 0 ? $", tối đa {m:N0} đ mỗi đơn" : "");

    public async Task TryNotifyAsync(Guid userId)
    {
        try
        {
            var cfg = await vip.GetSettingsAsync();
            if (!cfg.VipEnabled) return;                       // ưu đãi đang tắt: chưa chúc mừng, sẽ gửi khi bật và khách thực sự được hưởng
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null || user.VipNotified || user.Role != "User") return;
            var st = await vip.StatusAsync(userId, cfg);
            if (!st.IsVip) return;

            user.VipNotified = true;
            var benefit = Benefit(cfg);
            db.Notifications.Add(new Notification { UserId = userId, Title = "👑 Bạn đã là khách VIP", Message = benefit + ". Ưu đãi tự động trừ khi đặt hàng.", Link = "/Account/Profile" });
            await db.SaveChangesAsync();

            var to = user.Email; var html = EmailTemplates.VipWelcome(user.FullName, benefit, auto: !st.Manual);
            _ = Task.Run(async () =>   // gửi ở nền: SMTP chậm không được kéo dài thanh toán / thao tác của admin
            {
                try { await email.SendAsync(to, "👑 Chúc mừng! Bạn đã trở thành khách VIP của TicketGo", html); }
                catch (Exception ex) { log.LogWarning(ex, "Không gửi được email chúc mừng VIP tới {UserId}", userId); }
            });
        }
        catch (Exception ex) { log.LogWarning(ex, "Không gửi được chúc mừng VIP cho {UserId}", userId); }
    }

    /// Admin bỏ nhãn VIP: nếu khách không còn được hưởng VIP (kể cả theo chi tiêu) thì lần lên VIP sau sẽ chúc mừng lại.
    public async Task ResetIfNoLongerVipAsync(Guid userId)
    {
        try
        {
            var st = await vip.StatusAsync(userId);
            if (!st.IsVip) await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.VipNotified, false));
        }
        catch (Exception ex) { log.LogWarning(ex, "Không đặt lại cờ chúc mừng VIP cho {UserId}", userId); }
    }
}
