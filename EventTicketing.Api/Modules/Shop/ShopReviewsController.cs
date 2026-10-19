using System.Security.Claims;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Shop;

public record ReviewInput(Guid ProductId, string? VariantName, int Stars, string? Content);
public record ReportInput(string? Reason, string? Detail);

/// Đánh giá + bình luận sản phẩm (kiểu Shopee) và báo cáo bình luận vi phạm.
/// Chỉ khách đã nhận hàng (đơn "Hoàn tất") mới đánh giá được; ai đăng nhập cũng báo cáo được bình luận của người khác.
[ApiController, Route("api/shop")]
public class ShopReviewsController(AppDbContext db) : ControllerBase
{
    private static readonly string[] Reasons = ["Spam", "Offensive", "Fake", "Personal", "Other"];
    private const int PageSize = 10;

    /// "Nguyễn Văn An" -> "N***n" (che tên người đánh giá như Shopee).
    public static string Mask(string? name)
    {
        var n = (name ?? "").Replace(" ", "");
        if (n.Length == 0) return "Khách hàng";
        if (n.Length <= 2) return n[0] + "***";
        return n[0] + "***" + n[^1];
    }

    private Guid? Me() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    // ---------- Công khai: danh sách đánh giá của một sản phẩm ----------
    [HttpGet("products/{productId:guid}/reviews")]
    public async Task<IActionResult> ForProduct(Guid productId, int page = 1, int stars = 0)
    {
        var me = Me();
        var visible = db.ShopReviews.AsNoTracking().Where(r => r.ProductId == productId && !r.IsHidden);
        var dist = await visible.GroupBy(r => r.Stars).Select(x => new { Stars = x.Key, N = x.Count() }).ToListAsync();
        var total = dist.Sum(d => d.N);
        var avg = total == 0 ? 0 : Math.Round(dist.Sum(d => d.Stars * d.N) / (double)total, 1);
        var filtered = stars is >= 1 and <= 5 ? visible.Where(r => r.Stars == stars) : visible;
        page = Math.Max(1, page);
        var rows = await filtered.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id, (r, u) => new { r.Id, r.UserId, u.FullName, r.Stars, r.Content, r.VariantName, r.CreatedAt, r.UpdatedAt, r.AdminReply, r.AdminReplyAt })
            .ToListAsync();
        var more = rows.Count > PageSize;
        if (more) rows.RemoveAt(rows.Count - 1);

        var reported = new HashSet<Guid>();
        if (me is Guid uid0 && rows.Count > 0)
        {
            var ids = rows.Select(r => r.Id).ToList();
            reported = (await db.ShopReviewReports.AsNoTracking().Where(x => x.ReporterId == uid0 && ids.Contains(x.ReviewId)).Select(x => x.ReviewId).ToListAsync()).ToHashSet();
        }
        return Ok(new
        {
            average = avg,
            count = total,
            distribution = Enumerable.Range(1, 5).Select(s => new { stars = s, count = dist.FirstOrDefault(d => d.Stars == s)?.N ?? 0 }).ToList(),
            page, hasMore = more,
            items = rows.Select(r => new
            {
                r.Id, name = Mask(r.FullName), r.Stars, r.Content, r.VariantName, r.CreatedAt, edited = r.UpdatedAt != null,
                r.AdminReply, r.AdminReplyAt,
                mine = me == r.UserId, reportedByMe = reported.Contains(r.Id)
            })
        });
    }

    // ---------- Khách: đánh giá các món trong đơn của mình ----------
    [HttpGet("orders/{orderId:guid}/reviews"), Authorize]
    public async Task<IActionResult> ForOrder(Guid orderId)
    {
        var uid = User.UserId();
        if (!await db.ShopOrders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.UserId == uid)) throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        var list = await db.ShopReviews.AsNoTracking().Where(r => r.OrderId == orderId && r.UserId == uid).ToListAsync();
        return Ok(list.Select(r => new { r.Id, r.ProductId, r.VariantName, r.Stars, r.Content, r.CreatedAt, edited = r.UpdatedAt != null, r.IsHidden, r.AdminReply, r.AdminReplyAt }));
    }

    /// Gửi hoặc sửa đánh giá cho một món của đơn.
    [HttpPut("orders/{orderId:guid}/reviews"), Authorize]
    public async Task<IActionResult> Save(Guid orderId, ReviewInput dto)
    {
        var uid = User.UserId();
        var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId && x.UserId == uid) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        if (o.Status != ShopOrderStatus.Done) throw new AppException(409, "NOT_DONE", "Chỉ đánh giá được khi đơn hàng đã hoàn tất");
        if (dto.Stars is < 1 or > 5) throw new AppException(400, "BAD_STARS", "Vui lòng chọn số sao từ 1 đến 5");
        var content = (dto.Content ?? "").Trim();
        if (content.Length > 1000) throw new AppException(400, "TOO_LONG", "Nội dung tối đa 1000 ký tự");
        var variant = string.IsNullOrWhiteSpace(dto.VariantName) ? null : dto.VariantName.Trim();

        var inOrder = await db.ShopOrderItems.AsNoTracking().AnyAsync(i => i.OrderId == orderId && i.ProductId == dto.ProductId && i.VariantName == variant);
        if (!inOrder) throw new AppException(400, "NOT_IN_ORDER", "Sản phẩm này không có trong đơn hàng");

        var r = await db.ShopReviews.FirstOrDefaultAsync(x => x.OrderId == orderId && x.UserId == uid && x.ProductId == dto.ProductId && x.VariantName == variant);
        if (r is null)
        {
            r = new ShopReview { OrderId = orderId, ProductId = dto.ProductId, VariantName = variant, UserId = uid, Stars = dto.Stars, Content = content.Length == 0 ? null : content };
            db.ShopReviews.Add(r);
        }
        else
        {
            r.Stars = dto.Stars; r.Content = content.Length == 0 ? null : content; r.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return Ok(new { r.Id, r.ProductId, r.VariantName, r.Stars, r.Content, r.CreatedAt, edited = r.UpdatedAt != null, r.IsHidden, r.AdminReply, r.AdminReplyAt, message = "Cảm ơn bạn đã đánh giá!" });
    }

    [HttpDelete("reviews/{id:guid}"), Authorize]
    public async Task<IActionResult> Delete(Guid id)
    {
        var uid = User.UserId();
        var r = await db.ShopReviews.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đánh giá");
        await db.ShopReviewReports.Where(x => x.ReviewId == id).ExecuteDeleteAsync();
        db.ShopReviews.Remove(r);
        await db.SaveChangesAsync();
        return Ok(new { message = "Đã xoá đánh giá" });
    }

    // ---------- Báo cáo bình luận ----------
    [HttpPost("reviews/{id:guid}/report"), Authorize]
    public async Task<IActionResult> Report(Guid id, ReportInput dto)
    {
        var uid = User.UserId();
        var r = await db.ShopReviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsHidden) ?? throw new AppException(404, "NOT_FOUND", "Bình luận không tồn tại hoặc đã bị ẩn");
        if (r.UserId == uid) throw new AppException(400, "OWN_REVIEW", "Bạn không thể báo cáo bình luận của chính mình");
        var reason = (dto.Reason ?? "").Trim();
        if (!Reasons.Contains(reason)) throw new AppException(400, "BAD_REASON", "Vui lòng chọn lý do báo cáo");
        var detail = (dto.Detail ?? "").Trim();
        if (detail.Length > 300) throw new AppException(400, "TOO_LONG", "Mô tả tối đa 300 ký tự");
        if (reason == "Other" && detail.Length < 5) throw new AppException(400, "NEED_DETAIL", "Vui lòng mô tả lý do (ít nhất 5 ký tự)");
        if (await db.ShopReviewReports.AnyAsync(x => x.ReviewId == id && x.ReporterId == uid)) throw new AppException(409, "ALREADY_REPORTED", "Bạn đã báo cáo bình luận này rồi");
        var since = DateTime.UtcNow.AddDays(-1);
        if (await db.ShopReviewReports.CountAsync(x => x.ReporterId == uid && x.CreatedAt > since) >= 30) throw new AppException(429, "TOO_MANY", "Bạn báo cáo quá nhiều trong hôm nay, vui lòng thử lại sau");

        db.ShopReviewReports.Add(new ShopReviewReport { ReviewId = id, ReporterId = uid, Reason = reason, Detail = detail.Length == 0 ? null : detail });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw new AppException(409, "ALREADY_REPORTED", "Bạn đã báo cáo bình luận này rồi"); }   // gửi trùng cùng lúc
        return Ok(new { message = "Đã gửi báo cáo. Cửa hàng sẽ xem xét sớm." });
    }
}
