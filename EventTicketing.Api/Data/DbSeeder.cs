using System.Text.Json;
using EventTicketing.Models;
using EventTicketing.Modules.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Data;

/// Dữ liệu demo (chỉ Development). Tài khoản: admin@demo.com / staff@demo.com / user@demo.com — mật khẩu mặc định: Demo@12345 (đổi bằng Seed:Password)
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher<User>>();
        var pw = cfg["Seed:Password"] is { Length: >= 8 } cfgPw ? cfgPw : "Demo@12345";

        User Make(string email, string name, string role)
        {
            var u = new User { Email = email, FullName = name, Role = role, IsVerified = true };
            u.PasswordHash = hasher.HashPassword(u, pw);
            return u;
        }

        // Tài khoản admin mẫu (admin@demo.com): luôn bảo đảm tồn tại, đúng vai trò Admin, đã xác thực, không bị khoá.
        // Mật khẩu = Seed:Password khi tạo mới. Nếu đã tồn tại mà không nhớ mật khẩu: đặt Seed:ResetAdminPassword=true rồi khởi động lại
        // để đặt lại về Seed:Password (nhớ tắt sau khi đăng nhập xong, và nên đổi mật khẩu).
        async Task EnsureDemoAdminAsync()
        {
            var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == "admin@demo.com");
            if (admin is null) db.Users.Add(Make("admin@demo.com", "Quản trị viên", "Admin"));
            else
            {
                admin.Role = "Admin"; admin.IsVerified = true; admin.IsLocked = false;
                if (cfg.GetValue("Seed:ResetAdminPassword", false)) admin.PasswordHash = hasher.HashPassword(admin, pw);
            }
            await db.SaveChangesAsync();
        }

        // DB đã có người dùng: không nạp lại dữ liệu demo, nhưng bảo đảm luôn có admin mẫu để vào khu quản trị.
        if (await db.Users.AnyAsync())
        {
            await EnsureDemoAdminAsync();
            await SeedFavoritesAsync(db);
            await SeedShopAsync(db);
            return;
        }
        db.Users.AddRange(Make("admin@demo.com", "Quản trị viên", "Admin"),
                          Make("staff@demo.com", "Nhân viên soát vé", "Staff"),
                          Make("user@demo.com", "Khách hàng Demo", "User"));

        var concert = new Event
        {
            Name = "Đại nhạc hội Sao Đêm 2026", Category = "Concert", Venue = "SVĐ Mỹ Đình, Hà Nội",
            StartsAt = DateTime.UtcNow.AddDays(30),
            TicketTypes =
            {
                new TicketType { Name = "VIP", Zone = "Khu A", Price = 2_500_000, TotalQuantity = 200, AvailableQuantity = 200 },
                new TicketType { Name = "Thường", Zone = "Khu B", Price = 800_000, TotalQuantity = 2000, AvailableQuantity = 2000 }
            }
        };
        var match = new Event
        {
            Name = "Chung kết Cúp Quốc gia", Category = "Sport", Venue = "SVĐ Thống Nhất, TP.HCM",
            StartsAt = DateTime.UtcNow.AddDays(14),
            TicketTypes = { new TicketType { Name = "Khán đài A", Zone = "A", Price = 300_000, TotalQuantity = 500, AvailableQuantity = 500 } }
        };
        db.Events.AddRange(concert, match);
        db.Coupons.Add(new Coupon { Code = "GIAM10", Description = "Giảm 10% (tối đa 200.000 đ)", Type = CouponType.Percent, Value = 10, MaxDiscount = 200_000, MinOrder = 500_000, PerUserLimit = 1 });
        await db.SaveChangesAsync();
        await SeedFavoritesAsync(db);
        await SeedShopAsync(db);
    }

    /// Vài nghệ sĩ / chương trình mẫu (chỉ khi chưa có mục nào) để thấy ngay mục Yêu thích. Admin sửa hoặc xoá ở /Manage/Favorites.
    private static async Task SeedFavoritesAsync(AppDbContext db)
    {
        if (await db.FeaturedItems.AnyAsync()) return;
        db.FeaturedItems.AddRange(
            new FeaturedItem { Kind = "Artist", Name = "Ca sĩ Sao Đêm", Description = "Giọng ca chủ lực của Đại nhạc hội Sao Đêm 2026." },
            new FeaturedItem { Kind = "Artist", Name = "Ban nhạc Hoàng Hôn", Description = "Ban nhạc indie được yêu thích với những bản ballad nhẹ nhàng." },
            new FeaturedItem { Kind = "Program", Name = "Đại nhạc hội Sao Đêm", Description = "Chương trình ca nhạc quy mô lớn nhất mùa hè." },
            new FeaturedItem { Kind = "Program", Name = "Giải bóng đá Cúp Quốc gia", Description = "Những trận cầu đỉnh cao của bóng đá trong nước." });
        await db.SaveChangesAsync();
    }

    /// Vài sản phẩm lưu niệm mẫu (chỉ khi chưa có sản phẩm nào). Admin sửa hoặc xoá ở /Manage/Shop.
    private static async Task SeedShopAsync(AppDbContext db)
    {
        if (await db.Products.AnyAsync()) return;
        var stick = new Product { Name = "Light stick chính hãng", Category = "LightStick", Price = 250000, Stock = 100, Description = "Đèn cổ vũ đổi được nhiều màu, pin AAA đi kèm. Dùng cho mọi đêm nhạc." };
        var shirt = new Product { Name = "Áo thun lưu niệm", Category = "Apparel", Price = 180000, Stock = 80, Description = "Cotton 100%, in hình logo chương trình." };
        db.Products.AddRange(
            stick,
            new Product { Name = "Light stick phiên bản giới hạn", Category = "LightStick", Price = 390000, Stock = 50, Description = "Bản đặc biệt có dây đeo tay, phát sáng đồng bộ ở sự kiện hỗ trợ." },
            shirt,
            new Product { Name = "Móc khoá kỷ niệm", Category = "Accessory", Price = 45000, Stock = 200, Description = "Móc khoá kim loại khắc tên sự kiện." },
            new Product { Name = "Băng đô cổ vũ", Category = "Accessory", Price = 60000, Stock = 120, Description = "Băng đô phát sáng, hợp để cổ vũ ngoài trời." });
        // Phân loại mẫu (kiểu Shopee): tồn kho và giá của sản phẩm = tổng/thấp nhất của các phân loại
        var order = 0;
        foreach (var c in new[] { "Đỏ", "Xanh dương", "Tím", "Trắng" })
            db.ProductVariants.Add(new ProductVariant { ProductId = stick.Id, Name = c, Price = 250000, Stock = 25, SortOrder = order++ });
        order = 0;
        foreach (var (sz, price) in new[] { ("S", 180000m), ("M", 180000m), ("L", 180000m), ("XL", 190000m) })
            db.ProductVariants.Add(new ProductVariant { ProductId = shirt.Id, Name = "Size " + sz, Price = price, Stock = 20, SortOrder = order++ });
        await db.SaveChangesAsync();
    }
}
