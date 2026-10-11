using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using POS.Client.Services;
using POS.Shared.Interfaces;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

// Kestrel + Chromium exercise the actual linked MAUI Razor component, not a copy.
// Only authentication is synthetic; this host never contacts the API or a database.
public sealed class ClientPinBrowserTests
{
    [Fact]
    public async Task Mouse_digit_then_keyboard_preserves_focus_and_exact_pin()
    {
        await using var host = await PinHost.StartAsync();
        var page = host.Page;
        await page.GetByRole(AriaRole.Button, new() { Name = "1", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".pin-dot.filled")).ToHaveCountAsync(1);
        await page.Keyboard.TypeAsync("2");
        await Assertions.Expect(page.Locator(".pin-dot.filled")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".hidden-pin-input")).ToHaveValueAsync("12");
        await page.Keyboard.TypeAsync("34");
        await Assertions.Expect(page.Locator(".pos-error-badge")).ToContainTextAsync("Invalid PIN");
        Assert.Equal(new[] { "1234" }, host.Users.Attempts.ToArray());
    }

    [Fact]
    public async Task Keyboard_mouse_backspace_clear_and_invalid_retry_keep_exact_input()
    {
        await using var host = await PinHost.StartAsync();
        var page = host.Page;
        var input = page.Locator(".hidden-pin-input");
        await page.Keyboard.TypeAsync("12");
        await Assertions.Expect(input).ToHaveValueAsync("12");
        await Assertions.Expect(page.Locator(".pin-dot.filled")).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Button, new() { Name = "3", Exact = true }).ClickAsync();
        await Assertions.Expect(input).ToHaveValueAsync("123");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Backspace");
        await Assertions.Expect(input).ToHaveValueAsync("12");
        await Assertions.Expect(page.Locator(".pin-dot.filled")).ToHaveCountAsync(2);
        await page.Locator(".backspace-btn").ClickAsync();
        await Assertions.Expect(input).ToHaveValueAsync("1");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("9");
        await Assertions.Expect(input).ToHaveValueAsync("19");
        await page.Locator(".clear-btn").ClickAsync();
        await Assertions.Expect(input).ToHaveValueAsync("");
        await Assertions.Expect(input).ToBeFocusedAsync();
        // Empty actions must restore focus too, even when PIN state does not change.
        await page.Locator(".clear-btn").ClickAsync();
        await Assertions.Expect(input).ToBeFocusedAsync();
        await page.Locator(".backspace-btn").ClickAsync();
        await Assertions.Expect(input).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("56");
        await Assertions.Expect(input).ToHaveValueAsync("56");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(input).ToHaveValueAsync("");
        await page.Keyboard.TypeAsync("987");
        await Assertions.Expect(input).ToHaveValueAsync("987");
        await page.GetByRole(AriaRole.Button, new() { Name = "6", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".pos-error-badge")).ToContainTextAsync("Invalid PIN");
        await Assertions.Expect(input).ToHaveValueAsync("");
        await Assertions.Expect(input).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("4321");
        await Assertions.Expect(page.Locator(".pos-error-badge")).ToContainTextAsync("Invalid PIN");
        await Assertions.Expect(input).ToHaveValueAsync("");
        Assert.Equal(new[] { "9876", "4321" }, host.Users.Attempts.ToArray());
    }

    [Fact]
    public async Task Native_delete_and_nondigits_do_not_desynchronize_dots_or_duplicate_submission()
    {
        await using var host = await PinHost.StartAsync();
        var page = host.Page;
        var input = page.Locator(".hidden-pin-input");
        await page.Keyboard.TypeAsync("x");
        await Assertions.Expect(input).ToHaveValueAsync("");
        await page.Keyboard.TypeAsync("123");
        await Assertions.Expect(input).ToHaveValueAsync("123");
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("Delete");
        await Assertions.Expect(input).ToHaveValueAsync("23");
        await Assertions.Expect(page.Locator(".pin-dot.filled")).ToHaveCountAsync(2);
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.TypeAsync("45");
        await Assertions.Expect(page.Locator(".pos-error-badge")).ToContainTextAsync("Invalid PIN");
        Assert.Equal(new[] { "2345" }, host.Users.Attempts.ToArray());
    }

    [Fact]
    public async Task Valid_pin_resolves_outlet_and_lock_clears_terminal()
    {
        await using var host = await PinHost.StartAsync();
        host.Users.Valid = true;
        await host.Page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(host.Page.Locator(".terminal-identity")).ToHaveTextAsync("BR01 • Counter A");
        Assert.True(host.Session.IsAuthenticated);
        Assert.Equal("synthetic-token", host.Http.DefaultRequestHeaders.Authorization?.Parameter);
        await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Lock Terminal" })).ToHaveCountAsync(1);
        await host.Page.GetByRole(AriaRole.Button, new() { Name = "Lock Terminal" }).ClickAsync();
        await Assertions.Expect(host.Page.Locator(".hidden-pin-input")).ToBeFocusedAsync();
        Assert.False(host.Session.IsAuthenticated);
        Assert.Null(host.Session.Outlet);
        Assert.Null(host.Session.Token);
        Assert.Null(host.Http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("missing", "This IP address is not listed")]
    [InlineData("failure", "Server error")]
    [InlineData("inactive", "This outlet is inactive")]
    [InlineData("address", "Cannot determine a unique LAN IP")]
    public async Task Outlet_failures_stay_on_login_without_session_or_bearer(string mode, string message)
    {
        await using var host = await PinHost.StartAsync();
        host.Users.Valid = true;
        host.Outlets.Mode = mode;
        host.Address.Fail = mode == "address";
        await host.Page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(host.Page.Locator(".pos-error-badge")).ToContainTextAsync(message);
        await Assertions.Expect(host.Page.Locator(".hidden-pin-input")).ToBeFocusedAsync();
        Assert.EndsWith("/login", host.Page.Url);
        Assert.False(host.Session.IsAuthenticated);
        Assert.Null(host.Session.Token);
        Assert.Null(host.Http.DefaultRequestHeaders.Authorization);
        Assert.Equal(mode == "address" ? 0 : 1, host.Outlets.Calls);
    }

    [Fact]
    public async Task Invalid_pin_never_resolves_device_or_outlet()
    {
        await using var host = await PinHost.StartAsync();
        await host.Page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(host.Page.Locator(".pos-error-badge")).ToContainTextAsync("Invalid PIN");
        Assert.Equal(0, host.Address.Calls);
        Assert.Equal(0, host.Outlets.Calls);
    }

    private sealed class PinHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly IPlaywright playwright;
        private readonly IBrowser browser;
        public IPage Page { get; }
        public SyntheticUsers Users { get; }
        public SyntheticOutlets Outlets => app.Services.GetRequiredService<SyntheticOutlets>();
        public SyntheticAddress Address => app.Services.GetRequiredService<SyntheticAddress>();
        public PosSessionService Session => app.Services.GetRequiredService<PosSessionService>();
        public HttpClient Http => app.Services.GetRequiredService<HttpClient>();

        private PinHost(WebApplication app, IPlaywright playwright, IBrowser browser, IPage page, SyntheticUsers users)
            => (this.app, this.playwright, this.browser, Page, Users) = (app, playwright, browser, page, users);

        public static async Task<PinHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ClientHost.App).Assembly.GetName().Name,
                EnvironmentName = "Development"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var users = new SyntheticUsers();
            builder.Services.AddSingleton<IUserService>(users);
            builder.Services.AddSingleton<SyntheticAddress>();
            builder.Services.AddSingleton<ITerminalAddressProvider>(sp => sp.GetRequiredService<SyntheticAddress>());
            builder.Services.AddSingleton<SyntheticOutlets>();
            builder.Services.AddSingleton<IOutletService>(sp => sp.GetRequiredService<SyntheticOutlets>());
            builder.Services.AddSingleton<PosSessionService>();
            builder.Services.AddSingleton(new HttpClient());
            var app = builder.Build();
            app.UseAntiforgery();
            app.MapStaticAssets();
            app.MapRazorComponents<ClientHost.App>().AddInteractiveServerRenderMode();
            await app.StartAsync();
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var host = new PinHost(app, playwright, browser, page, users);
            try
            {
                page.SetDefaultTimeout(10000);
                await page.GotoAsync(app.Urls.Single() + "/login");
                await Assertions.Expect(page.Locator(".hidden-pin-input")).ToBeFocusedAsync();
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await browser.DisposeAsync();
            playwright.Dispose();
            await app.DisposeAsync();
        }
    }

    internal sealed class SyntheticAddress : ITerminalAddressProvider
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<string> GetAddressAsync()
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Cannot determine a unique LAN IP");
            return Task.FromResult("192.168.1.25");
        }
    }
    internal sealed class SyntheticOutlets : IOutletService
    {
        public string Mode { get; set; } = "found";
        public string? BusinessType { get; set; } = "FNB";
        public int BranchId { get; set; } = 1;
        public int Calls { get; private set; }
        public Task<TerminalOutletDto?> ResolveTerminalAsync(string ipAddress, string token)
        {
            Calls++;
            if (Mode == "missing") return Task.FromResult<TerminalOutletDto?>(null);
            if (Mode == "failure") throw new HttpRequestException("Synthetic outage");
            if (Mode == "inactive") throw new InvalidOperationException("This outlet is inactive");
            Assert.Equal("192.168.1.25", ipAddress);
            Assert.Equal("synthetic-token", token);
            return Task.FromResult<TerminalOutletDto?>(new() { BranchId = BranchId, BranchCode = "BR01", OutletName = "Counter A", BusinessTypeId = BusinessType == null ? null : 7, BusinessType = BusinessType });
        }
    }
    internal sealed class SyntheticUsers : IUserService
    {
        public bool Valid { get; set; }
        public string Username { get; set; } = "Test cashier";
        public ConcurrentQueue<string> Attempts { get; } = new();
        public Task<(string Token, string Username, string Role)?> PinLoginAsync(string pinCode)
        {
            Attempts.Enqueue(pinCode);
            return Task.FromResult<(string, string, string)?>(Valid ? ("synthetic-token", Username, "Cashier") : null);
        }
        public Task<List<UserDto>> GetUsersAsync() => throw new NotSupportedException();
        public Task<(bool Success, string? ErrorMessage)> CreateUserAsync(CreateUserDto user) => throw new NotSupportedException();
        public Task<bool> DeleteUserAsync(int id) => throw new NotSupportedException();
    }
}
