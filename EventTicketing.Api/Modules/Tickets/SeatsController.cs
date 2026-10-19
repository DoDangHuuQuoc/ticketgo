using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Tickets;

/// Sơ đồ ghế của một loại vé: khách xem để chọn ghế khi mua, admin xem để chọn ghế cho khách mời.
/// Công khai (sự kiện đang mở bán); admin xem được cả sự kiện chưa đăng.
[ApiController, AllowAnonymous, Route("api/ticket-types")]
public class SeatsController(AppDbContext db, SeatService seats) : ControllerBase
{
    [HttpGet("{id:guid}/seats")]
    public async Task<IActionResult> Map(Guid id)
    {
        var tt = await db.TicketTypes.AsNoTracking().Include(t => t.Event).FirstOrDefaultAsync(t => t.Id == id);
        if (tt is null || (!tt.Event!.IsPublished && !User.IsInRole("Admin")))
            throw new AppException(404, "TICKET_TYPE_NOT_FOUND", "Loại vé không tồn tại");

        if (tt.SeatMode != SeatMode.Choose)
            return Ok(new { id, tt.Name, tt.Zone, mode = (int)tt.SeatMode, rows = tt.SeatRows, perRow = tt.SeatsPerRow, rowLabels = Array.Empty<string>(), taken = Array.Empty<string>() });

        var taken = await seats.TakenAsync(id);
        return Ok(new
        {
            id, tt.Name, tt.Zone, mode = (int)tt.SeatMode, rows = tt.SeatRows, perRow = tt.SeatsPerRow,
            rowLabels = Enumerable.Range(0, tt.SeatRows).Select(SeatLayout.RowLabel).ToArray(),
            taken = taken.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()
        });
    }
}
