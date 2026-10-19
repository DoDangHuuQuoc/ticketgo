using System.Data;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Coupons;
using EventTicketing.Modules.Promo;
using EventTicketing.Modules.Tickets;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Bookings;

public record CreateBookingDto(Guid TicketTypeId, int Quantity, string? CouponCode = null, string[]? Seats = null);
public record BookingDto(Guid BookingId, decimal TotalAmount, DateTime ExpiresAt, string Status);

public class BookingService(AppDbContext db)
{
    private readonly CouponService coupons = new(db);   // tạo trực tiếp để giữ constructor gọn (test dựng BookingService(ctx))
    private readonly VipService vip = new(db);
    private readonly SeatService seatSvc = new(db);

    public const int MaxPerBooking = 6;
    public static readonly TimeSpan HoldTime = TimeSpan.FromMinutes(10);

    public async Task<BookingDto> CreateAsync(Guid userId, CreateBookingDto dto)
    {
        if (dto.Quantity < 1 || dto.Quantity > MaxPerBooking)
            throw new AppException(400, "BAD_QUANTITY", $"Số lượng từ 1 đến {MaxPerBooking}");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
                   ?? throw new AppException(401, "UNAUTHORIZED", "Tài khoản không tồn tại");
        if (user.IsLocked) throw new AppException(403, "ACCOUNT_LOCKED", "Tài khoản đã bị khoá");
        if (!user.IsVerified) throw new AppException(403, "NOT_VERIFIED", "Cần xác thực email trước khi mua vé");

        // Xác định event (đọc không khoá) để kiểm tra trạng thái mở bán trước khi vào vùng tranh chấp
        var meta = await db.TicketTypes.AsNoTracking().Where(t => t.Id == dto.TicketTypeId)
            .Select(t => new { t.EventId, t.Event!.IsPublished, t.Event.StartsAt }).FirstOrDefaultAsync()
            ?? throw new AppException(404, "TICKET_TYPE_NOT_FOUND", "Loại vé không tồn tại");

        if (!meta.IsPublished || meta.StartsAt <= DateTime.UtcNow)
            throw new AppException(409, "EVENT_CLOSED", "Sự kiện hiện không mở bán");

        // Khách VIP: xác định trước khi vào vùng găng (không giữ khoá dòng loại vé lâu hơn); số tiền giảm tính sau khi có giá vé
        var (promo, isVip) = await vip.ResolveAsync(userId);

        // ===== Vùng găng: transaction + khoá dòng =====
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

                // SQL Server: UPDLOCK giữ khoá cập nhật tới hết transaction => các request cùng loại vé xếp hàng tuần tự
        var tt = (await db.TicketTypes
            .FromSqlInterpolated($"SELECT * FROM TicketTypes WITH (UPDLOCK, ROWLOCK) WHERE Id = {dto.TicketTypeId}")
            .ToListAsync()).Single();

        if (tt.AvailableQuantity < dto.Quantity)
            throw new AppException(409, "SOLD_OUT", $"Chỉ còn {tt.AvailableQuantity} vé");   // tx tự rollback khi dispose

        // Loại vé khách tự chọn ghế: kiểm tra ghế (đủ số lượng, đúng sơ đồ, còn trống) trong lúc đang giữ khoá loại vé => hai người không thể giữ cùng một ghế
        List<string>? chosenSeats = null;
        if (tt.SeatMode == SeatMode.Choose) chosenSeats = await seatSvc.NormalizeChosenAsync(tt, dto.Seats, dto.Quantity);

        // Mã giảm giá: khoá dòng mã (sau khoá loại vé, luôn cùng thứ tự => không deadlock) rồi tính giảm
        var subtotal = tt.Price * dto.Quantity;
        var vipDiscount = isVip ? VipService.Compute(promo, subtotal, CouponService.MinPayable) : 0m;
        Coupon? coupon = null; decimal discount = 0;
        var code = CouponService.Normalize(dto.CouponCode);
        if (code.Length > 0)
        {
            coupon = await coupons.FindForUpdateAsync(code);
            discount = await coupons.EvaluateAsync(coupon, userId, tt.EventId, subtotal, null, vipDiscount);
        }

        tt.AvailableQuantity -= dto.Quantity;   // trừ kho tạm thời

        var booking = new Booking
        {
            UserId = userId, EventId = tt.EventId, TicketTypeId = tt.Id, Quantity = dto.Quantity,
            TotalAmount = subtotal - vipDiscount - discount, Status = BookingStatus.Pending,
            CouponId = coupon?.Id, CouponCode = coupon?.Code, DiscountAmount = discount, VipDiscount = vipDiscount,
            Seats = chosenSeats is null ? null : string.Join(",", chosenSeats),
            ExpiresAt = DateTime.UtcNow.Add(HoldTime)
        };
        db.Bookings.Add(booking);

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // ===============================================

        return new BookingDto(booking.Id, booking.TotalAmount, booking.ExpiresAt, booking.Status.ToString());
    }

    public record CouponAppliedDto(string? CouponCode, string? Description, decimal Subtotal, decimal DiscountAmount, decimal TotalAmount, decimal VipDiscount = 0);

    /// Khoá dòng đơn (chỉ chủ đơn, còn Pending và còn hạn giữ chỗ). Gọi TRONG transaction.
    private async Task<Booking> LockPendingAsync(Guid userId, Guid bookingId)
    {
        var b = (await db.Bookings
            .FromSqlInterpolated($"SELECT * FROM Bookings WITH (UPDLOCK, ROWLOCK) WHERE Id = {bookingId} AND UserId = {userId}")
            .ToListAsync()).SingleOrDefault()
            ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        if (b.Status != BookingStatus.Pending || b.ExpiresAt <= DateTime.UtcNow)
            throw new AppException(409, "NOT_PENDING", "Đơn này đã hết hạn giữ vé hoặc không còn chờ thanh toán");
        return b;
    }

    /// Khách lưu mã giảm giá ngay ở trang thanh toán: tính lại số tiền của đơn (QR phải sinh lại theo số tiền mới).
    public async Task<CouponAppliedDto> ApplyCouponAsync(Guid userId, Guid bookingId, string? rawCode)
    {
        var code = CouponService.Normalize(rawCode);
        if (code.Length == 0) throw new AppException(400, "COUPON_EMPTY", "Hãy nhập mã giảm giá");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var b = await LockPendingAsync(userId, bookingId);
        var coupon = await coupons.FindForUpdateAsync(code);
        var subtotal = b.TotalAmount + b.DiscountAmount + b.VipDiscount;   // giá gốc của đơn tại lúc đặt (không phụ thuộc việc admin đổi giá sau đó)
        var discount = await coupons.EvaluateAsync(coupon, userId, b.EventId, subtotal, b.Id, b.VipDiscount);

        b.CouponId = coupon!.Id; b.CouponCode = coupon.Code; b.DiscountAmount = discount; b.TotalAmount = subtotal - b.VipDiscount - discount;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new CouponAppliedDto(coupon.Code, coupon.Description, subtotal, discount, b.TotalAmount, b.VipDiscount);
    }

    public async Task<CouponAppliedDto> RemoveCouponAsync(Guid userId, Guid bookingId)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var b = await LockPendingAsync(userId, bookingId);
        var subtotal = b.TotalAmount + b.DiscountAmount + b.VipDiscount;
        b.CouponId = null; b.CouponCode = null; b.DiscountAmount = 0; b.TotalAmount = subtotal - b.VipDiscount;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new CouponAppliedDto(null, null, subtotal, 0, b.TotalAmount, b.VipDiscount);
    }
}
