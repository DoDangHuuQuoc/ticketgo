using System.Net.Mail;
using System.Security.Claims;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Chatbot;

public record FeedbackDto(string? Name, string? Email, string? Category, string? Message);

/// Khách gửi phản hồi/yêu cầu hỗ trợ cho nhân viên (từ chatbot). Công khai; nếu đã đăng nhập thì gắn với tài khoản.
[ApiController, Route("api/feedback")]
public class FeedbackController(AppDbContext db, IMemoryCache cache) : ControllerBase
{
    private static readonly HashSet<string> Cats = ["Order", "Payment", "Ticket", "Refund", "Event", "Other"];

    [HttpPost]
    public async Task<IActionResult> Create(FeedbackDto dto, CancellationToken ct)
    {
        var msg = (dto.Message ?? "").Trim();
        if (msg.Length < 5) throw new AppException(400, "EMPTY", "Vui lòng mô tả vấn đề (ít nhất 5 ký tự).");
        if (msg.Length > 2000) throw new AppException(400, "TOO_LONG", "Nội dung tối đa 2000 ký tự.");

        Guid? uid = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
        var name = (dto.Name ?? "").Trim();
        var email = (string.IsNullOrWhiteSpace(dto.Email) ? User.FindFirstValue(ClaimTypes.Email) ?? "" : dto.Email).Trim();
        if (uid is not null && name.Length == 0)
            name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? "";
        if (name.Length == 0) throw new AppException(400, "NAME", "Vui lòng nhập họ tên.");
        if (name.Length > 200) name = name[..200];
        if (email.Length > 256 || !MailAddress.TryCreate(email, out var addr) || addr.Address != email)
            throw new AppException(400, "EMAIL", "Email chưa hợp lệ — nhân viên sẽ trả lời qua email này.");

        // Tối đa 5 phản hồi/giờ cho mỗi tài khoản hoặc IP để tránh spam
        var who = uid?.ToString() ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var n = cache.GetOrCreate($"fbrl:{who}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
        if (Interlocked.Increment(ref n[0]) > 5) throw new AppException(429, "RATE_LIMIT", "Bạn đã gửi quá nhiều phản hồi. Vui lòng thử lại sau hoặc gọi hotline.");

        var cat = Cats.Contains(dto.Category ?? "") ? dto.Category! : "Other";
        var f = new Feedback { UserId = uid, Name = name, Email = email, Category = cat, Message = msg };
        db.Feedbacks.Add(f);
        await db.SaveChangesAsync(ct);
        return Ok(new { id = f.Id, message = "Đã gửi cho nhân viên. TicketGo sẽ phản hồi qua email của bạn sớm nhất có thể." });
    }

    /// Phản hồi của chính tôi (để chatbot cho khách xem câu trả lời của nhân viên)
    [Authorize, HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = User.UserId();
        return Ok(await db.Feedbacks.AsNoTracking().Where(f => f.UserId == uid).OrderByDescending(f => f.CreatedAt).Take(10)
            .Select(f => new { f.Id, f.Category, f.Message, f.Status, f.AdminReply, f.RepliedAt, f.CreatedAt }).ToListAsync());
    }
}
