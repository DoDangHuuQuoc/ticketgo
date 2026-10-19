using System.Net;

namespace EventTicketing.Modules.Auth;

public enum OtpKind { Verify, Reset }

/// Mẫu email giao diện đẹp, dùng bảng + style nội tuyến để hiển thị ổn trên Gmail/Outlook.
/// Hiệu ứng động (@keyframes) chỉ chạy trên các ứng dụng mail hỗ trợ (Apple Mail, iOS Mail, Samsung Mail, Thunderbird…);
/// Gmail/Outlook sẽ bỏ qua hiệu ứng nhưng vẫn hiển thị đầy đủ nội dung và màu sắc.
public static class EmailTemplates
{
    private const string Brand = "#3050e0", Navy = "#0f1d44", Gold = "#f0a93a";

    internal static string Shell(string preheader, string emoji, string headline, string bodyHtml) => $$"""
        <!doctype html>
        <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="color-scheme" content="light"><title>TicketGo</title>
        <style>
          @keyframes bob { 0% { transform: translateY(0) } 50% { transform: translateY(-7px) } 100% { transform: translateY(0) } }
          @keyframes pop { 0% { transform: scale(.6); opacity: 0 } 70% { transform: scale(1.12); opacity: 1 } 100% { transform: scale(1); opacity: 1 } }
          @keyframes glow { 0% { box-shadow: 0 0 0 0 rgba(240,169,58,.55) } 100% { box-shadow: 0 0 0 14px rgba(240,169,58,0) } }
          @keyframes shine { 0% { background-position: 0% 50% } 100% { background-position: 100% 50% } }
          .bob { animation: bob 2.2s ease-in-out infinite }
          .pop { animation: pop .6s cubic-bezier(.2,.9,.3,1.3) both }
          .glow { animation: glow 1.8s ease-out infinite }
          .hero { background-size: 200% 200%; animation: shine 6s ease-in-out infinite alternate }
          @media (max-width: 520px) { .card { border-radius: 0 !important } .digit { width: 42px !important; height: 54px !important; font-size: 24px !important } }
          @media (prefers-reduced-motion: reduce) { .bob, .pop, .glow, .hero { animation: none !important } }
        </style></head>
        <body style="margin:0;padding:0;background:#eef1ff;font-family:'Segoe UI',Roboto,Arial,sans-serif;color:{{Navy}}">
        <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent">{{WebUtility.HtmlEncode(preheader)}}</div>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#eef1ff"><tr><td align="center" style="padding:28px 12px">
          <table role="presentation" class="card" width="560" cellpadding="0" cellspacing="0" style="width:100%;max-width:560px;background:#ffffff;border-radius:24px;overflow:hidden;box-shadow:0 14px 40px -16px rgba(15,29,68,.35)">
            <tr><td class="hero" align="center" bgcolor="{{Brand}}" style="background:linear-gradient(135deg,{{Brand}},{{Navy}},#6a3fe0);padding:34px 24px 40px">
              <div style="font-size:15px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#cfd8ff">TicketGo</div>
              <div class="bob" style="display:inline-block;margin-top:16px;width:84px;height:84px;line-height:84px;border-radius:50%;background:#ffffff;font-size:42px;text-align:center;box-shadow:0 10px 24px -8px rgba(0,0,0,.45)">{{emoji}}</div>
              <h1 style="margin:18px 0 0;font-size:26px;line-height:1.25;color:#ffffff">{{headline}}</h1>
            </td></tr>
            <tr><td style="padding:30px 32px 8px;font-size:16px;line-height:1.6;color:#34496e">{{bodyHtml}}</td></tr>
            <tr><td style="padding:8px 32px 30px"><table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td style="border-top:1px dashed #cdd6e8;padding-top:18px;font-size:13px;line-height:1.6;color:#66758f;text-align:center">
              Email tự động từ <b style="color:{{Brand}}">TicketGo</b> 🎟️ — vui lòng không trả lời trực tiếp.<br>Cần giúp đỡ? Hãy gửi yêu cầu hỗ trợ trên website.
            </td></tr></table></td></tr>
          </table>
        </td></tr></table></body></html>
        """;

    private static string Digits(string otp)
    {
        var cells = string.Concat(otp.Select((c, i) =>
            $"<td style=\"padding:0 4px\"><div class=\"digit pop\" style=\"animation-delay:{i * 90}ms;width:48px;height:60px;line-height:60px;text-align:center;font-size:28px;font-weight:800;color:{Brand};background:#eef1ff;border:2px solid {Brand};border-radius:14px\">{WebUtility.HtmlEncode(c.ToString())}</div></td>"));
        return $"<table role=\"presentation\" align=\"center\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:22px auto\"><tr>{cells}</tr></table>";
    }

    /// Email chứa mã OTP (xác thực email khi đăng ký, hoặc đặt lại mật khẩu).
    public static string Otp(OtpKind kind, string? name, string otp, int minutes)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Xin chào" : $"Xin chào <b>{WebUtility.HtmlEncode(name.Trim())}</b>";
        var (emoji, headline, lead, tip) = kind == OtpKind.Reset
            ? ("🔐", "Đặt lại mật khẩu", "Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn. Hãy nhập mã bên dưới vào trang đặt lại mật khẩu.",
               "Nếu bạn <b>không</b> yêu cầu, hãy bỏ qua email này — mật khẩu của bạn vẫn an toàn và không bị thay đổi.")
            : ("✉️", "Xác thực email của bạn", "Chỉ còn một bước nữa là bạn có thể bắt đầu săn vé yêu thích. Hãy nhập mã bên dưới để xác thực email.",
               "Nếu bạn không tạo tài khoản TicketGo, hãy bỏ qua email này.");

        var body = $$"""
            <p style="margin:0 0 6px;font-size:17px">{{hello}} 👋</p>
            <p style="margin:0">{{lead}}</p>
            {{Digits(otp)}}
            <div style="text-align:center;margin:0 0 22px">
              <span class="glow" style="display:inline-block;padding:9px 18px;border-radius:999px;background:#fff6e4;color:#b86e00;font-weight:700;font-size:14px;border:1px solid {{Gold}}">⏳ Mã có hiệu lực trong {{minutes}} phút</span>
            </div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#e5f7f0;border-radius:14px"><tr><td style="padding:14px 16px;font-size:14px;line-height:1.55;color:#0b6b4b">
              🔒 <b>Giữ bí mật mã này.</b> Nhân viên TicketGo không bao giờ hỏi mã OTP của bạn qua điện thoại hay tin nhắn.
            </td></tr></table>
            <p style="margin:18px 0 0;font-size:14px;color:#66758f">{{tip}}</p>
            """;
        return Shell($"Mã OTP của bạn là {otp} (hết hạn sau {minutes} phút)", emoji, headline, body);
    }

    /// Email báo mật khẩu vừa được đổi (cảnh báo bảo mật).
    public static string PasswordChanged(string? name, DateTime whenLocal)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Xin chào" : $"Xin chào <b>{WebUtility.HtmlEncode(name.Trim())}</b>";
        var body = $$"""
            <p style="margin:0 0 6px;font-size:17px">{{hello}} 🎉</p>
            <p style="margin:0">Mật khẩu tài khoản TicketGo của bạn đã được đổi thành công lúc <b>{{whenLocal:HH:mm 'ngày' dd/MM/yyyy}}</b>. Bạn có thể đăng nhập bằng mật khẩu mới ngay bây giờ.</p>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:20px;background:#fdecee;border-radius:14px"><tr><td style="padding:14px 16px;font-size:14px;line-height:1.55;color:#a3252c">
              ⚠️ <b>Không phải bạn?</b> Hãy liên hệ hỗ trợ ngay để được khoá tài khoản và bảo vệ vé của bạn.
            </td></tr></table>
            """;
        return Shell("Mật khẩu của bạn vừa được thay đổi", "✅", "Đổi mật khẩu thành công", body);
    }

    /// Email tặng mã chào mừng sau khi tạo tài khoản thành công. offText: "Giảm 10% (tối đa 50.000 đ)"; condText: điều kiện + hạn dùng.
    public static string WelcomeCoupon(string? name, string code, string offText, string condText)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Xin chào" : $"Xin chào <b>{WebUtility.HtmlEncode(name.Trim())}</b>";
        var body = $$"""
            <p style="margin:0 0 6px;font-size:17px">{{hello}} 🎉</p>
            <p style="margin:0">Tài khoản TicketGo của bạn đã sẵn sàng. TicketGo tặng bạn một mã giảm giá dành riêng cho bạn:</p>
            <div style="margin:22px 0;text-align:center">
              <div style="display:inline-block;padding:14px 26px;border-radius:16px;border:2px dashed {{Brand}};background:#eef1ff;font-size:26px;font-weight:800;letter-spacing:.08em;color:{{Brand}}">{{WebUtility.HtmlEncode(code)}}</div>
              <div style="margin-top:12px;font-size:17px;font-weight:700;color:{{Navy}}">{{WebUtility.HtmlEncode(offText)}}</div>
            </div>
            <p style="margin:0;font-size:14px;color:#66758f">{{WebUtility.HtmlEncode(condText)}}</p>
            <p style="margin:14px 0 0;font-size:14px;color:#66758f">Mã đã được lưu sẵn trong <b>Ví voucher</b> của bạn; chỉ tài khoản này dùng được. Nhập mã ở bước thanh toán hoặc chọn từ ví.</p>
            """;
        return Shell($"Quà chào mừng: mã {code}", "🎁", "Quà chào mừng thành viên mới", body);
    }

    /// Email chúc mừng khách lên VIP. benefit: mô tả ưu đãi (vd. "Giảm 5% giá vé và đơn hàng lưu niệm, tối đa 100.000 đ mỗi đơn").
    public static string VipWelcome(string? name, string benefit, bool auto)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Xin chào" : $"Xin chào <b>{WebUtility.HtmlEncode(name.Trim())}</b>";
        var why = auto ? "Cảm ơn bạn đã đồng hành cùng TicketGo — tổng chi tiêu của bạn đã đạt mức để trở thành khách VIP." : "TicketGo vừa nâng tài khoản của bạn lên khách VIP để cảm ơn sự ủng hộ của bạn.";
        var body = $$"""
            <p style="margin:0 0 6px;font-size:17px">{{hello}} 🎉</p>
            <p style="margin:0">{{why}}</p>
            <div style="margin:22px 0;text-align:center">
              <div style="display:inline-block;padding:14px 26px;border-radius:16px;background:#fff6e4;border:2px solid {{Gold}};font-size:22px;font-weight:800;color:#b86e00">★ Khách VIP</div>
              <div style="margin-top:12px;font-size:16px;font-weight:700;color:{{Navy}}">{{WebUtility.HtmlEncode(benefit)}}</div>
            </div>
            <p style="margin:0;font-size:14px;color:#66758f">Ưu đãi được trừ tự động khi bạn đặt vé hoặc mua đồ lưu niệm — không cần nhập mã. Có thể dùng cùng mã giảm giá.</p>
            """;
        return Shell("Bạn đã trở thành khách VIP của TicketGo", "👑", "Chúc mừng, bạn là khách VIP!", body);
    }
}
