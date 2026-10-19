using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Coupons;

/// Kiểm tra và tính giảm giá. Dùng chung cho "xem thử" (CouponsController) và lúc tạo đơn (BookingService).
public class CouponService(AppDbContext db)
{
    /// Mỗi đơn luôn phải trả tối thiểu số này (VietQR không tạo được mã 0 đ).
    public const decimal MinPayable = 1000m;

    public static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    public Task<Coupon?> FindAsync(string code) => db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code);

    /// Gọi TRONG transaction: khoá dòng mã để hai khách không cùng lấy lượt cuối.
    public async Task<Coupon?> FindForUpdateAsync(string code)
    {
        var list = await db.Coupons
            .FromSqlInterpolated($"SELECT * FROM Coupons WITH (UPDLOCK, ROWLOCK) WHERE Code = {code}")
            .ToListAsync();
        return list.SingleOrDefault();
    }

    /// Trả số tiền được giảm (đã làm tròn đ, không vượt trần, luôn chừa lại MinPayable). Ném AppException nếu mã không dùng được.
    /// vipDiscount: ưu đãi VIP đã trừ của đơn. Điều kiện đơn tối thiểu vẫn xét trên giá gốc; mã giảm tính trên số tiền còn lại sau ưu đãi VIP.
    public async Task<decimal> EvaluateAsync(Coupon? c, Guid userId, Guid eventId, decimal subtotal, Guid? excludeBookingId = null, decimal vipDiscount = 0)
    {
        if (c is null || !c.IsActive) throw new AppException(400, "COUPON_INVALID", "Mã giảm giá không tồn tại hoặc đã bị tắt");
        if (c.OwnerUserId is Guid owner && owner != userId) throw new AppException(400, "COUPON_INVALID", "Mã giảm giá không tồn tại hoặc đã bị tắt");   // mã cá nhân của người khác: không lộ là có tồn tại
        var now = DateTime.UtcNow;
        if (c.StartsAt is DateTime s && s > now) throw new AppException(400, "COUPON_NOT_STARTED", "Mã giảm giá chưa đến thời gian áp dụng");
        if (c.EndsAt is DateTime e && e < now) throw new AppException(400, "COUPON_EXPIRED", "Mã giảm giá đã hết hạn");
        if (c.EventId is Guid ev && ev != eventId) throw new AppException(400, "COUPON_NOT_FOR_EVENT", "Mã này không áp dụng cho sự kiện đang chọn");
        if (subtotal < c.MinOrder) throw new AppException(400, "COUPON_MIN_ORDER", $"Đơn tối thiểu {c.MinOrder:N0} đ mới dùng được mã này");

        await CheckUsageAsync(c, userId, excludeBookingId, now);

        return Discount(c, subtotal - vipDiscount, MinPayable);
    }

    /// Đơn lưu niệm luôn phải trả tối thiểu số này (payOS không nhận đơn quá nhỏ).
    public const decimal ShopMinPayable = 2000m;

    /// Giống EvaluateAsync nhưng cho đơn hàng lưu niệm: mã phải được bật "áp dụng cho đơn lưu niệm" và không gắn với một sự kiện cụ thể.
    public async Task<decimal> EvaluateShopAsync(Coupon? c, Guid userId, decimal subtotal, decimal vipDiscount = 0)
    {
        if (c is null || !c.IsActive) throw new AppException(400, "COUPON_INVALID", "Mã giảm giá không tồn tại hoặc đã bị tắt");
        if (c.OwnerUserId is Guid owner && owner != userId) throw new AppException(400, "COUPON_INVALID", "Mã giảm giá không tồn tại hoặc đã bị tắt");
        var now = DateTime.UtcNow;
        if (c.StartsAt is DateTime s && s > now) throw new AppException(400, "COUPON_NOT_STARTED", "Mã giảm giá chưa đến thời gian áp dụng");
        if (c.EndsAt is DateTime e && e < now) throw new AppException(400, "COUPON_EXPIRED", "Mã giảm giá đã hết hạn");
        if (!c.AppliesToShop || c.EventId is not null) throw new AppException(400, "COUPON_NOT_FOR_SHOP", "Mã này không áp dụng cho đơn hàng lưu niệm");
        if (subtotal < c.MinOrder) throw new AppException(400, "COUPON_MIN_ORDER", $"Đơn tối thiểu {c.MinOrder:N0} đ mới dùng được mã này");
        await CheckUsageAsync(c, userId, null, now);
        return Discount(c, subtotal - vipDiscount, ShopMinPayable);
    }

    /// Lượt đang dùng = đơn vé đã trả tiền + đơn vé đang giữ chỗ còn hạn + đơn lưu niệm chưa huỷ (đơn hết hạn/huỷ tự trả lượt).
    /// excludeBookingId: khi đổi/áp lại mã cho một đơn vé đang có, không tính chính đơn đó vào số lượt đã dùng.
    private async Task CheckUsageAsync(Coupon c, Guid userId, Guid? excludeBookingId, DateTime now)
    {
        var live = db.Bookings.AsNoTracking().Where(b => b.CouponId == c.Id && (excludeBookingId == null || b.Id != excludeBookingId)
            && (b.Status == BookingStatus.Paid || (b.Status == BookingStatus.Pending && b.ExpiresAt > now)));
        var shop = db.ShopOrders.AsNoTracking().Where(o => o.CouponId == c.Id && o.Status != ShopOrderStatus.Cancelled);
        if (c.MaxUses is int max && await live.CountAsync() + await shop.CountAsync() >= max)
            throw new AppException(409, "COUPON_USED_UP", "Mã giảm giá đã hết lượt sử dụng");
        if (await live.CountAsync(b => b.UserId == userId) + await shop.CountAsync(o => o.UserId == userId) >= Math.Max(1, c.PerUserLimit))
            throw new AppException(409, "COUPON_LIMIT", "Bạn đã dùng hết số lần cho phép của mã này");
    }

    private static decimal Discount(Coupon c, decimal subtotal, decimal minPayable)
    {
        var d = c.Type == CouponType.Percent
            ? decimal.Round(subtotal * c.Value / 100m, 0, MidpointRounding.AwayFromZero)
            : c.Value;
        if (c.MaxDiscount is decimal md && md > 0 && d > md) d = md;
        var cap = Math.Max(0m, subtotal - minPayable);
        return Math.Max(0m, Math.Min(d, cap));
    }
}
