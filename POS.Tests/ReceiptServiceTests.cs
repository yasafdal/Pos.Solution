using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;
using DatabaseReceiptService = POS.Api.Services.ReceiptService;

namespace POS.Tests;

public class ReceiptServiceTests
{
    private sealed class ReceiptResponseHandler(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://receipt.test/api/orders/123/receipt", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Http_adapter_does_not_hide_server_or_authorization_errors(int status)
    {
        using var client = new HttpClient(new ReceiptResponseHandler((System.Net.HttpStatusCode)status, "{}")) { BaseAddress = new Uri("https://receipt.test/") };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new POS.Shared.Services.ReceiptService(client).GetReceiptAsync(123));
        Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
    }

    [Fact]
    public async Task Http_adapter_maps_conflict_to_unpaid_exception()
    {
        using var client = new HttpClient(new ReceiptResponseHandler(System.Net.HttpStatusCode.Conflict, "{}")) { BaseAddress = new Uri("https://receipt.test/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new POS.Shared.Services.ReceiptService(client).GetReceiptAsync(123));
    }

    [Fact]
    public async Task Http_adapter_returns_null_only_for_not_found()
    {
        using var client = new HttpClient(new ReceiptResponseHandler(System.Net.HttpStatusCode.NotFound, "")) { BaseAddress = new Uri("https://receipt.test/") };
        Assert.Null(await new POS.Shared.Services.ReceiptService(client).GetReceiptAsync(123));
    }

    [Fact]
    public async Task Http_adapter_deserializes_receipt()
    {
        using var client = new HttpClient(new ReceiptResponseHandler(System.Net.HttpStatusCode.OK, "{\"orderId\":123,\"orderNumber\":\"ORD-123\"}")) { BaseAddress = new Uri("https://receipt.test/") };
        var receipt = await new POS.Shared.Services.ReceiptService(client).GetReceiptAsync(123);
        Assert.NotNull(receipt);
        Assert.Equal(123, receipt.OrderId);
        Assert.Equal("ORD-123", receipt.OrderNumber);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Http_adapter_rejects_invalid_success_payload(string body)
    {
        using var client = new HttpClient(new ReceiptResponseHandler(System.Net.HttpStatusCode.OK, body)) { BaseAddress = new Uri("https://receipt.test/") };
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new POS.Shared.Services.ReceiptService(client).GetReceiptAsync(123));
    }

    private const string TestSigningKey = "Receipt-tests-only-signing-key-at-least-32-bytes-long";

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<POS.Api.Controllers.AuthController> CreateApi()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<POS.Api.Controllers.AuthController>()
            .WithWebHostBuilder(builder => builder
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = TestSigningKey, ["Jwt:Issuer"] = "receipt-tests", ["Jwt:Audience"] = "receipt-tests"
                }))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<PosDbContext>();
                    services.RemoveAll<DbContextOptions<PosDbContext>>();
                    services.AddScoped<PosDbContext>(_ => new ReceiptTestDb(options));
                }));
    }

    private static string Token(string username, string role, string id = "1")
    {
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "receipt-tests", audience: "receipt-tests",
            claims: new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, id),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)
            }, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(TestSigningKey)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    [Theory]
    [InlineData(null, "Cashier", "PAID", 123, 401)]
    [InlineData("cashier.one", "Cashier", "PAID", 123, 200)]
    [InlineData("another.cashier", "Cashier", "PAID", 123, 403)]
    [InlineData("another.cashier", "Admin", "PAID", 123, 200)]
    [InlineData("cashier.one", "Cashier", "UNPAID", 123, 409)]
    [InlineData("another.cashier", "Cashier", "UNPAID", 123, 403)]
    [InlineData("cashier.one", "Cashier", "PAID", 404, 404)]
    public async Task Receipt_endpoint_enforces_authentication_owner_or_admin_and_paid_status(string? username, string role, string paymentStatus, long orderId, int status)
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using (var scope = api.Services.CreateScope())
        {
            var db = (ReceiptTestDb)scope.ServiceProvider.GetRequiredService<PosDbContext>();
            var order = await SeedPaidOrder(db);
            db.Attach(order);
            order.PaymentStatus = paymentStatus;
            await db.SaveChangesAsync();
        }
        if (username != null)
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(username, role));
        using var response = await client.GetAsync($"/api/orders/{orderId}/receipt");
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200)
        {
            var receipt = await response.Content.ReadFromJsonAsync<ReceiptDto>();
            Assert.NotNull(receipt);
            Assert.Equal("Original coffee", Assert.Single(receipt.Items).ProductName);
            Assert.Equal("Counter 2", receipt.TerminalName);
        }
    }

    // Never call the scaffolded base OnConfiguring: it unconditionally selects PostgreSQL.
    private sealed class ReceiptTestDb(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }
    }

    private static ReceiptTestDb CreateDb() => new(new DbContextOptionsBuilder<PosDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Order> SeedPaidOrder(ReceiptTestDb db)
    {
        var order = new Order
        {
            OrderId = 123, OrderNumber = "ORD-SNAPSHOT", BranchId = 7, OutletId = 9,
            CashierId = "cashier.one", OrderType = "DINING", OrderStatus = "COMPLETED",
            PaymentStatus = "PAID", Subtotal = 25m, DiscountAmount = 3m, TotalAmount = 22m,
            CreatedAt = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc),
            OrderItems = new List<OrderItem>
            {
                new()
                {
                    OrderItemId = 11, ProductId = 100, ProductName = "Original coffee", Quantity = 2m,
                    BasePrice = 10m, ModifierTotal = 2.5m, UnitPrice = 12.5m, LineTotal = 25m,
                    OrderItemModifiers = new List<OrderItemModifier>
                    {
                        new() { OrderItemModifierId = 12, ModifierOptionId = 200, ModifierName = "Original oat milk", AdditionalPrice = 2.5m }
                    }
                }
            },
            Payments = new List<Payment>
            {
                new()
                {
                    PaymentId = 13, PaymentMethod = "CASH", Amount = 22m, AmountTendered = 30m,
                    ChangeAmount = 8m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier.one",
                    ReferenceNumber = "REF-1", PaidAt = new DateTime(2026, 10, 12, 1, 5, 0, DateTimeKind.Utc)
                }
            }
        };
        db.Branches.Add(new Branch { BranchId = 7, Name = "Main branch", Code = "MAIN", Address = "1 Main Street", Phone = "555-0100", TaxType = "NONE" });
        db.Outlets.Add(new Outlet { OutletId = 9, BranchId = 7, Name = "Counter 2" });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return order;
    }

    [Fact]
    public async Task Failed_payment_does_not_make_up_a_successful_payment_shortfall()
    {
        await using var db = CreateDb();
        var order = await SeedPaidOrder(db);
        db.Attach(order);
        order.Payments.Single().Amount = 10m;
        db.Payments.Add(new Payment { OrderId = order.OrderId, PaymentMethod = "CARD", Amount = 100m, PaymentStatus = "FAILED", ProcessedBy = "cashier.one" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseReceiptService(db).GetReceiptAsync(order.OrderId));
    }

    [Fact]
    public async Task Missing_order_returns_null()
    {
        await using var db = CreateDb();
        Assert.Null(await new DatabaseReceiptService(db).GetReceiptAsync(404));
    }

    [Theory]
    [InlineData("UNPAID", "SUCCESS", 22)]
    [InlineData("PARTIAL", "SUCCESS", 22)]
    [InlineData("PAID", "FAILED", 22)]
    [InlineData("PAID", "PENDING", 22)]
    [InlineData("PAID", "SUCCESS", 21)]
    public async Task Unpaid_or_insufficient_successful_payments_reject_receipt(string orderStatus, string paymentStatus, int amount)
    {
        await using var db = CreateDb();
        var order = await SeedPaidOrder(db);
        db.Attach(order);
        order.PaymentStatus = orderStatus;
        order.Payments.Single().PaymentStatus = paymentStatus;
        order.Payments.Single().Amount = amount;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseReceiptService(db).GetReceiptAsync(order.OrderId));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Only_successful_payments_are_returned_with_latest_success_time_and_combined_amount()
    {
        await using var db = CreateDb();
        var order = await SeedPaidOrder(db);
        db.Attach(order);
        order.Payments.Single().Amount = 10m;
        var lastSuccess = order.Payments.Single().PaidAt.AddMinutes(1);
        db.Payments.Add(new Payment { OrderId = order.OrderId, PaymentId = 14, PaymentMethod = "CARD", Amount = 12m, AmountTendered = 12m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier.one", PaidAt = lastSuccess });
        db.Payments.Add(new Payment { OrderId = order.OrderId, PaymentId = 15, PaymentMethod = "CARD", Amount = 100m, PaymentStatus = "FAILED", ProcessedBy = "cashier.one", PaidAt = lastSuccess.AddMinutes(1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var receipt = await new DatabaseReceiptService(db).GetReceiptAsync(order.OrderId);
        Assert.NotNull(receipt);
        Assert.Equal(new long[] { 13, 14 }, receipt.Payments.Select(p => p.PaymentId));
        Assert.Equal(22m, receipt.Payments.Sum(p => p.Amount));
        Assert.Equal(lastSuccess, receipt.PaidAt);
    }

    [Fact]
    public async Task Paid_order_without_payment_is_rejected_even_when_total_is_zero()
    {
        await using var db = CreateDb();
        var order = await SeedPaidOrder(db);
        db.Attach(order);
        db.Payments.RemoveRange(order.Payments);
        order.TotalAmount = 0m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseReceiptService(db).GetReceiptAsync(order.OrderId));
    }

    [Fact]
    public async Task Paid_receipt_reads_persisted_snapshots_and_branch_terminal_details_without_tracking()
    {
        await using var db = CreateDb();
        var order = await SeedPaidOrder(db);
        // No catalog product or modifier exists: a receipt must not require live catalog data.
        var receipt = await new DatabaseReceiptService(db).GetReceiptAsync(order.OrderId);
        Assert.NotNull(receipt);
        Assert.Equal(order.OrderId, receipt.OrderId);
        Assert.Equal(order.OrderNumber, receipt.OrderNumber);
        Assert.Equal(7, receipt.BranchId);
        Assert.Equal("Main branch", receipt.BranchName);
        Assert.Equal("MAIN", receipt.BranchCode);
        Assert.Equal("1 Main Street", receipt.BranchAddress);
        Assert.Equal("555-0100", receipt.BranchPhone);
        Assert.Equal(9, receipt.OutletId);
        Assert.Equal("Counter 2", receipt.TerminalName);
        Assert.Equal("cashier.one", receipt.CashierId);
        Assert.Equal(order.CreatedAt, receipt.CreatedAt);
        Assert.Equal(25m, receipt.Subtotal);
        Assert.Equal(3m, receipt.DiscountAmount);
        Assert.Equal(22m, receipt.TotalAmount);
        var item = Assert.Single(receipt.Items);
        Assert.Equal(11, item.OrderItemId);
        Assert.Equal(100, item.ProductId);
        Assert.Equal("Original coffee", item.ProductName);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(10m, item.BasePrice);
        Assert.Equal(2.5m, item.ModifierTotal);
        Assert.Equal(12.5m, item.UnitPrice);
        Assert.Equal(25m, item.LineTotal);
        var modifier = Assert.Single(item.Modifiers);
        Assert.Equal(12, modifier.OrderItemModifierId);
        Assert.Equal(200, modifier.ModifierOptionId);
        Assert.Equal("Original oat milk", modifier.ModifierName);
        Assert.Equal(2.5m, modifier.AdditionalPrice);
        var payment = Assert.Single(receipt.Payments);
        Assert.Equal(13, payment.PaymentId);
        Assert.Equal(123, payment.OrderId);
        Assert.Equal("CASH", payment.PaymentMethod);
        Assert.Equal(22m, payment.Amount);
        Assert.Equal(30m, payment.AmountTendered);
        Assert.Equal(8m, payment.ChangeAmount);
        Assert.Equal("SUCCESS", payment.PaymentStatus);
        Assert.Equal("REF-1", payment.ReferenceNumber);
        Assert.Equal("cashier.one", payment.ProcessedBy);
        Assert.Equal(order.Payments.Single().PaidAt, receipt.PaidAt);
        Assert.Equal(receipt.PaidAt, payment.PaidAt);
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
