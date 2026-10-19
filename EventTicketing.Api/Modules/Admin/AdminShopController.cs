using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Shop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record VariantInput(Guid? Id, string? Name, decimal Price, int Stock, bool IsActive = true);
public record ProductInput(string? Name, string? Category, string? Description, string? ImageUrl, decimal Price, int Stock, bool IsActive = true, List<VariantInput>? Variants = null);
public record ProductActiveDto(bool IsActive);
public record ShopStatusDto(string? Status);
public record ReviewHiddenDto(bool Hidden);
public record ReviewReplyDto(string? Text);
public record ReviewReportsDto(string? Action);

/// Admin quản lý sản phẩm lưu niệm và đơn hàng ở /Manage/Shop.
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/shop")]
public class AdminShopController(AppDbContext db, AuditService audit) : ControllerBase
{
    private static readonly string[] Categories = ["LightStick", "Apparel", "Accessory", "Other"];

    // ---------- Sản phẩm ----------
    [HttpGet("products")]
    public async Task<IActionResult> Products()
    {
        var list = await db.Products.AsNoTracking().OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.Name, p.Category, p.Description, p.ImageUrl, p.Price, p.Stock, p.IsActive, p.CreatedAt }).ToListAsync();
        var vars = await db.ProductVariants.AsNoTracking().OrderBy(v => v.SortOrder).ThenBy(v => v.Name).ToListAsync();
        return Ok(list.Select(p => new
        {
            p.Id, p.Name, p.Category, p.Description, p.ImageUrl, p.Price, p.Stock, p.IsActive, p.CreatedAt,
            variants = vars.Where(v => v.ProductId == p.Id).Select(v => new { v.Id, v.Name, v.Price, v.Stock, v.IsActive }).ToList()
        }));
    }

    /// Đồng bộ danh sách phân loại gửi lên với DB. null = không đụng tới phân loại. Phân loại đã có trong đơn hàng thì chỉ bị ẩn (giữ lịch sử đơn).
    /// Có phân loại đang bán: giá sản phẩm = giá thấp nhất, tồn kho = tổng tồn các phân loại đang bán.
    private async Task SyncVariantsAsync(Product x, List<VariantInput>? input)
    {
        if (input is null) return;
        var clean = new List<VariantInput>();
        foreach (var v in input)
        {
            var vn = (v.Name ?? "").Trim();
            if (vn.Length == 0) continue;
            if (vn.Length > 100) vn = vn[..100];
            if (v.Price < 0 || v.Price > 100_000_000) throw new AppException(400, "BAD_PRICE", $"Giá của phân loại «{vn}» không hợp lệ");
            if (v.Stock < 0 || v.Stock > 1_000_000) throw new AppException(400, "BAD_STOCK", $"Tồn kho của phân loại «{vn}» không hợp lệ");
            clean.Add(v with { Name = vn });
        }
        if (clean.Count > 40) throw new AppException(400, "TOO_MANY", "Mỗi sản phẩm tối đa 40 phân loại");
        if (clean.Select(q => q.Name!.ToLowerInvariant()).Distinct().Count() != clean.Count) throw new AppException(400, "DUP_VARIANT", "Tên phân loại bị trùng nhau");

        var existing = await db.ProductVariants.Where(q => q.ProductId == x.Id).ToListAsync();
        var existIds = existing.Select(r => r.Id).ToList();
        var used = existIds.Count == 0 ? new List<Guid>()
            : await db.ShopOrderItems.AsNoTracking().Where(i => i.VariantId.HasValue && existIds.Contains(i.VariantId.Value)).Select(i => i.VariantId!.Value).Distinct().ToListAsync();

        var keep = new HashSet<Guid>(); var order = 0;
        foreach (var v in clean)
        {
            var row = v.Id is null ? null : existing.FirstOrDefault(r => r.Id == v.Id);
            if (row is null) { row = new ProductVariant { ProductId = x.Id }; db.ProductVariants.Add(row); }
            row.Name = v.Name!; row.Price = v.Price; row.Stock = v.Stock; row.IsActive = v.IsActive; row.SortOrder = order++;
            keep.Add(row.Id);
        }
        foreach (var old in existing.Where(r => !keep.Contains(r.Id)).ToList())
        {
            if (used.Contains(old.Id)) old.IsActive = false; else db.ProductVariants.Remove(old);
        }
        var active = clean.Where(q => q.IsActive).ToList();
        if (active.Count > 0) { x.Price = active.Min(q => q.Price); x.Stock = active.Sum(q => q.Stock); }
    }

    private static void Apply(Product x, ProductInput dto)
    {
        var name = (dto.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException(400, "BAD_NAME", "Vui lòng nhập tên sản phẩm (tối thiểu 2 ký tự)");
        if (dto.Price < 0 || dto.Price > 100_000_000) throw new AppException(400, "BAD_PRICE", "Giá không hợp lệ");
        if (dto.Stock < 0 || dto.Stock > 1_000_000) throw new AppException(400, "BAD_STOCK", "Số lượng tồn kho không hợp lệ");
        x.Name = name.Length > 200 ? name[..200] : name;
        x.Category = Categories.Contains(dto.Category) ? dto.Category! : "Other";
        x.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        x.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        x.Price = dto.Price; x.Stock = dto.Stock; x.IsActive = dto.IsActive;
    }

    [HttpPost("products")]
    public async Task<IActionResult> Create(ProductInput dto)
    {
        var x = new Product(); Apply(x, dto);
        db.Products.Add(x); await SyncVariantsAsync(x, dto.Variants); await db.SaveChangesAsync();
        await audit.LogAsync(User, "shop.product.create", "Product", x.Id, x.Name);
        return Ok(new { x.Id, message = "Đã thêm sản phẩm" });
    }

    [HttpPut("products/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ProductInput dto)
    {
        var x = await db.Products.FirstOrDefaultAsync(p => p.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy sản phẩm");
        Apply(x, dto); await SyncVariantsAsync(x, dto.Variants); await db.SaveChangesAsync();
        await audit.LogAsync(User, "shop.product.update", "Product", x.Id, x.Name);
        return Ok(new { message = "Đã lưu" });
    }

    [HttpPut("products/{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, ProductActiveDto dto)
    {
        var x = await db.Products.FirstOrDefaultAsync(p => p.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy sản phẩm");
        x.IsActive = dto.IsActive; await db.SaveChangesAsync();
        await audit.LogAsync(User, dto.IsActive ? "shop.product.show" : "shop.product.hide", "Product", x.Id, x.Name);
        return Ok(new { message = dto.IsActive ? "Đã hiện sản phẩm" : "Đã ẩn sản phẩm" });
    }

    [HttpDelete("products/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var x = await db.Products.FirstOrDefaultAsync(p => p.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy sản phẩm");
        if (await db.ShopOrderItems.AnyAsync(i => i.ProductId == id))
        {
            x.IsActive = false; await db.SaveChangesAsync();   // đã có đơn: giữ lại để lịch sử đơn không mất, chỉ ẩn
            await audit.LogAsync(User, "shop.product.hide", "Product", x.Id, x.Name);
            return Ok(new { message = "Sản phẩm đã có đơn hàng nên chỉ được ẩn, không xoá hẳn" });
        }
        await db.ProductVariants.Where(v => v.ProductId == id).ExecuteDeleteAsync();
        db.Products.Remove(x); await db.SaveChangesAsync();
        await audit.LogAsync(User, "shop.product.delete", "Product", id, x.Name);
        return Ok(new { message = "Đã xoá sản phẩm" });
    }

    // ---------- Đánh giá & báo cáo bình luận ----------
    /// filter: reported (có báo cáo chưa xử lý) | hidden | all
    [HttpGet("reviews")]
    public async Task<IActionResult> Reviews(string? filter)
    {
        var openIds = db.ShopReviewReports.Where(x => x.Status == "Open").Select(x => x.ReviewId);
        var qry = db.ShopReviews.AsNoTracking().AsQueryable();
        if (filter == "reported") qry = qry.Where(r => openIds.Contains(r.Id));
        else if (filter == "hidden") qry = qry.Where(r => r.IsHidden);
        var rows = await qry.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync();

        var rIds = rows.Select(r => r.Id).ToList();
        var uIds = rows.Select(r => r.UserId).ToList();
        var pIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var reports = await db.ShopReviewReports.AsNoTracking().Where(x => rIds.Contains(x.ReviewId)).OrderByDescending(x => x.CreatedAt).ToListAsync();
        uIds.AddRange(reports.Select(x => x.ReporterId));
        var users = await db.Users.AsNoTracking().Where(u => uIds.Contains(u.Id)).Select(u => new { u.Id, u.FullName, u.Email }).ToDictionaryAsync(u => u.Id);
        var names = await db.Products.AsNoTracking().Where(p => pIds.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name);

        return Ok(rows.Select(r => new
        {
            r.Id, r.OrderId, r.ProductId, productName = names.GetValueOrDefault(r.ProductId, "(đã xoá)"), r.VariantName, r.Stars, r.Content, r.CreatedAt, r.IsHidden, r.AdminReply, r.AdminReplyAt,
            userName = users.TryGetValue(r.UserId, out var u) ? u.FullName : "", userEmail = users.TryGetValue(r.UserId, out var u2) ? u2.Email : "",
            reports = reports.Where(x => x.ReviewId == r.Id).Select(x => new
            {
                x.Id, x.Reason, x.Detail, x.Status, x.CreatedAt, x.HandledBy,
                reporter = users.TryGetValue(x.ReporterId, out var ru) ? ru.FullName : ""
            }).ToList()
        }));
    }

    [HttpPut("reviews/{id:guid}/hidden")]
    public async Task<IActionResult> HideReview(Guid id, ReviewHiddenDto dto)
    {
        var r = await db.ShopReviews.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đánh giá");
        r.IsHidden = dto.Hidden; await db.SaveChangesAsync();
        await audit.LogAsync(User, dto.Hidden ? "shop.review.hide" : "shop.review.show", "ShopReview", r.Id);
        return Ok(new { message = dto.Hidden ? "Đã ẩn bình luận" : "Đã hiện lại bình luận" });
    }

    [HttpPut("reviews/{id:guid}/reply")]
    public async Task<IActionResult> ReplyReview(Guid id, ReviewReplyDto dto)
    {
        var r = await db.ShopReviews.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đánh giá");
        var t = (dto.Text ?? "").Trim();
        if (t.Length > 1000) throw new AppException(400, "TOO_LONG", "Phản hồi tối đa 1000 ký tự");
        r.AdminReply = t.Length == 0 ? null : t; r.AdminReplyAt = t.Length == 0 ? null : DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "shop.review.reply", "ShopReview", r.Id);
        return Ok(new { message = t.Length == 0 ? "Đã xoá phản hồi" : "Đã gửi phản hồi" });
    }

    /// Xử lý các báo cáo chưa xử lý của một bình luận: action = hide (báo cáo đúng -> ẩn bình luận) | dismiss (bỏ qua báo cáo).
    [HttpPut("reviews/{id:guid}/reports")]
    public async Task<IActionResult> HandleReports(Guid id, ReviewReportsDto dto)
    {
        var r = await db.ShopReviews.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đánh giá");
        if (dto.Action is not ("hide" or "dismiss")) throw new AppException(400, "BAD_ACTION", "Thao tác không hợp lệ");
        var hide = dto.Action == "hide";
        if (hide) r.IsHidden = true;
        var by = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "admin";
        var newStatus = hide ? "Resolved" : "Dismissed";
        await db.ShopReviewReports.Where(x => x.ReviewId == id && x.Status == "Open")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, newStatus).SetProperty(x => x.HandledAt, DateTime.UtcNow).SetProperty(x => x.HandledBy, by));
        await db.SaveChangesAsync();
        await audit.LogAsync(User, hide ? "shop.review.report.hide" : "shop.review.report.dismiss", "ShopReview", r.Id);
        return Ok(new { message = hide ? "Đã ẩn bình luận và đóng báo cáo" : "Đã bỏ qua báo cáo" });
    }

    // ---------- Đơn hàng ----------
    [HttpGet("orders")]
    public async Task<IActionResult> Orders()
    {
        var orders = await db.ShopOrders.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync();
        var oids = orders.Select(o => o.Id).ToList();
        var uids = orders.Select(o => o.UserId).Distinct().ToList();
        var items = await db.ShopOrderItems.AsNoTracking().Where(i => oids.Contains(i.OrderId)).ToListAsync();
        var users = await db.Users.AsNoTracking().Where(u => uids.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync();
        return Ok(orders.Select(o => new
        {
            o.Id, o.Total, status = o.Status.ToString(), o.CreatedAt, o.ReceiverName, o.Phone, o.Address, o.Note,
            paymentMethod = ShopPay.IsPayOs(o) ? ShopPay.PayOs : ShopPay.Cod,
            paymentStatus = o.PaymentStatus == ShopPay.Paid ? ShopPay.Paid : ShopPay.IsPayOs(o) && o.Status != ShopOrderStatus.Cancelled ? ShopPay.Pending : ShopPay.Unpaid,
            o.PaidAt, o.CouponCode, o.DiscountAmount,
            email = users.FirstOrDefault(u => u.Id == o.UserId)?.Email,
            items = items.Where(i => i.OrderId == o.Id).Select(i => new { i.ProductName, i.VariantName, i.UnitPrice, i.Quantity })
        }));
    }

    private static readonly Dictionary<ShopOrderStatus, ShopOrderStatus[]> Next = new()
    {
        [ShopOrderStatus.New] = [ShopOrderStatus.Confirmed, ShopOrderStatus.Cancelled],
        [ShopOrderStatus.Confirmed] = [ShopOrderStatus.Shipping, ShopOrderStatus.Cancelled],
        [ShopOrderStatus.Shipping] = [ShopOrderStatus.Done, ShopOrderStatus.Cancelled]
    };

    [HttpPut("orders/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, ShopStatusDto dto)
    {
        if (!Enum.TryParse<ShopOrderStatus>(dto.Status, out var to)) throw new AppException(400, "BAD_STATUS", "Trạng thái không hợp lệ");
        await using var tx = await db.Database.BeginTransactionAsync();
        var o = await db.ShopOrders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException(404, "NOT_FOUND", "Không tìm thấy đơn hàng");
        if (!Next.TryGetValue(o.Status, out var allowed) || !allowed.Contains(to))
            throw new AppException(409, "BAD_TRANSITION", "Không thể chuyển đơn sang trạng thái này");
        var from = o.Status;
        // Đơn payOS phải được thanh toán rồi mới xác nhận / giao (huỷ thì được)
        if (ShopPay.IsPayOs(o) && o.PaymentStatus != ShopPay.Paid && to != ShopOrderStatus.Cancelled)
            throw new AppException(409, "UNPAID", "Đơn payOS này chưa được thanh toán nên chưa thể xác nhận");
        o.Status = to;
        if (to == ShopOrderStatus.Done && !ShopPay.IsPayOs(o) && o.PaymentStatus != ShopPay.Paid) { o.PaymentStatus = ShopPay.Paid; o.PaidAt = DateTime.UtcNow; }   // COD: giao xong = đã thu tiền
        if (to == ShopOrderStatus.Cancelled && o.PaymentStatus == ShopPay.Pending) o.PaymentStatus = ShopPay.Unpaid;
        await db.SaveChangesAsync();
        if (to == ShopOrderStatus.Cancelled) await ShopStock.RestockAsync(db, o.Id);   // huỷ đơn: trả hàng về kho
        await tx.CommitAsync();
        await audit.LogAsync(User, "shop.order.status", "ShopOrder", o.Id, $"{from} → {to}");
        var refund = to == ShopOrderStatus.Cancelled && ShopPay.IsPayOs(o) && o.PaymentStatus == ShopPay.Paid;
        return Ok(new { message = refund ? "Đã huỷ đơn. Đơn này khách đã trả qua payOS, nhớ hoàn tiền cho khách." : "Đã cập nhật đơn hàng" });
    }
}
