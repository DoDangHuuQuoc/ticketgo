using System.Net.Mail;
using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Admin;

public record SettingsDto(string? Hotline, string? ContactEmail, bool BannerEnabled, string? BannerText, string? BannerLink, string? BannerTone, bool ChatbotEnabled);

/// Cài đặt chung: thông tin liên hệ, banner thông báo trang chủ, bật/tắt chatbot. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/site/settings")]
public class AdminSettingsController(AppDbContext db, IMemoryCache cache, IConfiguration cfg, AuditService audit) : ControllerBase
{
    private static readonly Regex PhoneRx = new(@"^[0-9 .+\-()]{6,40}$", RegexOptions.Compiled);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
        var p = await SiteSettingsStore.GetPublicAsync(db, cache, cfg);
        return Ok(new
        {
            hotline = s?.Hotline ?? "", contactEmail = s?.ContactEmail ?? "", bannerEnabled = s?.BannerEnabled ?? false,
            bannerText = s?.BannerText ?? "", bannerLink = s?.BannerLink ?? "", bannerTone = s?.BannerTone ?? "info", chatbotEnabled = s?.ChatbotEnabled ?? true,
            defaultHotline = cfg["Chatbot:Hotline"] ?? "1900 6408", defaultEmail = cfg["Chatbot:Email"] ?? "hotro@ticketgo.vn", effective = p, updatedAt = s?.UpdatedAt
        });
    }

    [HttpPut]
    public async Task<IActionResult> Set(SettingsDto dto)
    {
        var hotline = (dto.Hotline ?? "").Trim(); var email = (dto.ContactEmail ?? "").Trim();
        var text = (dto.BannerText ?? "").Trim(); var link = (dto.BannerLink ?? "").Trim(); var tone = (dto.BannerTone ?? "info").Trim();
        if (hotline.Length > 0 && !PhoneRx.IsMatch(hotline)) throw new AppException(400, "BAD_HOTLINE", "Hotline chỉ gồm số, dấu cách và + - . ( ), từ 6 đến 40 ký tự");
        if (email.Length > 0 && (email.Length > 256 || !MailAddress.TryCreate(email, out var a) || a.Address != email)) throw new AppException(400, "BAD_EMAIL", "Email liên hệ chưa hợp lệ");
        if (text.Length > 200) throw new AppException(400, "BAD_BANNER", "Nội dung banner tối đa 200 ký tự");
        if (dto.BannerEnabled && text.Length == 0) throw new AppException(400, "BAD_BANNER", "Hãy nhập nội dung banner trước khi bật");
        if (link.Length > 300) throw new AppException(400, "BAD_LINK", "Liên kết tối đa 300 ký tự");
        // Chỉ cho phép link nội bộ (/...) hoặc http(s) — chặn javascript: và các scheme nguy hiểm khác
        if (link.Length > 0 && (!link.StartsWith('/') || link.StartsWith("//")) && !(link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            throw new AppException(400, "BAD_LINK", "Liên kết phải bắt đầu bằng / hoặc https://");
        if (tone is not ("info" or "warn" or "promo")) throw new AppException(400, "BAD_TONE", "Kiểu banner không hợp lệ");

        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null) { s = new SiteSetting { Id = 1 }; db.SiteSettings.Add(s); }
        s.Hotline = hotline; s.ContactEmail = email; s.BannerEnabled = dto.BannerEnabled; s.BannerText = text; s.BannerLink = link; s.BannerTone = tone;
        s.ChatbotEnabled = dto.ChatbotEnabled; s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        SiteSettingsStore.InvalidatePublic(cache);
        await audit.LogAsync(User, "site.settings", "Site", 1, $"banner={(dto.BannerEnabled ? "bật" : "tắt")}, chatbot={(dto.ChatbotEnabled ? "bật" : "tắt")}");
        return Ok(new { message = "Đã lưu cài đặt" });
    }
}
