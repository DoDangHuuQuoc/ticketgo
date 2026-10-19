using EventTicketing.Data;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Modules.Bookings;

/// Mỗi phút: huỷ booking PENDING quá hạn và hoàn kho — gói trong MỘT câu SQL nguyên tử.
/// Race với webhook thanh toán được chặn vì webhook khoá dòng booking (FOR UPDATE) và kiểm tra Status='Pending'.
public class ExpiredBookingWorker(IServiceScopeFactory scopes, ILogger<ExpiredBookingWorker> log) : BackgroundService
{
    private const string Sql = """
        SET XACT_ABORT ON;
        BEGIN TRAN;
        DECLARE @expired TABLE (TicketTypeId uniqueidentifier NOT NULL, Quantity int NOT NULL);

        UPDATE Bookings SET Status = 'Expired'
        OUTPUT inserted.TicketTypeId, inserted.Quantity INTO @expired
        WHERE Status = 'Pending' AND ExpiresAt < SYSUTCDATETIME();

        UPDATE t SET AvailableQuantity = t.AvailableQuantity + s.Qty
        FROM TicketTypes t
        JOIN (SELECT TicketTypeId, SUM(Quantity) AS Qty FROM @expired GROUP BY TicketTypeId) s ON s.TicketTypeId = t.Id;

        COMMIT;
        """;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var n = await db.Database.ExecuteSqlRawAsync(Sql, ct);
                if (n > 0) log.LogInformation("Restocked {N} ticket types from expired bookings", n);
            }
            catch (Exception ex) { log.LogError(ex, "Expire worker error"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
