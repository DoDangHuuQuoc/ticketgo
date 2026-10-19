using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Mvc.Controllers;

// Các controller trả View (giao diện). Dữ liệu và bảo mật nằm ở API /api/* (JWT + phân quyền theo role).
public class HomeController : Controller
{
    public IActionResult Index() => View();
}

public class EventController : Controller
{
    public IActionResult Detail(Guid id) => View(model: id);
    public IActionResult Book(Guid id) => View(model: id);
    public IActionResult Checkout(Guid id) => View(model: id);   // trang thanh toán của một đơn đã giữ vé
}

public class AccountController : Controller
{
    public IActionResult Login() => View();
    public IActionResult Register() => View();
    public IActionResult Verify() => View();
    public IActionResult Forgot() => View();                      // quên mật khẩu: OTP qua email
    public IActionResult MyBookings() => View();
    public IActionResult Profile() => View();                     // thông tin cá nhân + đổi mật khẩu
    public IActionResult Order(Guid id) => View(model: id);      // đơn hàng + các vé vừa mua
    public IActionResult Ticket(Guid id) => View(model: id);     // xem một vé (QR lớn)
}

public class FavoritesController : Controller
{
    public IActionResult Index() => View();   // nghệ sĩ & chương trình yêu thích + đánh giá sao
}

public class ShopController : Controller
{
    public IActionResult Index() => View();   // cửa hàng lưu niệm (light stick...) + đơn của tôi
    public IActionResult Success(Guid id) => View(model: id);   // đặt hàng thành công (sau khi thanh toán xong)
    public IActionResult Pay(Guid id) => View(model: id);   // thanh toán payOS cho một đơn lưu niệm
    public IActionResult Order(Guid id) => View(model: id);   // chi tiết một đơn lưu niệm (kiểu Shopee): trạng thái, sản phẩm + phân loại, tiền
}

public class PromotionsController : Controller
{
    public IActionResult Index() => View();   // Khuyến mãi + ví "Voucher của tôi" (?tab=mine)
}

public class ManageController : Controller
{
    public IActionResult Profile() => View();    // hồ sơ + đổi mật khẩu của tài khoản quản trị
    public IActionResult Dashboard() => View();
    public IActionResult Events() => View();
    public IActionResult Orders() => View();
    public IActionResult Payments() => View();
    public IActionResult Transactions() => View();
    public IActionResult Reports() => View();
    public IActionResult Coupons() => View();
    public IActionResult Tickets() => View();
    public IActionResult Users() => View();
    public IActionResult Staff() => View();
    public IActionResult Audit() => View();
    public IActionResult Feedback() => View();
    public IActionResult Chatbot() => View();
    public IActionResult Settings() => View();
    public IActionResult Broadcast() => View();
    public IActionResult Insights() => View();
    public IActionResult CheckIn() => RedirectToActionPermanent("CheckIn", "Staff");   // soát vé đã chuyển sang khu Nhân viên (/Staff)
    public IActionResult Favorites() => View();
    public IActionResult Shop() => View();
    public IActionResult LiveChat() => View();   // chat trực tiếp với khách (Admin + Staff)
}

// Khu Nhân viên: chỉ soát vé. Tách hẳn khỏi khu Quản trị (/Manage) về giao diện, đăng nhập và đường dẫn.
public class StaffController : Controller
{
    public IActionResult Index() => RedirectToAction(nameof(CheckIn));
    public IActionResult CheckIn() => View();
    public IActionResult Profile() => View();   // hồ sơ + đổi mật khẩu của nhân viên
}
