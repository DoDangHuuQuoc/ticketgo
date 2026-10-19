using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace EventTicketing.Modules.Auth;

public record RegisterDto(string Email, string Password, string FullName);
public record VerifyEmailDto(string Email, string Otp);
public record LoginDto(string Email, string Password);
public record ForgotDto(string Email);
public record ResetPasswordDto(string Email, string Otp, string NewPassword);

public class AuthService(AppDbContext db, IConnectionMultiplexer redis, IEmailService email,
    IPasswordHasher<User> hasher, IConfiguration cfg, IMemoryCache cache, ILogger<AuthService> log, WelcomeCouponService welcome)
{
    private IDatabase R => redis.GetDatabase();
    private static string OtpKey(string e) => $"otp:{e}";
    private static string OtpTries(string e) => $"otp:tries:{e}";

    // OTP lưu ở Redis; nếu Redis chưa bật / không với tới được (vd. máy chủ hosting) thì dùng bộ nhớ của ứng dụng
    // để đăng ký và đăng nhập vẫn hoạt động thay vì báo "Lỗi hệ thống".
    private sealed class Counter { public long N; }
    private async Task<string?> KvGet(string k)
    {
        if (redis.IsConnected)
            try { var v = await R.StringGetAsync(k); return v.IsNullOrEmpty ? null : v.ToString(); } catch (RedisException ex) { log.LogWarning(ex, "Redis lỗi, dùng bộ nhớ"); }
        return cache.TryGetValue(k, out string? s) ? s : null;
    }
    private async Task KvSet(string k, string v, TimeSpan ttl)
    {
        if (redis.IsConnected)
            try { await R.StringSetAsync(k, v, ttl); return; } catch (RedisException ex) { log.LogWarning(ex, "Redis lỗi, dùng bộ nhớ"); }
        cache.Set(k, v, ttl);
    }
    private async Task KvDel(params string[] keys)
    {
        foreach (var k in keys)
        {
            if (redis.IsConnected) try { await R.KeyDeleteAsync(k); } catch (RedisException) { /* bỏ qua */ }
            cache.Remove(k);
        }
    }
    private async Task<long> KvIncr(string k, TimeSpan ttl)
    {
        if (redis.IsConnected)
            try { var n = await R.StringIncrementAsync(k); if (n == 1) await R.KeyExpireAsync(k, ttl); return n; } catch (RedisException ex) { log.LogWarning(ex, "Redis lỗi, dùng bộ nhớ"); }
        var c = cache.GetOrCreate(k, e => { e.AbsoluteExpirationRelativeToNow = ttl; return new Counter(); })!;
        return Interlocked.Increment(ref c.N);
    }

    public async Task RegisterAsync(RegisterDto dto)
    {
        var mail = dto.Email.Trim().ToLowerInvariant();
        if (dto.Password.Length < 8) throw new AppException(400, "WEAK_PASSWORD", "Mật khẩu tối thiểu 8 ký tự");
        if (await db.Users.AnyAsync(u => u.Email == mail))
            throw new AppException(409, "EMAIL_EXISTS", "Email đã được đăng ký");

        var user = new User { Email = mail, FullName = dto.FullName.Trim(), IsVerified = false };
        user.PasswordHash = hasher.HashPassword(user, dto.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await SendOtpAsync(mail);
    }

    public async Task SendOtpAsync(string mail)
    {
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var ttl = TimeSpan.FromMinutes(cfg.GetValue("Otp:TtlMinutes", 5));
        await KvSet(OtpKey(mail), HashOtp(mail, otp), ttl);   // chỉ lưu HASH
        await KvDel(OtpTries(mail));

        try
        {
            var name = await db.Users.AsNoTracking().Where(u => u.Email == mail).Select(u => u.FullName).FirstOrDefaultAsync();
            await email.SendAsync(mail, "✉️ Mã xác thực email TicketGo của bạn", EmailTemplates.Otp(OtpKind.Verify, name, otp, (int)ttl.TotalMinutes));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Không gửi được email OTP tới {Mail}. Kiểm tra mục Smtp trong appsettings.", mail);
            throw new AppException(502, "EMAIL_FAILED", "Không gửi được email xác thực. Bạn có thể bấm “Gửi lại mã” sau ít phút hoặc liên hệ quản trị viên để được xác thực tài khoản.");
        }
    }

    private static string ResetKey(string e) => $"pwreset:{e}";
    private static string ResetTries(string e) => $"pwreset:tries:{e}";
    private static string ResetSends(string e) => $"pwreset:sends:{e}";

    /// Quên mật khẩu: gửi OTP tới email. Luôn trả kết quả giống nhau dù email có tồn tại hay không (không lộ danh sách tài khoản).
    public async Task ForgotPasswordAsync(string rawEmail)
    {
        var mail = (rawEmail ?? "").Trim().ToLowerInvariant();
        if (mail.Length < 5 || mail.Length > 256 || !mail.Contains('@')) throw new AppException(400, "BAD_EMAIL", "Email chưa đúng định dạng");
        if (await KvIncr(ResetSends(mail), TimeSpan.FromHours(1)) > 5)
            throw new AppException(429, "TOO_MANY", "Bạn đã yêu cầu quá nhiều lần. Vui lòng thử lại sau 1 giờ.");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == mail);
        if (user is null || user.IsLocked) return;

        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var ttl = TimeSpan.FromMinutes(cfg.GetValue("Otp:TtlMinutes", 5));
        await KvSet(ResetKey(mail), HashOtp("reset:" + mail, otp), ttl);
        await KvDel(ResetTries(mail));
        try { await email.SendAsync(mail, "🔐 Mã đặt lại mật khẩu TicketGo", EmailTemplates.Otp(OtpKind.Reset, user.FullName, otp, (int)ttl.TotalMinutes)); }
        catch (Exception ex)
        {
            log.LogError(ex, "Không gửi được email đặt lại mật khẩu tới {Mail}. Kiểm tra mục Smtp trong appsettings.", mail);
            throw new AppException(502, "EMAIL_FAILED", "Không gửi được email lúc này. Vui lòng thử lại sau ít phút hoặc liên hệ hỗ trợ.");
        }
    }

    /// Đặt lại mật khẩu bằng OTP đã nhận qua email.
    public async Task ResetPasswordAsync(ResetPasswordDto dto)
    {
        var mail = (dto.Email ?? "").Trim().ToLowerInvariant();
        var otp = (dto.Otp ?? "").Trim();
        if ((dto.NewPassword ?? "").Length < 8) throw new AppException(400, "WEAK_PASSWORD", "Mật khẩu tối thiểu 8 ký tự");
        if (dto.NewPassword!.Length > 128) throw new AppException(400, "PASSWORD_LONG", "Mật khẩu tối đa 128 ký tự");

        var tries = await KvIncr(ResetTries(mail), TimeSpan.FromMinutes(10));
        if (tries > 5) throw new AppException(429, "OTP_LOCKED", "Nhập sai quá nhiều lần, vui lòng gửi lại mã");

        var stored = await KvGet(ResetKey(mail));
        if (string.IsNullOrEmpty(stored)) throw new AppException(400, "OTP_EXPIRED", "Mã OTP đã hết hạn hoặc chưa được gửi");
        var a = Encoding.UTF8.GetBytes(stored);
        var b = Encoding.UTF8.GetBytes(HashOtp("reset:" + mail, otp));
        if (!CryptographicOperations.FixedTimeEquals(a, b)) throw new AppException(400, "OTP_INVALID", "Mã OTP không đúng");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == mail) ?? throw new AppException(404, "USER_NOT_FOUND", "Không tìm thấy tài khoản");
        var firstVerify = !user.IsVerified;
        user.PasswordHash = hasher.HashPassword(user, dto.NewPassword);
        user.IsVerified = true;   // đã chứng minh quyền sở hữu email qua OTP
        await db.SaveChangesAsync();
        await KvDel(ResetKey(mail), ResetTries(mail));
        if (firstVerify) await welcome.TryIssueAsync(user.Id);   // tài khoản chưa xác thực mà xác thực được qua đặt lại MK: vẫn tính là tạo tài khoản thành công

        try { await email.SendAsync(mail, "✅ Mật khẩu TicketGo đã được thay đổi", EmailTemplates.PasswordChanged(user.FullName, DateTime.Now)); }
        catch (Exception ex) { log.LogWarning(ex, "Không gửi được email báo đổi mật khẩu tới {Mail}", mail); }
    }

    public async Task VerifyEmailAsync(VerifyEmailDto dto)
    {
        var mail = dto.Email.Trim().ToLowerInvariant();
        var tries = await KvIncr(OtpTries(mail), TimeSpan.FromMinutes(10));
        if (tries > 5) throw new AppException(429, "OTP_LOCKED", "Nhập sai quá nhiều lần, vui lòng gửi lại mã");

        var stored = await KvGet(OtpKey(mail));
        if (string.IsNullOrEmpty(stored)) throw new AppException(400, "OTP_EXPIRED", "Mã OTP đã hết hạn");

        var a = Encoding.UTF8.GetBytes(stored);
        var b = Encoding.UTF8.GetBytes(HashOtp(mail, dto.Otp));
        if (!CryptographicOperations.FixedTimeEquals(a, b))
            throw new AppException(400, "OTP_INVALID", "Mã OTP không đúng");

        var before = await db.Users.AsNoTracking().Where(u => u.Email == mail).Select(u => new { u.Id, u.IsVerified }).FirstOrDefaultAsync();
        var n = await db.Users.Where(u => u.Email == mail).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsVerified, true));
        if (n == 0 || before is null) throw new AppException(404, "USER_NOT_FOUND", "Không tìm thấy tài khoản");
        await KvDel(OtpKey(mail), OtpTries(mail));

        // Tạo tài khoản thành công (xác thực email lần đầu): tặng mã chào mừng theo cấu hình admin. Chỉ với tài khoản vừa được xác thực
        // (khách cũ xin OTP rồi xác thực lại không nhận thêm); lỗi cấp mã không được làm hỏng việc xác thực.
        if (!before.IsVerified) await welcome.TryIssueAsync(before.Id);
    }

    public async Task<object> LoginAsync(LoginDto dto)
    {
        var mail = dto.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == mail);
        if (user is null || !PasswordOk(user, dto.Password))
            throw new AppException(401, "BAD_CREDENTIALS", "Sai email hoặc mật khẩu");
        if (user.IsLocked) throw new AppException(403, "ACCOUNT_LOCKED", "Tài khoản đã bị khoá. Vui lòng liên hệ hỗ trợ.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: cfg["Jwt:Issuer"],
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role)
            },
            expires: DateTime.UtcNow.AddMinutes(cfg.GetValue("Jwt:ExpiresMinutes", 120)),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), isVerified = user.IsVerified, user.FullName, role = user.Role };
    }

    // Hash hỏng/không đúng định dạng (vd. dữ liệu nhập tay vào DB) coi như sai mật khẩu, không để văng 500
    private bool PasswordOk(User user, string password)
    {
        try { return hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed; }
        catch (FormatException) { return false; }
    }

    private string HashOtp(string mail, string otp)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(cfg["Otp:Secret"]!));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{mail}:{otp}")));
    }
}
