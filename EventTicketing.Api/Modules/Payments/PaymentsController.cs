using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Payments;

public record WebhookDto(Guid BookingId, string TransactionRef, string Status, decimal Amount);

/// Giao dịch ngân hàng báo về (định dạng webhook của SePay / Casso: có transferType, transferAmount, content...).
public record BankTxDto(long? Id, string? Gateway, string? Content, string? Code, string? Description, string? TransferType, decimal TransferAmount, string? ReferenceCode);

[ApiController, Route("api/payments")]
public class PaymentsController(AppDbContext db, TicketService tickets, IConfiguration cfg, ILogger<PaymentsController> log, IMemoryCache cache, EventTicketing.Modules.Shop.ShopPayments shopPay, EventTicketing.Modules.Promo.VipNotifier vipNotifier) : ControllerBase
{
    private string Sign(string raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(cfg["Payment:WebhookSecret"]!));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        // 1) Xác thực chữ ký HMAC-SHA256 trên raw body
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();
        var sig = Request.Headers["X-Signature"].ToString().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(raw)), Encoding.UTF8.GetBytes(sig)))
            return Unauthorized(new { code = "BAD_SIGNATURE" });

        var dto = JsonSerializer.Deserialize<WebhookDto>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? throw new AppException(400, "BAD_PAYLOAD", "Payload không hợp lệ");

        if (!dto.Status.Equals("PAID", StringComparison.OrdinalIgnoreCase))
            return Ok(new { message = "ignored" });

        return await SettleAsync(dto.BookingId, dto.Amount, dto.TransactionRef, exact: true);
    }

    /// Webhook ngân hàng (SePay / Casso): khách quét QR chuyển khoản xong là tiền về đây, đơn tự chuyển sang Paid.
    /// Bảo vệ bằng header "Authorization: Apikey {Payment:BankWebhookKey}". Chưa đặt khoá thì endpoint tắt.
    [HttpPost("bank-webhook")]
    public async Task<IActionResult> BankWebhook([FromBody] BankTxDto dto)
    {
        var key = cfg["Payment:BankWebhookKey"];
        if (string.IsNullOrWhiteSpace(key)) return NotFound();
        var got = Request.Headers["Authorization"].ToString().Trim();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes("Apikey " + key)))
            return Unauthorized(new { success = false, code = "BAD_KEY" });

        if (!string.Equals(dto.TransferType, "in", StringComparison.OrdinalIgnoreCase))
            return Ok(new { success = true, message = "ignored (not incoming)" });

        // Nội dung chuyển khoản có dạng "TG" + 8 ký tự đầu của mã đơn (khớp PaymentQrController.TransferContent)
        var text = $"{dto.Content} {dto.Code} {dto.Description}".ToUpperInvariant();
        var m = Regex.Match(text, "TG([0-9A-F]{8})");
        if (!m.Success) m = Regex.Match(Regex.Replace(text, "[^A-Z0-9]", ""), "TG([0-9A-F]{8})");
        if (!m.Success) return Ok(new { success = true, message = "no order code in transfer content" });
        var prefix = m.Groups[1].Value;

        var pending = await db.Bookings.AsNoTracking().Where(x => x.Status == BookingStatus.Pending || x.Status == BookingStatus.Expired).Select(x => x.Id).ToListAsync();
        var hit = pending.Where(g => g.ToString("N")[..8].Equals(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count != 1)
        {
            log.LogWarning("Bank transfer {Ref} ({Amount}) with code TG{Prefix}: {Count} pending bookings matched -> check manually / refund", dto.ReferenceCode ?? dto.Id?.ToString(), dto.TransferAmount, prefix, hit.Count);
            return Ok(new { success = true, message = "no matching pending booking" });
        }

        var txRef = "BANK-" + (dto.ReferenceCode ?? dto.Id?.ToString() ?? Guid.NewGuid().ToString("N"));
        var res = await SettleAsync(hit[0], dto.TransferAmount, txRef, exact: false);
        if (res is BadRequestObjectResult) return Ok(new { success = false, message = "amount is less than the order total" });
        return res;
    }


    // ================= payOS (cổng thanh toán thật, cá nhân đăng ký được) =================
    // Luồng: khách bấm "Thanh toán bằng payOS" -> server tạo link -> khách trả tiền trên trang payOS -> payOS gọi webhook
    // (hoặc trang thanh toán tự hỏi lại payOS khi khách quay về) -> đơn Paid, phát vé, gửi email, trang tự mở vé.
    // Cấu hình: Payment:PayOS:ClientId / ApiKey / ChecksumKey (lấy ở my.payos.vn > Kênh thanh toán).
    private static readonly HttpClient PayosHttp = new() { BaseAddress = new Uri("https://api-merchant.payos.vn"), Timeout = TimeSpan.FromSeconds(20) };

    private bool PayosOn => !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ClientId"])
                         && !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ApiKey"])
                         && !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ChecksumKey"]);

    // orderCode của payOS phải là số nguyên: 40 bit đầu của mã đơn x 10 + lần thử (tạo lại link sau khi huỷ thì tăng lần thử)
    private static long PayosPrefix(Guid id) => Convert.ToInt64(id.ToString("N")[..10], 16);
    private static long PayosOrderCode(Guid id, int attempt) => PayosPrefix(id) * 10 + attempt;

    private string PayosSign(string raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(cfg["Payment:PayOS:ChecksumKey"]!));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private async Task<JsonElement> PayosCall(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("x-client-id", cfg["Payment:PayOS:ClientId"]);
        req.Headers.Add("x-api-key", cfg["Payment:PayOS:ApiKey"]);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var res = await PayosHttp.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "payOS call {Path} failed", path);
            throw new AppException(502, "PAYOS_ERROR", "Không kết nối được payOS, vui lòng thử lại");
        }
    }

    [HttpGet("payos/enabled")]
    public IActionResult PayosEnabled() => Ok(new { enabled = PayosOn });

    private sealed record PayosLink(string CheckoutUrl, string? QrCode, string? Bin, string? AccountNo, string? AccountName, long Amount, string Description);

    /// Lấy (hoặc tạo) link thanh toán payOS cho đơn. needQr = true: cần mã QR + thông tin tài khoản để hiện ngay trên trang (không chuyển khách đi).
    /// payOS chỉ trả mã QR lúc tạo link, nên link vừa tạo được nhớ 10 phút (theo đơn + số tiền) để mở lại trang không phải tạo link mới.
    private async Task<PayosLink> PayosEnsureLink(Booking b, bool needQr)
    {
        var amount = (long)b.TotalAmount;
        var cacheKey = $"payos-link:{b.Id}:{amount}";
        if (cache.TryGetValue(cacheKey, out PayosLink? cached) && cached is not null) return cached;

        var desc = "TG" + b.Id.ToString("N")[..6].ToUpperInvariant();   // payOS giới hạn độ dài nội dung
        var baseUrl = (cfg["Payment:PayOS:BaseUrl"] ?? cfg["Web:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        var back = $"{baseUrl}/Event/Checkout/{b.Id}";

        string? lastDesc = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var code = PayosOrderCode(b.Id, attempt);
            var sig = PayosSign($"amount={amount}&cancelUrl={back}&description={desc}&orderCode={code}&returnUrl={back}");
            var res = await PayosCall(HttpMethod.Post, "/v2/payment-requests", new { orderCode = code, amount, description = desc, cancelUrl = back, returnUrl = back, signature = sig });
            if (Str(res, "code") == "00" && res.TryGetProperty("data", out var created) && !string.IsNullOrEmpty(Str(created, "checkoutUrl")))
            {
                var link = new PayosLink(Str(created, "checkoutUrl")!, Str(created, "qrCode"), Str(created, "bin"), Str(created, "accountNumber"), Str(created, "accountName"), amount, desc);
                cache.Set(cacheKey, link, TimeSpan.FromMinutes(10));
                return link;
            }

            lastDesc = Str(res, "desc");
            // Mã này đã dùng (tạo link trước đó): link còn chờ và đúng số tiền thì dùng lại; sai số tiền (hoặc cần lấy QR mới) thì huỷ rồi thử mã kế tiếp
            var info = await PayosCall(HttpMethod.Get, $"/v2/payment-requests/{code}");
            if (Str(info, "code") != "00" || !info.TryGetProperty("data", out var old)) break;   // lỗi khác (sai khoá, kênh tạm dừng...) thì dừng
            if (Str(old, "status") == "PENDING")
            {
                if (!needQr && Str(old, "amount") == amount.ToString(CultureInfo.InvariantCulture) && !string.IsNullOrEmpty(Str(old, "id")))
                    return new PayosLink("https://pay.payos.vn/web/" + Str(old, "id"), null, null, null, null, amount, desc);
                await PayosCall(HttpMethod.Post, $"/v2/payment-requests/{code}/cancel", new { cancellationReason = "Đơn thay đổi số tiền" });
            }
        }
        log.LogWarning("payOS create link failed for booking {Id}: {Desc}", b.Id, lastDesc);
        throw new AppException(502, "PAYOS_ERROR", lastDesc ?? "Không tạo được link thanh toán payOS");
    }

    private async Task<Booking> PendingBookingOf(Guid bookingId)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId && x.UserId == User.UserId())
                ?? throw new AppException(404, "NOT_FOUND", "Booking không tồn tại");
        if (b.Status != BookingStatus.Pending) throw new AppException(409, "NOT_PENDING", "Đơn không còn ở trạng thái chờ thanh toán");
        return b;
    }

    [HttpPost("payos/create/{bookingId:guid}"), Authorize]
    public async Task<IActionResult> PayosCreate(Guid bookingId)
    {
        if (!PayosOn) throw new AppException(400, "PAYOS_OFF", "Chưa cấu hình payOS");
        var b = await PendingBookingOf(bookingId);
        var link = await PayosEnsureLink(b, needQr: false);
        return Ok(new { checkoutUrl = link.CheckoutUrl });
    }

    /// Mã QR chuyển khoản do payOS cấp cho đơn (hiện ngay trên trang thanh toán). Khách quét xong payOS báo tiền về
    /// (webhook, hoặc trang tự hỏi lại payOS qua payos/check) -> đơn Paid -> trang tự chuyển sang "Đặt vé thành công".
    [HttpPost("payos/qr/{bookingId:guid}"), Authorize]
    public async Task<IActionResult> PayosQr(Guid bookingId)
    {
        if (!PayosOn) throw new AppException(400, "PAYOS_OFF", "Chưa cấu hình payOS");
        var b = await PendingBookingOf(bookingId);
        var link = await PayosEnsureLink(b, needQr: true);
        if (string.IsNullOrEmpty(link.QrCode))
            throw new AppException(502, "PAYOS_ERROR", "payOS chưa trả mã QR, vui lòng thử lại");
        var png = TicketService.RenderQrPng(link.QrCode);
        return Ok(new
        {
            payload = link.QrCode,
            image = "data:image/png;base64," + Convert.ToBase64String(png),
            amount = link.Amount, content = link.Description,
            bankBin = link.Bin ?? "", bankName = VietQr.BankName(link.Bin ?? ""), accountNo = link.AccountNo ?? "", accountName = link.AccountName ?? "",
            simulate = false, checkoutUrl = link.CheckoutUrl
        });
    }

    /// Khách quay về từ payOS: hỏi lại payOS xem đơn đã PAID chưa (chạy được cả khi webhook không tới được máy local).
    [HttpPost("payos/check/{bookingId:guid}"), Authorize]
    public async Task<IActionResult> PayosCheck(Guid bookingId)
    {
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId && x.UserId == User.UserId())
                ?? throw new AppException(404, "NOT_FOUND", "Booking không tồn tại");
        if (b.Status == BookingStatus.Paid) return Ok(new { paid = true });
        if (!PayosOn || (b.Status != BookingStatus.Pending && b.Status != BookingStatus.Expired)) return Ok(new { paid = false });   // Expired: tiền có thể về trễ sau khi đơn hết hạn giữ vé

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var info = await PayosCall(HttpMethod.Get, $"/v2/payment-requests/{PayosOrderCode(b.Id, attempt)}");
            if (Str(info, "code") != "00" || !info.TryGetProperty("data", out var d)) break;   // chưa có lần thử này thì các lần sau cũng chưa có
            log.LogInformation("payOS check booking {Id} attempt {A}: status {S}", b.Id, attempt, Str(d, "status"));
            if (Str(d, "status") != "PAID") continue;
            decimal.TryParse(Str(d, "amountPaid"), NumberStyles.Any, CultureInfo.InvariantCulture, out var paid);
            await SettleAsync(b.Id, paid, "PAYOS-" + Str(d, "id"), exact: false);
            break;
        }
        return Ok(new { paid = await db.Bookings.AsNoTracking().AnyAsync(x => x.Id == b.Id && x.Status == BookingStatus.Paid) });
    }

    /// Webhook payOS: xác thực chữ ký HMAC-SHA256 (các trường của "data" sắp xếp theo alphabet, nối key=value bằng &).
    /// Đăng ký URL này ở my.payos.vn > Kênh thanh toán > Webhook: https://<tên miền>/api/payments/payos-webhook
    [HttpPost("payos-webhook")]
    public async Task<IActionResult> PayosWebhook()
    {
        if (!PayosOn) return NotFound();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();
        log.LogInformation("payOS webhook received ({Len} bytes)", raw.Length);
        JsonElement root;
        try { using var doc = JsonDocument.Parse(raw); root = doc.RootElement.Clone(); }
        catch (JsonException) { return BadRequest(new { success = false }); }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return Ok(new { success = true });   // payOS gửi thử khi xác nhận webhook
        var pairs = data.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name + "=" + (x.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => x.Value.GetString() ?? "",
                _ => x.Value.GetRawText()
            }));
        var expect = PayosSign(string.Join("&", pairs));
        var got = (Str(root, "signature") ?? "").ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expect), Encoding.UTF8.GetBytes(got)))
        {
            log.LogWarning("payOS webhook: BAD_SIGNATURE (kiểm tra Payment:PayOS:ChecksumKey)");
            return Unauthorized(new { success = false, code = "BAD_SIGNATURE" });
        }

        var success = root.TryGetProperty("success", out var sv) && sv.ValueKind == JsonValueKind.True;
        if (!success || Str(data, "code") != "00") return Ok(new { success = true, message = "ignored" });
        if (!long.TryParse(Str(data, "orderCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderCode)) return Ok(new { success = true, message = "bad orderCode" });
        decimal.TryParse(Str(data, "amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount);

        // orderCode ở dải riêng (>= CodeOffset) là đơn lưu niệm, không phải vé
        if (orderCode >= EventTicketing.Modules.Shop.ShopPay.CodeOffset)
        {
            var msg = await shopPay.SettleByCodeAsync(orderCode, amount, "PAYOS-" + (Str(data, "reference") ?? Str(data, "paymentLinkId") ?? orderCode.ToString(CultureInfo.InvariantCulture)));
            return Ok(new { success = true, message = msg });
        }

        var pending = await db.Bookings.AsNoTracking().Where(x => x.Status == BookingStatus.Pending || x.Status == BookingStatus.Expired).Select(x => x.Id).ToListAsync();
        var hit = pending.Where(g => PayosPrefix(g) == orderCode / 10).ToList();
        if (hit.Count != 1)
        {
            log.LogWarning("payOS payment {Ref} ({Amount}) orderCode {Code}: {Count} pending bookings matched -> check manually / refund", Str(data, "reference"), amount, orderCode, hit.Count);
            return Ok(new { success = true, message = "no matching pending booking" });
        }
        var res = await SettleAsync(hit[0], amount, "PAYOS-" + (Str(data, "reference") ?? Str(data, "paymentLinkId") ?? orderCode.ToString(CultureInfo.InvariantCulture)), exact: false);
        if (res is BadRequestObjectResult) return Ok(new { success = true, message = "amount is less than the order total" });
        return res;
    }

    /// Đánh dấu đơn đã thanh toán + tạo vé + gửi email. Idempotent, khoá dòng booking trong 1 transaction.
    /// exact = true: số tiền phải bằng đúng tổng đơn (webhook ký HMAC); false: chấp nhận chuyển dư (chuyển khoản ngân hàng).
    private async Task<IActionResult> SettleAsync(Guid bookingId, decimal amount, string transactionRef, bool exact)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var booking = (await db.Bookings
            .FromSqlInterpolated($"SELECT * FROM Bookings WITH (UPDLOCK, ROWLOCK) WHERE Id = {bookingId}")
            .ToListAsync()).SingleOrDefault();

        if (booking is null) return NotFound(new { code = "BOOKING_NOT_FOUND" });
        if (booking.Status == BookingStatus.Paid) return Ok(new { success = true, message = "already processed" }); // webhook gửi lặp
        if (booking.Status == BookingStatus.Expired)
        {
            // Tiền về trễ khi đơn đã hết hạn (kho đã được trả lại): nếu kho còn đủ thì giữ lại vé và xử lý như đơn bình thường
            // (ghế khách chọn mà đã bị người khác lấy thì CreateTicketsAsync đổi sang ghế trống)
            var taken = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE TicketTypes SET AvailableQuantity = AvailableQuantity - {booking.Quantity} WHERE Id = {booking.TicketTypeId} AND AvailableQuantity >= {booking.Quantity}");
            if (taken == 1) { booking.Status = BookingStatus.Pending; log.LogInformation("Late payment for expired booking {Id}: stock re-reserved", booking.Id); }
        }
        if (booking.Status != BookingStatus.Pending)
        {
            // Đơn đã hết hạn/huỷ nhưng tiền đã thu -> cần hoàn tiền
            log.LogWarning("PAID webhook for non-pending booking {Id} ({Status}) -> REFUND NEEDED", booking.Id, booking.Status);
            return Ok(new { success = true, message = "booking not payable, refund required" });
        }
        if (exact ? amount != booking.TotalAmount : amount < booking.TotalAmount) return BadRequest(new { code = "AMOUNT_MISMATCH" });

        db.Payments.Add(new Payment { BookingId = booking.Id, Amount = amount, Status = PaymentStatus.Paid, TransactionRef = transactionRef });
        booking.Status = BookingStatus.Paid;
        var created = await tickets.CreateTicketsAsync(booking);
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        // Thông báo trên chuông cho khách (lỗi ở đây không được làm hỏng đơn đã thanh toán)
        try
        {
            db.Notifications.Add(new Notification { UserId = booking.UserId, Title = "Thanh toán thành công", Message = $"Đơn của bạn đã được thanh toán, {created.Count} vé đã sẵn sàng.", Link = $"/Account/Order/{booking.Id}" });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Create notification failed for booking {Id}", booking.Id); }

        // Khách vừa đạt ngưỡng chi tiêu tự lên VIP thì chúc mừng (không bao giờ ném lỗi)
        await vipNotifier.TryNotifyAsync(booking.UserId);

        // Gửi email sau commit — lỗi email không được làm hỏng đơn đã thanh toán
        try { await tickets.SendTicketEmailAsync(booking.Id); }
        catch (Exception ex) { log.LogError(ex, "Send ticket email failed for booking {Id}", booking.Id); }

        return Ok(new { success = true, message = "ok", tickets = created.Count });
    }

    // Giả lập cổng thanh toán: chạy ở Development, hoặc khi bật Payment:AllowSimulate (máy chủ demo).
    // Chỉ chủ đơn mới được giả lập thanh toán cho đơn của mình. Tắt AllowSimulate khi nối cổng thanh toán thật.
    [HttpPost("simulate/{bookingId:guid}"), Authorize]
    public async Task<IActionResult> Simulate(Guid bookingId, [FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment() && !cfg.GetValue("Payment:AllowSimulate", false)) return NotFound();
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId && x.UserId == User.UserId())
                ?? throw new AppException(404, "NOT_FOUND", "Booking không tồn tại");
        var payload = JsonSerializer.Serialize(new { bookingId, transactionRef = $"SIM-{Guid.NewGuid():N}", status = "PAID", amount = b.TotalAmount });
        return Ok(new { payload, signature = Sign(payload) });
    }
}
