using EventTicketing.Common;
using EventTicketing.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.WaitingRoom;

/// Hàng chờ mua vé: trang mua vé gọi join khi mở, rồi gọi status định kỳ (cả khi đang chờ lẫn khi đang mua) để giữ chỗ.
[ApiController, Authorize, Route("api/queue")]
public class WaitingRoomController(WaitingRoomService queue, AppDbContext db) : ControllerBase
{
    [HttpPost("{eventId:guid}/join")]
    public async Task<IActionResult> Join(Guid eventId)
    {
        if (!await db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId && e.IsPublished))
            throw new AppException(404, "EVENT_NOT_FOUND", "Sự kiện không tồn tại hoặc chưa mở bán");
        return Ok(queue.Join(eventId, User.UserId()));
    }

    [HttpGet("{eventId:guid}/status")]
    public IActionResult Status(Guid eventId) => Ok(queue.Status(eventId, User.UserId()));

    [HttpPost("{eventId:guid}/leave")]
    public IActionResult Leave(Guid eventId)
    {
        queue.Release(eventId, User.UserId());
        return Ok(new { ok = true });
    }
}

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/queue")]
public class AdminWaitingRoomController(WaitingRoomService queue, AppDbContext db) : ControllerBase
{
    /// Tình hình hàng chờ: cấu hình hiện tại và các sự kiện đang có người đặt vé / xếp hàng.
    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        var rooms = queue.Snapshot();
        var ids = rooms.Select(r => r.EventId).ToList();
        var names = await db.Events.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name);
        return Ok(new
        {
            enabled = queue.Enabled, capacity = queue.Capacity, slotSeconds = queue.SlotSeconds, activeIdleSeconds = queue.ActiveIdleSeconds, bypassAdmin = queue.BypassAdmin, waitTimeoutSeconds = queue.WaitTimeoutSeconds,
            rooms = rooms.Select(r => new { r.EventId, EventName = names.GetValueOrDefault(r.EventId, ""), r.Active, r.Waiting })
        });
    }
}
