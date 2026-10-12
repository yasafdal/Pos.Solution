using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using Xunit;

namespace POS.Tests;

public class ModifierPostgresTests
{
    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_ProductModifiers_script_to_pos_db()
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

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "ProductModifiers.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        // Verify tables exist
        using var context = new PosDbContext();
        var mgCount = await context.ModifierGroups.CountAsync();
        var moCount = await context.ModifierOptions.CountAsync();
        Assert.True(mgCount >= 0);
        Assert.True(moCount >= 0);
    }

    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_SeedProductModifiers_script_and_verify_seed_data()
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

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "SeedProductModifiers.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        // Verify seeded modifier groups and options
        using var context = new PosDbContext();
        var groups = await context.ModifierGroups.Include(g => g.ModifierOptions).ToListAsync();

        var sizeGroup = groups.FirstOrDefault(g => g.Name == "Size");
        Assert.NotNull(sizeGroup);
        Assert.True(sizeGroup.IsRequired);
        Assert.Equal(1, sizeGroup.MinSelections);
        Assert.Equal(1, sizeGroup.MaxSelections);
        Assert.Contains(sizeGroup.ModifierOptions, o => o.Name == "Regular" && o.Price == 0.00m);
        Assert.Contains(sizeGroup.ModifierOptions, o => o.Name == "Large" && o.Price == 30.00m);

        var sugarGroup = groups.FirstOrDefault(g => g.Name == "Sugar Level");
        Assert.NotNull(sugarGroup);
        Assert.True(sugarGroup.IsRequired);
        Assert.Contains(sugarGroup.ModifierOptions, o => o.Name == "0% (No Sugar)");
        Assert.Contains(sugarGroup.ModifierOptions, o => o.Name == "50% (Half Sugar)");
        Assert.Contains(sugarGroup.ModifierOptions, o => o.Name == "100% (Regular)");

        var addonsGroup = groups.FirstOrDefault(g => g.Name == "Add-ons & Extras");
        Assert.NotNull(addonsGroup);
        Assert.False(addonsGroup.IsRequired);
        Assert.Equal(0, addonsGroup.MinSelections);
        Assert.Equal(3, addonsGroup.MaxSelections);
        Assert.Contains(addonsGroup.ModifierOptions, o => o.Name == "Extra Shot" && o.Price == 40.00m);
        Assert.Contains(addonsGroup.ModifierOptions, o => o.Name == "Pearls / Sinkers" && o.Price == 20.00m);
        Assert.Contains(addonsGroup.ModifierOptions, o => o.Name == "Caramel Syrup" && o.Price == 25.00m);
    }
}
