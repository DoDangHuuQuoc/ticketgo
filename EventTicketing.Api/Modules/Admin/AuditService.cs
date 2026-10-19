using System.Security.Claims;
using System.Text;
using EventTicketing.Data;
using EventTicketing.Models;

namespace EventTicketing.Modules.Admin;

/// Ghi nhật ký thao tác quản trị. Gọi sau khi thao tác chính đã thành công.
public class AuditService(AppDbContext db)
{
    public async Task LogAsync(ClaimsPrincipal actor, string action, string entityType, object? entityId, string? detail = null)
    {
        Guid? id = Guid.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = id,
            ActorEmail = actor.FindFirstValue(ClaimTypes.Email) ?? "",
            Action = action, EntityType = entityType, EntityId = entityId?.ToString(),
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail
        });
        await db.SaveChangesAsync();
    }
}

public record UserState(string Role, bool IsLocked);

public static class Csv
{
    /// CSV có BOM UTF-8 để Excel đọc đúng tiếng Việt. Chống CSV-injection: ô bắt đầu bằng = + - @ được thêm dấu nháy.
    public static byte[] Build(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Cell)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Cell(object? v)
    {
        var s = v switch { null => "", DateTime d => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), decimal m => m.ToString("0.##"), _ => v.ToString() ?? "" };
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
