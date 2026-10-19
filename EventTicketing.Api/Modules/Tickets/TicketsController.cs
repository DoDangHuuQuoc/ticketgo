using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Tickets;

[ApiController, Authorize, Route("api/tickets")]
public class TicketsController(AppDbContext db) : ControllerBase
{
    public record TicketRow(Guid Id, string SeatInfo, TicketStatus Status, DateTime? CheckedInAt, Guid BookingId,
        Guid EventId, string EventName, string Venue, DateTime StartsAt, string? PosterUrl, string Zone, string TypeName, decimal Price);

    // Lọc theo vé TRƯỚC khi chiếu sang TicketRow để EF dịch được sang SQL
    private IQueryable<TicketRow> Project(IQueryable<Ticket> tickets) =>
        from t in tickets
        join b in db.Bookings on t.BookingId equals b.Id
        join tt in db.TicketTypes on b.TicketTypeId equals tt.Id
        join e in db.Events on t.EventId equals e.Id
        orderby e.StartsAt, t.SeatInfo
        select new TicketRow(t.Id, t.SeatInfo, t.Status, t.CheckedInAt, t.BookingId,
            e.Id, e.Name, e.Venue, e.StartsAt, e.PosterUrl, tt.Zone, tt.Name, tt.Price);

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var uid = User.UserId();
        return Ok(await Project(db.Tickets.AsNoTracking().Where(t => t.UserId == uid)).ToListAsync());
    }

    // Một vé (chỉ chủ vé) – trang xem vé
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> One(Guid id)
    {
        var uid = User.UserId();
        var row = await Project(db.Tickets.AsNoTracking().Where(t => t.UserId == uid && t.Id == id)).FirstOrDefaultAsync();
        return row is null ? NotFound(new { code = "NOT_FOUND", message = "Không tìm thấy vé" }) : Ok(row);
    }

    // Tất cả vé của một đơn – trang đơn hàng sau khi thanh toán
    [HttpGet("booking/{bookingId:guid}")]
    public async Task<IActionResult> ByBooking(Guid bookingId)
    {
        var uid = User.UserId();
        return Ok(await Project(db.Tickets.AsNoTracking().Where(t => t.UserId == uid && t.BookingId == bookingId)).ToListAsync());
    }

    // Ảnh QR của vé (chỉ chủ vé xem được)
    [HttpGet("{id:guid}/qr")]
    public async Task<IActionResult> Qr(Guid id)
    {
        var uid = User.UserId();
        var code = await db.Tickets.AsNoTracking().Where(t => t.Id == id && t.UserId == uid)
            .Select(t => t.QrCode).FirstOrDefaultAsync();
        if (code is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(TicketService.RenderQrPng(code), "image/png");
    }
}
