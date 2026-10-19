using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Modules.Auth;

[ApiController, Route("api/auth")]
public class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    { await auth.RegisterAsync(dto); return Ok(new { message = "Đã gửi OTP tới email" }); }

    [HttpPost("verify-email")]
    public async Task<IActionResult> Verify(VerifyEmailDto dto)
    { await auth.VerifyEmailAsync(dto); return Ok(new { message = "Xác thực thành công" }); }

    [HttpPost("resend-otp")]
    public async Task<IActionResult> Resend([FromBody] string email)
    { await auth.SendOtpAsync(email.Trim().ToLowerInvariant()); return Ok(new { message = "Đã gửi lại OTP" }); }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> Forgot(ForgotDto dto)
    { await auth.ForgotPasswordAsync(dto.Email); return Ok(new { message = "Nếu email tồn tại trong hệ thống, mã OTP đã được gửi. Hãy kiểm tra hộp thư (cả mục Spam)." }); }

    [HttpPost("reset-password")]
    public async Task<IActionResult> Reset(ResetPasswordDto dto)
    { await auth.ResetPasswordAsync(dto); return Ok(new { message = "Đặt lại mật khẩu thành công" }); }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto) => Ok(await auth.LoginAsync(dto));
}
