using System.Globalization;
using System.Net;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Auth;
using EventTicketing.Modules.Tickets;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Shop;

/// Gửi email xác nhận đơn lưu niệm. Chạy nền (scope riêng) để SMTP chậm/lỗi không làm treo hay hỏng việc đặt hàng.
public class ShopMailer(IServiceScopeFactory scopes, ILogger<ShopMailer> log)
{
    /// paid = false: vừa đặt đơn (COD) | paid = true: payOS đã thanh toán xong.
    public void Queue(Guid orderId, bool paid) =>
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId);
                if (o is null) return;
                var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == o.UserId);
                if (u is null || string.IsNullOrWhiteSpace(u.Email)) return;
                var items = await db.ShopOrderItems.AsNoTracking().Where(i => i.OrderId == orderId).ToListAsync();
                var code = o.Id.ToString("N")[..8].ToUpperInvariant();
                var subject = paid ? $"Đã thanh toán đơn lưu niệm #{code}" : $"Xác nhận đơn lưu niệm #{code}";
                await email.SendAsync(u.Email, subject, Build(o, items, u.FullName, code, paid));
            }
            catch (Exception ex) { log.LogError(ex, "Send shop order email failed for {Id}", orderId); }
        });

    private static string Money(decimal v) => v.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " đ";
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Build(ShopOrder o, List<ShopOrderItem> items, string? name, string code, bool paid)
    {
        var hello = string.IsNullOrWhiteSpace(name) ? "Xin chào" : $"Xin chào <b>{H(name.Trim())}</b>";
        var isPayos = ShopPay.IsPayOs(o);
        var rows = string.Concat(items.Select(i =>
            $"<tr><td style=\"padding:8px 0;border-bottom:1px solid #e6ebf5;font-size:14px\">{H(i.ProductName)}{(string.IsNullOrEmpty(i.VariantName) ? "" : " (" + H(i.VariantName) + ")")}<div style=\"color:#66758f;font-size:12px\">{Money(i.UnitPrice)} × {i.Quantity}</div></td>" +
            $"<td align=\"right\" style=\"padding:8px 0;border-bottom:1px solid #e6ebf5;font-size:14px;font-weight:700\">{Money(i.UnitPrice * i.Quantity)}</td></tr>"));
        var discount = o.DiscountAmount > 0
            ? $"<tr><td style=\"padding:6px 0;font-size:14px;color:#1c8a4a\">Mã giảm giá {H(o.CouponCode)}</td><td align=\"right\" style=\"padding:6px 0;font-size:14px;color:#1c8a4a\">-{Money(o.DiscountAmount)}</td></tr>" : "";
        var vipRow = o.VipDiscount > 0
            ? $"<tr><td style=\"padding:6px 0;font-size:14px;color:#1c8a4a\">Ưu đãi khách VIP</td><td align=\"right\" style=\"padding:6px 0;font-size:14px;color:#1c8a4a\">-{Money(o.VipDiscount)}</td></tr>" : "";
        var lead = paid
            ? "Chúng tôi đã nhận được thanh toán cho đơn hàng của bạn. Cửa hàng sẽ sớm xác nhận và giao hàng."
            : isPayos
                ? "Đơn hàng của bạn đã được tạo và đang chờ thanh toán qua payOS."
                : "Cảm ơn bạn đã đặt hàng! Cửa hàng sẽ sớm xác nhận và giao hàng, bạn thanh toán khi nhận hàng.";
        var pay = isPayos ? (paid ? "Đã thanh toán qua payOS" : "Chờ thanh toán qua payOS") : "Thanh toán khi nhận hàng (COD)";
        var note = string.IsNullOrWhiteSpace(o.Note) ? "" : $"<br>Ghi chú: {H(o.Note)}";
        var body = $$"""
            <p style="margin:0 0 6px;font-size:17px">{{hello}} 🛍️</p>
            <p style="margin:0 0 4px">{{lead}}</p>
            <p style="margin:14px 0 6px;font-size:14px;color:#66758f">Mã đơn: <b style="color:#0f1d44">#{{code}}</b> · {{o.CreatedAt.AddHours(7):HH:mm dd/MM/yyyy}}</p>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:6px">{{rows}}{{vipRow}}{{discount}}
              <tr><td style="padding:10px 0 0;font-size:16px;font-weight:800">Tổng cộng</td><td align="right" style="padding:10px 0 0;font-size:18px;font-weight:800;color:#3050e0">{{Money(o.Total)}}</td></tr>
            </table>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin-top:18px;background:#eef1ff;border-radius:14px"><tr><td style="padding:14px 16px;font-size:14px;line-height:1.6">
              <b>Giao tới:</b> {{H(o.ReceiverName)}} · {{H(o.Phone)}}<br>{{H(o.Address)}}{{note}}<br><b>Thanh toán:</b> {{pay}}
            </td></tr></table>
            <p style="margin:18px 0 0;font-size:14px">Bạn có thể theo dõi đơn tại mục <b>Cửa hàng → Đơn hàng của tôi</b> trên website.</p>
            """;
        return EmailTemplates.Shell(paid ? "Đơn lưu niệm đã được thanh toán" : "Cảm ơn bạn đã đặt hàng", paid ? "✅" : "🛍️", paid ? "Thanh toán thành công" : "Đặt hàng thành công", body);
    }
}
