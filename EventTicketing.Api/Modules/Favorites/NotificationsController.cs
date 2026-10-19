using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Favorites;

/// Chuông thông báo cạnh tên người dùng.
[ApiController, Authorize, Route("api/notifications")]
public class NotificationsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var uid = User.UserId();
        var unread = await db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead);
        var items = await db.Notifications.AsNoTracking().Where(n => n.UserId == uid)
            .OrderByDescending(n => n.CreatedAt).Take(20)
            .Select(n => new { n.Id, n.Title, n.Message, n.Link, n.IsRead, n.CreatedAt }).ToListAsync();
        return Ok(new { unread, items });
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id)
    {
        var uid = User.UserId();
        await db.Notifications.Where(n => n.Id == id && n.UserId == uid).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return Ok(new { ok = true });
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll()
    {
        var uid = User.UserId();
        await db.Notifications.Where(n => n.UserId == uid && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return Ok(new { ok = true });
    }
}

public static class NotificationExtensions
{
    /// Gửi một thông báo cho toàn bộ khách (tài khoản vai trò User, đã xác minh, chưa bị khoá).
    public static async Task NotifyAllUsersAsync(this AppDbContext db, string title, string? message, string? link)
    {
        var ids = await db.Users.AsNoTracking().Where(u => u.Role == "User" && u.IsVerified && !u.IsLocked).Select(u => u.Id).ToListAsync();
        foreach (var id in ids) db.Notifications.Add(new Notification { UserId = id, Title = title, Message = message, Link = link });
        await db.SaveChangesAsync();
    }
}
