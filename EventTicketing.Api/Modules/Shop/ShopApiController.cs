using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Promo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Shop;

public record ShopItemDto(Guid ProductId, int Quantity, Guid? VariantId = null);
public record ShopOrderDto(List<ShopItemDto>? Items, string? ReceiverName, string? Phone, string? Address, string? Note, string? PaymentMethod = null, string? CouponCode = null);
public record ShopCouponDto(List<ShopItemDto>? Items, string? Code);

public static class ShopStock
{
    /// Sản phẩm có phân loại: Price = giá thấp nhất, Stock = tổng tồn của các phân loại đang bán (danh sách sản phẩm dựa vào hai số này).
    /// Không có phân loại đang bán thì giữ nguyên giá/tồn của chính sản phẩm.
    public static async Task RecalcAsync(AppDbContext db, Guid productId)
    {
        var vs = await db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId && v.IsActive).Select(v => new { v.Price, v.Stock }).ToListAsync();
        if (vs.Count == 0) return;
        var price = vs.Min(v => v.Price); var stock = vs.Sum(v => v.Stock);
        await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, price).SetProperty(p => p.Stock, stock));
    }

    /// Trả hàng của một đơn về kho (khi huỷ đơn). Gọi trong transaction của nơi gọi.
    public static async Task RestockAsync(AppDbContext db, Guid orderId)
    {
        var items = await db.ShopOrderItems.AsNoTracking().Where(i => i.OrderId == orderId).ToListAsync();
        foreach (var i in items)
        {
            var pid = i.ProductId; var qty = i.Quantity; var vid = i.VariantId;
            if (vid is not null)
            {
                await db.ProductVariants.Where(v => v.Id == vid).ExecuteUpdateAsync(s => s.SetProperty(v => v.Stock, v => v.Stock + qty));
                await RecalcAsync(db, pid);
            }
            else await db.Products.Where(p => p.Id == pid).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + qty));
        }
    }
}

/// Phía khách: xem sản phẩm lưu niệm, đặt hàng (thanh toán khi nhận hàng hoặc qua payOS), xem / huỷ đơn của mình.
[ApiController, Route("api/shop")]
public class ShopApiController(AppDbContext db, PayosService payos, ShopPayments pay, EventTicketing.Modules.Coupons.CouponService coupons, ShopMailer mailer, VipService vipSvc) : ControllerBase
{
    [HttpGet("products")]
    public async Task<IActionResult> Products()
    {
        var list = await db.Products.AsNoTracking().Where(p => p.IsActive)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Name, p.Category, p.Description, p.ImageUrl, p.Price, p.Stock })
            .ToListAsync();
        var ids = list.Select(p => p.Id).ToList();
        var vars = await db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.IsActive)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name).ToListAsync();
        var rat = (await db.ShopReviews.AsNoTracking().Where(r => ids.Contains(r.ProductId) && !r.IsHidden)
            .GroupBy(r => r.ProductId).Select(x => new { Id = x.Key, N = x.Count(), Avg = x.Average(r => (double)r.Stars) }).ToListAsync())
            .ToDictionary(x => x.Id);
        return Ok(list.Select(p => new
        {
            p.Id, p.Name, p.Category, p.Description, p.ImageUrl, p.Price, p.Stock,
            rating = rat.TryGetValue(p.Id, out var rt) ? Math.Round(rt.Avg, 1) : 0, reviews = rat.TryGetValue(p.Id, out var rt2) ? rt2.N : 0,
            variants = vars.Where(v => v.ProductId == p.Id).Select(v => new { v.Id, v.Name, v.Price, v.Stock }).ToList()
        }).OrderBy(p => p.Stock <= 0));   // còn hàng lên trước, hết hàng xuống cuối
    }

    private sealed record Line(Product P, ProductVariant? V, int Qty)
    {
        public decimal Price => V?.Price ?? P.Price;
        public string Label => V is null ? P.Name : $"{P.Name} ({V.Name})";
    }

    /// Ghép từng dòng giỏ hàng với sản phẩm + phân loại hiện tại trong DB. strict=false: bỏ qua dòng không còn hợp lệ (dùng khi tính thử mã giảm giá).
    private async Task<List<Line>> ResolveAsync(IEnumerable<ShopItemDto>? items, bool strict)
    {
        var raw = (items ?? []).Where(i => i.Quantity > 0).GroupBy(i => (i.ProductId, i.VariantId))
            .Select(g => new ShopItemDto(g.Key.ProductId, g.Sum(x => x.Quantity), g.Key.VariantId)).ToList();
        if (raw.Count == 0) throw new AppException(400, "EMPTY_CART", "Giỏ hàng đang trống");
        if (strict && (raw.Count > 30 || raw.Any(l => l.Quantity > 20))) throw new AppException(400, "TOO_MANY", "Mỗi sản phẩm mua tối đa 20 cái mỗi đơn");
        var ids = raw.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).ToListAsync();
        var variants = await db.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId) && v.IsActive).ToListAsync();
        var res = new List<Line>();
        foreach (var l in raw)
        {
            var p = products.FirstOrDefault(x => x.Id == l.ProductId);
            if (p is null) { if (strict) throw new AppException(409, "PRODUCT_GONE", "Có sản phẩm không còn bán, vui lòng tải lại trang"); continue; }
            var pv = variants.Where(q => q.ProductId == p.Id).ToList();
            ProductVariant? v = null;
            if (pv.Count > 0)
            {
                v = l.VariantId is null ? null : pv.FirstOrDefault(x => x.Id == l.VariantId);
                if (v is null)
                {
                    if (!strict) continue;
                    throw new AppException(l.VariantId is null ? 400 : 409, l.VariantId is null ? "NEED_VARIANT" : "PRODUCT_GONE",
                        l.VariantId is null ? $"Vui lòng chọn phân loại cho «{p.Name}»" : "Phân loại bạn chọn không còn bán, vui lòng tải lại trang");
                }
            }
            res.Add(new Line(p, v, Math.Min(l.Quantity, strict ? int.MaxValue : 20)));
        }
        return res;
    }

    /// Khách bấm "Áp dụng" mã ở giỏ hàng: tính thử theo giá hiện tại trong DB, chưa giữ lượt.
    [HttpPost("coupon/validate"), Authorize]
    public async Task<IActionResult> ValidateCoupon(ShopCouponDto dto)
    {
        var code = EventTicketing.Modules.Coupons.CouponService.Normalize(dto.Code);
        if (code.Length == 0) throw new AppException(400, "COUPON_EMPTY", "Hãy nhập mã giảm giá");
        var lines = await ResolveAsync(dto.Items, strict: false);
        var subtotal = lines.Sum(l => l.Price * l.Qty);
        var coupon = await coupons.FindAsync(code);
        var (promo, isVip) = await vipSvc.ResolveAsync(User.UserId());
        var vipDiscount = isVip ? VipService.Compute(promo, subtotal, EventTicketing.Modules.Coupons.CouponService.ShopMinPayable) : 0m;
        var discount = await coupons.EvaluateShopAsync(coupon, User.UserId(), subtotal, vipDiscount);
        return Ok(new { code = coupon!.Code, description = coupon.Description, subtotal, vipDiscount, discount, total = subtotal - vipDiscount - discount });
    }

    [HttpPost("orders"), Authorize]
    public async Task<IActionResult> Create(ShopOrderDto dto)
    {
        var uid = User.UserId();
        var name = (dto.ReceiverName ?? "").Trim();
        var phone = Regex.Replace(dto.Phone ?? "", @"[\s.\-]", "");
        var address = (dto.Address ?? "").Trim();
        var note = (dto.Note ?? "").Trim();
        if (name.Length < 2) throw new AppException(400, "BAD_NAME", "Vui lòng nhập tên người nhận");
        if (!Regex.IsMatch(phone, @"^(\+?84|0)\d{9,10}$")) throw new AppException(400, "BAD_PHONE", "Số điện thoại không hợp lệ");
        if (address.Length < 8) throw new AppException(400, "BAD_ADDRESS", "Vui lòng nhập địa chỉ nhận hàng đầy đủ");
        if (note.Length > 500) note = note[..500];
        var usePayos = string.Equals(dto.PaymentMethod, ShopPay.PayOs, StringComparison.OrdinalIgnoreCase);
        if (usePayos && !payos.On) throw new AppException(400, "PAYOS_OFF", "Chưa bật thanh toán payOS, vui lòng chọn thanh toán khi nhận hàng");

        var lines = await ResolveAsync(dto.Items, strict: true);
        var (promo, isVip) = await vipSvc.ResolveAsync(uid);   // khách VIP: xác định trước transaction

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var l in lines)
        {
            var qty = l.Qty; var pid = l.P.Id;
            // Trừ kho nguyên tử: chỉ thành công khi còn đủ hàng, nên 2 khách mua cùng lúc không bán lố
            int n;
            if (l.V is not null)
            {
                var vid = l.V.Id;
                n = await db.ProductVariants.Where(v => v.Id == vid && v.IsActive && v.Stock >= qty)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Stock, v => v.Stock - qty));
            }
            else
                n = await db.Products.Where(p => p.Id == pid && p.IsActive && p.Stock >= qty)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - qty));
            if (n != 1) throw new AppException(409, "OUT_OF_STOCK", $"«{l.Label}» không đủ hàng, vui lòng giảm số lượng hoặc chọn loại khác");
            if (l.V is not null) await ShopStock.RecalcAsync(db, pid);
        }

        // Mã giảm giá: khoá dòng mã rồi tính giảm trên giá hiện tại (cùng transaction với việc trừ kho)
        var subtotal = lines.Sum(l => l.Price * l.Qty);
        var vipDiscount = isVip ? VipService.Compute(promo, subtotal, EventTicketing.Modules.Coupons.CouponService.ShopMinPayable) : 0m;
        Coupon? coupon = null; decimal discount = 0;
        var couponCode = EventTicketing.Modules.Coupons.CouponService.Normalize(dto.CouponCode);
        if (couponCode.Length > 0)
        {
            coupon = await coupons.FindForUpdateAsync(couponCode);
            discount = await coupons.EvaluateShopAsync(coupon, uid, subtotal, vipDiscount);
        }

        var order = new ShopOrder
        {
            CouponId = coupon?.Id, CouponCode = coupon?.Code, DiscountAmount = discount, VipDiscount = vipDiscount,
            UserId = uid, ReceiverName = name, Phone = phone, Address = address,
            Note = note.Length == 0 ? null : note,
            PaymentMethod = usePayos ? ShopPay.PayOs : ShopPay.Cod,
            PaymentStatus = usePayos ? ShopPay.Pending : ShopPay.Unpaid,
            Total = subtotal - vipDiscount - discount
        };
        db.ShopOrders.Add(order);
        foreach (var l in lines)
            db.ShopOrderItems.Add(new ShopOrderItem { OrderId = order.Id, ProductId = l.P.Id, ProductName = l.P.Name, VariantId = l.V?.Id, VariantName = l.V?.Name, UnitPrice = l.Price, Quantity = l.Qty });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (!usePayos) mailer.Queue(order.Id, paid: false);   // COD: gửi mail xác nhận đơn; payOS gửi sau khi thanh toán xong
        return Ok(new
        {
            order.Id, order.Total, payos = usePayos,
            message = usePayos ? "Đã tạo đơn, đang chuyển sang payOS để thanh toán…" : "Đặt hàng thành công! Cửa hàng sẽ xác nhận và giao cho bạn, thanh toán khi nhận hàng."
        });
    }

    /// Dữ liệu một đơn cho khách (danh sách + trang chi tiết kiểu Shopee): sản phẩm kèm ảnh + phân loại, tạm tính, giảm giá, tổng.
    private async Task<List<object>> ShapeAsync(IList<ShopOrder> orders)
    {
        var oids = orders.Select(o => o.Id).ToList();
        var items = await db.ShopOrderItems.AsNoTracking().Where(i => oids.Contains(i.OrderId)).ToListAsync();
        var pids = items.Select(i => i.ProductId).Distinct().ToList();
        var prods = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).Select(p => new { p.Id, p.ImageUrl, p.Category }).ToListAsync();
        return orders.Select(o => (object)new
        {
            o.Id, o.Total, status = o.Status.ToString(), o.CreatedAt, o.ReceiverName, o.Phone, o.Address, o.Note,
            paymentMethod = ShopPay.IsPayOs(o) ? ShopPay.PayOs : ShopPay.Cod,
            paymentStatus = o.PaymentStatus == ShopPay.Paid ? ShopPay.Paid : ShopPay.IsPayOs(o) && o.Status != ShopOrderStatus.Cancelled ? ShopPay.Pending : ShopPay.Unpaid,
            o.PaidAt, o.CouponCode, o.DiscountAmount, o.VipDiscount, subtotal = o.Total + o.DiscountAmount + o.VipDiscount,
            expiresAt = ShopPay.IsPayOs(o) && o.PaymentStatus == ShopPay.Pending ? o.CreatedAt.AddMinutes(ShopPay.HoldMinutes) : (DateTime?)null,
            items = items.Where(i => i.OrderId == o.Id).Select(i =>
            {
                var pr = prods.FirstOrDefault(x => x.Id == i.ProductId);
                return new { i.ProductId, i.ProductName, i.VariantName, i.UnitPrice, i.Quantity, imageUrl = pr?.ImageUrl, category = pr?.Category };
            }).ToList()
        }).ToList();
    }

    [HttpGet("payos/enabled")]
    public IActionResult PayosEnabled() => Ok(new { enabled = payos.On });

    [HttpGet("orders/{id:guid}"), Authorize]
    public async Task<IActionResult> One(Guid id)
    {
        var uid = User.UserId();
        var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        return Ok((await ShapeAsync([o]))[0]);
    }

    private async Task<ShopOrder> PendingPayosOrder(Guid id)
    {
        var uid = User.UserId();
        var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        if (!ShopPay.IsPayOs(o)) throw new AppException(409, "NOT_PAYOS", "Đơn này thanh toán khi nhận hàng");
        if (o.PaymentStatus == ShopPay.Paid) throw new AppException(409, "ALREADY_PAID", "Đơn đã được thanh toán");
        if (o.Status == ShopOrderStatus.Cancelled) throw new AppException(409, "CANCELLED", "Đơn đã bị huỷ (quá hạn thanh toán), vui lòng đặt lại");
        return o;
    }

    /// Tạo link thanh toán payOS cho đơn, trả về địa chỉ để chuyển khách sang trang payOS.
    [HttpPost("orders/{id:guid}/payos"), Authorize]
    public async Task<IActionResult> PayosCreate(Guid id)
    {
        if (!payos.On) throw new AppException(400, "PAYOS_OFF", "Chưa bật thanh toán payOS");
        var o = await PendingPayosOrder(id);
        var baseUrl = payos.BaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        var link = await pay.EnsureLinkAsync(o, baseUrl);
        return Ok(new { checkoutUrl = link.CheckoutUrl });
    }

    /// Mã QR chuyển khoản do payOS cấp cho đơn, hiện ngay trên trang (giống trang thanh toán vé). Khách quét xong trang tự chuyển sang đơn.
    [HttpPost("orders/{id:guid}/payos/qr"), Authorize]
    public async Task<IActionResult> PayosQr(Guid id)
    {
        if (!payos.On) throw new AppException(400, "PAYOS_OFF", "Chưa bật thanh toán payOS");
        var o = await PendingPayosOrder(id);
        var link = await pay.EnsureLinkAsync(o, payos.BaseUrl ?? $"{Request.Scheme}://{Request.Host}", needQr: true);
        if (string.IsNullOrEmpty(link.QrCode)) throw new AppException(502, "PAYOS_ERROR", "payOS chưa trả mã QR, vui lòng thử lại");
        var png = EventTicketing.Modules.Tickets.TicketService.RenderQrPng(link.QrCode);
        return Ok(new
        {
            image = "data:image/png;base64," + Convert.ToBase64String(png),
            amount = link.Amount, content = link.Description,
            bankName = EventTicketing.Modules.Payments.VietQr.BankName(link.Bin ?? ""), accountNo = link.AccountNo ?? "", accountName = link.AccountName ?? "",
            checkoutUrl = link.CheckoutUrl
        });
    }

    /// Hỏi lại payOS xem khách đã trả tiền chưa (khách quay về từ payOS / trang đang chờ).
    [HttpPost("orders/{id:guid}/payos/check"), Authorize]
    public async Task<IActionResult> PayosCheck(Guid id)
    {
        var uid = User.UserId();
        var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        var paid = await pay.CheckAsync(o);
        return Ok(new { paid });
    }

    [HttpGet("orders/mine"), Authorize]
    public async Task<IActionResult> Mine()
    {
        var uid = User.UserId();
        var orders = await db.ShopOrders.AsNoTracking().Where(o => o.UserId == uid).OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync();
        return Ok(await ShapeAsync(orders));
    }

    /// Khách tự huỷ khi đơn còn "Chờ xác nhận"; hàng được trả lại kho.
    [HttpPost("orders/{id:guid}/cancel"), Authorize]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var uid = User.UserId();
        var cur = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.UserId == uid);
        if (cur is not null && ShopPay.IsPayOs(cur) && cur.PaymentStatus == ShopPay.Paid)
            throw new AppException(409, "CANT_CANCEL", "Đơn đã thanh toán qua payOS nên không thể tự huỷ. Vui lòng liên hệ hỗ trợ để được hoàn tiền");
        if (cur is not null && ShopPay.IsPayOs(cur) && await pay.CheckAsync(cur))   // vừa trả xong: không huỷ
            throw new AppException(409, "CANT_CANCEL", "Đơn vừa được thanh toán qua payOS nên không thể tự huỷ. Vui lòng liên hệ hỗ trợ");
        if (cur is not null && ShopPay.IsPayOs(cur))
        {
            if (!await pay.CancelUnpaidAsync(id, uid)) throw new AppException(409, "CANT_CANCEL", "Đơn đã được xác nhận nên không thể tự huỷ. Vui lòng liên hệ hỗ trợ");
            return Ok(new { message = "Đã huỷ đơn hàng" });
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        var n = await db.ShopOrders.Where(o => o.Id == id && o.UserId == uid && o.Status == ShopOrderStatus.New)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, ShopOrderStatus.Cancelled));
        if (n != 1) throw new AppException(409, "CANT_CANCEL", "Đơn đã được xác nhận nên không thể tự huỷ. Vui lòng liên hệ hỗ trợ");
        await ShopStock.RestockAsync(db, id);
        await tx.CommitAsync();
        return Ok(new { message = "Đã huỷ đơn hàng" });
    }
}
