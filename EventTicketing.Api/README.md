# EventTicketing.Api – ASP.NET Core 8 Web API

## Chạy thử
```bash
docker run -d --name pg -p 5432:5432 -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=ticketing postgres:16
docker run -d --name redis -p 6379:6379 redis:7
dotnet tool install --global dotnet-ef
dotnet ef migrations add Init && dotnet ef database update
dotnet run
```
Swagger: /swagger (Development). Đặt Role = Admin cho user đầu tiên bằng SQL:
`UPDATE "Users" SET "Role"='Admin' WHERE "Email"='...';`

## Luồng test nhanh
1. POST /api/auth/register -> nhận OTP qua email -> POST /api/auth/verify-email -> POST /api/auth/login
2. POST /api/bookings -> Pending 10 phút
3. POST /api/payments/simulate/{bookingId} (dev) lấy payload+signature -> POST /api/payments/webhook với header X-Signature
4. Email vé QR được gửi; nhân viên POST /api/admin/tickets/check-in
5. Thông tin cá nhân: GET/PUT /api/account/profile, POST /api/account/change-password (cần đăng nhập)

## Thanh toán bằng mã QR (VietQR)

- Admin vào **Quản trị › Thanh toán** (`/Manage/Payments`) chọn ngân hàng, nhập số tài khoản và tên chủ tài khoản. Có ô xem thử QR với số tiền bất kỳ.
- Cũng có thể đặt sẵn trong `appsettings.json` → `Payment:Bank` (`BankBin`, `AccountNo`, `AccountName`); cấu hình admin lưu trong DB sẽ được ưu tiên.
- Trang thanh toán (`/Event/Checkout/{id}`) và chi tiết đơn ở Quản trị hiển thị mã QR chuẩn VietQR (NAPAS 247): **đúng số tiền của đơn**, nội dung chuyển khoản `TG` + 8 ký tự đầu của mã đơn.
- API: `GET /api/payments/qr/{bookingId}` (chủ đơn hoặc Admin, chỉ đơn đang `Pending`).
- Tự động xác nhận khi tiền về cần dịch vụ báo biến động số dư (SePay, Casso…) gọi `POST /api/payments/webhook`; hiện nút **Thanh toán** trên trang vẫn là cổng giả lập.

## Tự động nhận tiền khi khách quét QR (webhook ngân hàng)

Trang thanh toán hiện mã VietQR ngay và tự dò trạng thái đơn mỗi 2,5 giây; khi đơn chuyển sang Paid thì tự mở vé có mã QR.
Để đơn tự chuyển sang Paid khi tiền về, nối webhook của SePay (hoặc Casso) tới:

    POST https://<tên-miền>/api/payments/bank-webhook
    Authorization: Apikey <Payment:BankWebhookKey>

Đặt `Payment:BankWebhookKey` trong appsettings (để trống thì endpoint tắt). Hệ thống tìm đơn theo nội dung chuyển khoản `TG` + 8 ký tự đầu của mã đơn,
chấp nhận chuyển bằng hoặc dư số tiền của đơn. Khi chạy thử (AllowSimulate = true) trang thanh toán có nút "Giả lập đã chuyển khoản".


## Triển khai lên host & sửa lỗi database

- Cấu hình nằm ở `appsettings.json` (chuỗi kết nối, SMTP, JWT/OTP secret, Seed…).
- Khi khởi động, ứng dụng tự **đồng bộ cấu trúc database theo code** (`Data/ModelSchemaSync.cs`): tạo bảng/cột/index còn thiếu, nới cột chuỗi quá ngắn, cho phép NULL với cột cũ không còn dùng. Không xoá dữ liệu.
- Mở `/health/db` (khi `Diagnostics:Enabled = true`) để xem: kết nối DB, `schemaChanges` (đã tự sửa gì) và `schemaIssues` (chỗ còn lệch cần xử lý tay). **Tắt Diagnostics sau khi chạy ổn.**
- Nếu `schemaIssues` còn lệch kiểu dữ liệu và không cần giữ dữ liệu: chạy `Database/reset-database.sql` (XOÁ HẾT) rồi khởi động lại để tạo DB mới sạch.

## Tài khoản admin mẫu

Khi `Seed:Enabled = true`, mỗi lần khởi động ứng dụng bảo đảm có **admin@demo.com** (vai trò Admin, đã xác thực). Mật khẩu = `Seed:Password` (mặc định `Demo@12345`).
Nếu tài khoản đã có sẵn mà không nhớ mật khẩu: đặt `Seed:ResetAdminPassword = true` → khởi động lại → đăng nhập bằng `Seed:Password`. **Sau đó tắt cờ này và đổi mật khẩu**, nếu không mỗi lần khởi động mật khẩu sẽ bị đặt lại.


## Thanh toán payOS cho đơn lưu niệm (/Shop)

- Khi đã cấu hình `Payment:PayOS:*`, giỏ hàng có thêm lựa chọn **Thanh toán online qua payOS** (bên cạnh COD). Đặt xong, khách được chuyển thẳng sang trang payOS; trả tiền xong payOS đưa khách về `/Shop/Pay/{id}` và đơn tự chuyển sang "Đã thanh toán".
- Đơn payOS giữ hàng 15 phút; quá hạn chưa trả thì tự huỷ và trả hàng về kho (`ShopPayExpiryWorker`). Admin chỉ xác nhận/giao được đơn payOS đã thanh toán.
- Webhook dùng chung URL `https://<tên miền>/api/payments/payos-webhook` (orderCode của đơn lưu niệm nằm ở dải riêng, không trùng với vé).
- Bảng `ShopOrders` có thêm cột `PaymentMethod`, `PaymentStatus`, `PaidAt`, `PaymentRef` (tự thêm khi khởi động nhờ `ModelSchemaSync`; đơn cũ coi là COD).
- Đơn đã trả payOS mà bị huỷ: hệ thống không tự hoàn tiền, cần hoàn thủ công trên payOS.

## Mã giảm giá cho đơn hàng lưu niệm

- Admin: ở form tạo/sửa mã (Quản trị › Mã giảm giá) tick **Áp dụng cả cho đơn hàng lưu niệm** (chỉ với mã áp dụng "Mọi sự kiện"). Mã cũ mặc định chưa áp dụng cho Shop.
- Khách: giỏ hàng ở `/Shop` có ô **Mã giảm giá** (nhập mã hoặc chọn mã đã lưu trong ví), hiện Tạm tính / Mã / Tổng cộng. Server tính lại mức giảm khi đặt hàng; số tiền payOS thu = tổng sau giảm.
- Lượt dùng mã tính chung cho vé và đơn lưu niệm (đơn lưu niệm đã huỷ trả lại lượt). Đơn lưu niệm luôn phải trả tối thiểu 2.000 đ.
- Bảng `ShopOrders` thêm `CouponId`, `CouponCode`, `DiscountAmount`; bảng `Coupons` thêm `AppliesToShop` (tự thêm khi khởi động).
- Chatbot trả lời được về đồ lưu niệm: sản phẩm/giá/tồn, mã giảm giá cho Shop, cách thanh toán, trạng thái đơn lưu niệm của khách.

## Chat trực tiếp khách ↔ nhân viên

- Khung chatbot có nút **Chat với nhân viên** (hoặc khách gõ "gặp nhân viên"). Khách đã đăng nhập vào chat ngay; khách vãng lai nhập tên (nhận một khoá bí mật lưu ở trình duyệt để quay lại đúng cuộc chat).
- Nhân viên (Admin + Staff) trả lời ở **Quản trị › Chat trực tiếp** (`/Manage/LiveChat`): danh sách cuộc chat, số tin chưa đọc, tiếng báo khi có tin mới, đóng cuộc chat. Mở trang này thì khách thấy "Đang trực tuyến" (không có ai mở trang trong 45 giây thì hiện "Chưa có nhân viên trực tuyến", khách vẫn nhắn được và nhân viên trả lời sau).
- Cập nhật bằng cách hỏi lại server mỗi 3 giây (không cần WebSocket nên chạy được trên host miễn phí). Khách có tài khoản mà đang đóng khung chat sẽ nhận thông báo ở chuông khi nhân viên trả lời.
- Bảng mới `LiveChats`, `LiveChatMessages` tự tạo khi khởi động. Form "Phản hồi qua email" vẫn dùng cho khiếu nại cần xử lý lâu.
