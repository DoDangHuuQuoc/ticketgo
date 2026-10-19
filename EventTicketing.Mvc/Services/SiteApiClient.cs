using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Mvc.Services;

/// Thông tin hiển thị cho khách (liên hệ, banner, bật/tắt chatbot) do API trả về ở GET api/site/public.
public record SitePublic(string Hotline, string Email, bool BannerEnabled, string BannerText, string BannerLink, string BannerTone, bool ChatbotEnabled);

/// Project MVC không truy cập CSDL: mọi dữ liệu đều lấy từ Web API. Client này gọi API (phía server) cho layout và trang bảo trì.
/// Có cache ngắn; API không trả lời thì dùng giá trị mặc định để trang vẫn hiển thị được.
public class SiteApiClient(HttpClient http, IMemoryCache cache, IConfiguration cfg)
{
    public sealed record StatusDto(bool Maintenance, string? Message);

    public async Task<SitePublic> GetPublicAsync()
    {
        if (cache.TryGetValue("site:public", out SitePublic? hit) && hit is not null) return hit;
        var fallback = new SitePublic(cfg["Chatbot:Hotline"] ?? "1900 6408", cfg["Chatbot:Email"] ?? "hotro@ticketgo.vn", false, "", "", "info", true);
        var st = fallback; var ok = false;
        if (http.BaseAddress is not null)
        {
            try { var r = await http.GetFromJsonAsync<SitePublic>("api/site/public"); if (r is not null) { st = r; ok = true; } }
            catch { /* API chưa chạy / lỗi mạng: dùng mặc định */ }
        }
        cache.Set("site:public", st, TimeSpan.FromSeconds(ok ? 15 : 5));
        return st;
    }

    public async Task<(bool Enabled, string Message)> GetMaintenanceAsync()
    {
        if (cache.TryGetValue("site:maintenance", out (bool, string) hit)) return hit;
        (bool, string) st = (false, "");
        if (http.BaseAddress is not null)
        {
            try { var r = await http.GetFromJsonAsync<StatusDto>("api/site/status"); if (r is not null) st = (r.Maintenance, r.Message ?? ""); }
            catch { /* không hỏi được API: coi như không bảo trì để không chặn nhầm cả site */ }
        }
        cache.Set("site:maintenance", st, TimeSpan.FromSeconds(5));
        return st;
    }
}
