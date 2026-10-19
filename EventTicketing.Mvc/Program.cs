using EventTicketing.Mvc.Services;

// ==========================================================
// EventTicketing.Mvc – giao diện (ASP.NET Core MVC).
// Chỉ trả HTML/CSS/JS. KHÔNG truy cập CSDL: mọi dữ liệu và nghiệp vụ do project EventTicketing.Api (Web API) xử lý;
// JS trong trình duyệt gọi API ở địa chỉ Api:BaseUrl (kèm JWT).
// ==========================================================
var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();

// Client gọi API từ phía server (layout lấy thông tin liên hệ/banner, trang bảo trì hỏi trạng thái).
var apiBase = (cfg["Api:BaseUrl"] ?? "").Trim().TrimEnd('/');
builder.Services.AddHttpClient<SiteApiClient>(c =>
{
    if (apiBase.Length > 0) c.BaseAddress = new Uri(apiBase + "/");
    c.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<MaintenanceGate>();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();
