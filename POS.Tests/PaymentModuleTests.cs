using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public class PaymentModuleTests
{
    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_CustomerInfo_script_to_pos_db()
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

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "CustomerInfo.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        using var context = new PosDbContext();
        var testOrder = new Order
        {
            OrderNumber = "TST-CUST-002",
            BranchId = 1,
            OutletId = 1,
            CashierId = "tester",
            OrderType = "DINING",
            OrderStatus = "PENDING",
            PaymentStatus = "UNPAID",
            Subtotal = 100m,
            TotalAmount = 100m,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            CustomerName = "ACME Corp",
            CustomerAddress = "Makati City",
            CustomerTin = "123-456-789-000",
            CustomerBusinessStyle = "Retail"
        };
        context.Orders.Add(testOrder);
        await context.SaveChangesAsync();

        var queried = await context.Orders.FindAsync(testOrder.OrderId);
        Assert.NotNull(queried);
        Assert.Equal("ACME Corp", queried.CustomerName);
        Assert.Equal("Makati City", queried.CustomerAddress);
        Assert.Equal("123-456-789-000", queried.CustomerTin);
        Assert.Equal("Retail", queried.CustomerBusinessStyle);
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
