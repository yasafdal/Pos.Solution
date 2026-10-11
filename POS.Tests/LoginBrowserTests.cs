using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;
using POS.Web.Auth;
using Xunit;

namespace POS.Tests;

public sealed class LoginBrowserTests
{
    // Isolated synthetic account only; never uses the developer's actual Users records.
    internal const string TestPassword = "Synthetic-test-only-42!";
    internal const string TestKey = "local-test-signing-key-not-for-production-1234567890";

    internal static WebApplicationFactory<AuthController> CreateApi()
    {
        var database = Guid.NewGuid().ToString();
        return new WebApplicationFactory<AuthController>().WithWebHostBuilder(builder =>
            builder.UseUrls("http://127.0.0.1:0").UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = TestKey, ["Jwt:Issuer"] = "pos-tests", ["Jwt:Audience"] = "pos-tests"
                }))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<PosDbContext>();
                    services.RemoveAll<DbContextOptions<PosDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<PosDbContext>>();
                    var options = new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(database).Options;
                    services.AddScoped<PosDbContext>(_ => new TestPosDbContext(options));
                }));
    }

    internal static async Task SeedAsync(WebApplicationFactory<AuthController> api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        db.Users.Add(new User
        {
            UserId = 1, Username = "test-cashier", Role = "Cashier",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(TestPassword, workFactor: 4)
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Users_table_login_survives_refresh_and_logout_clears_session()
    {
        using var api = CreateApi();
        api.UseKestrel(0);
        using var apiClient = api.CreateClient();
        await SeedAsync(api);
        using var web = new WebApplicationFactory<CustomAuthStateProvider>().WithWebHostBuilder(builder =>
            builder.UseUrls("http://127.0.0.1:0").UseEnvironment("Development").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = apiClient.BaseAddress!.ToString() })));
        web.UseKestrel(0);
        using var webClient = web.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15000);
        await page.GotoAsync(new Uri(webClient.BaseAddress!, "/login").ToString());
        await page.Locator("input:not([type=hidden]):not([type=password])").FillAsync("test-cashier");
        await page.Locator("input[type=password]").FillAsync("wrong-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        try
        {
            await Assertions.Expect(page.Locator(".alert-danger")).ToBeVisibleAsync();
        }
        catch
        {
            throw new Xunit.Sdk.XunitException("Login page result: " + await page.Locator("body").InnerTextAsync());
        }
        await page.Locator("input[type=password]").FillAsync(TestPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Logout" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".app-header")).ToContainTextAsync("test-cashier");
        await page.GotoAsync(new Uri(webClient.BaseAddress!, "/auth").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "You are authenticated" })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "You are authenticated" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Logout" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign In" })).ToBeVisibleAsync();
        Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('authToken')"));
        await page.GotoAsync(new Uri(webClient.BaseAddress!, "/auth").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign In" })).ToBeVisibleAsync();
    }

    private sealed class TestPosDbContext(DbContextOptions<PosDbContext> options) : PosDbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }
    }
}
