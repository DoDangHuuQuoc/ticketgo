using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Admin;

public record RoleDto(string Role);
public record LockDto(bool IsLocked, string? Reason);
public record CreateUserDto(string Email, string FullName, string Role, string Password);
public record PasswordDto(string Password);

/// Quản lý người dùng + nhật ký thao tác. Chỉ Admin.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin")]
public class AdminUsersController(AppDbContext db, AuditService audit, IPasswordHasher<User> hasher, IMemoryCache cache, VipService vip, WelcomeCouponService welcome, VipNotifier vipNotifier) : ControllerBase
{
    private static readonly string[] Roles = ["User", "Staff", "Admin"];
    private Guid Me => User.UserId();

    [HttpGet("users")]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? role, [FromQuery] string? state,
        [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var promo = await vip.GetSettingsAsync();
        var autoVip = promo.VipAutoSpend;
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(u => u.Email.Contains(k) || u.FullName.Contains(k)); }
        var counts = await query.GroupBy(u => u.Role).Select(g => new { role = g.Key, count = g.Count() }).ToListAsync();
        if (Roles.Contains(role)) query = query.Where(u => u.Role == role);
        else query = query.Where(u => u.Role != "Staff");   // nhân viên có trang riêng (/Manage/Staff, gọi với role=Staff)
        query = state switch
        {
            "locked" => query.Where(u => u.IsLocked),
            "unverified" => query.Where(u => !u.IsVerified),
            "vip" => query.Where(u => u.IsVip || (autoVip > 0 && (db.Bookings.Where(b => b.UserId == u.Id && b.Status == BookingStatus.Paid).Sum(b => (decimal?)b.TotalAmount) ?? 0) >= autoVip)),
            "active" => query.Where(u => !u.IsLocked && u.IsVerified),
            _ => query
        };
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(u => u.CreatedAt).Skip((page - 1) * size).Take(size).Select(u => new
        {
            u.Id, u.Email, u.FullName, u.Role, u.IsVerified, u.IsLocked, u.IsVip, u.CreatedAt,
            Orders = db.Bookings.Count(b => b.UserId == u.Id && b.Status == BookingStatus.Paid),
            Spent = db.Bookings.Where(b => b.UserId == u.Id && b.Status == BookingStatus.Paid).Sum(b => (decimal?)b.TotalAmount) ?? 0
        }).ToListAsync();
        return Ok(new { items, total, page, size, counts, vipEnabled = promo.VipEnabled, vipAutoSpend = promo.VipAutoSpend });
    }

    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Email, x.FullName, x.Role, x.IsVerified, x.IsLocked, x.IsVip, x.CreatedAt }).FirstOrDefaultAsync();
        if (u is null) return NotFound();
        var bookings = await db.Bookings.AsNoTracking().Where(b => b.UserId == id).OrderByDescending(b => b.CreatedAt).Take(20)
            .Select(b => new { b.Id, b.Quantity, b.TotalAmount, b.Status, b.CreatedAt, EventName = b.TicketType!.Event!.Name, TypeName = b.TicketType.Name }).ToListAsync();
        var logs = await db.AuditLogs.AsNoTracking().Where(l => l.EntityType == "User" && l.EntityId == id.ToString())
            .OrderByDescending(l => l.CreatedAt).Take(10).Select(l => new { l.Action, l.ActorEmail, l.Detail, l.CreatedAt }).ToListAsync();
        return Ok(new { user = u, bookings, logs });
    }

    [HttpPost("users")]
    public async Task<IActionResult> Create(CreateUserDto dto)
    {
        var mail = (dto.Email ?? "").Trim().ToLowerInvariant();
        if (!System.Net.Mail.MailAddress.TryCreate(mail, out _)) throw new AppException(400, "BAD_EMAIL", "Email không hợp lệ");
        if (string.IsNullOrWhiteSpace(dto.FullName)) throw new AppException(400, "BAD_NAME", "Họ tên bắt buộc");
        if (!Roles.Contains(dto.Role)) throw new AppException(400, "BAD_ROLE", "Vai trò không hợp lệ");
        if ((dto.Password ?? "").Length < 8) throw new AppException(400, "WEAK_PASSWORD", "Mật khẩu tối thiểu 8 ký tự");
        if (await db.Users.AnyAsync(u => u.Email == mail)) throw new AppException(409, "EMAIL_EXISTS", "Email đã được đăng ký");

        var u = new User { Email = mail, FullName = dto.FullName.Trim(), Role = dto.Role, IsVerified = true };
        u.PasswordHash = hasher.HashPassword(u, dto.Password!);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "user.create", "User", u.Id, $"{mail} ({dto.Role})");
        return Ok(new { u.Id, message = "Đã tạo tài khoản" });
    }

    [HttpPut("users/{id:guid}/role")]
    public async Task<IActionResult> SetRole(Guid id, RoleDto dto)
    {
        if (!Roles.Contains(dto.Role)) throw new AppException(400, "BAD_ROLE", "Vai trò không hợp lệ");
        if (id == Me) throw new AppException(409, "SELF", "Không thể tự đổi vai trò của chính mình");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Người dùng không tồn tại");
        if (u.Role == "Admin" && dto.Role != "Admin" && await db.Users.CountAsync(x => x.Role == "Admin" && !x.IsLocked) <= 1)
            throw new AppException(409, "LAST_ADMIN", "Phải còn ít nhất một Admin hoạt động");
        var old = u.Role; u.Role = dto.Role;
        await db.SaveChangesAsync();
        cache.Remove($"ustate:{id}");
        await audit.LogAsync(User, "user.role", "User", id, $"{u.Email}: {old} -> {dto.Role}");
        return Ok(new { message = "Đã đổi vai trò. Người dùng cần đăng nhập lại." });
    }

    [HttpPost("users/{id:guid}/lock")]
    public async Task<IActionResult> SetLock(Guid id, LockDto dto)
    {
        if (id == Me) throw new AppException(409, "SELF", "Không thể tự khoá tài khoản của chính mình");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Người dùng không tồn tại");
        if (dto.IsLocked && u.Role == "Admin" && await db.Users.CountAsync(x => x.Role == "Admin" && !x.IsLocked) <= 1)
            throw new AppException(409, "LAST_ADMIN", "Phải còn ít nhất một Admin hoạt động");
        u.IsLocked = dto.IsLocked;
        await db.SaveChangesAsync();
        cache.Remove($"ustate:{id}");
        await audit.LogAsync(User, dto.IsLocked ? "user.lock" : "user.unlock", "User", id, $"{u.Email}{(string.IsNullOrWhiteSpace(dto.Reason) ? "" : " — " + dto.Reason)}");
        return Ok(new { message = dto.IsLocked ? "Đã khoá tài khoản" : "Đã mở khoá tài khoản" });
    }

    [HttpPost("users/{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, PasswordDto dto)
    {
        if ((dto.Password ?? "").Length < 8) throw new AppException(400, "WEAK_PASSWORD", "Mật khẩu tối thiểu 8 ký tự");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Người dùng không tồn tại");
        u.PasswordHash = hasher.HashPassword(u, dto.Password!);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "user.reset-password", "User", id, u.Email);   // không ghi mật khẩu
        return Ok(new { message = "Đã đặt lại mật khẩu" });
    }

    [HttpPost("users/{id:guid}/verify")]
    public async Task<IActionResult> ForceVerify(Guid id)
    {
        var was = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => (bool?)u.IsVerified).FirstOrDefaultAsync();
        if (was is null) return NotFound();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsVerified, true));
        await audit.LogAsync(User, "user.verify", "User", id, "xác thực thủ công");
        if (was == false) await welcome.TryIssueAsync(id);   // tài khoản được duyệt thủ công cũng nhận mã chào mừng
        return Ok(new { message = "Đã xác thực email" });
    }

    /// Gán / bỏ nhãn khách VIP (ưu đãi VIP cấu hình ở /Manage/Coupons). Khách đạt ngưỡng chi tiêu vẫn tự hưởng ưu đãi nếu admin bật tự lên VIP.
    [HttpPut("users/{id:guid}/vip")]
    public async Task<IActionResult> SetVip(Guid id, VipFlagDto dto)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Người dùng không tồn tại");
        if (u.Role != "User") throw new AppException(400, "NOT_CUSTOMER", "Chỉ gán VIP cho tài khoản khách");
        u.IsVip = dto.IsVip;
        await db.SaveChangesAsync();
        await audit.LogAsync(User, dto.IsVip ? "user.vip" : "user.unvip", "User", id, u.Email);
        if (dto.IsVip) await vipNotifier.TryNotifyAsync(id); else await vipNotifier.ResetIfNoLongerVipAsync(id);
        return Ok(new { message = dto.IsVip ? "Đã gán khách VIP" : "Đã bỏ nhãn VIP" });
    }

    // ---------- Nhật ký ----------
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] string? q, [FromQuery] string? type, [FromQuery] int page = 1, [FromQuery] int size = 30)
    {
        page = Math.Max(1, page); size = Math.Clamp(size, 5, 100);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(l => l.EntityType == type);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(l => l.ActorEmail.Contains(k) || l.Action.Contains(k) || (l.Detail != null && l.Detail.Contains(k))); }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(l => l.CreatedAt).Skip((page - 1) * size).Take(size)
            .Select(l => new { l.Id, l.ActorEmail, l.Action, l.EntityType, l.EntityId, l.Detail, l.CreatedAt }).ToListAsync();
        return Ok(new { items, total, page, size });
    }
}
