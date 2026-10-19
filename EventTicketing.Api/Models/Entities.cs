namespace EventTicketing.Models;

public enum BookingStatus { Pending, Paid, Cancelled, Expired }
public enum PaymentStatus { Paid, Failed, Refunded }
public enum TicketStatus { Valid, CheckedIn, Cancelled }
/// Cách đánh số ghế của một loại vé: None = vé không số ghế (#1, #2…); Random = có hàng×ghế, hệ thống tự xếp ngẫu nhiên khi phát vé; Choose = khách tự chọn ghế khi mua (admin chọn ghế khi phát vé mời).
public enum SeatMode { None = 0, Random = 1, Choose = 2 }
public enum CouponType { Percent, Amount }
public enum FeedbackStatus { New, InProgress, Resolved }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "User"; // User | Admin | Staff
    public bool IsVerified { get; set; }
    public bool IsLocked { get; set; }          // admin khoá tài khoản
    public bool VipNotified { get; set; }       // đã gửi email/thông báo chúc mừng lên VIP (tránh gửi lại mỗi lần)
    public bool IsVip { get; set; }             // khách VIP do admin gán tay (ngoài ra có thể tự lên VIP theo mức chi tiêu, xem PromoSetting.VipAutoSpend)
    // Thông tin cá nhân (khách tự cập nhật ở /Account/Profile). Tất cả đều tuỳ chọn.
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }  // chỉ lấy phần ngày
    public string? Gender { get; set; }         // Male | Female | Other
    public string? Address { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string? Description { get; set; }
    public string? SeatMapUrl { get; set; }         // link ảnh sơ đồ chỗ ngồi (https://... hoặc /img/...), chỉ để khách xem
    public bool IsPublished { get; set; } = true;   // false = bản nháp, khách không thấy
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Venue { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public string Category { get; set; } = "Concert";   // Concert (ca nhạc) | Sport (thể thao) | Theatre (sân khấu)
    public List<TicketType> TicketTypes { get; set; } = new();
}

public class TicketType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Event? Event { get; set; }
    public string Name { get; set; } = "";
    public string Zone { get; set; } = "";
    public decimal Price { get; set; }
    public int TotalQuantity { get; set; }
    public int AvailableQuantity { get; set; }
    public SeatMode SeatMode { get; set; } = SeatMode.None;
    public int SeatRows { get; set; }               // số hàng (A, B, … Z, AA …) khi SeatMode != None
    public int SeatsPerRow { get; set; }            // số ghế mỗi hàng; nhãn ghế = chữ hàng + số, vd A1, A2, B1
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid EventId { get; set; }
    public Guid TicketTypeId { get; set; }
    public TicketType? TicketType { get; set; }
    public int Quantity { get; set; }
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CouponId { get; set; }             // mã giảm giá đã áp dụng (nếu có)
    public string? CouponCode { get; set; }         // ghi lại mã tại thời điểm đặt, để hiển thị dù admin đổi mã sau này
    public decimal DiscountAmount { get; set; }     // số tiền đã giảm bằng mã giảm giá; TotalAmount là số tiền cuối cùng phải trả
    public decimal VipDiscount { get; set; }        // ưu đãi khách VIP (chụp lại lúc đặt đơn); giá gốc = TotalAmount + DiscountAmount + VipDiscount
    public string? Seats { get; set; }              // ghế khách đã chọn (loại vé SeatMode.Choose), cách nhau bởi dấu phẩy, vd "A5,A6"
}

public class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public string QrCode { get; set; } = "";   // chuỗi định danh độc nhất
    public string SeatInfo { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Valid;
    public DateTime? CheckedInAt { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string TransactionRef { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Tài khoản ngân hàng nhận tiền (1 dòng duy nhất, Id = 1) – admin chỉnh ở /Manage/Payments
public class PaymentSetting
{
    public int Id { get; set; } = 1;
    public string BankBin { get; set; } = "";
    public string BankName { get; set; } = "";
    public string AccountNo { get; set; } = "";
    public string AccountName { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Nhật ký thao tác quản trị (ai làm gì, lúc nào)
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; }
    public string ActorEmail { get; set; } = "";
    public string Action { get; set; } = "";       // vd: event.create, booking.refund
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Cài đặt toàn site (1 dòng duy nhất, Id = 1) – hiện có chế độ bảo trì, admin bật/tắt ở /Manage/Dashboard
public class SiteSetting
{
    public int Id { get; set; } = 1;
    public bool MaintenanceEnabled { get; set; }
    public string MaintenanceMessage { get; set; } = "";
    // Cài đặt chung (trang /Manage/Settings). Chuỗi rỗng = dùng giá trị mặc định
    public string Hotline { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public bool BannerEnabled { get; set; }
    public string BannerText { get; set; } = "";
    public string BannerLink { get; set; } = "";
    public string BannerTone { get; set; } = "info";   // info | warn | promo
    public bool ChatbotEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// Mã giảm giá do admin tạo ở /Manage/Coupons. Lượt dùng không lưu bộ đếm mà tính từ các đơn Paid + Pending còn hạn,
/// nên đơn hết hạn/huỷ tự trả lại lượt dùng.
public class Coupon
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";          // luôn viết hoa, duy nhất
    public string? Description { get; set; }
    public CouponType Type { get; set; } = CouponType.Percent;
    public decimal Value { get; set; }              // Percent: 1–100; Amount: số tiền đ
    public decimal? MaxDiscount { get; set; }       // trần giảm khi theo %
    public decimal MinOrder { get; set; }           // giá trị đơn tối thiểu (trước giảm)
    public int? MaxUses { get; set; }               // null = không giới hạn
    public int PerUserLimit { get; set; } = 1;      // số lần mỗi khách được dùng
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AppliesToShop { get; set; }         // true = dùng được cho đơn hàng lưu niệm (/Shop); mã gắn với một sự kiện (EventId) thì không áp dụng cho Shop
    public bool IsPublic { get; set; } = true;      // true = hiện ở mục "Mã giảm giá đang có" trên trang chủ; false = mã riêng, chỉ ai biết mã mới dùng
    public Guid? EventId { get; set; }              // null = áp dụng mọi sự kiện
    public Guid? OwnerUserId { get; set; }          // có giá trị = mã cá nhân, chỉ chủ mã này dùng được (vd. mã chào mừng)
    public string? Source { get; set; }             // null = admin tạo tay; "Welcome" = mã chào mừng tự cấp khi tạo tài khoản thành công
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Cấu hình ưu đãi thành viên (1 dòng duy nhất, Id = 1) – admin chỉnh ở /Manage/Coupons.
/// Chưa có dòng nào thì dùng giá trị mặc định ghi ở đây.
public class PromoSetting
{
    public int Id { get; set; } = 1;
    // Ưu đãi khách VIP: giảm % trên giá vé mỗi đơn
    public bool VipEnabled { get; set; } = true;
    public int VipDiscountPercent { get; set; } = 5;       // 1–50
    public decimal? VipMaxDiscount { get; set; }           // trần giảm mỗi đơn (null = không giới hạn)
    public decimal VipAutoSpend { get; set; }              // tổng chi tiêu (đơn đã thanh toán) để tự lên VIP; 0 = tắt, chỉ admin gán tay
    // Mã chào mừng tự cấp khi tạo tài khoản thành công (xác thực email xong)
    public bool WelcomeEnabled { get; set; } = true;
    public CouponType WelcomeType { get; set; } = CouponType.Percent;
    public decimal WelcomeValue { get; set; } = 10;
    public decimal? WelcomeMaxDiscount { get; set; } = 50_000;
    public decimal WelcomeMinOrder { get; set; }
    public int WelcomeValidDays { get; set; } = 30;
    public bool WelcomeAppliesToShop { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// Voucher khách đã lưu vào ví (bấm "Lưu" ở trang Khuyến mãi). Mỗi khách chỉ lưu một mã một lần.
public class UserCoupon
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid CouponId { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

/// Cuộc chat trực tiếp giữa khách và nhân viên (mở từ khung chatbot). Khách vãng lai nhận một GuestKey bí mật để quay lại đúng cuộc chat.
public class LiveChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? GuestKey { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Open";     // Open | Closed
    public string? StaffName { get; set; }           // nhân viên trả lời đầu tiên
    public string LastText { get; set; } = "";       // xem trước tin nhắn cuối ở danh sách
    public int StaffUnread { get; set; }             // số tin của khách mà nhân viên chưa đọc
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
}

public class LiveChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatId { get; set; }
    public bool FromStaff { get; set; }
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Phản hồi / yêu cầu hỗ trợ của khách (gửi từ chatbot hoặc form). Admin xem và trả lời ở /Manage/Feedback.
public class Feedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }               // null = khách chưa đăng nhập
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Category { get; set; } = "Other"; // Order | Payment | Ticket | Refund | Event | Other
    public string Message { get; set; } = "";
    public string Source { get; set; } = "chatbot";
    public FeedbackStatus Status { get; set; } = FeedbackStatus.New;
    public string? AdminReply { get; set; }
    public string? RepliedBy { get; set; }
    public DateTime? RepliedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Câu hỏi – đáp do admin soạn cho chatbot; ưu tiên hơn các câu trả lời có sẵn. Khớp theo từ khoá (không phân biệt dấu/hoa thường).
public class ChatFaq
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Question { get; set; } = "";
    public string Keywords { get; set; } = "";      // phân tách bằng dấu phẩy
    public string Answer { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int Hits { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// Nhật ký câu hỏi gửi chatbot (để thống kê và tìm câu bot chưa trả lời được). Intent: faq | rule | ai | unknown
public class ChatLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Message { get; set; } = "";
    public string Intent { get; set; } = "unknown";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Đợt gửi email hàng loạt cho khách (/Manage/Broadcast)
public class Broadcast
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public string Audience { get; set; } = "";      // buyers | nobuy | all | event
    public Guid? EventId { get; set; }
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "Sending"; // Sending | Done
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

/// Nghệ sĩ / chương trình yêu thích do admin thêm ở /Manage/Favorites. Khách xem ở /Favorites và đánh giá 1–5 sao.
public class FeaturedItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "Artist";    // Artist (nghệ sĩ) | Program (chương trình)
    public string Name { get; set; } = "";
    public string? ImageUrl { get; set; }           // ảnh đại diện (avatar)
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;      // false = ẩn khỏi khách
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Đánh giá sao của khách cho một nghệ sĩ/chương trình. Mỗi khách chỉ có 1 đánh giá cho mỗi mục (đánh giá lại = sửa số sao).
public class ItemRating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public Guid UserId { get; set; }
    public int Stars { get; set; }                  // 1–5
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// Thông báo gửi tới từng khách (hiện ở chuông cạnh tên).
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}


public enum ShopOrderStatus { New, Confirmed, Shipping, Done, Cancelled }

/// Sản phẩm lưu niệm (light stick, áo, phụ kiện...) bán ở /Shop. Admin quản lý ở /Manage/Shop.
public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "LightStick";   // LightStick | Apparel | Accessory | Other
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// Phân loại hàng của một sản phẩm (kiểu Shopee), vd. "Đỏ / M". Có phân loại thì giá + tồn kho tính theo từng phân loại;
/// Product.Price = giá thấp nhất và Product.Stock = tổng tồn của các phân loại đang bán (để danh sách sản phẩm vẫn lọc/sắp xếp được).
public class ProductVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// Đơn mua đồ lưu niệm (thanh toán khi nhận hàng hoặc qua payOS). Đặt hàng trừ kho ngay; huỷ đơn trả lại kho.
public class ShopOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ReceiverName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Note { get; set; }
    public decimal Total { get; set; }
    public ShopOrderStatus Status { get; set; } = ShopOrderStatus.New;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// COD (thanh toán khi nhận hàng) | PAYOS (trả trước qua payOS). Đơn cũ để trống = COD.
    public string? PaymentMethod { get; set; } = "COD";
    /// Unpaid (COD chưa nhận tiền) | Pending (chờ khách trả payOS) | Paid
    public string? PaymentStatus { get; set; } = "Unpaid";
    public DateTime? PaidAt { get; set; }
    public string? PaymentRef { get; set; }
    /// Mã giảm giá đã áp (nếu có). Total = tổng tiền hàng - DiscountAmount.
    public Guid? CouponId { get; set; }
    public string? CouponCode { get; set; }
    public decimal DiscountAmount { get; set; }
    /// Ưu đãi khách VIP (chụp lại lúc đặt). Tổng tiền hàng = Total + DiscountAmount + VipDiscount.
    public decimal VipDiscount { get; set; }
}

public class ShopOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";   // chụp lại tên + giá lúc mua
    public Guid? VariantId { get; set; }            // phân loại đã chọn (nếu sản phẩm có phân loại)
    public string? VariantName { get; set; }        // chụp lại tên phân loại lúc mua
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}


/// Đánh giá / bình luận của khách về một món trong đơn đã hoàn tất (kiểu Shopee). Mỗi (đơn, sản phẩm, phân loại) chỉ có một đánh giá, khách được sửa lại.
public class ShopReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string? VariantName { get; set; }
    public Guid UserId { get; set; }
    public int Stars { get; set; }                 // 1..5
    public string? Content { get; set; }           // tối đa 1000 ký tự
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsHidden { get; set; }             // admin ẩn (vi phạm) -> không hiện công khai, không tính vào điểm trung bình
    public string? AdminReply { get; set; }        // phản hồi của cửa hàng
    public DateTime? AdminReplyAt { get; set; }
}

/// Báo cáo bình luận vi phạm do khách gửi. Mỗi khách chỉ báo cáo một lần cho một bình luận.
public class ShopReviewReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewId { get; set; }
    public Guid ReporterId { get; set; }
    public string Reason { get; set; } = "Other";  // Spam | Offensive | Fake | Personal | Other
    public string? Detail { get; set; }            // tối đa 300 ký tự
    public string Status { get; set; } = "Open";   // Open | Resolved (đã xử lý, bình luận bị ẩn) | Dismissed (bỏ qua)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? HandledAt { get; set; }
    public string? HandledBy { get; set; }
}
