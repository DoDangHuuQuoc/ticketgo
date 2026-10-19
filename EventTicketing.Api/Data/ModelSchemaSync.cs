using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EventTicketing.Data;

/// Đồng bộ database với model hiện tại của ứng dụng khi khởi động (SQL Server).
/// Vấn đề giải quyết: DB trên host được tạo bằng EnsureCreated từ một phiên bản cũ; EnsureCreated không bao giờ sửa DB đã có bảng,
/// nên khi code thêm bảng/cột mới (vd. bảng Tickets, cột Bookings.TotalAmount) thì truy vấn báo "Invalid object name" / "Invalid column name".
///
/// Việc làm (chỉ THÊM / NỚI, không bao giờ xoá dữ liệu hay cột):
///  1. Tạo bảng chưa có.            2. Thêm cột chưa có (dòng cũ nhận giá trị mặc định).
///  3. Tạo index chưa có.           4. Cột cũ NOT NULL không còn trong model (làm INSERT lỗi) -> chuyển thành cho phép NULL.
///  5. Cột chuỗi trong DB ngắn hơn model (làm INSERT lỗi "String or binary data would be truncated") -> nới rộng.
///  6. Các chỗ lệch kiểu dữ liệu không tự sửa được -> ghi vào Issues để xem ở /health/db.
/// Idempotent. Lỗi ở một bảng chỉ ghi log, không chặn các bảng khác.
public static class ModelSchemaSync
{
    /// Những thay đổi đã thực hiện ở lần khởi động này (xem ở /health/db).
    public static string[] Changes { get; private set; } = Array.Empty<string>();
    /// Những chỗ lệch còn lại cần xử lý tay (xem ở /health/db).
    public static string[] Issues { get; private set; } = Array.Empty<string>();

    private sealed class ColInfo
    {
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public int MaxLength { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public bool HasDefault { get; set; }
    }

    private static string Q(string n) => "[" + n.Replace("]", "]]") + "]";

    public static async Task RunAsync(AppDbContext db, ILogger log)
    {
        var changes = new List<string>();
        var issues = new List<string>();

        void Changed(string msg) { changes.Add(msg); log.LogInformation("Schema: {Msg}", msg); }
        void Issue(string msg) { issues.Add(msg); log.LogWarning("Schema: {Msg}", msg); }

        var entities = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() != null && !e.IsOwned() && e.GetViewName() == null)
            .ToList();

        foreach (var et in entities)
        {
            var table = et.GetTableName()!;
            var schema = et.GetSchema();
            var full = schema is null ? Q(table) : Q(schema) + "." + Q(table);
            try { await SyncTableAsync(db, et, table, full, Changed, Issue); }
            catch (Exception ex)
            {
                Issue($"{table}: không đồng bộ được ({Root(ex)})");
                log.LogWarning(ex, "Đồng bộ bảng {Table} theo model không thành công", table);
            }
        }

        Changes = changes.ToArray();
        Issues = issues.ToArray();
    }

    private static string Root(Exception ex) { while (ex.InnerException is not null) ex = ex.InnerException; return ex.Message; }

    private static async Task SyncTableAsync(AppDbContext db, IEntityType et, string table, string full, Action<string> changed, Action<string> issue)
    {
        var props = et.GetProperties().Where(p => p.GetColumnName() != null).ToList();

        var exists = (await db.Database.SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID({full}, N'U') IS NULL THEN 0 ELSE 1 END AS [Value]").ToListAsync()).First() == 1;

        if (!exists)
        {
            var cols = props.Select(p => ColumnDef(p, forAdd: false)).ToList();
            var pk = et.FindPrimaryKey();
            if (pk != null) cols.Add($"CONSTRAINT {Q("PK_" + table)} PRIMARY KEY ({string.Join(", ", pk.Properties.Select(p => Q(p.GetColumnName()!)))})");
            var create = $"CREATE TABLE {full} (\n    {string.Join(",\n    ", cols)}\n);";
            await db.Database.ExecuteSqlRawAsync(create);
            changed($"Đã tạo bảng thiếu {table}");
        }
        else
        {
            var existing = await db.Database.SqlQuery<ColInfo>($@"
                SELECT c.name AS Name, t.name AS TypeName, CAST(c.max_length AS int) AS MaxLength, CAST(c.precision AS int) AS [Precision], CAST(c.scale AS int) AS Scale,
                       c.is_nullable AS IsNullable, c.is_identity AS IsIdentity, c.is_computed AS IsComputed,
                       CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit) AS HasDefault
                FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID({full})").ToListAsync();
            var byName = existing.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var modelCols = props.Select(p => p.GetColumnName()!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 2) Cột còn thiếu
            foreach (var p in props)
            {
                var col = p.GetColumnName()!;
                if (byName.ContainsKey(col)) continue;
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {full} ADD {ColumnDef(p, forAdd: true)};");
                changed($"Đã thêm cột thiếu {table}.{col}");
            }

            // 4) Cột cũ NOT NULL không còn trong model: INSERT mới sẽ lỗi vì không có giá trị -> cho phép NULL
            foreach (var c in existing.Where(c => !modelCols.Contains(c.Name) && !c.IsNullable && !c.IsIdentity && !c.IsComputed && !c.HasDefault))
            {
                var type = LegacyType(c);
                if (type is null) { issue($"{table}.{c.Name}: cột cũ NOT NULL kiểu {c.TypeName} không còn dùng, cần xử lý tay"); continue; }
                try
                {
                    await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {full} ALTER COLUMN {Q(c.Name)} {type} NULL;");
                    changed($"Cột cũ {table}.{c.Name} không còn dùng nên đã cho phép NULL");
                }
                catch (Exception ex) { issue($"{table}.{c.Name}: cột cũ NOT NULL không còn dùng, không tự sửa được ({Root(ex)})"); }
            }

            // 5) + 6) So kiểu dữ liệu với model
            foreach (var p in props)
            {
                var col = p.GetColumnName()!;
                if (!byName.TryGetValue(col, out var have) || have.IsComputed) continue;

                var (eBase, eLen) = Parse(ColType(p));
                var aBase = have.TypeName.ToLowerInvariant();

                if (IsString(eBase) && IsString(aBase))
                {
                    // chiều dài thực của cột hiện có (ký tự); -1 = max
                    var aChars = have.MaxLength == -1 ? -1 : (aBase.StartsWith("n") ? have.MaxLength / 2 : have.MaxLength);
                    var tooShort = aChars != -1 && (eLen is null || eLen > aChars);
                    if (tooShort)
                    {
                        var newType = eLen is null ? $"{aBase}(max)" : $"{aBase}({eLen})";
                        try
                        {
                            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {full} ALTER COLUMN {Q(col)} {newType} {(have.IsNullable ? "NULL" : "NOT NULL")};");
                            changed($"Đã nới rộng {table}.{col} lên {newType}");
                        }
                        catch (Exception ex) { issue($"{table}.{col}: DB {aBase}({aChars}) ngắn hơn model {eBase}({(eLen?.ToString() ?? "max")}), không tự nới được ({Root(ex)})"); }
                    }
                }
                else if (!Compatible(eBase, aBase))
                {
                    issue($"{table}.{col}: DB đang là {aBase}, model cần {eBase} — cần chuyển đổi tay (hoặc chạy Database/reset-database.sql nếu không cần giữ dữ liệu)");
                }
            }
        }

        // 3) Index (kể cả unique) còn thiếu. Không tạo được (vd. dữ liệu cũ trùng) thì chỉ ghi nhận.
        var haveIx = (await db.Database.SqlQuery<string>($"SELECT i.name AS [Value] FROM sys.indexes i WHERE i.object_id = OBJECT_ID({full}) AND i.name IS NOT NULL").ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ix in et.GetIndexes())
        {
            var name = ix.GetDatabaseName();
            if (string.IsNullOrEmpty(name) || haveIx.Contains(name)) continue;
            try
            {
                var cols = string.Join(", ", ix.Properties.Select(p => Q(p.GetColumnName()!)));
                await db.Database.ExecuteSqlRawAsync($"CREATE {(ix.IsUnique ? "UNIQUE " : "")}INDEX {Q(name)} ON {full} ({cols});");
                changed($"Đã tạo index thiếu {name} trên {table}");
            }
            catch (Exception ex) { issue($"Index {name} trên {table} không tạo được ({Root(ex)})"); }
        }
    }

    // ---------- Kiểu dữ liệu ----------
    private static bool IsString(string t) => t is "nvarchar" or "varchar" or "nchar" or "char";

    private static readonly string[] DateFamily = { "datetime", "datetime2", "smalldatetime", "date" };
    private static bool Compatible(string expected, string actual) =>
        expected == actual || (DateFamily.Contains(expected) && DateFamily.Contains(actual));

    private static readonly Regex TypeRx = new(@"^\s*([a-z0-9_]+)\s*(?:\(\s*(max|\d+)(?:\s*,\s*\d+)?\s*\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// "nvarchar(200)" -> ("nvarchar", 200); "nvarchar(max)" -> ("nvarchar", null); "int" -> ("int", null)
    private static (string Base, int? Len) Parse(string type)
    {
        var m = TypeRx.Match(type);
        if (!m.Success) return (type.ToLowerInvariant(), null);
        var b = m.Groups[1].Value.ToLowerInvariant();
        var l = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var n) ? n : (int?)null;
        return (b, l);
    }

    private static string? LegacyType(ColInfo c)
    {
        var t = c.TypeName.ToLowerInvariant();
        switch (t)
        {
            case "nvarchar": case "nchar":
                return $"{t}({(c.MaxLength == -1 ? "max" : (c.MaxLength / 2).ToString())})";
            case "varchar": case "char": case "varbinary": case "binary":
                return $"{t}({(c.MaxLength == -1 ? "max" : c.MaxLength.ToString())})";
            case "decimal": case "numeric":
                return $"{t}({c.Precision},{c.Scale})";
            case "datetime2": case "time": case "datetimeoffset":
                return $"{t}({c.Scale})";
            case "timestamp": case "rowversion": case "sql_variant": case "xml": case "text": case "ntext": case "image":
            case "geography": case "geometry": case "hierarchyid": case "float": case "real":
                return null;   // không đổi tự động
            default:
                return t;      // int, bigint, bit, uniqueidentifier, date, datetime, money ...
        }
    }

    // ---------- Định nghĩa cột ----------
    private static string ColumnDef(IProperty p, bool forAdd)
    {
        var type = ColType(p);
        var name = Q(p.GetColumnName()!);
        var identity = !forAdd && p.ClrType == typeof(int) && p.ValueGenerated == ValueGenerated.OnAdd ? " IDENTITY(1,1)" : "";

        if (p.IsNullable) return $"{name} {type} NULL";

        if (forAdd)
        {
            // Thêm cột NOT NULL vào bảng đã có dòng: bắt buộc có giá trị mặc định cho các dòng cũ
            var def = DefaultFor(type);
            return def is null ? $"{name} {type} NULL" : $"{name} {type} NOT NULL DEFAULT {def}";
        }
        return $"{name} {type}{identity} NOT NULL";
    }

    private static string ColType(IProperty p)
    {
        try { var t = p.GetColumnType(); if (!string.IsNullOrWhiteSpace(t)) return t; }
        catch (Exception) { /* dùng bảng ánh xạ dự phòng bên dưới */ }

        var c = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        if (c == typeof(Guid)) return "uniqueidentifier";
        if (c == typeof(bool)) return "bit";
        if (c == typeof(int)) return "int";
        if (c == typeof(long)) return "bigint";
        if (c == typeof(decimal)) return $"decimal({p.GetPrecision() ?? 18},{p.GetScale() ?? 2})";
        if (c == typeof(DateTime)) return "datetime2";
        if (c == typeof(string)) return p.GetMaxLength() is int n ? $"nvarchar({n})" : "nvarchar(max)";
        if (c.IsEnum) return "nvarchar(32)";
        return "nvarchar(max)";
    }

    private static string? DefaultFor(string type)
    {
        var t = type.ToLowerInvariant();
        if (t.StartsWith("nvarchar") || t.StartsWith("varchar") || t.StartsWith("nchar") || t.StartsWith("char")) return "N''";
        if (t is "bit" or "int" or "bigint" or "smallint" or "tinyint" or "float" or "real" || t.StartsWith("decimal") || t.StartsWith("numeric")) return "0";
        if (t == "uniqueidentifier") return "'00000000-0000-0000-0000-000000000000'";
        if (t == "date") return "'0001-01-01'";
        if (t.StartsWith("datetime")) return "SYSUTCDATETIME()";
        if (t.StartsWith("varbinary")) return "0x";
        return null;
    }
}
