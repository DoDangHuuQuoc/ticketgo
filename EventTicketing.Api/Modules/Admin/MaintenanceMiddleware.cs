using System.Net;
using EventTicketing.Data;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Admin;

/// Khi bật bảo trì: API trả 503 JSON { code: "MAINTENANCE", message }. (Trang HTML bảo trì do project MVC hiển thị.)
/// Luôn cho qua: khu quản trị (/api/admin/*), đăng nhập (để admin vào tắt bảo trì),
/// webhook thanh toán (đơn đã trả tiền vẫn phải được ghi nhận), /api/site/* (trạng thái + thông tin công khai)
/// và /api/feedback (khách vẫn gửi được yêu cầu hỗ trợ).
public class MaintenanceMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly string[] Exempt =
    {
        "/api/admin", "/api/auth", "/api/payments/webhook", "/api/payments/bank-webhook", "/api/payments/payos", "/api/site", "/api/feedback",
        "/swagger", "/health"
    };

    public async Task Invoke(HttpContext ctx, AppDbContext db)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (ctx.Request.Method == HttpMethods.Options || Exempt.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase))) { await next(ctx); return; }

        var st = await SiteSettingsStore.GetMaintenanceAsync(db, cache);
        if (!st.Enabled) { await next(ctx); return; }

        ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        ctx.Response.Headers["Retry-After"] = "300";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.WriteAsJsonAsync(new { code = "MAINTENANCE", message = st.Message });
    }
}
