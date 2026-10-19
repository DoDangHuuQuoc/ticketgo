using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record FaqInput(string? Question, string? Keywords, string? Answer, bool IsActive = true);
public record FaqActiveDto(bool IsActive);

/// Quản lý chatbot: câu hỏi–đáp (FAQ), thống kê và các câu bot chưa trả lời được. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/chatbot")]
public class AdminChatbotController(AppDbContext db, AuditService audit) : ControllerBase
{
    private static (string q, string k, string a) Clean(FaqInput d)
    {
        var q = (d.Question ?? "").Trim(); var a = (d.Answer ?? "").Trim();
        var k = string.Join(", ", (d.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase));
        if (q.Length is < 3 or > 200) throw new AppException(400, "BAD_QUESTION", "Câu hỏi từ 3 đến 200 ký tự");
        if (k.Length == 0) throw new AppException(400, "BAD_KEYWORDS", "Nhập ít nhất một từ khoá (mỗi từ khoá ≥ 2 ký tự, cách nhau bằng dấu phẩy)");
        if (k.Length > 300) throw new AppException(400, "BAD_KEYWORDS", "Từ khoá tối đa 300 ký tự");
        if (a.Length is < 3 or > 1500) throw new AppException(400, "BAD_ANSWER", "Câu trả lời từ 3 đến 1500 ký tự");
        return (q, k, a);
    }

    [HttpGet("faqs")]
    public async Task<IActionResult> Faqs() => Ok(await db.ChatFaqs.AsNoTracking().OrderByDescending(f => f.IsActive).ThenByDescending(f => f.Hits).ThenBy(f => f.Question)
        .Select(f => new { f.Id, f.Question, f.Keywords, f.Answer, f.IsActive, f.Hits, f.UpdatedAt }).ToListAsync());

    [HttpPost("faqs")]
    public async Task<IActionResult> Create(FaqInput dto)
    {
        var (q, k, a) = Clean(dto);
        if (await db.ChatFaqs.CountAsync() >= 300) throw new AppException(409, "TOO_MANY", "Tối đa 300 câu hỏi–đáp");
        var f = new ChatFaq { Question = q, Keywords = k, Answer = a, IsActive = dto.IsActive };
        db.ChatFaqs.Add(f); await db.SaveChangesAsync();
        await audit.LogAsync(User, "chatbot.faq.create", "ChatFaq", f.Id, q);
        return Ok(new { f.Id, message = "Đã thêm câu hỏi–đáp" });
    }

    [HttpPut("faqs/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, FaqInput dto)
    {
        var (q, k, a) = Clean(dto);
        var f = await db.ChatFaqs.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy câu hỏi–đáp");
        f.Question = q; f.Keywords = k; f.Answer = a; f.IsActive = dto.IsActive; f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "chatbot.faq.update", "ChatFaq", id, q);
        return Ok(new { message = "Đã lưu" });
    }

    [HttpPut("faqs/{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, FaqActiveDto dto)
    {
        var n = await db.ChatFaqs.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, dto.IsActive).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        if (n == 0) return NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy câu hỏi–đáp" });
        await audit.LogAsync(User, "chatbot.faq.toggle", "ChatFaq", id, dto.IsActive ? "bật" : "tắt");
        return Ok(new { message = dto.IsActive ? "Đã bật" : "Đã tắt" });
    }

    [HttpDelete("faqs/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var n = await db.ChatFaqs.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (n == 0) return NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy câu hỏi–đáp" });
        await audit.LogAsync(User, "chatbot.faq.delete", "ChatFaq", id);
        return Ok(new { message = "Đã xoá" });
    }

    /// Thống kê trong N ngày gần nhất + các câu hỏi bot chưa trả lời được (gợi ý để soạn FAQ mới).
    [HttpGet("stats")]
    public async Task<IActionResult> Stats([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var logs = db.ChatLogs.AsNoTracking().Where(l => l.CreatedAt >= since);
        var byIntent = await logs.GroupBy(l => l.Intent).Select(g => new { intent = g.Key, n = g.Count() }).ToListAsync();
        var daily = (await logs.Select(l => l.CreatedAt).ToListAsync())
            .GroupBy(d => d.AddHours(7).ToString("yyyy-MM-dd")).OrderBy(g => g.Key).Select(g => new { date = g.Key, n = g.Count() }).ToList();
        var unanswered = await logs.Where(l => l.Intent == "unknown").GroupBy(l => l.Message)
            .Select(g => new { message = g.Key, n = g.Count(), last = g.Max(x => x.CreatedAt) }).OrderByDescending(x => x.n).ThenByDescending(x => x.last).Take(15).ToListAsync();
        var total = byIntent.Sum(x => x.n);
        var totalAll = await db.ChatLogs.CountAsync();
        return Ok(new { days, total, byIntent, daily, unanswered, totalAll });
    }

    /// Xoá nhật ký cũ hơn N ngày (bảo vệ quyền riêng tư, giữ DB gọn).
    [HttpDelete("logs")]
    public async Task<IActionResult> Purge([FromQuery] int olderThanDays = 30)
    {
        olderThanDays = Math.Clamp(olderThanDays, 1, 3650);
        var cut = DateTime.UtcNow.AddDays(-olderThanDays);
        var n = await db.ChatLogs.Where(l => l.CreatedAt < cut).ExecuteDeleteAsync();
        await audit.LogAsync(User, "chatbot.logs.purge", "ChatLog", null, $"xoá {n} dòng cũ hơn {olderThanDays} ngày");
        return Ok(new { message = $"Đã xoá {n} câu hỏi cũ" });
    }
}
