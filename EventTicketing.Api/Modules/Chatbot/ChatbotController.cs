using System.Security.Claims;
using EventTicketing.Common;
using Microsoft.AspNetCore.Mvc;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Admin;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Chatbot;

public record ChatRequest(string? Message, List<ChatTurn>? History);

/// POST /api/chatbot — công khai (khách chưa đăng nhập vẫn hỏi được); nếu có JWT hợp lệ thì bot trả lời được về đơn của chính họ.
[ApiController, Route("api/chatbot")]
public class ChatbotController(ChatbotService bot, IMemoryCache cache, AppDbContext db, IConfiguration cfg, ILogger<ChatbotController> log) : ControllerBase
{
    private const int MaxLen = 500, PerMinute = 20;

    [HttpPost]
    public async Task<IActionResult> Ask(ChatRequest req, CancellationToken ct)
    {
        if (!(await SiteSettingsStore.GetPublicAsync(db, cache, cfg)).ChatbotEnabled)
            throw new AppException(503, "CHATBOT_OFF", "Trợ lý tự động đang tạm tắt. Vui lòng liên hệ hotline để được hỗ trợ.");
        var msg = (req.Message ?? "").Trim();
        if (msg.Length == 0) throw new AppException(400, "EMPTY", "Bạn chưa nhập câu hỏi.");
        if (msg.Length > MaxLen) throw new AppException(400, "TOO_LONG", $"Câu hỏi tối đa {MaxLen} ký tự.");

        // Giới hạn tần suất theo người dùng/IP để tránh bị lạm dụng (đặc biệt khi bật Claude)
        Guid? uid = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
        var who = uid?.ToString() ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        var key = $"chatrl:{who}:{DateTime.UtcNow:yyyyMMddHHmm}";
        var n = cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); return new int[1]; })!;
        if (Interlocked.Increment(ref n[0]) > PerMinute) throw new AppException(429, "RATE_LIMIT", "Bạn hỏi quá nhanh, vui lòng thử lại sau ít phút.");

        var history = (req.History ?? []).TakeLast(8)
            .Select(h => new ChatTurn(h.Role == "bot" ? "bot" : "user", (h.Text ?? "").Length > MaxLen ? h.Text![..MaxLen] : h.Text ?? "")).ToList();
        var reply = await bot.AnswerAsync(msg, history, uid, ct);
        try { db.ChatLogs.Add(new ChatLog { UserId = uid, Message = msg, Intent = reply.Intent ?? "rule" }); await db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception ex) { log.LogWarning(ex, "Không ghi được ChatLog"); }
        return Ok(reply);
    }
}
