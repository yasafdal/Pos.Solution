using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public sealed class ClientPinLoginTests
{
    [Theory]
    [InlineData("1234", "cashier1", "Cashier", HttpStatusCode.OK)]
    [InlineData("5678", "cashier2", "Cashier", HttpStatusCode.OK)]
    [InlineData("9999", null, null, HttpStatusCode.Unauthorized)]
    public async Task PinLogin_endpoint_authenticates_cashiers_correctly(string pin, string? expectedUser, string? expectedRole, HttpStatusCode expectedStatus)
    {
        using var api = new WebApplicationFactory<AuthController>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = LoginBrowserTests.TestKey, ["Jwt:Issuer"] = "pos-tests", ["Jwt:Audience"] = "pos-tests"
                }))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<PosDbContext>();
                    services.RemoveAll<DbContextOptions<PosDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<PosDbContext>>();
                    var database = Guid.NewGuid().ToString();
                    var options = new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(database).Options;
                    services.AddScoped<PosDbContext>(_ => new TestPosDbContext(options));
                }));

        using var client = api.CreateClient();

        // Seed cashiers
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            db.Users.AddRange(
                new User { UserId = 1, Username = "cashier1", Role = "Cashier", PinCode = "1234", PasswordHash = "dummy" },
                new User { UserId = 2, Username = "cashier2", Role = "Cashier", PinCode = "5678", PasswordHash = "dummy" }
            );
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/auth/pin-login", new PinLoginDto { PinCode = pin });
        Assert.Equal(expectedStatus, response.StatusCode);

        if (expectedStatus == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<PinResponse>();
            Assert.NotNull(result);
            Assert.Equal(expectedUser, result.Username);
            Assert.Equal(expectedRole, result.Role);
            Assert.False(string.IsNullOrEmpty(result.Token));
        }
    }

    private sealed record PinResponse(string Token, string Username, string Role);

    private sealed class TestPosDbContext(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }
    }
}
