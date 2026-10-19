using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Admin;
using EventTicketing.Modules.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EventTicketing.Modules.Chatbot;

public record ChatTurn(string Role, string Text);                 // Role: "user" | "bot"
public record ChatLink(string Label, string Url);
public record ChatReply(string Reply, List<string> Suggestions, List<ChatLink> Links, string? Action = null, string? Intent = null);   // Action: "feedback" = widget mở form gửi phản hồi, "live" = widget mở chat trực tiếp với nhân viên

/// Chatbot hỗ trợ khách hàng.
/// - Mặc định chạy hoàn toàn nội bộ: nhận diện ý định bằng từ khoá (bỏ dấu tiếng Việt) và trả lời bằng DỮ LIỆU THẬT trong DB
///   (sự kiện, giá, tồn vé, mã giảm giá, đơn của chính khách đã đăng nhập). Không cần khoá API nào.
/// - Tuỳ chọn: đặt Chatbot:Anthropic:ApiKey để câu hỏi tự do được Claude trả lời dựa trên cùng dữ liệu đó; lỗi mạng/khoá sai
///   thì tự rơi về chế độ nội bộ nên khách không bao giờ thấy lỗi.
public class ChatbotService(AppDbContext db, IConfiguration cfg, IMemoryCache cache, IHttpClientFactory http, ILogger<ChatbotService> log)
{
    private static readonly string[] Quick = ["Sự kiện đang mở bán", "Cách mua vé", "Thanh toán", "Mã giảm giá", "Đồ lưu niệm", "Chat với nhân viên"];
    private static readonly TimeZoneInfo Vn = FindVn();
    private static TimeZoneInfo FindVn()
    {
        foreach (var id in new[] { "SE Asia Standard Time", "Asia/Ho_Chi_Minh" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.CreateCustomTimeZone("VN", TimeSpan.FromHours(7), "VN", "VN");
    }

    public static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }

    private static bool Has(string q, params string[] keys) => keys.Any(k => Regex.IsMatch(q, $@"(^|\W){Regex.Escape(k)}(\W|$)"));
    private static string Vnd(decimal n) => n.ToString("N0", new CultureInfo("vi-VN")) + " đ";
    private static string When(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Vn).ToString("HH:mm 'ngày' dd/MM/yyyy", new CultureInfo("vi-VN"));
    private static ChatReply R(string text, IEnumerable<string>? sug = null, IEnumerable<ChatLink>? links = null)
        => new(text, (sug ?? Quick).ToList(), (links ?? []).ToList());

    public async Task<ChatReply> AnswerAsync(string message, IList<ChatTurn> history, Guid? userId, CancellationToken ct)
    {
        var site = await SiteSettingsStore.GetPublicAsync(db, cache, cfg);
        Hotline = site.Hotline; Email = site.Email;
        var q = Fold(message);

        // 1) FAQ do admin soạn (ưu tiên cao nhất)
        var faq = await FaqAsync(q, ct);
        if (faq is not null) return faq with { Intent = "faq" };

        // 2) Luật nội bộ dùng dữ liệu thật
        var local = await RuleBasedAsync(q, userId, ct);
        if (local is not null) return local with { Intent = "rule" };

        // 3) Claude (nếu cấu hình) cho câu hỏi tự do
        var ai = await TryClaudeAsync(message, history, userId, ct);
        if (ai is not null) return R(ai) with { Intent = "ai" };

        return R("Mình chưa hiểu câu hỏi này. Bạn hỏi về sự kiện, giá vé, thanh toán, mã giảm giá, đồ lưu niệm hoặc đơn hàng nhé, hoặc chat với nhân viên.", ["Chat với nhân viên", "Cách mua vé"]) with { Intent = "unknown" };
    }

    private string Hotline = "1900 6408", Email = "hotro@ticketgo.vn";

    private async Task<ChatReply?> FaqAsync(string q, CancellationToken ct)
    {
        if (q.Length == 0) return null;
        List<ChatFaq> faqs;
        try { faqs = await db.ChatFaqs.AsNoTracking().Where(f => f.IsActive).Take(300).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Không đọc được ChatFaqs"); return null; }

        ChatFaq? best = null; var bestLen = 0;
        foreach (var f in faqs)
            foreach (var k in f.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Fold).Where(k => k.Length > 1))
                if (k.Length > bestLen && Has(q, k)) { best = f; bestLen = k.Length; }
        if (best is null) return null;
        var id = best.Id;
        try { await db.ChatFaqs.Where(f => f.Id == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Hits, f => f.Hits + 1), ct); } catch { /* thống kê không quan trọng */ }
        return R(best.Answer);
    }

    // ───────────────────────── Luật nội bộ ─────────────────────────
    private async Task<ChatReply?> RuleBasedAsync(string q, Guid? userId, CancellationToken ct)
    {
        if (q.Length == 0) return R("Bạn cứ nhập câu hỏi, mình sẽ hỗ trợ ngay.");

        if (Has(q, "phan hoi cua toi", "tra loi cua nhan vien", "yeu cau cua toi", "phan hoi da gui", "ket qua phan hoi"))
            return await MyFeedbackAsync(userId, ct);

        // Muốn nói chuyện với nhân viên => mở chat trực tiếp (đặt trước luật phản hồi/hotline)
        if (Has(q, "chat voi nhan vien", "chat nhan vien", "chat truc tiep", "live chat", "livechat", "gap nhan vien", "noi chuyen voi nhan vien", "nhan vien ho tro", "ho tro truc tiep", "tu van vien", "gap nguoi that", "nguoi that", "gap nguoi", "nhan vien", "gui cho nhan vien"))
            return R(LiveChatPresence.Online ? "Mình kết nối bạn với nhân viên ngay đây. Nhân viên đang trực tuyến." : "Mình mở khung chat với nhân viên. Hiện chưa có ai trực tuyến, bạn cứ nhắn, nhân viên sẽ trả lời sớm nhất có thể.",
                     ["Cách mua vé"]) with { Action = "live" };

        if (Has(q, "phan hoi", "gop y", "khieu nai", "gui yeu cau", "bao loi", "feedback"))
            return R("Bạn điền form bên dưới, nhân viên sẽ trả lời qua email.", ["Chat với nhân viên", "Phản hồi của tôi"]) with { Action = "feedback" };

        if (Has(q, "hotline", "lien he", "tong dai", "so dien thoai", "sdt", "email"))
            return R($"Hotline {Hotline} · Email {Email}. Hãy chuẩn bị mã đơn hàng để được hỗ trợ nhanh.", ["Chat với nhân viên"], links: [new("Vé của tôi", "/Account/MyBookings")]);

        // ── Cửa hàng lưu niệm (đặt TRƯỚC các luật vé để "đơn hàng lưu niệm", "mã giảm giá lưu niệm"... không bị trả lời nhầm theo vé) ──
        var shopWord = Has(q, "luu niem", "do luu niem", "qua luu niem", "cua hang", "shop", "light stick", "lightstick", "gay co vu", "ao thun", "phu kien", "merch", "merchandise");
        if (shopWord)
        {
            if (Has(q, "ma giam gia", "giam gia", "coupon", "voucher", "khuyen mai", "uu dai", "code", "ap ma", "nhap ma")) return await ShopCouponsAsync(ct);
            if (Has(q, "don luu niem", "don shop", "don hang", "don cua toi", "don da dat", "don da mua", "trang thai", "kiem tra don", "lich su", "giao chua", "giao hang chua", "bao gio nhan")) return await MyShopOrdersAsync(userId, ct);
            if (Has(q, "thanh toan", "tra tien", "payos", "cod", "chuyen khoan", "qr", "nhan hang")) return R(ShopPayText, ["Mã giảm giá cho đồ lưu niệm", "Đơn lưu niệm của tôi", "Đồ lưu niệm"], [new("Cửa hàng lưu niệm", "/Shop")]);
            if (Has(q, "huy", "hoan", "doi", "tra hang")) return R("Đơn \"Chờ xác nhận\" bạn tự huỷ được ở Shop → Đơn của tôi. Đơn đã xác nhận hoặc đã trả qua payOS thì chat với nhân viên kèm mã đơn.", ["Chat với nhân viên", "Đơn lưu niệm của tôi"], [new("Đơn của tôi", "/Shop?tab=orders")]);
            return await ShopProductsAsync(q, ct);
        }
        if (Has(q, "don hang", "don cua toi", "ve cua toi", "ve da mua", "da dat", "trang thai don", "kiem tra don", "lich su"))
            return await MyOrdersAsync(userId, ct);

        if (Has(q, "hoan ve", "hoan tien", "tra ve", "huy ve", "doi ve", "refund", "chuyen nhuong", "nhuong ve"))
            return R($"Hoàn/đổi vé do nhân viên xử lý theo từng đơn. Bạn chat với nhân viên hoặc gọi {Hotline} kèm mã đơn. Vé đã check-in không hoàn được.",
                     ["Chat với nhân viên"], links: [new("Vé của tôi", "/Account/MyBookings")]);

        if (Has(q, "ma giam gia", "giam gia", "coupon", "voucher", "khuyen mai", "uu dai", "promo", "code"))
            return await CouponsAsync(ct);

        if (Has(q, "giu cho", "het han", "10 phut", "qua han", "het gio", "giu ve"))
            return R("Vé được giữ 10 phút. Quá hạn chưa thanh toán, đơn tự hết hạn và bạn đặt lại nếu còn vé. Lượt dùng mã giảm giá cũng được hoàn lại.");

        if (Has(q, "thanh toan", "tra tien", "chuyen khoan", "qr", "vietqr", "ngan hang", "momo", "visa", "atm"))
            return R("Quét mã QR ở trang thanh toán bằng app ngân hàng bất kỳ, không sửa số tiền và nội dung. Hệ thống tự xác nhận khi tiền về rồi gửi vé về email. Hoàn tất trong 10 phút giữ chỗ.",
                     ["Vé nhận ở đâu?", "Mã giảm giá"]);

        if (Has(q, "nhan ve", "ve nhan", "ve dien tu", "ma qr", "email ve", "khong nhan duoc", "chua nhan", "in ve", "check in", "checkin", "vao cong", "soat ve"))
            return R("Vé QR được gửi về email sau khi thanh toán và luôn xem được ở \"Vé của tôi\". Đưa mã QR trên điện thoại cho nhân viên quét khi vào cổng, mỗi mã dùng một lần. Chưa thấy email thì kiểm tra thư rác.",
                     ["Chat với nhân viên"], links: [new("Vé của tôi", "/Account/MyBookings")]);

        if (Has(q, "dang ky", "dang nhap", "tai khoan", "otp", "xac thuc", "mat khau", "quen mat khau", "ma xac nhan"))
            return R("Đăng ký bằng email rồi nhập mã OTP gửi về email (bấm \"gửi lại OTP\" nếu hết hạn). Quên mật khẩu hoặc bị khoá thì chat với nhân viên.",
                     ["Chat với nhân viên"], links: [new("Đăng nhập", "/Account/Login"), new("Đăng ký", "/Account/Register")]);

        if (Has(q, "cach mua", "mua ve", "dat ve", "huong dan", "mua nhu the nao", "lam sao mua", "mua sao", "buoc"))
            return R("1) Đăng nhập và chọn sự kiện.\n2) Chọn loại vé, số lượng (có thể áp mã giảm giá).\n3) Quét QR thanh toán trong 10 phút.\n4) Nhận vé QR qua email và ở \"Vé của tôi\".",
                     ["Thanh toán", "Mã giảm giá", "Sự kiện đang mở bán"],
                     [new("Xem sự kiện", "/")]);

        if (Has(q, "xin chao", "chao", "hello", "hi", "alo", "hey") && q.Split(' ').Length <= 4)
            return R("Xin chào! Bạn cần hỗ trợ gì?");

        if (Has(q, "cam on", "thanks", "thank", "ok cam on"))
            return R("Rất vui được hỗ trợ bạn!");

        // Sự kiện: tìm theo tên, hoặc liệt kê nếu hỏi chung chung
        var ev = await EventsAsync(q, ct);
        if (ev is not null) return ev;

        return null;
    }

    private async Task<List<Event>> UpcomingAsync(CancellationToken ct) =>
        await db.Events.AsNoTracking().Include(e => e.TicketTypes)
            .Where(e => e.IsPublished && e.StartsAt > DateTime.UtcNow).OrderBy(e => e.StartsAt).Take(40).ToListAsync(ct);

    private async Task<ChatReply?> EventsAsync(string q, CancellationToken ct)
    {
        var generic = Has(q, "su kien", "show", "concert", "ca nhac", "bong da", "tran", "dang mo ban", "con ve", "ve nao", "gia ve", "gia", "bao nhieu", "sap dien ra", "lich", "dia diem", "o dau", "khi nao", "may gio", "ngay nao", "con khong", "het ve", "ton kho", "so luong");
        var events = await UpcomingAsync(ct);

        // Khớp tên: sự kiện có ít nhất 2 từ (hoặc toàn bộ tên nếu ngắn) xuất hiện trong câu hỏi
        var stop = new HashSet<string> { "the", "va", "cua", "la", "o", "tai", "ve", "su", "kien", "show", "cho", "toi", "2025", "2026", "2027" };
        var named = events.Select(e =>
        {
            var words = Fold(e.Name).Split([' ', '-', ':', ',', '.', '/', '|'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !stop.Contains(w)).Distinct().ToArray();
            var hit = words.Count(w => Regex.IsMatch(q, $@"(^|\W){Regex.Escape(w)}(\W|$)"));
            return (e, hit, need: Math.Min(2, Math.Max(1, words.Length)));
        }).Where(x => x.hit >= x.need && x.hit > 0).OrderByDescending(x => x.hit).ToList();

        if (named.Count > 0)
        {
            var e = named[0].e;
            var sb = new StringBuilder();
            sb.Append($"**{e.Name}**\n📍 {e.Venue}\n🕒 {When(e.StartsAt)}\n");
            if (e.TicketTypes.Count == 0) sb.Append("Hiện chưa mở bán loại vé nào.");
            else
            {
                sb.Append("Các loại vé:\n");
                foreach (var t in e.TicketTypes.OrderByDescending(t => t.Price))
                    sb.Append($"• {t.Name}{(string.IsNullOrWhiteSpace(t.Zone) ? "" : $" ({t.Zone})")} — {Vnd(t.Price)} — {(t.AvailableQuantity == 0 ? "HẾT VÉ" : $"còn {t.AvailableQuantity}/{t.TotalQuantity}")}\n");
            }
            return R(sb.ToString().TrimEnd(), ["Cách mua vé", "Thanh toán thế nào?", "Mã giảm giá"], [new("Xem & đặt vé", $"/Event/Detail/{e.Id}")]);
        }

        if (!generic) return null;
        if (events.Count == 0) return R("Hiện chưa có sự kiện nào đang mở bán.");

        var avail = Has(q, "con ve", "dang mo ban", "con khong");
        var list = (avail ? events.Where(e => e.TicketTypes.Sum(t => t.AvailableQuantity) > 0).ToList() : events).Take(6).ToList();
        if (list.Count == 0) list = events.Take(6).ToList();
        var text = new StringBuilder(avail ? "Sự kiện còn vé:\n" : "Sự kiện sắp diễn ra:\n");
        foreach (var e in list)
        {
            var left = e.TicketTypes.Sum(t => t.AvailableQuantity);
            var min = e.TicketTypes.Count == 0 ? (decimal?)null : e.TicketTypes.Min(t => t.Price);
            text.Append($"• {e.Name} — {When(e.StartsAt)} — {e.Venue} — {(min is null ? "chưa mở bán" : $"từ {Vnd(min.Value)}")} — {(left == 0 ? "hết vé" : $"còn {left} vé")}\n");
        }
        return R(text.ToString().TrimEnd(), list.Take(3).Select(e => e.Name).Concat(["Cách mua vé"]), [new("Tất cả sự kiện", "/")]);
    }

    private async Task<ChatReply> MyOrdersAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null)
            return R("Bạn cần đăng nhập để mình xem đơn hàng của bạn.", links: [new("Đăng nhập", "/Account/Login?returnUrl=/Account/MyBookings")]);

        var rows = await db.Bookings.AsNoTracking().Where(b => b.UserId == userId).OrderByDescending(b => b.CreatedAt).Take(5)
            .Select(b => new { b.Id, b.Quantity, b.TotalAmount, b.Status, b.ExpiresAt, Event = b.TicketType!.Event!.Name, Type = b.TicketType.Name })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            if (await db.ShopOrders.AnyAsync(o => o.UserId == userId, ct)) return await MyShopOrdersAsync(userId, ct);   // chưa có vé nhưng có đơn lưu niệm
            return R("Bạn chưa có đơn hàng nào. Hãy chọn một sự kiện để bắt đầu nhé!", links: [new("Xem sự kiện", "/")]);
        }

        string St(BookingStatus s, DateTime exp) => s switch
        {
            BookingStatus.Paid => "✅ Đã thanh toán",
            BookingStatus.Pending when exp > DateTime.UtcNow => $"⏳ Chờ thanh toán (còn giữ chỗ đến {When(exp)})",
            BookingStatus.Pending => "⌛ Đã hết hạn giữ chỗ",
            BookingStatus.Cancelled => "❌ Đã huỷ/hoàn tiền",
            _ => "⌛ Hết hạn"
        };
        var sb = new StringBuilder("Các đơn gần đây của bạn:\n");
        foreach (var r in rows)
            sb.Append($"• {r.Event} — {r.Type} × {r.Quantity} — {Vnd(r.TotalAmount)} — {St(r.Status, r.ExpiresAt)}\n");
        var links = rows.Where(r => r.Status == BookingStatus.Pending && r.ExpiresAt > DateTime.UtcNow)
            .Select(r => new ChatLink("Thanh toán đơn " + r.Event, $"/Event/Checkout/{r.Id}")).ToList();
        links.Add(new("Tất cả đơn & vé", "/Account/MyBookings"));
        var shopOrders = await db.ShopOrders.AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt).Take(3).ToListAsync(ct);
        if (shopOrders.Count > 0)
        {
            sb.Append("Đơn lưu niệm gần đây:\n");
            foreach (var o in shopOrders) sb.Append($"• #{o.Id.ToString("N")[..8].ToUpperInvariant()} — {Vnd(o.Total)} — {ShopStatus(o)}\n");
            links.Add(new("Đơn lưu niệm", "/Shop?tab=orders"));
        }
        return R(sb.ToString().TrimEnd(), ["Vé nhận ở đâu?", "Hoàn/đổi vé"], links);
    }

    private async Task<ChatReply> MyFeedbackAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null)
            return R("Bạn cần đăng nhập để xem phản hồi của mình.",
                     links: [new("Đăng nhập", "/Account/Login")]);
        var rows = await db.Feedbacks.AsNoTracking().Where(f => f.UserId == userId).OrderByDescending(f => f.CreatedAt).Take(3)
            .Select(f => new { f.Message, f.Status, f.AdminReply, f.CreatedAt }).ToListAsync(ct);
        if (rows.Count == 0) return R("Bạn chưa gửi phản hồi nào.", ["Chat với nhân viên"]);
        var sb = new StringBuilder("Phản hồi gần đây của bạn:\n");
        foreach (var r in rows)
        {
            var m = r.Message.Length > 80 ? r.Message[..80] + "…" : r.Message;
            sb.Append($"• ({When(r.CreatedAt)}) \"{m}\"\n  ");
            sb.Append(r.AdminReply is not null ? $"💬 Nhân viên: {r.AdminReply}\n" : r.Status == FeedbackStatus.InProgress ? "⏳ Đang được xử lý\n" : "⏳ Đã tiếp nhận, đang chờ nhân viên trả lời\n");
        }
        return R(sb.ToString().TrimEnd(), ["Chat với nhân viên"]);
    }

    private const string ShopPayText = "Có 2 cách: thanh toán khi nhận hàng (COD) hoặc online qua payOS (quét QR, tự xác nhận khi tiền về, giữ hàng 15 phút). Nhập mã giảm giá ngay ở giỏ hàng.";

    private static string ShopStatus(ShopOrder o) => o.Status switch
    {
        ShopOrderStatus.Cancelled => "❌ Đã huỷ",
        ShopOrderStatus.Done => "✅ Đã giao",
        ShopOrderStatus.Shipping => "🚚 Đang giao",
        ShopOrderStatus.Confirmed => "📦 Đã xác nhận",
        _ => o.PaymentMethod == "PAYOS" && o.PaymentStatus != "Paid" ? "⏳ Chờ thanh toán payOS" : "⏳ Chờ cửa hàng xác nhận"
    };

    private async Task<ChatReply> ShopProductsAsync(string q, CancellationToken ct)
    {
        var items = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderByDescending(p => p.CreatedAt).Take(60).ToListAsync(ct);
        var links = new[] { new ChatLink("Cửa hàng lưu niệm", "/Shop") };
        if (items.Count == 0) return R("Cửa hàng lưu niệm đang cập nhật sản phẩm, bạn quay lại sau nhé.", links: links);

        var stop = new HashSet<string> { "the", "va", "cua", "la", "o", "tai", "cho", "toi", "mua", "gia", "bao", "nhieu", "con", "khong", "hang", "do", "luu", "niem", "cua", "shop", "mot" };
        var named = items.Select(p =>
        {
            var words = Fold(p.Name).Split([' ', '-', ':', ',', '.', '/', '|'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !stop.Contains(w)).Distinct().ToArray();
            var hit = words.Count(w => Regex.IsMatch(q, $@"(^|\W){Regex.Escape(w)}(\W|$)"));
            return (p, hit, need: Math.Min(2, Math.Max(1, words.Length)));
        }).Where(x => x.hit >= x.need && x.hit > 0).OrderByDescending(x => x.hit).ToList();
        if (named.Count > 0)
        {
            var sb = new StringBuilder("Sản phẩm phù hợp:\n");
            foreach (var (p, _, _) in named.Take(3))
                sb.Append($"• {p.Name} — {Vnd(p.Price)} — {(p.Stock <= 0 ? "HẾT HÀNG" : $"còn {p.Stock}")}{(string.IsNullOrWhiteSpace(p.Description) ? "" : $"\n  {p.Description}")}\n");
            return R(sb.ToString().TrimEnd(), ["Thanh toán đồ lưu niệm", "Mã giảm giá cho đồ lưu niệm", "Đồ lưu niệm"], links);
        }

        var inStock = items.Where(p => p.Stock > 0).Take(8).ToList();
        var text = new StringBuilder("Cửa hàng lưu niệm đang bán:\n");
        foreach (var p in inStock) text.Append($"• {p.Name} — {Vnd(p.Price)} — còn {p.Stock}\n");
        if (inStock.Count == 0) text.Append("Hiện các sản phẩm đều tạm hết hàng.\n");
        text.Append("Thêm vào giỏ ở trang Shop; trả khi nhận hàng hoặc qua payOS.");
        return R(text.ToString(), ["Thanh toán đồ lưu niệm", "Mã giảm giá cho đồ lưu niệm", "Đơn lưu niệm của tôi"], links);
    }

    private async Task<ChatReply> MyShopOrdersAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null)
            return R("Bạn cần đăng nhập để mình xem đơn lưu niệm của bạn.", links: [new("Đăng nhập", "/Account/Login?returnUrl=/Shop?tab=orders")]);
        var orders = await db.ShopOrders.AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt).Take(5).ToListAsync(ct);
        if (orders.Count == 0) return R("Bạn chưa có đơn lưu niệm nào. Ghé cửa hàng chọn vài món nhé!", ["Đồ lưu niệm"], [new("Cửa hàng lưu niệm", "/Shop")]);
        var ids = orders.Select(o => o.Id).ToList();
        var its = await db.ShopOrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).ToListAsync(ct);
        var sb = new StringBuilder("Các đơn lưu niệm gần đây của bạn:\n");
        foreach (var o in orders)
        {
            var names = string.Join(", ", its.Where(i => i.OrderId == o.Id).Take(3).Select(i => $"{i.ProductName}{(string.IsNullOrEmpty(i.VariantName) ? "" : " (" + i.VariantName + ")")} ×{i.Quantity}"));
            sb.Append($"• #{o.Id.ToString("N")[..8].ToUpperInvariant()} — {names} — {Vnd(o.Total)}{(o.DiscountAmount > 0 ? $" (đã giảm {Vnd(o.DiscountAmount)})" : "")} — {ShopStatus(o)}\n");
        }
        var links = orders.Where(o => o.PaymentMethod == "PAYOS" && o.PaymentStatus == "Pending" && o.Status == ShopOrderStatus.New)
            .Select(o => new ChatLink("Thanh toán đơn #" + o.Id.ToString("N")[..8].ToUpperInvariant(), $"/Shop/Pay/{o.Id}")).ToList();
        links.Add(new("Tất cả đơn lưu niệm", "/Shop?tab=orders"));
        return R(sb.ToString().TrimEnd(), ["Thanh toán đồ lưu niệm", "Đồ lưu niệm"], links);
    }

    private async Task<ChatReply> ShopCouponsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cps = await db.Coupons.AsNoTracking()
            .Where(c => c.IsActive && c.AppliesToShop && c.EventId == null && c.IsPublic && (c.StartsAt == null || c.StartsAt <= now) && (c.EndsAt == null || c.EndsAt >= now))
            .OrderBy(c => c.EndsAt ?? DateTime.MaxValue).Take(5).ToListAsync(ct);
        var how = "Nhập mã vào ô \"Mã giảm giá\" trong giỏ hàng rồi bấm Áp dụng.";
        if (cps.Count == 0) return R("Hiện chưa có mã công khai cho đồ lưu niệm. Mã riêng của bạn vẫn nhập được ở giỏ hàng.", ["Đồ lưu niệm", "Mã giảm giá"], [new("Cửa hàng lưu niệm", "/Shop")]);
        var sb = new StringBuilder("Các mã dùng được cho đồ lưu niệm:\n");
        foreach (var c in cps)
        {
            var val = c.Type == CouponType.Percent ? $"giảm {c.Value:0.##}%" + (c.MaxDiscount is > 0 ? $" (tối đa {Vnd(c.MaxDiscount.Value)})" : "") : $"giảm {Vnd(c.Value)}";
            sb.Append($"• {c.Code} — {val}" + (c.MinOrder > 0 ? $", đơn từ {Vnd(c.MinOrder)}" : "") + (c.EndsAt is null ? "" : $", đến {When(c.EndsAt.Value)}") + "\n");
        }
        return R(sb.ToString() + how, ["Đồ lưu niệm", "Thanh toán đồ lưu niệm"], [new("Cửa hàng lưu niệm", "/Shop")]);
    }

    private async Task<ChatReply> CouponsAsync(CancellationToken ct)
    {
        if (!cfg.GetValue("Chatbot:ShowCoupons", true))
            return R("Nhập mã ở bước chọn vé, hoặc ở giỏ hàng cửa hàng lưu niệm nếu mã cho phép.");
        var now = DateTime.UtcNow;
        var cps = await db.Coupons.AsNoTracking()
            .Where(c => c.IsActive && (c.StartsAt == null || c.StartsAt <= now) && (c.EndsAt == null || c.EndsAt >= now))
            .OrderBy(c => c.EndsAt ?? DateTime.MaxValue).Take(5).ToListAsync(ct);
        var how = "Nhập mã ở bước chọn vé (mã \"cả đồ lưu niệm\" cũng nhập được ở giỏ hàng Shop).";
        if (cps.Count == 0) return R("Hiện chưa có mã giảm giá nào. " + how);
        var sb = new StringBuilder("Các mã đang hoạt động:\n");
        foreach (var c in cps)
        {
            var val = c.Type == CouponType.Percent ? $"giảm {c.Value:0.##}%" + (c.MaxDiscount is > 0 ? $" (tối đa {Vnd(c.MaxDiscount.Value)})" : "") : $"giảm {Vnd(c.Value)}";
            sb.Append($"• {c.Code} — {val}" + (c.MinOrder > 0 ? $", đơn từ {Vnd(c.MinOrder)}" : "") + (c.EndsAt is null ? "" : $", đến {When(c.EndsAt.Value)}") + (c.AppliesToShop && c.EventId is null ? " (cả đồ lưu niệm)" : "") + "\n");
        }
        return R(sb.ToString() + how, ["Mã giảm giá cho đồ lưu niệm", "Cách mua vé"]);
    }

    // ───────────────────────── Claude (tuỳ chọn) ─────────────────────────
    private async Task<string?> TryClaudeAsync(string message, IList<ChatTurn> history, Guid? userId, CancellationToken ct)
    {
        var key = cfg["Chatbot:Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var events = await UpcomingAsync(ct);
            var facts = new StringBuilder();
            foreach (var e in events.Take(15))
            {
                facts.Append($"- {e.Name} | {e.Venue} | {When(e.StartsAt)} | ");
                facts.Append(string.Join("; ", e.TicketTypes.Select(t => $"{t.Name} {Vnd(t.Price)} (còn {t.AvailableQuantity})")));
                facts.Append('\n');
            }
            var prods = await db.Products.AsNoTracking().Where(p => p.IsActive && p.Stock > 0).OrderByDescending(p => p.CreatedAt).Take(15).ToListAsync(ct);
            var shopFacts = string.Join("\n", prods.Select(p => $"- {p.Name} | {Vnd(p.Price)} | còn {p.Stock}"));
            var system = $"""
                Bạn là trợ lý hỗ trợ khách hàng của TicketGo, nền tảng bán vé ca nhạc/thể thao tại Việt Nam. Trả lời bằng tiếng Việt, ngắn gọn (tối đa ~120 từ), lịch sự.
                Chỉ dùng thông tin dưới đây; không biết thì nói không chắc và hướng dẫn gọi hotline {Hotline} / email {Email}. Không bịa giá, ngày, chính sách. Không yêu cầu mật khẩu hay OTP.
                Quy trình: đăng nhập → chọn sự kiện/loại vé → giữ chỗ 10 phút → thanh toán quét VietQR → vé QR gửi email và ở "Vé của tôi". Hoàn/đổi vé do quản trị viên xử lý qua hotline/email.
                Cửa hàng lưu niệm (/Shop): giỏ hàng, thanh toán khi nhận hàng (COD) hoặc payOS quét QR (giữ hàng 15 phút), nhập mã giảm giá ngay ở giỏ hàng (chỉ mã được bật cho đơn lưu niệm).
                Khách {(userId is null ? "chưa đăng nhập" : "đã đăng nhập")}.
                Sản phẩm lưu niệm đang bán:
                {shopFacts}
                Sự kiện sắp diễn ra:
                {facts}
                """;
            var msgs = history.TakeLast(6).Where(h => !string.IsNullOrWhiteSpace(h.Text))
                .Select(h => new { role = h.Role == "bot" ? "assistant" : "user", content = h.Text }).ToList();
            // API yêu cầu bắt đầu bằng user và xen kẽ: bỏ các lượt đầu không hợp lệ rồi thêm câu hỏi hiện tại
            while (msgs.Count > 0 && msgs[0].role != "user") msgs.RemoveAt(0);
            var clean = new List<object>(); string? last = null;
            foreach (var m in msgs) { if (m.role == last) continue; clean.Add(m); last = m.role; }
            if (last == "user") clean.RemoveAt(clean.Count - 1);
            clean.Add(new { role = "user", content = message });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = JsonContent.Create(new { model = cfg["Chatbot:Anthropic:Model"] ?? "claude-haiku-4-5", max_tokens = 400, system, messages = clean });
            var client = http.CreateClient("chatbot");
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("Chatbot Claude API trả {Status}", (int)res.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Chatbot Claude lỗi, dùng chế độ nội bộ");
            return null;
        }
    }
}
