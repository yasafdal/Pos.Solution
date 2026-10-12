using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using Xunit;

namespace POS.Tests;

public class FnbSeedTests
{
    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_ResetAndSeedFnb_script_to_pos_db()
    {
        using var configured = new PosDbContext();
        var cs = new NpgsqlConnectionStringBuilder(configured.Database.GetConnectionString());
        Assert.Contains(cs.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.Equal("pos_db", cs.Database);
        cs.Pooling = false;
        await using var db = new NpgsqlConnection(cs.ConnectionString);
        await db.OpenAsync();
        
        await using var cmd = new NpgsqlCommand("SET search_path TO public", db);
        await cmd.ExecuteNonQueryAsync();

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "ResetAndSeedFnb.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        // Verify categories and products are seeded
        using var context = new PosDbContext();
        var catCount = await context.Categories.CountAsync();
        var prodCount = await context.Products.CountAsync();
        Assert.True(catCount >= 5);
        Assert.True(prodCount >= 12);
    }
}
