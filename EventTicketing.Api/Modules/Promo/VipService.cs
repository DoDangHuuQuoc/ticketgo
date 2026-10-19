using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Promo;

/// Trạng thái VIP của một khách. Manual = admin gán tay; Auto = tự lên VIP nhờ tổng chi tiêu đạt ngưỡng.
public record VipStatus(bool Enabled, bool IsVip, bool Manual, bool Auto, int Percent, decimal? MaxDiscount, decimal Spent, decimal AutoSpend);

/// Ưu đãi khách VIP: giảm % trên giá vé của mỗi đơn (cấu hình ở PromoSetting, admin chỉnh tại /Manage/Coupons).
public class VipService(AppDbContext db)
{
    public async Task<PromoSetting> GetSettingsAsync() =>
        await db.PromoSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1) ?? new PromoSetting();

    /// Tổng tiền các đơn vé đã thanh toán của khách.
    public async Task<decimal> SpentAsync(Guid userId) =>
        await db.Bookings.AsNoTracking().Where(b => b.UserId == userId && b.Status == BookingStatus.Paid)
            .SumAsync(b => (decimal?)b.TotalAmount) ?? 0m;

    public async Task<VipStatus> StatusAsync(Guid userId, PromoSetting? cfg = null)
    {
        cfg ??= await GetSettingsAsync();
        var manual = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsVip).FirstOrDefaultAsync();
        var spent = await SpentAsync(userId);
        var auto = cfg.VipAutoSpend > 0 && spent >= cfg.VipAutoSpend;
        return new VipStatus(cfg.VipEnabled, cfg.VipEnabled && (manual || auto), manual, auto, cfg.VipDiscountPercent, cfg.VipMaxDiscount, spent, cfg.VipAutoSpend);
    }

    /// Gọi TRƯỚC khi vào transaction giữ vé (để không kéo dài thời gian khoá dòng loại vé): trả cấu hình + khách có được hưởng ưu đãi VIP không.
    public async Task<(PromoSetting Cfg, bool IsVip)> ResolveAsync(Guid userId)
    {
        var cfg = await GetSettingsAsync();
        if (!cfg.VipEnabled || cfg.VipDiscountPercent <= 0) return (cfg, false);
        return (cfg, (await StatusAsync(userId, cfg)).IsVip);
    }

    /// Số tiền VIP được giảm trên tổng tiền vé (đã làm tròn đ, không vượt trần, luôn chừa lại minPayable).
    public static decimal Compute(PromoSetting cfg, decimal subtotal, decimal minPayable)
    {
        var d = decimal.Round(subtotal * cfg.VipDiscountPercent / 100m, 0, MidpointRounding.AwayFromZero);
        if (cfg.VipMaxDiscount is decimal md && md > 0 && d > md) d = md;
        return Math.Max(0m, Math.Min(d, Math.Max(0m, subtotal - minPayable)));
    }
}
