using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Admin;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Payments;

public record BankSettingDto(string? BankBin, string? AccountNo, string? AccountName);
public record QrPreviewDto(string? BankBin, string? AccountNo, decimal Amount);

/// Mã QR chuyển khoản VietQR: đúng số tiền của đơn + nội dung "TG" + 8 ký tự đầu của mã đơn.
[ApiController, Authorize]
public class PaymentQrController(AppDbContext db, IConfiguration cfg, AuditService audit, IWebHostEnvironment env) : ControllerBase
{
    private sealed record BankInfo(string Bin, string Name, string AccountNo, string AccountName);

    /// Nội dung chuyển khoản của một đơn – khớp với "Mã đơn" hiển thị ở trang thanh toán.
    public static string TransferContent(Guid bookingId) => "TG" + bookingId.ToString("N")[..8].ToUpperInvariant();

    /// Ưu tiên cấu hình admin lưu trong DB; chưa có thì dùng appsettings (Payment:Bank).
    private async Task<BankInfo?> LoadBankAsync()
    {
        var s = await db.PaymentSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1);
        if (s is not null && s.BankBin.Length > 0 && s.AccountNo.Length > 0)
            return new BankInfo(s.BankBin, s.BankName, s.AccountNo, s.AccountName);

        var c = cfg.GetSection("Payment:Bank");
        var bin = c["BankBin"] ?? ""; var acc = c["AccountNo"] ?? "";
        return bin.Length > 0 && acc.Length > 0 ? new BankInfo(bin, VietQr.BankName(bin), acc, c["AccountName"] ?? "") : null;
    }

    private static object MakeQr(BankInfo bank, decimal amount, string content, bool simulate = false)
    {
        var vnd = (long)decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        string payload;
        try { payload = VietQr.Build(bank.Bin, bank.AccountNo, vnd, content); }
        catch (ArgumentException ex) { throw new AppException(400, "BAD_BANK", ex.Message); }
        var png = TicketService.RenderQrPng(payload);
        return new
        {
            payload, image = "data:image/png;base64," + Convert.ToBase64String(png),
            amount = vnd, content = VietQr.SanitizeContent(content),
            bankBin = bank.Bin, bankName = bank.Name, accountNo = bank.AccountNo, accountName = bank.AccountName, simulate
        };
    }

    // ---------- Khách hàng (chủ đơn) và Admin: QR thanh toán của một đơn đang giữ chỗ ----------
    [HttpGet("api/payments/qr/{bookingId:guid}")]
    public async Task<IActionResult> BookingQr(Guid bookingId)
    {
        var uid = User.UserId(); var admin = User.IsInRole("Admin");
        var b = await db.Bookings.AsNoTracking().Where(x => x.Id == bookingId && (admin || x.UserId == uid))
            .Select(x => new { x.Id, x.TotalAmount, x.Status }).FirstOrDefaultAsync();
        if (b is null) return NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy đơn hàng" });
        if (b.Status != BookingStatus.Pending)
            throw new AppException(409, "NOT_PENDING", "Đơn này không còn ở trạng thái chờ thanh toán");

        var bank = await LoadBankAsync()
                   ?? throw new AppException(503, "NO_BANK", "Chưa cấu hình tài khoản nhận tiền. Admin vào Quản trị › Thanh toán để thiết lập.");
        var canSim = env.IsDevelopment() || cfg.GetValue("Payment:AllowSimulate", false);   // nút "giả lập đã chuyển khoản" chỉ hiện ở chế độ thử
        return Ok(MakeQr(bank, b.TotalAmount, TransferContent(b.Id), canSim));
    }

    // ---------- Admin: cấu hình tài khoản nhận tiền ----------
    [HttpGet("api/admin/payment-settings"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSettings()
    {
        var bank = await LoadBankAsync();
        return Ok(new
        {
            configured = bank is not null,
            bankBin = bank?.Bin ?? "", accountNo = bank?.AccountNo ?? "", accountName = bank?.AccountName ?? "",
            banks = VietQr.Banks.Select(x => new { bin = x.Bin, name = x.Name })
        });
    }

    [HttpPut("api/admin/payment-settings"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveSettings(BankSettingDto dto)
    {
        var bin = (dto.BankBin ?? "").Trim(); var acc = (dto.AccountNo ?? "").Trim().Replace(" ", ""); var name = (dto.AccountName ?? "").Trim();
        if (!VietQr.Banks.Any(x => x.Bin == bin)) throw new AppException(400, "BAD_BANK", "Vui lòng chọn ngân hàng");
        if (acc.Length is < 4 or > 19 || !acc.All(char.IsAsciiLetterOrDigit)) throw new AppException(400, "BAD_ACCOUNT", "Số tài khoản chỉ gồm chữ/số, từ 4 đến 19 ký tự");
        if (name.Length == 0) throw new AppException(400, "NAME_REQUIRED", "Vui lòng nhập tên chủ tài khoản");
        if (name.Length > 100) throw new AppException(400, "NAME_TOO_LONG", "Tên chủ tài khoản tối đa 100 ký tự");

        var s = await db.PaymentSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null) { s = new PaymentSetting { Id = 1 }; db.PaymentSettings.Add(s); }
        s.BankBin = bin; s.BankName = VietQr.BankName(bin); s.AccountNo = acc; s.AccountName = name.ToUpperInvariant(); s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await audit.LogAsync(User, "payment.settings", "PaymentSetting", 1, $"{s.BankName} · {acc}");
        return Ok(new { message = "Đã lưu tài khoản nhận tiền" });
    }

    /// Xem thử QR (chưa cần lưu) với số tiền bất kỳ để kiểm tra bằng app ngân hàng.
    [HttpPost("api/admin/payment-settings/preview"), Authorize(Roles = "Admin")]
    public IActionResult Preview(QrPreviewDto dto)
    {
        var bin = (dto.BankBin ?? "").Trim(); var acc = (dto.AccountNo ?? "").Trim().Replace(" ", "");
        if (bin.Length == 0 || acc.Length == 0) throw new AppException(400, "BAD_BANK", "Chọn ngân hàng và nhập số tài khoản để xem thử QR");
        if (dto.Amount < 1000) throw new AppException(400, "BAD_AMOUNT", "Số tiền thử tối thiểu 1.000 đ");
        return Ok(MakeQr(new BankInfo(bin, VietQr.BankName(bin), acc, ""), dto.Amount, "TGTEST"));
    }
}
