using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Favorites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record FeaturedInput(string? Kind, string? Name, string? ImageUrl, string? Description, bool IsActive = true, bool Notify = true);
public record FeaturedActiveDto(bool IsActive);

/// Admin thêm / sửa / ẩn / xoá nghệ sĩ & chương trình yêu thích (/Manage/Favorites).
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/favorites")]
public class AdminFavoritesController(AppDbContext db, AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await db.FeaturedItems.AsNoTracking().OrderByDescending(x => x.CreatedAt).Select(x => new
        {
            x.Id, x.Kind, x.Name, x.ImageUrl, x.Description, x.IsActive, x.CreatedAt,
            count = db.ItemRatings.Count(r => r.ItemId == x.Id),
            avg = db.ItemRatings.Where(r => r.ItemId == x.Id).Average(r => (double?)r.Stars) ?? 0
        }).ToListAsync();
        return Ok(new { hotMin = FavoriteRules.HotMinRatings, items = items.Select(x => new { x.Id, x.Kind, x.Name, x.ImageUrl, x.Description, x.IsActive, x.CreatedAt, x.count, avg = Math.Round(x.avg, 1), hot = FavoriteRules.IsHot(x.count) }) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(FeaturedInput dto)
    {
        var x = new FeaturedItem();
        Apply(x, dto);
        db.FeaturedItems.Add(x);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "favorite.create", "FeaturedItem", x.Id, $"{x.Kind}: {x.Name}");

        // Báo cho khách qua chuông thông báo (lỗi gửi thông báo không làm hỏng việc tạo)
        if (x.IsActive && dto.Notify)
        {
            try { await db.NotifyAllUsersAsync(x.Kind == "Artist" ? "Nghệ sĩ yêu thích mới" : "Chương trình yêu thích mới", $"{x.Name} vừa được thêm. Vào xem và đánh giá nhé!", "/Favorites"); }
            catch (Exception) { /* bỏ qua */ }
        }
        return Ok(new { x.Id, message = "Đã thêm" });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, FeaturedInput dto)
    {
        var x = await db.FeaturedItems.FirstOrDefaultAsync(i => i.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy mục này");
        Apply(x, dto);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "favorite.update", "FeaturedItem", x.Id, $"{x.Kind}: {x.Name}");
        return Ok(new { message = "Đã lưu" });
    }

    [HttpPut("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, FeaturedActiveDto dto)
    {
        var n = await db.FeaturedItems.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, dto.IsActive));
        if (n == 0) throw new AppException(404, "NOT_FOUND", "Không tìm thấy mục này");
        await audit.LogAsync(User, dto.IsActive ? "favorite.show" : "favorite.hide", "FeaturedItem", id);
        return Ok(new { message = dto.IsActive ? "Đã hiện cho khách" : "Đã ẩn khỏi khách" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var x = await db.FeaturedItems.FirstOrDefaultAsync(i => i.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy mục này");
        await db.ItemRatings.Where(r => r.ItemId == id).ExecuteDeleteAsync();
        db.FeaturedItems.Remove(x);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "favorite.delete", "FeaturedItem", id, x.Name);
        return Ok(new { message = "Đã xoá" });
    }

    private static void Apply(FeaturedItem x, FeaturedInput d)
    {
        if (d.Kind is not ("Artist" or "Program")) throw new AppException(400, "BAD_KIND", "Loại phải là Nghệ sĩ hoặc Chương trình");
        var name = (d.Name ?? "").Trim();
        if (name.Length < 2 || name.Length > 200) throw new AppException(400, "BAD_NAME", "Tên từ 2 đến 200 ký tự");
        var desc = (d.Description ?? "").Trim();
        if (desc.Length > 1000) throw new AppException(400, "DESC_LONG", "Mô tả tối đa 1000 ký tự");
        var img = (d.ImageUrl ?? "").Trim();
        if (img.Length > 1000) throw new AppException(400, "IMG_LONG", "Đường dẫn ảnh quá dài");
        if (img.Length > 0 && !(img.StartsWith('/') && !img.StartsWith("//")) && !(Uri.TryCreate(img, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
            throw new AppException(400, "BAD_IMG", "Ảnh phải là đường dẫn http(s):// hoặc bắt đầu bằng /");

        x.Kind = d.Kind; x.Name = name; x.Description = desc.Length == 0 ? null : desc;
        x.ImageUrl = img.Length == 0 ? null : img; x.IsActive = d.IsActive;
    }
}
