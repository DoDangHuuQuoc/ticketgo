using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Tickets;

/// Sơ đồ ghế dạng lưới hàng × ghế. Nhãn ghế = chữ hàng + số ghế: A1, A2, … B1 …; sau hàng Z là AA, AB…
public static class SeatLayout
{
    public const int MaxRows = 702;                // A..Z, AA..ZZ
    public const int MaxPerRow = 1000;
    public const int MaxCapacity = 100_000;        // trần cho mọi loại vé có số ghế
    public const int MaxChooseCapacity = 3_000;    // loại vé khách tự chọn ghế: sơ đồ vẽ trên trình duyệt nên giữ nhỏ

    private static readonly Regex LabelRx = new(@"^([A-Z]{1,2})(\d{1,4})$", RegexOptions.Compiled);

    public static string RowLabel(int rowIndex)   // 0 -> A, 25 -> Z, 26 -> AA
    {
        var s = ""; var n = rowIndex + 1;
        while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
        return s;
    }

    public static string Label(int rowIndex, int seat) => RowLabel(rowIndex) + seat;

    /// Chuẩn hoá nhãn khách/admin gửi lên (a5 -> A5) và kiểm tra nằm trong lưới. false nếu sai định dạng hoặc ngoài sơ đồ.
    public static bool TryNormalize(string? raw, int rows, int perRow, out string label)
    {
        label = "";
        var m = LabelRx.Match((raw ?? "").Trim().ToUpperInvariant());
        if (!m.Success) return false;
        var idx = 0; foreach (var c in m.Groups[1].Value) idx = idx * 26 + (c - 'A' + 1);
        var row = idx - 1; var seat = int.Parse(m.Groups[2].Value);
        if (row < 0 || row >= rows || seat < 1 || seat > perRow) return false;
        label = Label(row, seat);
        return true;
    }

    public static List<string> ParseList(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// Kiểm tra cấu hình ghế của một loại vé (dùng khi admin tạo/sửa). Ném AppException 400 nếu sai.
    public static void Validate(SeatMode mode, int rows, int perRow, int totalQuantity)
    {
        if (mode == SeatMode.None) return;
        if (rows < 1 || rows > MaxRows) throw new AppException(400, "BAD_SEAT_ROWS", $"Số hàng ghế từ 1 đến {MaxRows}");
        if (perRow < 1 || perRow > MaxPerRow) throw new AppException(400, "BAD_SEAT_PER_ROW", $"Số ghế mỗi hàng từ 1 đến {MaxPerRow}");
        var cap = rows * perRow;
        if (cap > MaxCapacity) throw new AppException(400, "BAD_SEAT_CAP", $"Sơ đồ tối đa {MaxCapacity:N0} ghế");
        if (mode == SeatMode.Choose && cap > MaxChooseCapacity) throw new AppException(400, "BAD_SEAT_CAP", $"Loại vé cho khách tự chọn ghế tối đa {MaxChooseCapacity:N0} ghế (sơ đồ hiển thị trên trình duyệt)");
        if (totalQuantity > cap) throw new AppException(400, "QTY_OVER_SEATS", $"Số vé ({totalQuantity}) vượt số ghế của sơ đồ ({cap} = {rows} hàng × {perRow} ghế)");
    }
}

/// Ghế đã có chủ, chọn ghế khi mua, xếp ghế ngẫu nhiên khi phát vé.
/// Mọi thao tác ghi PHẢI chạy trong transaction đã khoá dòng loại vé (LockTicketTypeAsync) để các đơn cùng loại vé xếp hàng tuần tự.
public class SeatService(AppDbContext db)
{
    public Task LockTicketTypeAsync(Guid ticketTypeId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM TicketTypes WITH (UPDLOCK, ROWLOCK) WHERE Id = {ticketTypeId}");

    /// Ghế đã phát cho vé còn hiệu lực (Valid / CheckedIn) của loại vé này.
    public async Task<HashSet<string>> IssuedAsync(Guid ticketTypeId)
    {
        var seats = await (from t in db.Tickets.AsNoTracking()
                           join b in db.Bookings.AsNoTracking() on t.BookingId equals b.Id
                           where b.TicketTypeId == ticketTypeId && t.Status != TicketStatus.Cancelled
                           select t.SeatInfo).ToListAsync();
        return new HashSet<string>(seats, StringComparer.OrdinalIgnoreCase);
    }

    /// Ghế không còn trống: vé đã phát + ghế đang giữ của các đơn Pending còn hạn (trừ đơn excludeBookingId).
    public async Task<HashSet<string>> TakenAsync(Guid ticketTypeId, Guid? excludeBookingId = null)
    {
        var set = await IssuedAsync(ticketTypeId);
        var now = DateTime.UtcNow;
        var held = await db.Bookings.AsNoTracking()
            .Where(b => b.TicketTypeId == ticketTypeId && b.Status == BookingStatus.Pending && b.ExpiresAt > now && b.Seats != null && b.Id != excludeBookingId)
            .Select(b => b.Seats).ToListAsync();
        foreach (var h in held) foreach (var s in SeatLayout.ParseList(h)) set.Add(s);
        return set;
    }

    /// Loại vé khách tự chọn ghế: kiểm tra danh sách ghế (đủ số lượng, đúng sơ đồ, không trùng, còn trống) và trả về nhãn đã chuẩn hoá.
    public async Task<List<string>> NormalizeChosenAsync(TicketType tt, IEnumerable<string>? seats, int quantity, Guid? excludeBookingId = null)
    {
        var raw = (seats ?? []).ToList();
        if (raw.Count != quantity) throw new AppException(400, "SEAT_COUNT", $"Hãy chọn đúng {quantity} ghế (đã chọn {raw.Count})");
        var list = new List<string>();
        foreach (var r in raw)
        {
            if (!SeatLayout.TryNormalize(r, tt.SeatRows, tt.SeatsPerRow, out var label))
                throw new AppException(400, "BAD_SEAT", $"Ghế “{r}” không có trong sơ đồ");
            if (list.Contains(label)) throw new AppException(400, "DUP_SEAT", $"Ghế {label} bị chọn hai lần");
            list.Add(label);
        }
        var taken = await TakenAsync(tt.Id, excludeBookingId);
        var clash = list.Where(taken.Contains).ToList();
        if (clash.Count > 0) throw new AppException(409, "SEAT_TAKEN", $"Ghế {string.Join(", ", clash)} vừa có người khác chọn. Hãy chọn ghế khác.");
        return list;
    }

    /// Chọn ngẫu nhiên `count` ghế còn trống (không nằm trong `taken`). Hết ghế -> 409.
    public static List<string> PickRandom(TicketType tt, int count, HashSet<string> taken)
    {
        var cap = tt.SeatRows * tt.SeatsPerRow;
        var picked = new List<string>(count);
        if (cap - taken.Count < count) throw new AppException(409, "SOLD_OUT", "Không còn đủ ghế trống trong sơ đồ");

        if (taken.Count * 2 < cap)
        {
            // Còn nhiều ghế trống: bốc ngẫu nhiên rồi thử lại nếu trùng (kỳ vọng < 2 lần thử mỗi ghế)
            var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            while (picked.Count < count)
            {
                var n = RandomNumberGenerator.GetInt32(cap);
                var label = SeatLayout.Label(n / tt.SeatsPerRow, n % tt.SeatsPerRow + 1);
                if (used.Add(label)) picked.Add(label);
            }
            return picked;
        }
        // Gần đầy: liệt kê ghế trống rồi bốc (Fisher–Yates từng phần)
        var free = new List<string>();
        for (var r = 0; r < tt.SeatRows; r++)
            for (var s = 1; s <= tt.SeatsPerRow; s++) { var l = SeatLayout.Label(r, s); if (!taken.Contains(l)) free.Add(l); }
        for (var i = 0; i < count; i++)
        {
            var j = i + RandomNumberGenerator.GetInt32(free.Count - i);
            (free[i], free[j]) = (free[j], free[i]);
            picked.Add(free[i]);
        }
        return picked;
    }
}
