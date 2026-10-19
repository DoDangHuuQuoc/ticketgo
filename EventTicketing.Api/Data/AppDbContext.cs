using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EventTicketing.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PaymentSetting> PaymentSettings => Set<PaymentSetting>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<UserCoupon> UserCoupons => Set<UserCoupon>();
    public DbSet<PromoSetting> PromoSettings => Set<PromoSetting>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<LiveChat> LiveChats => Set<LiveChat>();
    public DbSet<LiveChatMessage> LiveChatMessages => Set<LiveChatMessage>();
    public DbSet<ChatFaq> ChatFaqs => Set<ChatFaq>();
    public DbSet<ChatLog> ChatLogs => Set<ChatLog>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<FeaturedItem> FeaturedItems => Set<FeaturedItem>();
    public DbSet<ItemRating> ItemRatings => Set<ItemRating>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ShopReview> ShopReviews => Set<ShopReview>();
    public DbSet<ShopReviewReport> ShopReviewReports => Set<ShopReviewReport>();
    public DbSet<ShopOrder> ShopOrders => Set<ShopOrder>();
    public DbSet<ShopOrderItem> ShopOrderItems => Set<ShopOrderItem>();

    // SQL Server (datetime2) trả DateTime kiểu Unspecified -> ép về UTC để JSON có hậu tố "Z"
    // (nếu không, trình duyệt hiểu là giờ local và đồng hồ đếm ngược lệch 7 tiếng).
    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<DateTime>().HaveConversion<UtcConverter>();
        cb.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(x =>
        {
            x.Property(p => p.Email).HasMaxLength(256);
            x.Property(p => p.FullName).HasMaxLength(200);
            x.Property(p => p.Role).HasMaxLength(20);
            x.Property(p => p.Phone).HasMaxLength(20);
            x.Property(p => p.Gender).HasMaxLength(10);
            x.Property(p => p.Address).HasMaxLength(300);
            x.Property(p => p.DateOfBirth).HasColumnType("date");
            x.HasIndex(p => p.Email).IsUnique();
        });

        b.Entity<Event>(x =>
        {
            x.Property(p => p.Name).HasMaxLength(300);
            x.Property(p => p.Venue).HasMaxLength(300);
            x.Property(p => p.Category).HasMaxLength(20).HasDefaultValue("Concert");
            x.Property(p => p.PosterUrl).HasMaxLength(1000);
            x.Property(p => p.SeatMapUrl).HasMaxLength(1000);
            x.Property(p => p.Description).HasMaxLength(2000);
            x.HasIndex(p => p.StartsAt);
        });

        b.Entity<TicketType>(t =>
        {
            t.Property(x => x.Name).HasMaxLength(100);
            t.Property(x => x.Zone).HasMaxLength(100);
            t.Property(x => x.Price).HasPrecision(18, 2);
            // Lưới an toàn cuối cùng: DB tự từ chối nếu tồn kho âm
            t.ToTable(tb => tb.HasCheckConstraint("CK_TicketTypes_Available", "[AvailableQuantity] >= 0"));
        });

        b.Entity<Booking>(x =>
        {
            x.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            x.Property(p => p.TotalAmount).HasPrecision(18, 2);
            x.Property(p => p.DiscountAmount).HasPrecision(18, 2);
            x.Property(p => p.VipDiscount).HasPrecision(18, 2);
            x.Property(p => p.Seats).HasMaxLength(2000);
            x.Property(p => p.CouponCode).HasMaxLength(40);
            x.HasIndex(p => new { p.Status, p.ExpiresAt });
            x.HasIndex(p => p.CouponId);
        });

        b.Entity<Ticket>(x =>
        {
            x.Property(p => p.QrCode).HasMaxLength(64);
            x.Property(p => p.SeatInfo).HasMaxLength(50);
            x.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            x.HasIndex(p => p.QrCode).IsUnique();
            x.HasIndex(p => p.BookingId);
        });

        b.Entity<Payment>(x =>
        {
            x.Property(p => p.TransactionRef).HasMaxLength(100);
            x.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            x.Property(p => p.Amount).HasPrecision(18, 2);
            x.HasIndex(p => p.TransactionRef).IsUnique(); // idempotency cho webhook
        });

        b.Entity<AuditLog>(x =>
        {
            x.Property(p => p.ActorEmail).HasMaxLength(256);
            x.Property(p => p.Action).HasMaxLength(60);
            x.Property(p => p.EntityType).HasMaxLength(40);
            x.Property(p => p.EntityId).HasMaxLength(64);
            x.Property(p => p.Detail).HasMaxLength(1000);
            x.HasIndex(p => p.CreatedAt);
        });

        b.Entity<PaymentSetting>(x =>
        {
            x.HasKey(p => p.Id);
            x.Property(p => p.Id).ValueGeneratedNever();
            x.Property(p => p.BankBin).HasMaxLength(10);
            x.Property(p => p.BankName).HasMaxLength(100);
            x.Property(p => p.AccountNo).HasMaxLength(30);
            x.Property(p => p.AccountName).HasMaxLength(100);
        });

        b.Entity<Coupon>(x =>
        {
            x.Property(p => p.Code).HasMaxLength(40);
            x.Property(p => p.Description).HasMaxLength(200);
            x.Property(p => p.Type).HasConversion<string>().HasMaxLength(12);
            x.Property(p => p.Value).HasPrecision(18, 2);
            x.Property(p => p.MaxDiscount).HasPrecision(18, 2);
            x.Property(p => p.MinOrder).HasPrecision(18, 2);
            x.Property(p => p.Source).HasMaxLength(20);
            x.HasIndex(p => p.Code).IsUnique();
            x.HasIndex(p => p.OwnerUserId);
        });

        b.Entity<UserCoupon>(x => x.HasIndex(p => new { p.UserId, p.CouponId }).IsUnique());

        b.Entity<PromoSetting>(x =>
        {
            x.Property(p => p.Id).ValueGeneratedNever();
            x.Property(p => p.VipMaxDiscount).HasPrecision(18, 2);
            x.Property(p => p.VipAutoSpend).HasPrecision(18, 2);
            x.Property(p => p.WelcomeType).HasConversion<string>().HasMaxLength(12);
            x.Property(p => p.WelcomeValue).HasPrecision(18, 2);
            x.Property(p => p.WelcomeMaxDiscount).HasPrecision(18, 2);
            x.Property(p => p.WelcomeMinOrder).HasPrecision(18, 2);
        });

        b.Entity<Product>(x =>
        {
            x.Property(p => p.Name).HasMaxLength(200);
            x.Property(p => p.Category).HasMaxLength(20);
            x.Property(p => p.Description).HasMaxLength(2000);
            x.Property(p => p.ImageUrl).HasMaxLength(1000);
            x.Property(p => p.Price).HasPrecision(18, 2);
            x.HasIndex(p => new { p.IsActive, p.Category });
        });
        b.Entity<ProductVariant>(x =>
        {
            x.Property(p => p.Name).HasMaxLength(100);
            x.Property(p => p.Price).HasPrecision(18, 2);
            x.HasIndex(p => p.ProductId);
        });
        b.Entity<ShopReview>(x =>
        {
            x.Property(p => p.VariantName).HasMaxLength(100);
            x.Property(p => p.Content).HasMaxLength(1000);
            x.Property(p => p.AdminReply).HasMaxLength(1000);
            x.HasIndex(p => new { p.ProductId, p.IsHidden, p.CreatedAt });
            x.HasIndex(p => p.OrderId);
            x.HasIndex(p => p.UserId);
        });
        b.Entity<ShopReviewReport>(x =>
        {
            x.Property(p => p.Reason).HasMaxLength(20);
            x.Property(p => p.Detail).HasMaxLength(300);
            x.Property(p => p.Status).HasMaxLength(10);
            x.Property(p => p.HandledBy).HasMaxLength(256);
            x.HasIndex(p => new { p.ReviewId, p.ReporterId }).IsUnique();
            x.HasIndex(p => new { p.Status, p.CreatedAt });
        });
        b.Entity<ShopOrder>(x =>
        {
            x.Property(p => p.ReceiverName).HasMaxLength(200);
            x.Property(p => p.Phone).HasMaxLength(20);
            x.Property(p => p.Address).HasMaxLength(500);
            x.Property(p => p.Note).HasMaxLength(500);
            x.Property(p => p.Total).HasPrecision(18, 2);
            x.Property(p => p.Status).HasConversion<string>().HasMaxLength(12);
            x.Property(p => p.PaymentMethod).HasMaxLength(10);
            x.Property(p => p.PaymentStatus).HasMaxLength(10);
            x.Property(p => p.PaymentRef).HasMaxLength(100);
            x.Property(p => p.CouponCode).HasMaxLength(40);
            x.Property(p => p.DiscountAmount).HasPrecision(18, 2);
            x.Property(p => p.VipDiscount).HasPrecision(18, 2);
            x.HasIndex(p => p.CouponId);
            x.HasIndex(p => new { p.UserId, p.CreatedAt });
            x.HasIndex(p => p.Status);
        });
        b.Entity<ShopOrderItem>(x =>
        {
            x.Property(p => p.ProductName).HasMaxLength(200);
            x.Property(p => p.VariantName).HasMaxLength(100);
            x.Property(p => p.UnitPrice).HasPrecision(18, 2);
            x.HasIndex(p => p.OrderId);
        });

        b.Entity<LiveChat>(x =>
        {
            x.Property(p => p.GuestKey).HasMaxLength(64);
            x.Property(p => p.Name).HasMaxLength(200);
            x.Property(p => p.Status).HasMaxLength(10);
            x.Property(p => p.StaffName).HasMaxLength(200);
            x.Property(p => p.LastText).HasMaxLength(200);
            x.HasIndex(p => new { p.Status, p.LastMessageAt });
            x.HasIndex(p => p.UserId);
            x.HasIndex(p => p.GuestKey);
        });
        b.Entity<LiveChatMessage>(x =>
        {
            x.Property(p => p.SenderName).HasMaxLength(200);
            x.Property(p => p.Text).HasMaxLength(1000);
            x.HasIndex(p => new { p.ChatId, p.CreatedAt });
        });

        b.Entity<Feedback>(x =>
        {
            x.Property(p => p.Name).HasMaxLength(200);
            x.Property(p => p.Email).HasMaxLength(256);
            x.Property(p => p.Category).HasMaxLength(20);
            x.Property(p => p.Message).HasMaxLength(2000);
            x.Property(p => p.Source).HasMaxLength(20);
            x.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            x.Property(p => p.AdminReply).HasMaxLength(2000);
            x.Property(p => p.RepliedBy).HasMaxLength(256);
            x.HasIndex(p => new { p.Status, p.CreatedAt });
            x.HasIndex(p => p.UserId);
        });

        b.Entity<ChatFaq>(x =>
        {
            x.Property(p => p.Question).HasMaxLength(200);
            x.Property(p => p.Keywords).HasMaxLength(300);
            x.Property(p => p.Answer).HasMaxLength(1500);
        });

        b.Entity<ChatLog>(x =>
        {
            x.Property(p => p.Message).HasMaxLength(500);
            x.Property(p => p.Intent).HasMaxLength(10);
            x.HasIndex(p => p.CreatedAt);
        });

        b.Entity<Broadcast>(x =>
        {
            x.Property(p => p.Subject).HasMaxLength(150);
            x.Property(p => p.Message).HasMaxLength(3000);
            x.Property(p => p.Audience).HasMaxLength(10);
            x.Property(p => p.Status).HasMaxLength(10);
            x.Property(p => p.CreatedBy).HasMaxLength(256);
            x.HasIndex(p => p.CreatedAt);
        });

        b.Entity<FeaturedItem>(x =>
        {
            x.Property(p => p.Kind).HasMaxLength(10);
            x.Property(p => p.Name).HasMaxLength(200);
            x.Property(p => p.ImageUrl).HasMaxLength(1000);
            x.Property(p => p.Description).HasMaxLength(1000);
            x.HasIndex(p => new { p.Kind, p.IsActive });
        });

        b.Entity<ItemRating>(x =>
        {
            x.HasIndex(p => new { p.ItemId, p.UserId }).IsUnique();   // mỗi khách 1 đánh giá / mục
            x.HasIndex(p => p.UserId);
        });

        b.Entity<Notification>(x =>
        {
            x.Property(p => p.Title).HasMaxLength(200);
            x.Property(p => p.Message).HasMaxLength(500);
            x.Property(p => p.Link).HasMaxLength(300);
            x.HasIndex(p => new { p.UserId, p.IsRead, p.CreatedAt });
        });

        b.Entity<SiteSetting>(x =>
        {
            x.HasKey(p => p.Id);
            x.Property(p => p.Id).ValueGeneratedNever();
            x.Property(p => p.MaintenanceMessage).HasMaxLength(500);
            x.Property(p => p.Hotline).HasMaxLength(40);
            x.Property(p => p.ContactEmail).HasMaxLength(256);
            x.Property(p => p.BannerText).HasMaxLength(200);
            x.Property(p => p.BannerLink).HasMaxLength(300);
            x.Property(p => p.BannerTone).HasMaxLength(10);
        });
    }
}
