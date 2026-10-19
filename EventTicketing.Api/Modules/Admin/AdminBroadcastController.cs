using System.Net;
using System.Security.Claims;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record BroadcastDto(string? Subject, string? Message, string? Audience, Guid? EventId, bool TestOnly);

public class Rcpt { public string Email { get; set; } = ""; public string Name { get; set; } = ""; }

/// Gửi email hàng loạt cho khách theo nhóm. Chỉ Admin. Gửi nền, có tiến độ và lịch sử.
/// Nhóm: buyers = đã từng mua; nobuy = có tài khoản nhưng chưa mua; all = mọi tài khoản đã xác thực; event = khách đã thanh toán một sự kiện.
/// Chỉ gửi cho vai trò User, đã xác thực email và không bị khoá. Trong nội dung có thể dùng {{name}} = tên khách.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/broadcast")]
public class AdminBroadcastController(AppDbContext db, AuditService audit, IServiceScopeFactory scopes, IConfiguration cfg, IEmailService mail, ILogger<AdminBroadcastController> log) : ControllerBase
{
    private int Max => cfg.GetValue("Broadcast:MaxRecipients", 2000);

    private IQueryable<Rcpt> Query(string? audience, Guid? eventId)
    {
        var users = db.Users.AsNoTracking().Where(u => u.Role == "User" && u.IsVerified && !u.IsLocked);
        switch (audience)
        {
            case "all": break;
            case "buyers": users = users.Where(u => db.Bookings.Any(b => b.UserId == u.Id && b.Status == BookingStatus.Paid)); break;
            case "nobuy": users = users.Where(u => !db.Bookings.Any(b => b.UserId == u.Id && b.Status == BookingStatus.Paid)); break;
            case "event":
                if (eventId is null) throw new AppException(400, "NO_EVENT", "Hãy chọn sự kiện");
                users = users.Where(u => db.Bookings.Any(b => b.UserId == u.Id && b.EventId == eventId && b.Status == BookingStatus.Paid)); break;
            default: throw new AppException(400, "BAD_AUDIENCE", "Nhóm khách không hợp lệ");
        }
        return users.Select(u => new Rcpt { Email = u.Email, Name = u.FullName });
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> Recipients([FromQuery] string? audience, [FromQuery] Guid? eventId)
        => Ok(new { count = await Query(audience, eventId).CountAsync(), max = Max });

    [HttpGet]
    public async Task<IActionResult> History() => Ok(await db.Broadcasts.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(30)
        .Select(b => new { b.Id, b.Subject, b.Audience, b.EventId, b.Total, b.Sent, b.Failed, b.Status, b.CreatedBy, b.CreatedAt, b.FinishedAt }).ToListAsync());

    private static string Render(string message, string name)
    {
        var body = WebUtility.HtmlEncode(message).Replace("{{name}}", WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(name) ? "bạn" : name));
        return "<div style='font-family:Arial,sans-serif;max-width:600px;margin:auto'>" +
               $"<p>Xin chào {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(name) ? "bạn" : name)},</p>" +
               $"<div style='white-space:pre-wrap;line-height:1.6'>{body}</div>" +
               "<hr style='border:0;border-top:1px solid #e2e8f0;margin:24px 0'/><p style='color:#94a3b8;font-size:12px'>Bạn nhận email này vì có tài khoản trên TicketGo.</p></div>";
    }

    [HttpPost]
    public async Task<IActionResult> Send(BroadcastDto dto)
    {
        var subject = (dto.Subject ?? "").Trim(); var message = (dto.Message ?? "").Trim();
        if (subject.Length is < 3 or > 150) throw new AppException(400, "BAD_SUBJECT", "Tiêu đề từ 3 đến 150 ký tự");
        if (message.Length is < 5 or > 3000) throw new AppException(400, "BAD_MESSAGE", "Nội dung từ 5 đến 3000 ký tự");
        var me = User.FindFirstValue(ClaimTypes.Email) ?? "";

        if (dto.TestOnly)
        {
            if (me.Length == 0) throw new AppException(400, "NO_EMAIL", "Tài khoản của bạn không có email để gửi thử");
            await mail.SendAsync(me, "[Gửi thử] " + subject, Render(message, "Quản trị viên"));
            return Ok(new { message = "Đã gửi thử tới " + me });
        }

        var list = await Query(dto.Audience, dto.EventId).Take(Max + 1).ToListAsync();
        if (list.Count == 0) throw new AppException(409, "NO_RECIPIENTS", "Nhóm khách này chưa có ai");
        if (list.Count > Max) throw new AppException(409, "TOO_MANY", $"Quá {Max} người nhận. Hãy chọn nhóm nhỏ hơn.");
        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.Broadcasts.AnyAsync(b => b.Status == "Sending" && b.CreatedAt > since))
            throw new AppException(409, "BUSY", "Đang có một đợt gửi chạy. Vui lòng chờ đợt đó hoàn tất.");

        var bc = new Broadcast { Subject = subject, Message = message, Audience = dto.Audience!, EventId = dto.Audience == "event" ? dto.EventId : null, Total = list.Count, CreatedBy = me };
        db.Broadcasts.Add(bc); await db.SaveChangesAsync();
        await audit.LogAsync(User, "broadcast.send", "Broadcast", bc.Id, $"{list.Count} người · {subject}");

        var id = bc.Id;
        _ = Task.Run(async () =>
        {
            int ok = 0, fail = 0;
            using var scope = scopes.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var bdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var r in list)
            {
                try { await sender.SendAsync(r.Email, subject, Render(message, r.Name)); ok++; }
                catch (Exception ex) { fail++; log.LogWarning(ex, "Broadcast {Id} gửi lỗi tới một người nhận", id); }
                if ((ok + fail) % 5 == 0)
                    try { await bdb.Broadcasts.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Sent, ok).SetProperty(b => b.Failed, fail)); } catch { }
            }
            try { await bdb.Broadcasts.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Sent, ok).SetProperty(b => b.Failed, fail).SetProperty(b => b.Status, "Done").SetProperty(b => b.FinishedAt, DateTime.UtcNow)); }
            catch (Exception ex) { log.LogError(ex, "Không cập nhật được kết quả broadcast {Id}", id); }
        });
        return Accepted(new { id, count = list.Count, message = $"Đang gửi cho {list.Count} khách. Việc gửi chạy nền, bạn theo dõi tiến độ ở bảng lịch sử." });
    }
}
