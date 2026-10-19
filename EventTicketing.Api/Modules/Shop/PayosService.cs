using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTicketing.Common;

namespace EventTicketing.Modules.Shop;

/// Gọi API payOS dùng chung cho đơn lưu niệm (cùng bộ khoá Payment:PayOS:* với thanh toán vé).
public class PayosService(IConfiguration cfg, ILogger<PayosService> log)
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api-merchant.payos.vn"), Timeout = TimeSpan.FromSeconds(20) };

    public bool On => !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ClientId"])
                   && !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ApiKey"])
                   && !string.IsNullOrWhiteSpace(cfg["Payment:PayOS:ChecksumKey"]);

    /// Địa chỉ site MVC (payOS đưa khách quay về /Shop/Pay/...). Mặc định lấy Web:BaseUrl.
    public string? BaseUrl => cfg["Payment:PayOS:BaseUrl"] ?? cfg["Web:BaseUrl"];

    public string Sign(string raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(cfg["Payment:PayOS:ChecksumKey"]!));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    public async Task<JsonElement> Call(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("x-client-id", cfg["Payment:PayOS:ClientId"]);
        req.Headers.Add("x-api-key", cfg["Payment:PayOS:ApiKey"]);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "payOS call {Path} failed", path);
            throw new AppException(502, "PAYOS_ERROR", "Không kết nối được payOS, vui lòng thử lại");
        }
    }

    public static decimal Dec(string? s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
