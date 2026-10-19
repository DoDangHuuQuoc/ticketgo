using EventTicketing.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Events;

[ApiController, Route("api/events")]
public class EventsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(await db.Events.AsNoTracking()
        .Where(e => e.IsPublished && e.StartsAt > DateTime.UtcNow).OrderBy(e => e.StartsAt)
        .Select(e => new
        {
            e.Id, e.Name, e.PosterUrl, e.Venue, e.StartsAt, e.Category,
            MinPrice = e.TicketTypes.Min(t => (decimal?)t.Price),
            Available = e.TicketTypes.Sum(t => (int?)t.AvailableQuantity) ?? 0,
            Total = e.TicketTypes.Sum(t => (int?)t.TotalQuantity) ?? 0
        }).ToListAsync());

    // Tồn kho gần real-time: đọc trực tiếp AvailableQuantity (client polling)
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var ev = await db.Events.AsNoTracking().Where(e => e.Id == id && e.IsPublished).Select(e => new
        {
            e.Id, e.Name, e.PosterUrl, e.Description, e.Venue, e.StartsAt,
            e.SeatMapUrl,   // link ảnh sơ đồ chỗ ngồi (admin dán link, khách chỉ xem)
            TicketTypes = e.TicketTypes.OrderByDescending(t => t.Price).Select(t => new
            {
                t.Id, t.Name, t.Zone, t.Price, t.AvailableQuantity, t.TotalQuantity, SoldOut = t.AvailableQuantity == 0,
                SeatMode = (int)t.SeatMode, t.SeatRows, t.SeatsPerRow
            })
        }).FirstOrDefaultAsync();
        return ev is null ? NotFound() : Ok(ev);
    }
}
