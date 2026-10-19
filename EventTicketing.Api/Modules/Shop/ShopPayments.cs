using System.Globalization;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Shop;

public static class ShopPay
{
    public const string Cod = "COD", PayOs = "PAYOS";
    public const string Unpaid = "Unpaid", Pending = "Pending", Paid = "Paid";
    /// Đơn chưa trả tiền trong chừng này phút thì tự huỷ và trả hàng về kho (link payOS cũng hết hạn đúng lúc đó).
    public const int HoldMinutes = 15;
    /// orderCode payOS của đơn lưu niệm nằm ở dải riêng (>= mốc này) để không trùng với orderCode của vé.
    public const long CodeOffset = 20_000_000_000_000;

    public static bool IsPayOs(ShopOrder o) => o.PaymentMethod == PayOs;
    public static long Prefix(Guid id) => Convert.ToInt64(id.ToString("N")[..10], 16);
    public static long OrderCode(Guid id, int attempt) => CodeOffset + Prefix(id) * 10 + attempt;
}

/// Thanh toán payOS cho đơn lưu niệm: tạo link, hỏi lại payOS, nhận webhook, tự huỷ đơn quá hạn.
public class ShopPayments(AppDbContext db, PayosService payos, IMemoryCache cache, ILogger<ShopPayments> log, ShopMailer mailer)
{
    public sealed record Link(string CheckoutUrl, long Amount, string? QrCode = null, string? Bin = null, string? AccountNo = null, string? AccountName = null, string Description = "");

    /// Lấy (hoặc tạo) link thanh toán payOS cho đơn. baseUrl = địa chỉ web của mình để payOS đưa khách quay về.
    public async Task<Link> EnsureLinkAsync(ShopOrder o, string baseUrl, bool needQr = false)
    {
        var amount = (long)o.Total;
        var cacheKey = $"shop-payos:{o.Id}:{amount}";
        if (cache.TryGetValue(cacheKey, out Link? cached) && cached is not null && (!needQr || !string.IsNullOrEmpty(cached.QrCode))) return cached;

        var desc = "TGS" + o.Id.ToString("N")[..6].ToUpperInvariant();
        var back = $"{baseUrl.TrimEnd('/')}/Shop/Pay/{o.Id}";
        var cancel = back + "?cancel=true";
        var expiredAt = new DateTimeOffset(o.CreatedAt.AddMinutes(ShopPay.HoldMinutes), TimeSpan.Zero).ToUnixTimeSeconds();
        if (expiredAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120) expiredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120;

        string? lastDesc = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var code = ShopPay.OrderCode(o.Id, attempt);
            var sig = payos.Sign($"amount={amount}&cancelUrl={cancel}&description={desc}&orderCode={code}&returnUrl={back}");
            var res = await payos.Call(HttpMethod.Post, "/v2/payment-requests", new { orderCode = code, amount, description = desc, cancelUrl = cancel, returnUrl = back, expiredAt, signature = sig });
            if (PayosService.Str(res, "code") == "00" && res.TryGetProperty("data", out var created) && !string.IsNullOrEmpty(PayosService.Str(created, "checkoutUrl")))
            {
                var link = new Link(PayosService.Str(created, "checkoutUrl")!, amount, PayosService.Str(created, "qrCode"), PayosService.Str(created, "bin"), PayosService.Str(created, "accountNumber"), PayosService.Str(created, "accountName"), desc);
                cache.Set(cacheKey, link, TimeSpan.FromMinutes(10));
                return link;
            }
            lastDesc = PayosService.Str(res, "desc");
            // Mã này đã dùng: link còn chờ thì dùng lại; ngược lại thử mã kế tiếp
            var info = await payos.Call(HttpMethod.Get, $"/v2/payment-requests/{code}");
            if (PayosService.Str(info, "code") != "00" || !info.TryGetProperty("data", out var old)) break;
            if (PayosService.Str(old, "status") == "PENDING")
            {
                if (!needQr && PayosService.Str(old, "amount") == amount.ToString(CultureInfo.InvariantCulture) && !string.IsNullOrEmpty(PayosService.Str(old, "id")))
                    return new Link("https://pay.payos.vn/web/" + PayosService.Str(old, "id"), amount, Description: desc);
                await payos.Call(HttpMethod.Post, $"/v2/payment-requests/{code}/cancel", new { cancellationReason = "Đơn thay đổi số tiền" });
            }
        }
        log.LogWarning("payOS create link failed for shop order {Id}: {Desc}", o.Id, lastDesc);
        throw new AppException(502, "PAYOS_ERROR", lastDesc ?? "Không tạo được link thanh toán payOS");
    }

    /// Hỏi payOS xem đơn đã PAID chưa (chạy được cả khi webhook không tới được server). Trả true nếu đơn đã được ghi nhận thanh toán.
    public async Task<bool> CheckAsync(ShopOrder o)
    {
        if (o.PaymentStatus == ShopPay.Paid) return true;
        if (!payos.On || !ShopPay.IsPayOs(o)) return false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var info = await payos.Call(HttpMethod.Get, $"/v2/payment-requests/{ShopPay.OrderCode(o.Id, attempt)}");
            if (PayosService.Str(info, "code") != "00" || !info.TryGetProperty("data", out var d)) break;
            if (PayosService.Str(d, "status") != "PAID") continue;
            await SettleAsync(o.Id, PayosService.Dec(PayosService.Str(d, "amountPaid")), "PAYOS-" + PayosService.Str(d, "id"));
            break;
        }
        return await db.ShopOrders.AsNoTracking().AnyAsync(x => x.Id == o.Id && x.PaymentStatus == ShopPay.Paid);
    }

    /// Ghi nhận đã thanh toán. Idempotent (webhook gửi lặp), khoá dòng đơn trong 1 transaction.
    public async Task<bool> SettleAsync(Guid orderId, decimal paid, string reference)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var o = (await db.ShopOrders.FromSqlInterpolated($"SELECT * FROM ShopOrders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId}").ToListAsync()).SingleOrDefault();
        if (o is null || !ShopPay.IsPayOs(o)) return false;
        if (o.PaymentStatus == ShopPay.Paid) return true;
        if (paid < o.Total) { log.LogWarning("payOS shop order {Id}: paid {Paid} < total {Total}", o.Id, paid, o.Total); return false; }
        if (o.Status == ShopOrderStatus.Cancelled)
        {
            // Đơn đã huỷ (quá hạn / khách huỷ) và hàng đã trả kho nhưng tiền vẫn về: không tự phục hồi đơn, cần hoàn tiền thủ công
            log.LogWarning("payOS payment {Ref} for CANCELLED shop order {Id} ({Amount}) -> REFUND NEEDED", reference, o.Id, paid);
            return false;
        }
        o.PaymentStatus = ShopPay.Paid; o.PaidAt = DateTime.UtcNow; o.PaymentRef = reference.Length > 100 ? reference[..100] : reference;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        try
        {
            db.Notifications.Add(new Notification { UserId = o.UserId, Title = "Thanh toán đơn lưu niệm thành công", Message = $"Đơn #{o.Id.ToString("N")[..8].ToUpperInvariant()} đã được thanh toán, cửa hàng sẽ sớm xác nhận và giao hàng.", Link = "/Shop?tab=orders" });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Create notification failed for shop order {Id}", o.Id); }
        mailer.Queue(o.Id, paid: true);
        return true;
    }

    /// Webhook: tìm đơn theo orderCode rồi ghi nhận thanh toán.
    public async Task<string> SettleByCodeAsync(long orderCode, decimal amount, string reference)
    {
        var since = DateTime.UtcNow.AddDays(-14);
        var ids = await db.ShopOrders.AsNoTracking().Where(x => x.PaymentMethod == ShopPay.PayOs && x.PaymentStatus != ShopPay.Paid && x.CreatedAt > since).Select(x => x.Id).ToListAsync();
        var hit = ids.Where(g => ShopPay.Prefix(g) == (orderCode - ShopPay.CodeOffset) / 10).ToList();
        if (hit.Count != 1)
        {
            log.LogWarning("payOS payment {Ref} ({Amount}) orderCode {Code}: {Count} shop orders matched -> check manually / refund", reference, amount, orderCode, hit.Count);
            return "no matching pending shop order";
        }
        return await SettleAsync(hit[0], amount, reference) ? "ok" : "not settled";
    }

    /// Huỷ đơn payOS quá hạn chưa trả: hỏi payOS lần cuối (phòng tiền về trễ), nếu vẫn chưa trả thì huỷ link + huỷ đơn + trả kho.
    public async Task<int> ExpireAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-(ShopPay.HoldMinutes + 2));
        var due = await db.ShopOrders.AsNoTracking()
            .Where(o => o.PaymentMethod == ShopPay.PayOs && o.PaymentStatus == ShopPay.Pending && o.Status == ShopOrderStatus.New && o.CreatedAt < cutoff).ToListAsync();
        var n = 0;
        foreach (var o in due)
        {
            try
            {
                if (payos.On && await CheckAsync(o)) continue;
                if (payos.On)
                    for (var a = 0; a < 6; a++)
                    {
                        var code = ShopPay.OrderCode(o.Id, a);
                        var info = await payos.Call(HttpMethod.Get, $"/v2/payment-requests/{code}");
                        if (PayosService.Str(info, "code") != "00" || !info.TryGetProperty("data", out var d)) break;
                        if (PayosService.Str(d, "status") == "PENDING") await payos.Call(HttpMethod.Post, $"/v2/payment-requests/{code}/cancel", new { cancellationReason = "Quá hạn thanh toán" });
                    }
                if (await CancelUnpaidAsync(o.Id)) n++;
            }
            catch (Exception ex) { log.LogWarning(ex, "Expire shop order {Id} failed", o.Id); }
        }
        return n;
    }

    /// Huỷ đơn chưa trả tiền (nguyên tử: chỉ khi vẫn New + Pending) và trả hàng về kho.
    public async Task<bool> CancelUnpaidAsync(Guid id, Guid? userId = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var n = await db.ShopOrders.Where(o => o.Id == id && (userId == null || o.UserId == userId) && o.Status == ShopOrderStatus.New && o.PaymentStatus == ShopPay.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, ShopOrderStatus.Cancelled).SetProperty(o => o.PaymentStatus, ShopPay.Unpaid));
        if (n != 1) return false;
        await ShopStock.RestockAsync(db, id);
        await tx.CommitAsync();
        return true;
    }
}

/// Mỗi phút huỷ các đơn lưu niệm payOS quá hạn chưa thanh toán.
public class ShopPayExpiryWorker(IServiceScopeFactory scopes, ILogger<ShopPayExpiryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<ShopPayments>().ExpireAsync();
                if (n > 0) log.LogInformation("Cancelled {N} unpaid payOS shop orders", n);
            }
            catch (Exception ex) { log.LogError(ex, "Shop pay expiry worker error"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
