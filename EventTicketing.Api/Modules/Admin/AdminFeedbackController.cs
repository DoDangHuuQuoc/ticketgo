using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;

namespace EventTicketing.Modules.Admin;

public record FeedbackReplyDto(string? Reply, bool? SendEmail);
public record FeedbackStatusDto(string? Status);

/// Quản lý phản hồi khách hàng (chatbot/form gửi vào). Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/feedback")]
public class AdminFeedbackController(AppDbContext db, AuditService audit, IEmailService mail, ILogger<AdminFeedbackController> log) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? category, [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var query = db.Feedbacks.AsNoTracking().AsQueryable();
        if (Enum.TryParse<FeedbackStatus>(status, out var st)) query = query.Where(f => f.Status == st);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(f => f.Category == category);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(f => f.Name.Contains(k) || f.Email.Contains(k) || f.Message.Contains(k)); }
        var total = await query.CountAsync();
        var items = await query.OrderBy(f => f.Status == FeedbackStatus.Resolved).ThenByDescending(f => f.CreatedAt).Skip((page - 1) * size).Take(size)
            .Select(f => new { f.Id, f.UserId, f.Name, f.Email, f.Category, f.Message, f.Source, f.Status, f.AdminReply, f.RepliedBy, f.RepliedAt, f.CreatedAt }).ToListAsync();
        var counts = await db.Feedbacks.AsNoTracking().GroupBy(f => f.Status).Select(g => new { status = g.Key.ToString(), n = g.Count() }).ToListAsync();
        return Ok(new { items, total, page, size, counts });
    }

    [HttpPut("{id:guid}/reply")]
    public async Task<IActionResult> Reply(Guid id, FeedbackReplyDto dto)
    {
        var text = (dto.Reply ?? "").Trim();
        if (text.Length == 0) throw new AppException(400, "EMPTY", "Vui lòng nhập nội dung trả lời.");
        if (text.Length > 2000) throw new AppException(400, "TOO_LONG", "Nội dung trả lời tối đa 2000 ký tự.");
        var f = await db.Feedbacks.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy phản hồi.");
        f.AdminReply = text; f.RepliedAt = DateTime.UtcNow; f.RepliedBy = User.FindFirstValue(ClaimTypes.Email) ?? "admin"; f.Status = FeedbackStatus.Resolved;
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "feedback.reply", "Feedback", id, $"trả lời {f.Email}");

        var emailed = false;
        if (dto.SendEmail ?? true)
        {
            try
            {
                static string E(string? t) => WebUtility.HtmlEncode(t ?? "");
                var quoted = E(f.Message).Replace("\n", "<br>");
                var answer = E(text).Replace("\n", "<br>");
                await mail.SendAsync(f.Email, "TicketGo đã phản hồi yêu cầu của bạn",
                    $"<p>Xin chào {E(f.Name)},</p><p>Cảm ơn bạn đã liên hệ TicketGo. Phản hồi cho yêu cầu của bạn:</p>" +
                    $"<blockquote style='margin:8px 0;padding:8px 12px;border-left:3px solid #ccc;color:#555'>{quoted}</blockquote>" +
                    $"<p>{answer}</p><p>Trân trọng,<br>TicketGo</p>");
                emailed = true;
            }
            catch (Exception ex) { log.LogWarning(ex, "Không gửi được email phản hồi tới {Email}", f.Email); }
        }
        return Ok(new { message = emailed ? "Đã lưu và gửi email cho khách" : "Đã lưu phản hồi" + ((dto.SendEmail ?? true) ? " (chưa gửi được email – khách vẫn xem được trong chatbot)" : ""), emailed });
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, FeedbackStatusDto dto)
    {
        if (!Enum.TryParse<FeedbackStatus>(dto.Status, out var st)) throw new AppException(400, "BAD_STATUS", "Trạng thái không hợp lệ.");
        var n = await db.Feedbacks.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, st));
        if (n == 0) return NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy phản hồi." });
        await audit.LogAsync(User, "feedback.status", "Feedback", id, "→ " + st);
        return Ok(new { message = "Đã cập nhật trạng thái" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var n = await db.Feedbacks.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (n == 0) return NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy phản hồi." });
        await audit.LogAsync(User, "feedback.delete", "Feedback", id);
        return Ok(new { message = "Đã xoá" });
    }
}
