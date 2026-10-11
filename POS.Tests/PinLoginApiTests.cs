using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public sealed class PinLoginApiTests
{
    private static WebApplicationFactory<AuthController> CreateApi()
    {
        var database = Guid.NewGuid().ToString();
        return new WebApplicationFactory<AuthController>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = LoginBrowserTests.TestKey,
                    ["Jwt:Issuer"] = "pos-tests",
                    ["Jwt:Audience"] = "pos-tests"
                }))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<PosDbContext>();
                    services.RemoveAll<DbContextOptions<PosDbContext>>();
                    var options = new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(database).Options;
                    services.AddScoped<PosDbContext>(_ => new TestPosDbContext(options));
                }));
    }

    private static async Task SeedCashiersAsync(WebApplicationFactory<AuthController> api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        db.Users.Add(new User { UserId = 1, Username = "cashier 1", Role = "Cashier", PinCode = "1234", PasswordHash = "dummy" });
        db.Users.Add(new User { UserId = 2, Username = "cashier 2", Role = "Cashier", PinCode = "5678", PasswordHash = "dummy" });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("1234", "cashier 1", HttpStatusCode.OK)]
    [InlineData("5678", "cashier 2", HttpStatusCode.OK)]
    [InlineData("9999", null, HttpStatusCode.Unauthorized)]
    public async Task Pin_login_endpoint_authenticates_cashiers(string pin, string? expectedUsername, HttpStatusCode expectedStatus)
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        await SeedCashiersAsync(api);

        using var response = await client.PostAsJsonAsync("/api/auth/pin-login", new PinLoginDto { PinCode = pin });
        Assert.Equal(expectedStatus, response.StatusCode);

        if (expectedStatus == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<PinResponse>();
            Assert.NotNull(result);
            Assert.Equal(expectedUsername, result.Username);
            Assert.False(string.IsNullOrEmpty(result.Token));
        }
    }

    [Theory]
    [InlineData("RETAIL")]
    [InlineData("FNB")]
    [InlineData(null)]
    public async Task Outlet_lookup_requires_JWT_and_shared_service_resolves_using_request_bearer(string? businessType)
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        await SeedCashiersAsync(api);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            db.Branches.Add(new Branch { BranchId = 1, Name = "Synthetic branch", Code = "BR01", IsActive = true, TaxType = "VAT",
                BusinessType = businessType == null ? null : new BusinessType { Id = 7, Type = businessType, Description = "Synthetic" } });
            db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, Name = "Counter A", IsActive = true, DeviceIdentifier = "192.168.1.25" });
            await db.SaveChangesAsync();
        }
        using var anonymous = await client.GetAsync("/api/outlets/resolve-terminal?ipAddress=192.168.1.25");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var auth = await client.PostAsJsonAsync("/api/auth/pin-login", new PinLoginDto { PinCode = "1234" });
        var credentials = (await auth.Content.ReadFromJsonAsync<PinResponse>())!;
        var service = new POS.Shared.Services.OutletService(client);
        var outlet = await service.ResolveTerminalAsync("192.168.1.25", credentials.Token);
        Assert.Equal("BR01", outlet?.BranchCode);
        Assert.Equal("Counter A", outlet?.OutletName);
        // JSON assertion permits RED to exercise the old contract rather than fail compilation.
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(outlet);
        Assert.True(payload.TryGetProperty("BusinessTypeId", out var typeId), "Resolved branch business type ID is missing");
        Assert.Equal(businessType == null ? (int?)null : 7, typeId.ValueKind == System.Text.Json.JsonValueKind.Null ? null : typeId.GetInt32());
        Assert.Equal(businessType, payload.GetProperty("BusinessType").GetString());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(await service.ResolveTerminalAsync("192.168.1.26", credentials.Token));
    }

    private sealed record PinResponse(string Token, string Username, string Role);

    private sealed class TestPosDbContext(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }
    }
}
