using System.Text;
using System.Security.Claims;
using System.Text.Json.Serialization;
using EventTicketing.Common;
using EventTicketing.Data;
using EventTicketing.Models;
using EventTicketing.Modules.Admin;
using EventTicketing.Modules.Auth;
using EventTicketing.Modules.Bookings;
using EventTicketing.Modules.Chatbot;
using EventTicketing.Modules.Coupons;
using EventTicketing.Modules.Shop;
using EventTicketing.Modules.Tickets;
using EventTicketing.Modules.WaitingRoom;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cfg.GetConnectionString("Default")));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var o = ConfigurationOptions.Parse(cfg.GetConnectionString("Redis")!);
    o.AbortOnConnectFail = false;   // app vẫn khởi động khi Redis chưa bật (OTP vẫn chạy nhờ bộ nhớ ứng dụng khi Redis chưa bật)
    return ConnectionMultiplexer.Connect(o);
});

builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<WaitingRoomService>();   // hàng chờ mua vé khi quá đông
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<CouponService>();
builder.Services.AddScoped<EventTicketing.Modules.Promo.VipService>();
builder.Services.AddScoped<EventTicketing.Modules.Promo.WelcomeCouponService>();
builder.Services.AddScoped<EventTicketing.Modules.Promo.VipNotifier>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<EventTicketing.Modules.Tickets.SeatService>();   // SeatsController inject trực tiếp
builder.Services.AddScoped<TicketService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<ChatbotService>();
builder.Services.AddHttpClient("chatbot", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<ExpiredBookingWorker>();
builder.Services.AddSingleton<PayosService>();
builder.Services.AddSingleton<ShopMailer>();
builder.Services.AddScoped<ShopPayments>();
builder.Services.AddHostedService<ShopPayExpiryWorker>();

builder.Services.AddControllers(o => o.Filters.Add<AppExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// CORS: giao diện MVC (project EventTicketing.Mvc) chạy ở địa chỉ khác và gọi API này từ trình duyệt bằng JS.
// Khai báo địa chỉ của site MVC ở Cors:Origins trong appsettings.json.
var corsOrigins = (cfg.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>()).Select(o => o.TrimEnd('/')).ToArray();
builder.Services.AddCors(o => o.AddPolicy("web", p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().SetPreflightMaxAge(TimeSpan.FromHours(1)).WithExposedHeaders("Content-Disposition")));

// Nén Brotli/Gzip cho JSON: danh sách sự kiện, sơ đồ... nhỏ đi nhiều lần.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Nút "Authorize" trên Swagger: dán accessToken (lấy từ POST /api/auth/login) để gọi được các API cần đăng nhập / Admin.
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Dán accessToken lấy từ POST /api/auth/login (không cần gõ chữ Bearer)"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = Array.Empty<string>()
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = cfg["Jwt:Issuer"],
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    // Khoá tài khoản / đổi vai trò có hiệu lực gần như ngay (tối đa ~20s), không phải chờ JWT hết hạn.
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            if (!Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)) { ctx.Fail("Token không hợp lệ"); return; }
            var sp = ctx.HttpContext.RequestServices;
            UserState? st;
            try
            {
                st = await sp.GetRequiredService<IMemoryCache>().GetOrCreateAsync($"ustate:{uid}", async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20);
                    return await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking().Where(u => u.Id == uid)
                        .Select(u => new UserState(u.Role, u.IsLocked)).FirstOrDefaultAsync();
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lỗi DB (mất kết nối, thiếu cột...) KHÔNG phải lỗi đăng nhập: trả 503 thay vì 401 để khách không bị đăng xuất oan.
                sp.GetRequiredService<ILogger<Program>>().LogError(ex, "Không đọc được trạng thái tài khoản từ DB khi xác thực token");
                ctx.Fail(new AuthDbUnavailableException(ex));
                return;
            }
            if (st is null) { ctx.Fail(new AuthStateException("USER_NOT_FOUND", "Tài khoản trong token không tồn tại trong database hiện tại (DB đã đổi hoặc bị reset). Hãy đăng nhập lại.")); return; }
            if (st.IsLocked) { ctx.Fail(new AuthStateException("ACCOUNT_LOCKED", "Tài khoản đã bị khoá.")); return; }
            if (st.Role != ctx.Principal!.FindFirstValue(ClaimTypes.Role)) ctx.Fail(new AuthStateException("ROLE_CHANGED", "Quyền của tài khoản đã thay đổi. Hãy đăng nhập lại."));
        },
        OnChallenge = async ctx =>
        {
            // Trả JSON nói rõ VÌ SAO bị từ chối (trước đây thân phản hồi 401 để trống nên không biết nguyên nhân).
            ctx.HandleResponse();
            ctx.Response.ContentType = "application/json; charset=utf-8";
            if (ctx.AuthenticateFailure is AuthDbUnavailableException)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { code = "DB_UNAVAILABLE", message = "Máy chủ chưa kết nối được cơ sở dữ liệu. Vui lòng thử lại sau ít phút." });
                return;
            }
            var (code, msg) = ctx.AuthenticateFailure switch
            {
                null => ("NO_TOKEN", "Chưa gửi token. Hãy đăng nhập rồi gửi header Authorization: Bearer <accessToken> (trên Swagger: nút Authorize)."),
                AuthStateException a => (a.Code, a.Message),
                SecurityTokenExpiredException => ("TOKEN_EXPIRED", "Token đã hết hạn. Hãy đăng nhập lại."),
                SecurityTokenInvalidSignatureException => ("BAD_TOKEN_SIGNATURE", "Chữ ký token không khớp với Jwt:Key của API này (token được cấp bởi API/cấu hình khác). Hãy đăng nhập lại."),
                SecurityTokenInvalidIssuerException => ("BAD_ISSUER", "Token có Issuer không khớp với Jwt:Issuer của API này. Hãy đăng nhập lại."),
                _ => ("INVALID_TOKEN", "Token không hợp lệ. Hãy đăng nhập lại.")
            };
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ctx.Response.WriteAsJsonAsync(new { code, message = msg });
        }
    };
});

builder.Services.AddAuthorization();

// Chạy sau reverse proxy / load balancer của host: lấy đúng scheme (https) và host thật của khách,
// để returnUrl/cancelUrl gửi cho payOS không bị sinh ra thành http://... (khách quay về sẽ bị ERR_CONNECTION_RESET).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// ==========================================
// KHỞI TẠO CƠ SỞ DỮ LIỆU
// - Dự án chưa có thư mục Migrations nên MigrateAsync() không tạo bảng nào: DB mới sẽ thiếu bảng Users
//   và mọi API (kể cả đăng nhập) trả "Lỗi hệ thống". Vì vậy: có migration -> Migrate, chưa có -> EnsureCreated.
// - Sau đó luôn vá các cột/bảng của khu quản trị (idempotent) để DB cũ không bị thiếu cột như Users.IsLocked.
// ==========================================
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = services.GetRequiredService<AppDbContext>();

        if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();

        await DevSchemaPatch.ApplyAllAsync(db, logger);

        // Dữ liệu demo + tài khoản quản trị mặc định: luôn chạy ở Development; ở máy chủ thật chỉ chạy khi bật Seed:Enabled
        if (app.Environment.IsDevelopment() || cfg.GetValue("Seed:Enabled", false))
            await DbSeeder.SeedAsync(services, cfg);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Lỗi xảy ra trong quá trình khởi tạo cơ sở dữ liệu. Kiểm tra ConnectionStrings:Default.");
    }
}

if (app.Environment.IsDevelopment() || cfg.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseResponseCompression();

// Ghi log các request liên quan thanh toán để biết payOS / SePay / khách có thật sự gọi tới server hay không (xem ở log của host).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var watch = path.StartsWith("/api/payments", StringComparison.OrdinalIgnoreCase);
    if (watch) app.Logger.LogInformation("REQ {Method} {Path} {Scheme} host={Host} qs={HasQuery}", ctx.Request.Method, path, ctx.Request.Scheme, ctx.Request.Host, ctx.Request.QueryString.HasValue);
    await next();
    if (watch) app.Logger.LogInformation("RES {Status} {Path}", ctx.Response.StatusCode, path);
});
app.UseRouting();
app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MaintenanceMiddleware>();

app.MapControllers();

// Trang gốc của API: chỉ báo dịch vụ đang chạy (giao diện nằm ở project EventTicketing.Mvc).
app.MapGet("/", () => Results.Ok(new { service = "TicketGo API", status = "running", docs = "/swagger" }));

// Chẩn đoán kết nối DB: mở /health/db để biết chính xác vì sao không kết nối được (sai mật khẩu, bị chặn IP, thiếu quyền...).
// Chỉ hoạt động khi Diagnostics:Enabled=true. Tắt đi sau khi chạy ổn.
app.MapGet("/health/db", async (AppDbContext db, IConfiguration c) =>
{
    if (!c.GetValue("Diagnostics:Enabled", false)) return Results.NotFound();
    try { return Results.Ok(new { ok = true, users = await db.Users.CountAsync(), schemaChanges = ModelSchemaSync.Changes, schemaIssues = ModelSchemaSync.Issues }); }
    catch (Exception ex)
    {
        var root = ex; while (root.InnerException is not null) root = root.InnerException;
        return Results.Json(new { ok = false, type = root.GetType().Name, message = root.Message }, statusCode: 503);
    }
});

app.Run();

/// Lỗi truy cập DB trong lúc xác thực token (phân biệt với token sai/hết hạn để trả 503, không phải 401).
sealed class AuthDbUnavailableException(Exception inner) : Exception("DB unavailable", inner);

/// Token hợp lệ về chữ ký nhưng trạng thái tài khoản trong DB không cho phép (không tồn tại / khoá / đổi quyền).
sealed class AuthStateException(string code, string message) : Exception(message) { public string Code { get; } = code; }
