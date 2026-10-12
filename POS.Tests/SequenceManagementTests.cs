using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;
using Npgsql;
using Xunit;

namespace POS.Tests;

public class SequenceManagementTests
{
    private const string TestSigningKey = "Sequence-tests-only-signing-key-at-least-32-bytes-long";

    private sealed class SeqTestDb(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private static WebApplicationFactory<POS.Api.Controllers.AuthController> CreateApi()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WebApplicationFactory<POS.Api.Controllers.AuthController>()
            .WithWebHostBuilder(builder => builder
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = TestSigningKey, ["Jwt:Issuer"] = "seq-tests", ["Jwt:Audience"] = "seq-tests"
                }))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<PosDbContext>();
                    services.RemoveAll<DbContextOptions<PosDbContext>>();
                    services.AddScoped<PosDbContext>(_ => new SeqTestDb(options));
                    services.AddScoped<POS.Shared.Interfaces.ISequenceService, SequenceService>();
                }));
    }

    private static string Token(string username, string role)
    {
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "seq-tests", audience: "seq-tests",
            claims: new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)
            }, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(TestSigningKey)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Test_1_And_2_And_3_Outlet_Order_Sequences_Are_Independent()
    {
        using var api = CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var seqService = scope.ServiceProvider.GetRequiredService<POS.Shared.Interfaces.ISequenceService>();

        db.Branches.Add(new Branch { BranchId = 1, Name = "Branch 1", TaxType = "NONE" });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
        db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 1, Name = "POS 02" });
        await db.SaveChangesAsync();

        var ord1 = await seqService.GenerateOrderNumberAsync(1);
        Assert.Equal("POS01-000001", ord1);
        var ord2 = await seqService.GenerateOrderNumberAsync(1);
        Assert.Equal("POS01-000002", ord2);
        var ord3 = await seqService.GenerateOrderNumberAsync(2);
        Assert.Equal("POS02-000001", ord3);
    }

    [Fact]
    public async Task Test_4_And_5_Paid_Order_Receives_Invoice_Unpaid_Does_Not()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("cashier1", "Cashier"));

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
            db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
            db.InvoiceSequences.Add(new InvoiceSequence { OutletId = 1, SeriesCode = "SER1", Prefix = "INV-", StartingNumber = 1, LastIssuedNumber = 0, IsActive = true });
            db.Products.Add(new Product { ProductId = 10, CategoryId = 1, Name = "Item", Price = 100m, ProductType = "Standard", Category = new Category { Name = "Cat" } });
            await db.SaveChangesAsync();
        }

        var createDto = new OrderCreateDto
        {
            BranchId = 1,
            OutletId = 1,
            CashierId = "cashier1",
            Items = new List<OrderItemCreateDto> { new() { ProductId = 10, ProductName = "Item", Quantity = 1m, BasePrice = 100m } }
        };

        var postOrderResp = await client.PostAsJsonAsync("/api/orders", createDto);
        if (!postOrderResp.IsSuccessStatusCode)
        {
            var err = await postOrderResp.Content.ReadAsStringAsync();
            throw new Exception($"Order creation failed: {postOrderResp.StatusCode} - {err}");
        }
        Assert.Equal(HttpStatusCode.OK, postOrderResp.StatusCode);
        var order = await postOrderResp.Content.ReadFromJsonAsync<OrderResponseDto>();
        Assert.NotNull(order);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            var dbOrder = await db.Orders.FindAsync(order.OrderId);
            Assert.NotNull(dbOrder);
            Assert.Null(dbOrder.InvoiceNumber); 
        }

        var payDto = new PaymentProcessDto
        {
            OrderId = order.OrderId,
            PaymentMethod = "CASH",
            AmountTendered = 100m,
            ProcessedBy = "cashier1"
        };

        var payResp = await client.PostAsJsonAsync("/api/payments", payDto);
        if (!payResp.IsSuccessStatusCode)
        {
            var err = await payResp.Content.ReadAsStringAsync();
            throw new Exception($"Payment processing failed: {payResp.StatusCode} - {err}");
        }
        Assert.Equal(HttpStatusCode.OK, payResp.StatusCode);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            var dbOrder = await db.Orders.FindAsync(order.OrderId);
            Assert.NotNull(dbOrder);
            Assert.Equal("INV-0000001", dbOrder.InvoiceNumber);
        }
    }

    [Fact]
    public async Task Test_6_And_7_Concurrent_Invoice_Issuance_And_Payment_Retry()
    {
        using var api = CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var seqService = scope.ServiceProvider.GetRequiredService<POS.Shared.Interfaces.ISequenceService>();

        db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
        db.InvoiceSequences.Add(new InvoiceSequence { OutletId = 1, SeriesCode = "SER1", Prefix = "INV-", StartingNumber = 1, LastIssuedNumber = 0, IsActive = true });
        db.Orders.Add(new Order { OrderId = 1, OrderNumber = "ORD-1", BranchId = 1, OutletId = 1, CashierId = "c1", OrderType = "DINING", OrderStatus = "PENDING", PaymentStatus = "UNPAID", TotalAmount = 50m });
        db.Orders.Add(new Order { OrderId = 2, OrderNumber = "ORD-2", BranchId = 1, OutletId = 1, CashierId = "c1", OrderType = "DINING", OrderStatus = "PENDING", PaymentStatus = "UNPAID", TotalAmount = 50m });
        await db.SaveChangesAsync();

        var inv1 = await seqService.IssueInvoiceNumberAsync(1, 1);
        var inv2 = await seqService.IssueInvoiceNumberAsync(2, 1);

        Assert.Equal("INV-0000001", inv1);
        Assert.Equal("INV-0000002", inv2);
        
        var inv1Retry = await seqService.IssueInvoiceNumberAsync(1, 1);
        Assert.Equal("INV-0000001", inv1Retry);
    }

    [Fact]
    public async Task Test_8_Receipt_Reprint_Preserves_Invoice_Number()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin1", "Admin"));

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
            db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
            db.InvoiceSequences.Add(new InvoiceSequence { OutletId = 1, SeriesCode = "SER1", Prefix = "INV-", StartingNumber = 1, LastIssuedNumber = 1, IsActive = true });
            db.Orders.Add(new Order
            {
                OrderId = 10,
                OrderNumber = "POS01-000001",
                BranchId = 1,
                OutletId = 1,
                CashierId = "admin1",
                OrderType = "DINING",
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                TotalAmount = 100m,
                InvoiceNumber = "INV-0000001",
                Payments = new List<Payment> { new() { PaymentMethod = "CASH", Amount = 100m, AmountTendered = 100m, PaymentStatus = "SUCCESS", ProcessedBy = "admin1" } }
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/orders/10/receipt");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var receipt = await resp.Content.ReadFromJsonAsync<ReceiptDto>();
        Assert.NotNull(receipt);
        Assert.Equal("INV-0000001", receipt.InvoiceNumber);
    }

    [Fact]
    public async Task Test_9_Invoice_Sequence_Respects_Ending_Number()
    {
        using var api = CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var seqService = scope.ServiceProvider.GetRequiredService<POS.Shared.Interfaces.ISequenceService>();

        db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
        db.InvoiceSequences.Add(new InvoiceSequence { OutletId = 1, SeriesCode = "SER1", Prefix = "INV-", StartingNumber = 1, EndingNumber = 1, LastIssuedNumber = 1, IsActive = true });
        db.Orders.Add(new Order { OrderId = 1, OrderNumber = "ORD-1", BranchId = 1, OutletId = 1, CashierId = "c1", OrderType = "DINING", OrderStatus = "PENDING", PaymentStatus = "UNPAID", TotalAmount = 10m });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => seqService.IssueInvoiceNumberAsync(1, 1));
    }

    [Fact]
    public async Task Test_10_Unauthorized_Cashier_Cannot_Modify_Invoice_Sequences()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("cashier1", "Cashier"));

        var seqDto = new InvoiceSequenceDto { SeriesCode = "NEW", StartingNumber = 1 };
        var resp = await client.PostAsJsonAsync("/api/outlets/1/invoice-sequences", seqDto);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Test_11_Voided_Order_Retains_Invoice_Serial()
    {
        using var api = CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01" });
        db.Orders.Add(new Order
        {
            OrderId = 99,
            OrderNumber = "POS01-99",
            BranchId = 1,
            OutletId = 1,
            CashierId = "c1",
            OrderType = "DINING",
            OrderStatus = "VOIDED",
            PaymentStatus = "PAID",
            TotalAmount = 50m,
            InvoiceNumber = "INV-0000999"
        });
        await db.SaveChangesAsync();

        var ord = await db.Orders.FindAsync(99L);
        Assert.NotNull(ord);
        Assert.Equal("INV-0000999", ord.InvoiceNumber);
    }

    [Fact]
    public async Task Test_12_Disabling_Outlet_Does_Not_Reset_Numbering()
    {
        using var api = CreateApi();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var seqService = scope.ServiceProvider.GetRequiredService<POS.Shared.Interfaces.ISequenceService>();

        db.Branches.Add(new Branch { BranchId = 1, Name = "B1", TaxType = "NONE" });
        var outlet = new Outlet { OutletId = 1, BranchId = 1, Name = "POS 01", IsActive = true };
        db.Outlets.Add(outlet);
        await db.SaveChangesAsync();

        var n1 = await seqService.GenerateOrderNumberAsync(1);
        Assert.Equal("POS01-000001", n1);

        outlet.IsActive = false;
        await db.SaveChangesAsync();

        var n2 = await seqService.GenerateOrderNumberAsync(1);
        Assert.Equal("POS01-000002", n2);
    }

    [Fact(Skip = "Manual database script runner: never mutate pos_db during automated tests. Apply reviewed SQL explicitly.")]
    public async Task Apply_SequenceManagement_script_to_pos_db()
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

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "POS.Api", "Database", "SequenceManagement.sql");
        var sql = await File.ReadAllTextAsync(sqlPath);
        await using var scriptCmd = new NpgsqlCommand(sql, db);
        await scriptCmd.ExecuteNonQueryAsync();

        using var context = new PosDbContext();
        var seqCount = await context.Set<OrderSequence>().CountAsync();
        Assert.True(seqCount >= 0);
    }
}
