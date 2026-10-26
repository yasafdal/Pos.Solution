using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
namespace POS.Api.Services;

public static class ShiftLifecycle
{
    // Use READ COMMITTED on PostgreSQL so readers see a closure committed while waiting for this lock.
    public static async Task LockTerminalAsync(PosDbContext db, int terminalId, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Terminals\" WHERE \"TerminalId\" = {terminalId} FOR UPDATE", ct);
    }
}
