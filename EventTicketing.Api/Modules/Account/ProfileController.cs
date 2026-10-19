using System.Globalization;
using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Account;

public record UpdateProfileDto(string FullName, string? Phone, string? DateOfBirth, string? Gender, string? Address);
public record ChangePasswordDto(string CurrentPassword, string NewPassword);

/// Thông tin cá nhân của chính người đang đăng nhập (trang /Account/Profile).
[ApiController, Authorize, Route("api/account")]
public class ProfileController(AppDbContext db, IPasswordHasher<User> hasher, VipService vipSvc) : ControllerBase
{
    private static readonly string[] Genders = ["Male", "Female", "Other"];
    // Số điện thoại Việt Nam: 0xxxxxxxxx, 84xxxxxxxxx hoặc +84xxxxxxxxx (sau khi bỏ dấu cách, chấm, gạch)
    private static readonly Regex PhoneRx = new(@"^(\+84|84|0)\d{9}$", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[\s.\-()]", RegexOptions.Compiled);

    [HttpGet("profile")]
    public async Task<IActionResult> Get()
    {
        var uid = User.UserId();
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid)
                ?? throw new AppException(401, "UNAUTHORIZED", "Tài khoản không tồn tại");

        var tickets = await db.Tickets.AsNoTracking().CountAsync(t => t.UserId == uid && t.Status != TicketStatus.Cancelled);
        var orders = await db.Bookings.AsNoTracking().CountAsync(b => b.UserId == uid && b.Status == BookingStatus.Paid);
        var spent = await db.Bookings.AsNoTracking().Where(b => b.UserId == uid && b.Status == BookingStatus.Paid)
            .SumAsync(b => (decimal?)b.TotalAmount) ?? 0m;

        var vouchers = await db.UserCoupons.AsNoTracking().CountAsync(x => x.UserId == uid);
        var vip = await vipSvc.StatusAsync(uid);

        return Ok(new
        {
            u.Email, u.FullName, u.Phone, u.Gender, u.Address, u.IsVerified, u.Role, u.CreatedAt,
            DateOfBirth = u.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Stats = new { tickets, orders, spent, vouchers },
            Vip = new { vip.Enabled, vip.IsVip, vip.Percent, vip.MaxDiscount, vip.AutoSpend, Remaining = vip.Enabled && !vip.IsVip && vip.AutoSpend > 0 ? Math.Max(0m, vip.AutoSpend - vip.Spent) : (decimal?)null }
        });
    }

    [HttpPut("profile")]
    public async Task<IActionResult> Update(UpdateProfileDto dto)
    {
        var name = (dto.FullName ?? "").Trim();
        if (name.Length < 2 || name.Length > 200) throw new AppException(400, "BAD_NAME", "Họ tên từ 2 đến 200 ký tự");

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(dto.Phone))
        {
            phone = Separators.Replace(dto.Phone.Trim(), "");
            if (!PhoneRx.IsMatch(phone)) throw new AppException(400, "BAD_PHONE", "Số điện thoại không hợp lệ (ví dụ 0912345678)");
        }

        DateTime? dob = null;
        if (!string.IsNullOrWhiteSpace(dto.DateOfBirth))
        {
            if (!DateTime.TryParseExact(dto.DateOfBirth.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new AppException(400, "BAD_DOB", "Ngày sinh không hợp lệ");
            if (d > DateTime.UtcNow.AddHours(7).Date || d.Year < 1900) throw new AppException(400, "BAD_DOB", "Ngày sinh không hợp lệ");
            dob = d.Date;
        }

        string? gender = null;
        if (!string.IsNullOrWhiteSpace(dto.Gender))
        {
            gender = Genders.FirstOrDefault(g => g.Equals(dto.Gender.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw new AppException(400, "BAD_GENDER", "Giới tính không hợp lệ");
        }

        var address = string.IsNullOrWhiteSpace(dto.Address) ? null : dto.Address.Trim();
        if (address is { Length: > 300 }) throw new AppException(400, "BAD_ADDRESS", "Địa chỉ tối đa 300 ký tự");

        var uid = User.UserId();
        var n = await db.Users.Where(x => x.Id == uid).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.FullName, name).SetProperty(x => x.Phone, phone)
            .SetProperty(x => x.DateOfBirth, dob).SetProperty(x => x.Gender, gender).SetProperty(x => x.Address, address));
        if (n == 0) throw new AppException(404, "USER_NOT_FOUND", "Không tìm thấy tài khoản");

        return Ok(new { message = "Đã lưu thông tin cá nhân", fullName = name });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var uid = User.UserId();
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid)
                ?? throw new AppException(401, "UNAUTHORIZED", "Tài khoản không tồn tại");

        var cur = dto.CurrentPassword ?? "";
        var next = dto.NewPassword ?? "";
        bool ok;
        try { ok = hasher.VerifyHashedPassword(u, u.PasswordHash, cur) != PasswordVerificationResult.Failed; }
        catch (FormatException) { ok = false; }
        if (!ok) throw new AppException(400, "BAD_CURRENT_PASSWORD", "Mật khẩu hiện tại không đúng");

        if (next.Length < 8) throw new AppException(400, "WEAK_PASSWORD", "Mật khẩu mới tối thiểu 8 ký tự");
        if (next == cur) throw new AppException(400, "SAME_PASSWORD", "Mật khẩu mới phải khác mật khẩu hiện tại");

        u.PasswordHash = hasher.HashPassword(u, next);
        await db.SaveChangesAsync();
        return Ok(new { message = "Đã đổi mật khẩu" });
    }
}
