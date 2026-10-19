using System.Net;
using System.Security.Cryptography;
using System.Text;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using QRCoder;

namespace EventTicketing.Modules.Tickets;

public class TicketService(AppDbContext db, IEmailService email)
{
    /// Nội dung QR = "Mã vé" in trên vé + dấu chấm + phần ngẫu nhiên khó đoán (256-bit).
    /// Nhờ vậy QR và "Mã vé" luôn khớp nhau: quét QR ra chuỗi bắt đầu bằng đúng mã vé khách đang cầm.
    public static string NewQrCode(Guid ticketId) =>
        ticketId.ToString()[..8].ToUpperInvariant() + "." +
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly SeatService seats = new(db);

    /// Tạo N vé cho booking (chưa SaveChanges — để nằm chung transaction thanh toán; gọi TRONG transaction).
    /// Loại vé không số ghế: #1, #2…. Loại Random: xếp ghế ngẫu nhiên trong sơ đồ. Loại Choose: đúng ghế khách đã chọn
    /// (nếu ghế đó đã bị vé khác chiếm, ví dụ đơn quá hạn mà tiền về trễ, thì đổi sang ghế trống ngẫu nhiên để khách vẫn có vé).
    public async Task<List<Ticket>> CreateTicketsAsync(Booking booking)
    {
        var tt = await db.TicketTypes.FirstAsync(t => t.Id == booking.TicketTypeId);
        List<string> labels;
        if (tt.SeatMode == SeatMode.None)
            labels = Enumerable.Range(1, booking.Quantity).Select(i => $"#{i}").ToList();
        else
        {
            await seats.LockTicketTypeAsync(tt.Id);   // các đơn cùng loại vé phát ghế tuần tự, không trùng
            var chosen = tt.SeatMode == SeatMode.Choose ? SeatLayout.ParseList(booking.Seats) : [];
            if (chosen.Count != booking.Quantity) chosen = [];   // thiếu dữ liệu ghế (đơn cũ…) => xếp ngẫu nhiên
            var taken = await seats.TakenAsync(tt.Id, booking.Id);
            labels = [];
            foreach (var c in chosen) if (!taken.Contains(c) && !labels.Contains(c)) labels.Add(c);
            var need = booking.Quantity - labels.Count;
            if (need > 0)
            {
                var avoid = new HashSet<string>(taken.Concat(labels), StringComparer.OrdinalIgnoreCase);
                labels.AddRange(SeatService.PickRandom(tt, need, avoid));
            }
            labels.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var list = labels.Select(seat =>
        {
            var id = Guid.NewGuid();
            return new Ticket
            {
                Id = id, BookingId = booking.Id, EventId = booking.EventId, UserId = booking.UserId,
                QrCode = NewQrCode(id), SeatInfo = seat, Status = TicketStatus.Valid
            };
        }).ToList();
        db.Tickets.AddRange(list);
        return list;
    }

    public static byte[] RenderQrPng(string content)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    public async Task SendTicketEmailAsync(Guid bookingId)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Include(b => b.User).Include(b => b.TicketType).ThenInclude(t => t!.Event)
            .FirstAsync(b => b.Id == bookingId);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.BookingId == bookingId).OrderBy(t => t.SeatInfo).ToListAsync();
        var ev = booking.TicketType!.Event!;

        var body = new BodyBuilder();
        var cards = new StringBuilder();
        foreach (var t in tickets)
        {
            // Nhúng ảnh QR dạng CID (Gmail/Outlook chặn data: URI)
            var img = body.LinkedResources.Add($"qr-{t.Id}.png", RenderQrPng(t.QrCode), new ContentType("image", "png"));
            img.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
            cards.Append($"""
                <div style="border:2px dashed #cbd5e1;border-radius:12px;padding:16px;margin:16px 0;text-align:center">
                  <div style="font-weight:bold">{WebUtility.HtmlEncode(booking.TicketType!.Zone)} – {WebUtility.HtmlEncode(booking.TicketType.Name)} ({WebUtility.HtmlEncode(t.SeatInfo)})</div>
                  <img src="cid:{img.ContentId}" width="200" height="200" alt="QR" style="margin:12px 0"/>
                  <div style="font:12px monospace;color:#64748b">Mã vé: {t.Id.ToString()[..8].ToUpper()}</div>
                </div>
                """);
        }

        var poster = string.IsNullOrEmpty(ev.PosterUrl) ? "" :
            $"<img src=\"{WebUtility.HtmlEncode(ev.PosterUrl)}\" style=\"width:100%;display:block\" alt=\"Poster\"/>";

        body.HtmlBody = $"""
            <div style="font-family:Arial,sans-serif;max-width:600px;margin:auto;background:#fff;border:1px solid #e2e8f0;border-radius:16px;overflow:hidden">
              {poster}
              <div style="padding:24px">
                <h1 style="margin:0 0 8px">{WebUtility.HtmlEncode(ev.Name)}</h1>
                <p style="margin:4px 0">📍 <b>Địa điểm:</b> {WebUtility.HtmlEncode(ev.Venue)}</p>
                <p style="margin:4px 0">🕒 <b>Thời gian:</b> {ev.StartsAt.ToLocalTime():HH:mm dd/MM/yyyy}</p>
                <p style="margin:4px 0">🎟️ <b>Số vé:</b> {booking.Quantity} — <b>Tổng:</b> {booking.TotalAmount:N0} đ</p>
                <p>Xin chào {WebUtility.HtmlEncode(booking.User!.FullName)}, vé điện tử của bạn:</p>
                {cards}
                <p style="color:#64748b;font-size:12px">Vui lòng xuất trình mã QR tại cổng. Mỗi mã chỉ dùng được một lần.</p>
              </div>
            </div>
            """;

        await email.SendAsync(booking.User!.Email, $"Vé điện tử – {ev.Name}", body);
    }
}
