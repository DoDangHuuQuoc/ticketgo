using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Support;

/// Nhân viên đang mở trang chat trực tiếp (poll trong vòng 45 giây) => hiện "đang trực tuyến" cho khách.
public static class LiveChatPresence
{
    private static readonly ConcurrentDictionary<Guid, DateTime> Seen = new();
    public static void Touch(Guid staffId) => Seen[staffId] = DateTime.UtcNow;
    public static bool Online => Seen.Values.Any(t => DateTime.UtcNow - t < TimeSpan.FromSeconds(45));
}

public record LcStartDto(string? Name, string? Key, string? Text);
public record LcSendDto(string? Text, string? Key);
public record LcKeyDto(string? Key);
public record LcStaffSendDto(string? Text);

public static class LiveChatDto
{
    public const int MaxLen = 1000;
    public static object Msg(LiveChatMessage m) => new { m.Id, m.FromStaff, sender = m.SenderName, m.Text, m.CreatedAt };
    public static string Clean(string? t)
    {
        var s = (t ?? "").Trim();
        if (s.Length == 0) throw new AppException(400, "EMPTY", "Bạn chưa nhập nội dung.");
        return s.Length > MaxLen ? s[..MaxLen] : s;
    }
    public static string Preview(string t) => t.Length > 120 ? t[..120] + "…" : t;
}

/// Phía khách: bắt đầu / nhắn / xem tin của cuộc chat với nhân viên. Công khai (khách vãng lai dùng GuestKey), đăng nhập thì gắn với tài khoản.
[ApiController, Route("api/livechat")]
public class LiveChatController(AppDbContext db, IMemoryCache cache) : ControllerBase
{
    private Guid? Uid => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;

    private IQueryable<LiveChat> Mine(string? key)
    {
        var uid = Uid; var k = (key ?? "").Trim();
        return db.LiveChats.Where(c => (uid != null && c.UserId == uid) || (uid == null && k != "" && c.GuestKey == k));
    }

    private void Throttle(string who)
    {
        var n = cache.GetOrCreate($"lcrl:{who}:{DateTime.UtcNow:yyyyMMddHHmm}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); return new int[1]; })!;
        if (Interlocked.Increment(ref n[0]) > 30) throw new AppException(429, "RATE_LIMIT", "Bạn nhắn quá nhanh, vui lòng thử lại sau ít phút.");
    }

    private async Task<object> StateAsync(LiveChat c, DateTime? since)
    {
        cache.Set($"lc-seen:{c.Id}", true, TimeSpan.FromSeconds(40));   // khách đang mở khung chat: nhân viên trả lời thì không cần báo chuông
        var q = db.LiveChatMessages.AsNoTracking().Where(m => m.ChatId == c.Id);
        if (since is DateTime s) q = q.Where(m => m.CreatedAt > s.AddSeconds(-2));
        var msgs = (await q.OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync()).OrderBy(m => m.CreatedAt).ToList();
        return new { id = c.Id, status = c.Status, staffName = c.StaffName, online = LiveChatPresence.Online, messages = msgs.Select(LiveChatDto.Msg) };
    }

    private static DateTime? ParseSince(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// Cuộc chat đang mở của tôi (để khung chat nối lại sau khi tải lại trang).
    [HttpGet("current")]
    public async Task<IActionResult> Current([FromQuery] string? key)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var c = await Mine(key).Where(x => x.Status == "Open" && x.LastMessageAt > since).OrderByDescending(x => x.LastMessageAt).FirstOrDefaultAsync();
        return Ok(c is null ? new { id = (Guid?)null } : await StateAsync(c, null));
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(LcStartDto dto)
    {
        var uid = Uid;
        var key = (dto.Key ?? "").Trim();
        if (uid is null && key.Length is > 0 and < 16) key = "";   // khoá do client tự bịa quá ngắn thì cấp khoá mới
        var since = DateTime.UtcNow.AddHours(-24);
        var c = await Mine(key).Where(x => x.Status == "Open" && x.LastMessageAt > since).OrderByDescending(x => x.LastMessageAt).FirstOrDefaultAsync();
        if (c is null)
        {
            var name = (dto.Name ?? "").Trim();
            if (uid is not null) name = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync() ?? name;
            if (name.Length < 2) throw new AppException(400, "NAME", "Vui lòng nhập tên của bạn để nhân viên xưng hô.");
            if (name.Length > 200) name = name[..200];
            c = new LiveChat { UserId = uid, Name = name, GuestKey = uid is null ? (key.Length >= 16 ? key : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()) : null };
            db.LiveChats.Add(c);
            await db.SaveChangesAsync();
        }
        var first = (dto.Text ?? "").Trim();
        if (first.Length > 0) await AddCustomerMessage(c, LiveChatDto.Clean(first));
        var state = await StateAsync(c, null);
        return Ok(new { chat = state, key = c.GuestKey });
    }

    private async Task AddCustomerMessage(LiveChat c, string text)
    {
        Throttle(c.Id.ToString());
        db.LiveChatMessages.Add(new LiveChatMessage { ChatId = c.Id, FromStaff = false, SenderName = c.Name, Text = text });
        var id = c.Id; var now = DateTime.UtcNow; var prev = LiveChatDto.Preview(text);
        await db.SaveChangesAsync();
        await db.LiveChats.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.StaffUnread, x => x.StaffUnread + 1).SetProperty(x => x.LastMessageAt, now).SetProperty(x => x.LastText, prev).SetProperty(x => x.Status, "Open"));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string? key, [FromQuery] string? since)
    {
        var c = await Mine(key).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        return Ok(await StateAsync(c, ParseSince(since)));
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, LcSendDto dto)
    {
        var c = await Mine(dto.Key).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        var text = LiveChatDto.Clean(dto.Text);
        await AddCustomerMessage(c, text);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, LcKeyDto dto)
    {
        var n = await Mine(dto.Key).Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Closed"));
        if (n == 0) throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        return Ok(new { ok = true });
    }
}

/// Phía nhân viên (Admin + Staff): danh sách cuộc chat, đọc, trả lời, đóng.
[ApiController, Authorize(Roles = "Admin,Staff"), Route("api/admin/livechat")]
public class AdminLiveChatController(AppDbContext db, IMemoryCache cache) : ControllerBase
{
    private void Present() { if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g)) LiveChatPresence.Touch(g); }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        Present();
        var since = DateTime.UtcNow.AddDays(-3);
        var rows = await db.LiveChats.AsNoTracking().Where(c => c.Status == "Open" || c.LastMessageAt > since)
            .OrderBy(c => c.Status == "Open" ? 0 : 1).ThenByDescending(c => c.LastMessageAt).Take(100)
            .Select(c => new { c.Id, c.Name, hasAccount = c.UserId != null, c.Status, c.StaffName, c.LastText, c.LastMessageAt, c.StaffUnread, c.CreatedAt }).ToListAsync();
        return Ok(new { unread = rows.Where(r => r.Status == "Open").Sum(r => r.StaffUnread), items = rows });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        Present();
        var c = await db.LiveChats.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        if (c.StaffUnread > 0) await db.LiveChats.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StaffUnread, 0));
        var msgs = (await db.LiveChatMessages.AsNoTracking().Where(m => m.ChatId == id).OrderByDescending(m => m.CreatedAt).Take(300).ToListAsync()).OrderBy(m => m.CreatedAt).ToList();
        return Ok(new { c.Id, c.Name, hasAccount = c.UserId != null, c.Status, c.StaffName, messages = msgs.Select(LiveChatDto.Msg) });
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, LcStaffSendDto dto)
    {
        Present();
        var text = LiveChatDto.Clean(dto.Text);
        var c = await db.LiveChats.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        if (c.Status != "Open") throw new AppException(409, "CLOSED", "Cuộc chat đã đóng, khách cần mở lại để nhắn tiếp.");
        var uid = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : (Guid?)null;
        var me = uid is null ? "Nhân viên" : await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync() ?? "Nhân viên";
        db.LiveChatMessages.Add(new LiveChatMessage { ChatId = id, FromStaff = true, SenderName = me, Text = text });
        var now = DateTime.UtcNow; var prev = "Bạn: " + LiveChatDto.Preview(text); var first = c.StaffName ?? me;
        await db.SaveChangesAsync();
        await db.LiveChats.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastMessageAt, now).SetProperty(x => x.LastText, prev).SetProperty(x => x.StaffName, first).SetProperty(x => x.StaffUnread, 0));

        // Khách có tài khoản nhưng đang không mở khung chat: báo qua chuông thông báo
        if (c.UserId is Guid cu && !cache.TryGetValue($"lc-seen:{id}", out _))
        {
            try
            {
                db.Notifications.Add(new Notification { UserId = cu, Title = "Nhân viên đã trả lời bạn", Message = LiveChatDto.Preview(text), Link = "/" });
                await db.SaveChangesAsync();
            }
            catch { /* thông báo không được làm hỏng việc gửi tin */ }
        }
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id)
    {
        var n = await db.LiveChats.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Closed").SetProperty(x => x.StaffUnread, 0));
        if (n == 0) throw new AppException(404, "NOT_FOUND", "Không tìm thấy cuộc chat");
        return Ok(new { ok = true });
    }
}
