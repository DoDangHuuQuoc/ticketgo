using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Admin;

public record InsBucket(int Key, int Orders, int Tickets, decimal Revenue);
public record InsCustomer(Guid UserId, string Name, string Email, int Orders, int Tickets, decimal Spent, DateTime Last);
public record InsData(
    DateTime From, DateTime To, int Orders, int Paid, int Pending, int Cancelled, int Expired,
    int Buyers, int RepeatBuyers, int NewUsers, decimal AvgOrder,
    IReadOnlyList<InsBucket> ByHour, IReadOnlyList<InsBucket> ByWeekday, IReadOnlyList<InsCustomer> TopCustomers,
    int FbTotal, int FbNew, int FbInProgress, int FbResolved, double? FbAvgReplyHours, IReadOnlyDictionary<string, int> FbByCategory);

/// Phân tích sâu: giờ/thứ bán chạy, khách hàng giá trị cao, tỷ lệ chuyển đổi/huỷ, thống kê phản hồi. Giờ tính theo múi giờ Việt Nam (UTC+7).
[ApiController, Authorize(Roles = "Admin"), Route("api/admin/insights")]
public class AdminInsightsController(AppDbContext db, AuditService audit) : ControllerBase
{
    private const int Vn = 7;

    private async Task<InsData> Build(DateTime? from, DateTime? to)
    {
        var end = to ?? DateTime.UtcNow; var start = from ?? end.AddDays(-30);
        if (start >= end) throw new AppException(400, "BAD_RANGE", "Khoảng thời gian không hợp lệ");
        if ((end - start).TotalDays > 366) start = end.AddDays(-366);

        var inRange = db.Bookings.AsNoTracking().Where(b => b.CreatedAt >= start && b.CreatedAt < end);
        var sc = await inRange.GroupBy(b => b.Status).Select(g => new { Status = g.Key, C = g.Count() }).ToListAsync();
        int N(BookingStatus s) => sc.FirstOrDefault(x => x.Status == s)?.C ?? 0;

        // Đơn có thu tiền (loại vé mời 0đ)
        var paid = await inRange.Where(b => b.Status == BookingStatus.Paid && b.TotalAmount > 0)
            .Select(b => new { b.UserId, b.CreatedAt, b.TotalAmount, b.Quantity }).ToListAsync();

        var byHour = Enumerable.Range(0, 24).Select(h =>
        {
            var x = paid.Where(p => p.CreatedAt.AddHours(Vn).Hour == h).ToList();
            return new InsBucket(h, x.Count, x.Sum(p => p.Quantity), x.Sum(p => p.TotalAmount));
        }).ToList();
        var byDow = Enumerable.Range(0, 7).Select(d =>   // 0 = Thứ 2 … 6 = Chủ nhật
        {
            var x = paid.Where(p => ((int)p.CreatedAt.AddHours(Vn).DayOfWeek + 6) % 7 == d).ToList();
            return new InsBucket(d, x.Count, x.Sum(p => p.Quantity), x.Sum(p => p.TotalAmount));
        }).ToList();

        var perUser = paid.GroupBy(p => p.UserId).Select(g => new { UserId = g.Key, Orders = g.Count(), Tickets = g.Sum(x => x.Quantity), Spent = g.Sum(x => x.TotalAmount), Last = g.Max(x => x.CreatedAt) })
            .OrderByDescending(x => x.Spent).ToList();
        var top = perUser.Take(10).ToList();
        var ids = top.Select(t => t.UserId).ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.FullName, u.Email }).ToListAsync();
        var topCustomers = top.Select(t =>
        {
            var p = people.FirstOrDefault(x => x.Id == t.UserId);
            return new InsCustomer(t.UserId, p?.FullName ?? "(đã xoá)", p?.Email ?? "", t.Orders, t.Tickets, t.Spent, t.Last);
        }).ToList();

        // Khách quay lại: đã có từ 2 đơn đã thanh toán trở lên (tính toàn thời gian) trong số người mua ở kỳ này
        var buyerIds = perUser.Select(x => x.UserId).ToHashSet();
        var allPaid = await db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.Paid && b.TotalAmount > 0).Select(b => b.UserId).ToListAsync();
        var repeat = allPaid.GroupBy(x => x).Count(g => g.Count() >= 2 && buyerIds.Contains(g.Key));
        var newUsers = await db.Users.AsNoTracking().CountAsync(u => u.Role == "User" && u.CreatedAt >= start && u.CreatedAt < end);

        var fb = await db.Feedbacks.AsNoTracking().Where(f => f.CreatedAt >= start && f.CreatedAt < end)
            .Select(f => new { f.Status, f.Category, f.CreatedAt, f.RepliedAt }).ToListAsync();
        var replied = fb.Where(f => f.RepliedAt != null).Select(f => (f.RepliedAt!.Value - f.CreatedAt).TotalHours).ToList();

        return new InsData(start, end, sc.Sum(x => x.C), N(BookingStatus.Paid), N(BookingStatus.Pending), N(BookingStatus.Cancelled), N(BookingStatus.Expired),
            buyerIds.Count, repeat, newUsers, paid.Count == 0 ? 0 : Math.Round(paid.Sum(p => p.TotalAmount) / paid.Count),
            byHour, byDow, topCustomers,
            fb.Count, fb.Count(f => f.Status == FeedbackStatus.New), fb.Count(f => f.Status == FeedbackStatus.InProgress), fb.Count(f => f.Status == FeedbackStatus.Resolved),
            replied.Count == 0 ? null : Math.Round(replied.Average(), 1),
            fb.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count()));
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to) => Ok(await Build(from, to));

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? kind, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var d = await Build(from, to);
        byte[] bytes; string name;
        if (kind == "hour")
        {
            name = "ban-hang-theo-gio";
            bytes = Csv.Build(["Giờ", "Số đơn", "Vé bán", "Doanh thu (đ)"], d.ByHour.Select(x => new object?[] { $"{x.Key:00}:00", x.Orders, x.Tickets, x.Revenue }));
        }
        else
        {
            name = "khach-hang-gia-tri-cao";
            bytes = Csv.Build(["Họ tên", "Email", "Số đơn", "Số vé", "Tổng chi (đ)", "Đơn gần nhất"],
                d.TopCustomers.Select(x => new object?[] { x.Name, x.Email, x.Orders, x.Tickets, x.Spent, x.Last.AddHours(Vn).ToString("yyyy-MM-dd HH:mm") }));
        }
        await audit.LogAsync(User, "export.insights", "Report", null, $"{name} · {d.From:yyyy-MM-dd} → {d.To:yyyy-MM-dd}");
        return File(bytes, "text/csv; charset=utf-8", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }
}
