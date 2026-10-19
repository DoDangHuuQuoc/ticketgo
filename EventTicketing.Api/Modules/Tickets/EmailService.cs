using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EventTicketing.Modules.Tickets;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string html);
    Task SendAsync(string to, string subject, BodyBuilder body);
}

public class EmailService(IConfiguration cfg) : IEmailService
{
    public Task SendAsync(string to, string subject, string html) =>
        SendAsync(to, subject, new BodyBuilder { HtmlBody = html });

    /// Smtp:From đúng dạng "Tên <mail@x.com>" thì dùng nguyên; nếu chỉ ghi tên (vd. "TicketGo") thì lấy Smtp:User làm địa chỉ
    /// (Gmail cũng chỉ cho gửi từ chính tài khoản đã đăng nhập). Tránh lỗi "Invalid address" làm mất toàn bộ email OTP/vé.
    private MailboxAddress FromAddress()
    {
        var raw = cfg["Smtp:From"]?.Trim();
        if (!string.IsNullOrEmpty(raw) && MailboxAddress.TryParse(raw, out var a) && a.Address.Contains('@')) return a;
        var user = cfg["Smtp:User"]!;
        return new MailboxAddress(string.IsNullOrEmpty(raw) ? "TicketGo" : raw, user);
    }

    public async Task SendAsync(string to, string subject, BodyBuilder body)
    {
        var msg = new MimeMessage();
        msg.From.Add(FromAddress());
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = body.ToMessageBody();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // host chặn cổng SMTP thì báo lỗi sau 20s thay vì treo
        using var smtp = new SmtpClient { Timeout = 20000 };
        await smtp.ConnectAsync(cfg["Smtp:Host"], cfg.GetValue("Smtp:Port", 587), SecureSocketOptions.StartTls, cts.Token);
        await smtp.AuthenticateAsync(cfg["Smtp:User"], cfg["Smtp:Password"], cts.Token);
        await smtp.SendAsync(msg, cts.Token);
        await smtp.DisconnectAsync(true, cts.Token);
    }
}
