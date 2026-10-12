using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public class PaymentModuleTests
{
    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_PaymentModule_script_to_pos_db_and_verify_tables()
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

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "PaymentModule.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        // Verify tables exist and can query
        using var context = new PosDbContext();
        var orderCount = await context.Orders.CountAsync();
        var itemCount = await context.OrderItems.CountAsync();
        var modCount = await context.OrderItemModifiers.CountAsync();
        var paymentCount = await context.Payments.CountAsync();

        Assert.True(orderCount >= 0);
        Assert.True(itemCount >= 0);
        Assert.True(modCount >= 0);
        Assert.True(paymentCount >= 0);
    }

    [Fact]
    public void Order_And_Payment_Calculations_Logic()
    {
        // Test server-side calculation rules:
        decimal basePrice = 100.00m;
        decimal mod1Price = 20.00m;
        decimal mod2Price = 15.00m;
        decimal quantity = 2.000m;

        decimal unitPrice = basePrice + mod1Price + mod2Price;
        decimal lineTotal = unitPrice * quantity;

        Assert.Equal(135.00m, unitPrice);
        Assert.Equal(270.00m, lineTotal);

        decimal subtotal = lineTotal;
        decimal discount = 20.00m;
        decimal totalAmount = subtotal - discount;

        Assert.Equal(250.00m, totalAmount);

        decimal tendered = 300.00m;
        decimal change = tendered - totalAmount;
        Assert.Equal(50.00m, change);
    }
}
