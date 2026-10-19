using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Admin;

public record MaintenanceState(bool Enabled, string Message, DateTime? UpdatedAt);
public record SitePublic(string Hotline, string Email, bool BannerEnabled, string BannerText, string BannerLink, string BannerTone, bool ChatbotEnabled);

/// Đọc/ghi chế độ bảo trì. Có cache ngắn để middleware không phải hỏi DB ở mỗi request.
public static class SiteSettingsStore
{
    public const string DefaultMessage = "Hệ thống đang được bảo trì để nâng cấp. Vui lòng quay lại sau ít phút.";
    private const string CacheKey = "site:maintenance";

    public static async Task<MaintenanceState> GetMaintenanceAsync(AppDbContext db, IMemoryCache cache)
    {
        if (cache.TryGetValue(CacheKey, out MaintenanceState? hit) && hit is not null) return hit;
        MaintenanceState st;
        try
        {
            var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
            st = s is null ? new(false, DefaultMessage, null)
                           : new(s.MaintenanceEnabled, string.IsNullOrWhiteSpace(s.MaintenanceMessage) ? DefaultMessage : s.MaintenanceMessage, s.UpdatedAt);
        }
        catch
        {
            // Bảng chưa tồn tại / DB lỗi: coi như không bảo trì để không chặn nhầm cả site
            st = new(false, DefaultMessage, null);
        }
        cache.Set(CacheKey, st, TimeSpan.FromSeconds(5));
        return st;
    }

    public static async Task<MaintenanceState> SetMaintenanceAsync(AppDbContext db, IMemoryCache cache, bool enabled, string message)
    {
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null) { s = new SiteSetting { Id = 1 }; db.SiteSettings.Add(s); }
        s.MaintenanceEnabled = enabled;
        s.MaintenanceMessage = message;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        cache.Remove(CacheKey);
        return new(s.MaintenanceEnabled, s.MaintenanceMessage, s.UpdatedAt);
    }

    private const string PublicKey = "site:public";

    /// Thông tin hiển thị cho khách (liên hệ, banner, bật/tắt chatbot). Cache 15 giây; lỗi DB thì dùng mặc định.
    public static async Task<SitePublic> GetPublicAsync(AppDbContext db, IMemoryCache cache, IConfiguration cfg)
    {
        if (cache.TryGetValue(PublicKey, out SitePublic? hit) && hit is not null) return hit;
        var hotline = cfg["Chatbot:Hotline"] ?? "1900 6408";
        var email = cfg["Chatbot:Email"] ?? "hotro@ticketgo.vn";
        SitePublic st;
        try
        {
            var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
            st = s is null ? new(hotline, email, false, "", "", "info", true)
                : new(string.IsNullOrWhiteSpace(s.Hotline) ? hotline : s.Hotline, string.IsNullOrWhiteSpace(s.ContactEmail) ? email : s.ContactEmail,
                      s.BannerEnabled, s.BannerText ?? "", s.BannerLink ?? "", s.BannerTone ?? "info", s.ChatbotEnabled);
        }
        catch { st = new(hotline, email, false, "", "", "info", true); }
        cache.Set(PublicKey, st, TimeSpan.FromSeconds(15));
        return st;
    }

    public static void InvalidatePublic(IMemoryCache cache) => cache.Remove(PublicKey);
}
