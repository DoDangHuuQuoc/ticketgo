using System.Net;
using System.Text.Json;

namespace EventTicketing.Mvc.Services;

/// Khi admin bật bảo trì (hỏi API ở api/site/status): khách xem trang bảo trì (HTTP 503).
/// Luôn cho qua: file tĩnh, khu quản trị (/Manage/*) và trang đăng nhập (để admin vào tắt bảo trì).
public class MaintenanceGate(RequestDelegate next)
{
    private static readonly string[] Exempt = { "/manage", "/staff", "/account/login", "/css", "/js", "/img", "/lib", "/favicon" };

    public async Task Invoke(HttpContext ctx, SiteApiClient site, IConfiguration cfg)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (Exempt.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase))) { await next(ctx); return; }

        var (enabled, message) = await site.GetMaintenanceAsync();
        if (!enabled) { await next(ctx); return; }

        ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        ctx.Response.Headers["Retry-After"] = "300";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var apiBase = JsonSerializer.Serialize((cfg["Api:BaseUrl"] ?? "").TrimEnd('/'));
        await ctx.Response.WriteAsync(Page(WebUtility.HtmlEncode(message), apiBase));
    }

    private static string Page(string message, string apiBase) => $$"""
        <!DOCTYPE html>
        <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex"><title>Đang bảo trì – TicketGo</title>
        <link rel="icon" type="image/png" href="/img/favicon.png">
        <style>
          *{box-sizing:border-box}
          body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;background:#14264a;color:#fff;font:400 1rem/1.6 'Plus Jakarta Sans','Be Vietnam Pro',system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}
          main{width:min(520px,100%);background:#fff;color:#14264a;border-radius:24px;padding:36px 32px 30px;text-align:center;box-shadow:0 30px 70px -20px rgba(0,0,0,.5)}
          img{height:46px;width:auto;margin-bottom:22px}
          h1{margin:0 0 10px;font-size:1.6rem;line-height:1.2}
          p{margin:0 0 22px;color:#34496e;overflow-wrap:anywhere}
          a{display:inline-block;padding:12px 24px;border-radius:999px;background:#14264a;color:#fff;text-decoration:none;font-weight:700}
          a:focus-visible{outline:3px solid #d9a866;outline-offset:3px}
          small{display:block;margin-top:18px;color:#66758f}
          details{margin-top:22px;text-align:left;border-top:1px solid #e2e7f1;padding-top:16px}
          summary{cursor:pointer;font-weight:700;text-align:center;list-style:none;color:#14264a}
          summary::-webkit-details-marker{display:none}
          summary:focus-visible{outline:3px solid #d9a866;outline-offset:3px;border-radius:8px}
          form{display:grid;gap:12px;margin-top:16px}
          label{display:grid;gap:4px;font-size:.88rem;font-weight:600;color:#34496e}
          input,select,textarea{width:100%;min-height:44px;padding:10px 12px;border:1px solid #cdd6e8;border-radius:12px;font:inherit;color:#14264a;background:#fff}
          textarea{min-height:96px;resize:vertical}
          input:focus-visible,select:focus-visible,textarea:focus-visible{outline:3px solid #d9a866;outline-offset:1px}
          button{min-height:46px;border:0;border-radius:999px;background:#14264a;color:#fff;font:inherit;font-weight:700;cursor:pointer}
          button:disabled{opacity:.6;cursor:wait}
          #sMsg{margin:0;font-size:.9rem;font-weight:600}
          #sMsg.ok{color:#087a54}
          #sMsg.err{color:#d9363e}
        </style></head>
        <body><main role="main">
          <img src="/img/logo-wordmark.png" alt="TicketGo" width="165" height="46">
          <h1>Website đang bảo trì</h1>
          <p>{{message}}</p>
          <a href="javascript:location.reload()">Tải lại trang</a>
          <small>Vé đã mua vẫn an toàn, bạn không cần làm gì thêm.</small>
          <small id="adm" hidden><a href="/Manage/Dashboard" style="padding:9px 18px;font-size:.9rem">Vào trang quản trị</a></small>
          <details id="sup">
            <summary>Cần hỗ trợ? Gửi yêu cầu cho chúng tôi</summary>
            <form id="sForm" novalidate>
              <label>Họ tên<input id="sName" maxlength="200" autocomplete="name" required></label>
              <label>Email nhận phản hồi<input id="sEmail" type="email" maxlength="256" autocomplete="email" required></label>
              <label>Vấn đề cần hỗ trợ
                <select id="sCat">
                  <option value="Order">Đơn hàng</option><option value="Payment">Thanh toán</option><option value="Ticket">Vé</option>
                  <option value="Refund">Hoàn / đổi vé</option><option value="Event">Sự kiện</option><option value="Other" selected>Khác</option>
                </select></label>
              <label>Nội dung<textarea id="sText" maxlength="2000" placeholder="Mô tả vấn đề của bạn (ít nhất 5 ký tự)" required></textarea></label>
              <button type="submit" id="sBtn">Gửi yêu cầu</button>
              <p id="sMsg" role="status" aria-live="polite"></p>
            </form>
          </details>
        </main>
        <script>
          (function () {
            try { var r = localStorage.getItem('role'); if (r === 'Admin' || r === 'Staff') { var a = document.getElementById('adm'); a.hidden = false; if (r === 'Staff') a.firstChild.href = '/Staff/CheckIn'; } } catch (e) { }
          })();
          var API_BASE = {{apiBase}};
          (function () {
            var f = document.getElementById('sForm'), msg = document.getElementById('sMsg'), btn = document.getElementById('sBtn');
            function say(t, ok) { msg.textContent = t; msg.className = ok ? 'ok' : 'err'; }
            f.addEventListener('submit', function (e) {
              e.preventDefault();
              var body = { name: document.getElementById('sName').value, email: document.getElementById('sEmail').value, category: document.getElementById('sCat').value, message: document.getElementById('sText').value };
              btn.disabled = true; say('Đang gửi…', true);
              fetch(API_BASE + '/api/feedback', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
                .then(function (r) { return r.json().catch(function () { return {}; }).then(function (d) { return { ok: r.ok, d: d }; }); })
                .then(function (x) {
                  if (x.ok) { say(x.d.message || 'Đã gửi yêu cầu hỗ trợ.', true); f.reset(); }
                  else say(x.d.message || 'Không gửi được, vui lòng thử lại sau.', false);
                })
                .catch(function () { say('Không kết nối được máy chủ. Vui lòng thử lại.', false); })
                .then(function () { btn.disabled = false; });
            });
          })();
        </script>
        </body></html>
        """;
}
