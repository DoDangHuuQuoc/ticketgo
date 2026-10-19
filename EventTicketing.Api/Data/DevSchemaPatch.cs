using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Data;

/// Chỉ dùng khi DB được tạo bằng EnsureCreated (chưa có Migrations) ở môi trường Development.
/// Bổ sung các cột/bảng của khu quản trị vào DB cũ để khỏi phải xoá DB. Idempotent: chạy nhiều lần vẫn an toàn.
public static class DevSchemaPatch
{
    private const string Sql = """
        IF COL_LENGTH('Users','IsLocked') IS NULL
            ALTER TABLE Users ADD IsLocked bit NOT NULL CONSTRAINT DF_Users_IsLocked DEFAULT 0;
        IF COL_LENGTH('Events','Description') IS NULL
            ALTER TABLE Events ADD Description nvarchar(2000) NULL;
        IF COL_LENGTH('Events','IsPublished') IS NULL
            ALTER TABLE Events ADD IsPublished bit NOT NULL CONSTRAINT DF_Events_IsPublished DEFAULT 1;
        IF COL_LENGTH('Events','CreatedAt') IS NULL
            ALTER TABLE Events ADD CreatedAt datetime2 NOT NULL CONSTRAINT DF_Events_CreatedAt DEFAULT SYSUTCDATETIME();
        IF COL_LENGTH('Events','SeatMapUrl') IS NULL
            ALTER TABLE Events ADD SeatMapUrl nvarchar(1000) NULL;
        IF COL_LENGTH('Events','Category') IS NULL
            ALTER TABLE Events ADD Category nvarchar(20) NOT NULL CONSTRAINT DF_Events_Category DEFAULT N'Concert';
        IF OBJECT_ID('AuditLogs','U') IS NULL
        BEGIN
            CREATE TABLE AuditLogs (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ActorId uniqueidentifier NULL,
                ActorEmail nvarchar(256) NOT NULL,
                Action nvarchar(60) NOT NULL,
                EntityType nvarchar(40) NOT NULL,
                EntityId nvarchar(64) NULL,
                Detail nvarchar(1000) NULL,
                CreatedAt datetime2 NOT NULL
            );
            CREATE INDEX IX_AuditLogs_CreatedAt ON AuditLogs (CreatedAt);
        END
        IF OBJECT_ID('PaymentSettings','U') IS NULL
            CREATE TABLE PaymentSettings (
                Id int NOT NULL PRIMARY KEY,
                BankBin nvarchar(10) NOT NULL,
                BankName nvarchar(100) NOT NULL,
                AccountNo nvarchar(30) NOT NULL,
                AccountName nvarchar(100) NOT NULL,
                UpdatedAt datetime2 NOT NULL
            );
        """;

    public static Task<int> ApplyAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(Sql);

    /// Bảng cài đặt toàn site (chế độ bảo trì). Tách riêng, idempotent, chạy ở mọi môi trường khi khởi động.
    private const string SiteSettingsSql = """
        IF OBJECT_ID('SiteSettings','U') IS NULL
            CREATE TABLE SiteSettings (
                Id int NOT NULL PRIMARY KEY,
                MaintenanceEnabled bit NOT NULL CONSTRAINT DF_SiteSettings_Maint DEFAULT 0,
                MaintenanceMessage nvarchar(500) NOT NULL CONSTRAINT DF_SiteSettings_Msg DEFAULT N'',
                UpdatedAt datetime2 NOT NULL CONSTRAINT DF_SiteSettings_Upd DEFAULT SYSUTCDATETIME()
            );
        """;

    public static Task<int> EnsureSiteSettingsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(SiteSettingsSql);

    /// Bảng Coupons + 3 cột mã giảm giá trên Bookings. Idempotent, không đụng dữ liệu cũ.
    private const string CouponsSql = """
        IF OBJECT_ID('Coupons','U') IS NULL
            CREATE TABLE Coupons (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Code nvarchar(40) NOT NULL CONSTRAINT UQ_Coupons_Code UNIQUE,
                Description nvarchar(200) NULL,
                Type nvarchar(12) NOT NULL,
                Value decimal(18,2) NOT NULL,
                MaxDiscount decimal(18,2) NULL,
                MinOrder decimal(18,2) NOT NULL CONSTRAINT DF_Coupons_MinOrder DEFAULT 0,
                MaxUses int NULL,
                PerUserLimit int NOT NULL CONSTRAINT DF_Coupons_PerUser DEFAULT 1,
                StartsAt datetime2 NULL,
                EndsAt datetime2 NULL,
                IsActive bit NOT NULL CONSTRAINT DF_Coupons_Active DEFAULT 1,
                EventId uniqueidentifier NULL,
                CreatedAt datetime2 NOT NULL CONSTRAINT DF_Coupons_Created DEFAULT SYSUTCDATETIME()
            );
        IF OBJECT_ID('Coupons','U') IS NOT NULL AND COL_LENGTH('Coupons','IsPublic') IS NULL
            ALTER TABLE Coupons ADD IsPublic bit NOT NULL CONSTRAINT DF_Coupons_Public DEFAULT 1;
        IF OBJECT_ID('Bookings','U') IS NOT NULL AND COL_LENGTH('Bookings','CouponId') IS NULL
            ALTER TABLE Bookings ADD CouponId uniqueidentifier NULL;
        IF OBJECT_ID('Bookings','U') IS NOT NULL AND COL_LENGTH('Bookings','CouponCode') IS NULL
            ALTER TABLE Bookings ADD CouponCode nvarchar(40) NULL;
        IF OBJECT_ID('Bookings','U') IS NOT NULL AND COL_LENGTH('Bookings','DiscountAmount') IS NULL
            ALTER TABLE Bookings ADD DiscountAmount decimal(18,2) NOT NULL CONSTRAINT DF_Bookings_Discount DEFAULT 0;
        """;

    public static Task<int> EnsureCouponsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(CouponsSql);

    /// Bảng Feedbacks (phản hồi khách hàng). Idempotent.
    private const string FeedbackSql = """
        IF OBJECT_ID('Feedbacks','U') IS NULL
        BEGIN
            CREATE TABLE Feedbacks (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                UserId uniqueidentifier NULL,
                Name nvarchar(200) NOT NULL,
                Email nvarchar(256) NOT NULL,
                Category nvarchar(20) NOT NULL,
                Message nvarchar(2000) NOT NULL,
                Source nvarchar(20) NOT NULL,
                Status nvarchar(16) NOT NULL,
                AdminReply nvarchar(2000) NULL,
                RepliedBy nvarchar(256) NULL,
                RepliedAt datetime2 NULL,
                CreatedAt datetime2 NOT NULL
            );
            CREATE INDEX IX_Feedbacks_Status_CreatedAt ON Feedbacks (Status, CreatedAt);
            CREATE INDEX IX_Feedbacks_UserId ON Feedbacks (UserId);
        END
        """;

    public static Task<int> EnsureFeedbackAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(FeedbackSql);

    /// Cột cài đặt chung của SiteSettings + bảng ChatFaqs / ChatLogs / Broadcasts. Idempotent.
    private const string AdminToolsSql = """
        IF OBJECT_ID('SiteSettings','U') IS NOT NULL
        BEGIN
            IF COL_LENGTH('SiteSettings','Hotline') IS NULL ALTER TABLE SiteSettings ADD Hotline nvarchar(40) NOT NULL CONSTRAINT DF_SiteSettings_Hotline DEFAULT N'';
            IF COL_LENGTH('SiteSettings','ContactEmail') IS NULL ALTER TABLE SiteSettings ADD ContactEmail nvarchar(256) NOT NULL CONSTRAINT DF_SiteSettings_CEmail DEFAULT N'';
            IF COL_LENGTH('SiteSettings','BannerEnabled') IS NULL ALTER TABLE SiteSettings ADD BannerEnabled bit NOT NULL CONSTRAINT DF_SiteSettings_BOn DEFAULT 0;
            IF COL_LENGTH('SiteSettings','BannerText') IS NULL ALTER TABLE SiteSettings ADD BannerText nvarchar(200) NOT NULL CONSTRAINT DF_SiteSettings_BText DEFAULT N'';
            IF COL_LENGTH('SiteSettings','BannerLink') IS NULL ALTER TABLE SiteSettings ADD BannerLink nvarchar(300) NOT NULL CONSTRAINT DF_SiteSettings_BLink DEFAULT N'';
            IF COL_LENGTH('SiteSettings','BannerTone') IS NULL ALTER TABLE SiteSettings ADD BannerTone nvarchar(10) NOT NULL CONSTRAINT DF_SiteSettings_BTone DEFAULT N'info';
            IF COL_LENGTH('SiteSettings','ChatbotEnabled') IS NULL ALTER TABLE SiteSettings ADD ChatbotEnabled bit NOT NULL CONSTRAINT DF_SiteSettings_Chat DEFAULT 1;
        END
        IF OBJECT_ID('ChatFaqs','U') IS NULL
            CREATE TABLE ChatFaqs (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Question nvarchar(200) NOT NULL,
                Keywords nvarchar(300) NOT NULL,
                Answer nvarchar(1500) NOT NULL,
                IsActive bit NOT NULL,
                Hits int NOT NULL,
                CreatedAt datetime2 NOT NULL,
                UpdatedAt datetime2 NOT NULL
            );
        IF OBJECT_ID('ChatLogs','U') IS NULL
        BEGIN
            CREATE TABLE ChatLogs (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                UserId uniqueidentifier NULL,
                Message nvarchar(500) NOT NULL,
                Intent nvarchar(10) NOT NULL,
                CreatedAt datetime2 NOT NULL
            );
            CREATE INDEX IX_ChatLogs_CreatedAt ON ChatLogs (CreatedAt);
        END
        IF OBJECT_ID('Broadcasts','U') IS NULL
        BEGIN
            CREATE TABLE Broadcasts (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Subject nvarchar(150) NOT NULL,
                Message nvarchar(3000) NOT NULL,
                Audience nvarchar(10) NOT NULL,
                EventId uniqueidentifier NULL,
                Total int NOT NULL,
                Sent int NOT NULL,
                Failed int NOT NULL,
                Status nvarchar(10) NOT NULL,
                CreatedBy nvarchar(256) NOT NULL,
                CreatedAt datetime2 NOT NULL,
                FinishedAt datetime2 NULL
            );
            CREATE INDEX IX_Broadcasts_CreatedAt ON Broadcasts (CreatedAt);
        END
        """;

    public static Task<int> EnsureAdminToolsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(AdminToolsSql);

    /// Thông tin cá nhân của User (Phone, DateOfBirth, Gender, Address) + gỡ phòng chờ ảo (cột trên Events, bảng EventQueues). Idempotent.
    private const string ProfileAndQueueSql = """
        IF COL_LENGTH('Users','Phone') IS NULL ALTER TABLE Users ADD Phone nvarchar(20) NULL;
        IF COL_LENGTH('Users','DateOfBirth') IS NULL ALTER TABLE Users ADD DateOfBirth date NULL;
        IF COL_LENGTH('Users','Gender') IS NULL ALTER TABLE Users ADD Gender nvarchar(10) NULL;
        IF COL_LENGTH('Users','Address') IS NULL ALTER TABLE Users ADD Address nvarchar(300) NULL;

        -- Phòng chờ ảo đã bị gỡ: cột cũ là NOT NULL nên phải xoá (kèm default constraint nếu có), nếu không INSERT sự kiện mới sẽ lỗi.
        DECLARE @sql nvarchar(max);
        IF COL_LENGTH('Events','IsQueueEnabled') IS NOT NULL
        BEGIN
            SET @sql = N'';
            SELECT @sql += N'ALTER TABLE Events DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
              FROM sys.default_constraints dc
              JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
             WHERE dc.parent_object_id = OBJECT_ID('Events') AND c.name = 'IsQueueEnabled';
            SET @sql += N'ALTER TABLE Events DROP COLUMN IsQueueEnabled;';
            EXEC sp_executesql @sql;
        END
        IF COL_LENGTH('Events','QueueDripPerMinute') IS NOT NULL
        BEGIN
            SET @sql = N'';
            SELECT @sql += N'ALTER TABLE Events DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
              FROM sys.default_constraints dc
              JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
             WHERE dc.parent_object_id = OBJECT_ID('Events') AND c.name = 'QueueDripPerMinute';
            SET @sql += N'ALTER TABLE Events DROP COLUMN QueueDripPerMinute;';
            EXEC sp_executesql @sql;
        END
        IF OBJECT_ID('EventQueues','U') IS NOT NULL DROP TABLE EventQueues;
        """;

    public static Task<int> EnsureProfileAndDropQueueAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(ProfileAndQueueSql);

    /// Bảng UserCoupons (ví voucher của khách). Idempotent.
    private const string UserCouponsSql = """
        IF OBJECT_ID('UserCoupons','U') IS NULL
        BEGIN
            CREATE TABLE UserCoupons (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                UserId uniqueidentifier NOT NULL,
                CouponId uniqueidentifier NOT NULL,
                SavedAt datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_UserCoupons_UserId_CouponId ON UserCoupons (UserId, CouponId);
        END
        """;

    public static Task<int> EnsureUserCouponsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(UserCouponsSql);

    /// Bảng FeaturedItems (nghệ sĩ / chương trình yêu thích), ItemRatings (đánh giá sao), Notifications (chuông thông báo). Idempotent.
    private const string FavoritesSql = """
        IF OBJECT_ID('FeaturedItems','U') IS NULL
        BEGIN
            CREATE TABLE FeaturedItems (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Kind nvarchar(10) NOT NULL,
                Name nvarchar(200) NOT NULL,
                ImageUrl nvarchar(1000) NULL,
                Description nvarchar(1000) NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2 NOT NULL
            );
            CREATE INDEX IX_FeaturedItems_Kind_IsActive ON FeaturedItems (Kind, IsActive);
        END
        IF OBJECT_ID('ItemRatings','U') IS NULL
        BEGIN
            CREATE TABLE ItemRatings (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ItemId uniqueidentifier NOT NULL,
                UserId uniqueidentifier NOT NULL,
                Stars int NOT NULL,
                CreatedAt datetime2 NOT NULL,
                UpdatedAt datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_ItemRatings_ItemId_UserId ON ItemRatings (ItemId, UserId);
            CREATE INDEX IX_ItemRatings_UserId ON ItemRatings (UserId);
        END
        IF OBJECT_ID('Notifications','U') IS NULL
        BEGIN
            CREATE TABLE Notifications (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                UserId uniqueidentifier NOT NULL,
                Title nvarchar(200) NOT NULL,
                Message nvarchar(500) NULL,
                Link nvarchar(300) NULL,
                IsRead bit NOT NULL,
                CreatedAt datetime2 NOT NULL
            );
            CREATE INDEX IX_Notifications_UserId_IsRead_CreatedAt ON Notifications (UserId, IsRead, CreatedAt);
        END
        """;

    public static Task<int> EnsureFavoritesAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(FavoritesSql);

    /// Phân loại hàng cửa hàng lưu niệm: bảng ProductVariants + 2 cột trên ShopOrderItems. Idempotent.
    private const string ShopVariantsSql = """
        IF OBJECT_ID('ProductVariants','U') IS NULL
        BEGIN
            CREATE TABLE ProductVariants (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ProductId uniqueidentifier NOT NULL,
                Name nvarchar(100) NOT NULL,
                Price decimal(18,2) NOT NULL,
                Stock int NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_ProductVariants_Active DEFAULT 1,
                SortOrder int NOT NULL CONSTRAINT DF_ProductVariants_Sort DEFAULT 0
            );
            CREATE INDEX IX_ProductVariants_ProductId ON ProductVariants (ProductId);
        END
        IF OBJECT_ID('ShopOrderItems','U') IS NOT NULL AND COL_LENGTH('ShopOrderItems','VariantId') IS NULL
            ALTER TABLE ShopOrderItems ADD VariantId uniqueidentifier NULL;
        IF OBJECT_ID('ShopOrderItems','U') IS NOT NULL AND COL_LENGTH('ShopOrderItems','VariantName') IS NULL
            ALTER TABLE ShopOrderItems ADD VariantName nvarchar(100) NULL;
        """;

    public static Task<int> EnsureShopVariantsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(ShopVariantsSql);

    /// Đánh giá + báo cáo bình luận của cửa hàng. Idempotent.
    private const string ShopReviewsSql = """
        IF OBJECT_ID('ShopReviews','U') IS NULL
        BEGIN
            CREATE TABLE ShopReviews (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                OrderId uniqueidentifier NOT NULL,
                ProductId uniqueidentifier NOT NULL,
                VariantName nvarchar(100) NULL,
                UserId uniqueidentifier NOT NULL,
                Stars int NOT NULL,
                Content nvarchar(1000) NULL,
                CreatedAt datetime2 NOT NULL,
                UpdatedAt datetime2 NULL,
                IsHidden bit NOT NULL CONSTRAINT DF_ShopReviews_Hidden DEFAULT 0,
                AdminReply nvarchar(1000) NULL,
                AdminReplyAt datetime2 NULL
            );
            CREATE INDEX IX_ShopReviews_ProductId_IsHidden_CreatedAt ON ShopReviews (ProductId, IsHidden, CreatedAt);
            CREATE INDEX IX_ShopReviews_OrderId ON ShopReviews (OrderId);
            CREATE INDEX IX_ShopReviews_UserId ON ShopReviews (UserId);
        END
        IF OBJECT_ID('ShopReviewReports','U') IS NULL
        BEGIN
            CREATE TABLE ShopReviewReports (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ReviewId uniqueidentifier NOT NULL,
                ReporterId uniqueidentifier NOT NULL,
                Reason nvarchar(20) NOT NULL,
                Detail nvarchar(300) NULL,
                Status nvarchar(10) NOT NULL,
                CreatedAt datetime2 NOT NULL,
                HandledAt datetime2 NULL,
                HandledBy nvarchar(256) NULL
            );
            CREATE UNIQUE INDEX IX_ShopReviewReports_ReviewId_ReporterId ON ShopReviewReports (ReviewId, ReporterId);
            CREATE INDEX IX_ShopReviewReports_Status_CreatedAt ON ShopReviewReports (Status, CreatedAt);
        END
        """;

    public static Task<int> EnsureShopReviewsAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync(ShopReviewsSql);

    /// Chạy lần lượt mọi bản vá; một bản vá lỗi (vd. bảng chưa có) chỉ được ghi log, không làm hỏng các bản vá còn lại.
    public static async Task ApplyAllAsync(AppDbContext db, ILogger log)
    {
        foreach (var (name, run) in new (string, Func<Task<int>>)[]
        {
            ("admin-schema", () => ApplyAsync(db)),
            ("site-settings", () => EnsureSiteSettingsAsync(db)),
            ("coupons", () => EnsureCouponsAsync(db)),
            ("feedback", () => EnsureFeedbackAsync(db)),
            ("admin-tools", () => EnsureAdminToolsAsync(db)),
            ("profile-and-drop-queue", () => EnsureProfileAndDropQueueAsync(db)),
            ("user-coupons", () => EnsureUserCouponsAsync(db)),
            ("favorites", () => EnsureFavoritesAsync(db)),
            ("shop-variants", () => EnsureShopVariantsAsync(db)),
            ("shop-reviews", () => EnsureShopReviewsAsync(db)),
            // Luôn chạy sau cùng: bù mọi bảng/cột/index còn thiếu so với model (vd. DB trên host tạo từ phiên bản cũ thiếu bảng Tickets, cột TotalAmount)
            ("model-sync", async () => { await ModelSchemaSync.RunAsync(db, log); return 0; })
        })
        {
            try { await run(); }
            catch (Exception ex) { log.LogWarning(ex, "Bản vá schema '{Patch}' không chạy được", name); }
        }
    }
}
