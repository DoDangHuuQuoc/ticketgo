using EventTicketing.Common;
using EventTicketing.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Admin;

public record MaintenanceDto(bool Enabled, string? Message);

[ApiController]
public class SiteController(AppDbContext db, IMemoryCache cache, AuditService audit, IConfiguration cfg) : ControllerBase
{
    /// Công khai: trang bảo trì và giao diện hỏi trạng thái này.
    [HttpGet("api/site/status")]
    public async Task<IActionResult> Status()
    {
        var m = await SiteSettingsStore.GetMaintenanceAsync(db, cache);
        return Ok(new { maintenance = m.Enabled, message = m.Message });
    }

    /// Công khai: project MVC lấy thông tin liên hệ, banner, bật/tắt chatbot để hiển thị trên layout.
    [HttpGet("api/site/public")]
    public async Task<IActionResult> PublicInfo() => Ok(await SiteSettingsStore.GetPublicAsync(db, cache, cfg));

    [HttpGet("api/admin/site/maintenance"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Get()
    {
        var m = await SiteSettingsStore.GetMaintenanceAsync(db, cache);
        return Ok(new { enabled = m.Enabled, message = m.Message, updatedAt = m.UpdatedAt });
    }

    [HttpPut("api/admin/site/maintenance"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Set(MaintenanceDto dto)
    {
        var msg = (dto.Message ?? "").Trim();
        if (msg.Length > 500) throw new AppException(400, "MSG_TOO_LONG", "Lời nhắn tối đa 500 ký tự");
        if (msg.Length == 0) msg = SiteSettingsStore.DefaultMessage;
        var m = await SiteSettingsStore.SetMaintenanceAsync(db, cache, dto.Enabled, msg);
        await audit.LogAsync(User, dto.Enabled ? "site.maintenance.on" : "site.maintenance.off", "Site", 1, msg.Length > 120 ? msg[..120] : msg);
        return Ok(new { enabled = m.Enabled, message = m.Message, updatedAt = m.UpdatedAt });
    }
}
