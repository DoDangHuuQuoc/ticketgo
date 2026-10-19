using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Favorites;

public static class FavoriteRules
{
    /// Số lượt đánh giá tối thiểu để mục được gắn dấu sao nổi bật cạnh ảnh đại diện.
    public const int HotMinRatings = 5;
    public static bool IsHot(int count) => count >= HotMinRatings;
}

public record RateDto(int Stars);

/// Phía khách: xem danh sách nghệ sĩ / chương trình yêu thích và đánh giá 1–5 sao.
[ApiController, Route("api/favorites")]
public class FavoriteItemsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? kind)
    {
        Guid? me = User.Identity?.IsAuthenticated == true ? User.UserId() : null;
        var q = db.FeaturedItems.AsNoTracking().Where(x => x.IsActive);
        if (kind is "Artist" or "Program") q = q.Where(x => x.Kind == kind);

        var items = await q.Select(x => new
        {
            x.Id, x.Kind, x.Name, x.ImageUrl, x.Description,
            count = db.ItemRatings.Count(r => r.ItemId == x.Id),
            avg = db.ItemRatings.Where(r => r.ItemId == x.Id).Average(r => (double?)r.Stars) ?? 0
        }).ToListAsync();

        // Số sao của chính khách này: truy vấn riêng cho đơn giản và chắc chắn dịch được sang SQL
        var mine = new Dictionary<Guid, int>();
        if (me is Guid uid) mine = await db.ItemRatings.AsNoTracking().Where(r => r.UserId == uid).ToDictionaryAsync(r => r.ItemId, r => r.Stars);

        // Mục được đánh giá nhiều nhất lên đầu, rồi tới điểm trung bình
        return Ok(items.OrderByDescending(x => x.count).ThenByDescending(x => x.avg).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Kind, x.Name, x.ImageUrl, x.Description, x.count, avg = Math.Round(x.avg, 1), myStars = mine.GetValueOrDefault(x.Id), hot = FavoriteRules.IsHot(x.count) }));
    }

    [HttpPost("{id:guid}/rate"), Authorize]
    public async Task<IActionResult> Rate(Guid id, RateDto dto)
    {
        if (dto.Stars is < 1 or > 5) throw new AppException(400, "BAD_STARS", "Số sao phải từ 1 đến 5");
        if (!await db.FeaturedItems.AnyAsync(x => x.Id == id && x.IsActive)) throw new AppException(404, "NOT_FOUND", "Không tìm thấy nghệ sĩ / chương trình");

        var uid = User.UserId();
        var r = await db.ItemRatings.FirstOrDefaultAsync(x => x.ItemId == id && x.UserId == uid);
        if (r is null) db.ItemRatings.Add(new ItemRating { ItemId = id, UserId = uid, Stars = dto.Stars });
        else { r.Stars = dto.Stars; r.UpdatedAt = DateTime.UtcNow; }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)   // 2 request cùng lúc cho cùng một khách: chỉ giữ 1 dòng, cập nhật số sao
        {
            db.ChangeTracker.Clear();
            await db.ItemRatings.Where(x => x.ItemId == id && x.UserId == uid).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stars, dto.Stars).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        }

        var count = await db.ItemRatings.CountAsync(x => x.ItemId == id);
        var avg = await db.ItemRatings.Where(x => x.ItemId == id).AverageAsync(x => (double)x.Stars);
        return Ok(new { message = "Cảm ơn bạn đã đánh giá!", myStars = dto.Stars, count, avg = Math.Round(avg, 1), hot = FavoriteRules.IsHot(count) });
    }
}
