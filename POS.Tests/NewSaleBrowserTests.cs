using System.Collections.Concurrent;
using CurrieTechnologies.Razor.SweetAlert2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using POS.Client.Services;
using POS.Shared.Interfaces;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public sealed class NewSaleBrowserTests
{
    [Fact]
    public async Task NavMenu_is_togglable_and_has_all_requested_pos_links()
    {
        await using var host = await SaleHost.StartAsync();
        var page = host.Page;

        // Login first
        host.Users.Valid = true;
        await page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(page.Locator(".terminal-identity")).ToHaveTextAsync("BR01 • Counter A");

        // Open menu via hamburger button
        await page.Locator(".hamburger-btn").ClickAsync();
        await page.WaitForSelectorAsync(".nav-item .nav-link");

        // Verify requested links in NavMenu
        var navLinks = page.Locator(".nav-item .nav-link");
        await Assertions.Expect(navLinks).ToContainTextAsync(new[]
        {
            "Dashboard",
            "New Sale",
            "Transaction",
            "Held Sales",
            "Returns",
            "Cash Drawer",
            "My Shift"
        });

        // Verify hamburger button exists and toggles the menu
        var toggler = page.Locator(".app-header-bar .hamburger-btn");
        await Assertions.Expect(toggler).ToBeVisibleAsync();
        var navScrollable = page.Locator(".nav-scrollable");
        await Assertions.Expect(navScrollable).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("collapse"));

        // Click toggle to collapse
        await toggler.ClickAsync();
        await Assertions.Expect(page.Locator(".sidebar")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".nav-backdrop")).ToHaveCountAsync(0);

        // Click toggle again to expand
        await toggler.ClickAsync();
        await Assertions.Expect(navScrollable).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("collapse"));
    }

    [Fact]
    public async Task NewSale_displays_categories_filters_products_and_searches()
    {
        await using var host = await SaleHost.StartAsync();
        var page = host.Page;

        // Login
        host.Users.Valid = true;
        await page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(page.Locator(".terminal-identity")).ToHaveTextAsync("BR01 • Counter A");

        // Open menu and navigate to New Sale
        await page.Locator(".hamburger-btn").ClickAsync();
        await page.WaitForSelectorAsync(".nav-item .nav-link");
        await page.GetByRole(AriaRole.Link, new() { Name = "New Sale" }).ClickAsync();
        await Assertions.Expect(page.Locator(".sidebar")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".category-pill")).ToContainTextAsync(new[] { "All Categories", "Beverages", "Food" });

        // Initially all 3 products are visible
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(3);

        // Filter by Beverages category
        await page.GetByRole(AriaRole.Button, new() { Name = "Beverages", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".product-name")).ToContainTextAsync(new[] { "Iced Caramel Macchiato", "Cold Brew Coffee" });

        // Filter by Food category
        await page.GetByRole(AriaRole.Button, new() { Name = "Food", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".product-name")).ToContainTextAsync(new[] { "Blueberry Muffin" });

        // Reset to All Categories
        await page.GetByRole(AriaRole.Button, new() { Name = "All Categories" }).ClickAsync();
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(3);

        // Search for Macchiato
        var searchInput = page.Locator(".search-input");
        await searchInput.FillAsync("Cold");
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".product-name")).ToHaveTextAsync("Cold Brew Coffee");

        // Clear search
        await page.Locator(".clear-search-btn").ClickAsync();
        await Assertions.Expect(page.Locator(".product-card")).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task NewSale_cart_adds_items_adjusts_quantities_and_proceeds_to_payment()
    {
        await using var host = await SaleHost.StartAsync();
        var page = host.Page;

        // Login
        host.Users.Valid = true;
        await page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(page.Locator(".terminal-identity")).ToHaveTextAsync("BR01 • Counter A");

        // Open menu and navigate to New Sale
        await page.Locator(".hamburger-btn").ClickAsync();
        await page.WaitForSelectorAsync(".nav-item .nav-link");
        await page.GetByRole(AriaRole.Link, new() { Name = "New Sale" }).ClickAsync();
        await Assertions.Expect(page.Locator(".sidebar")).ToHaveCountAsync(0);

        // Empty cart check
        await Assertions.Expect(page.Locator(".empty-cart")).ToBeVisibleAsync();
        var proceedBtn = page.Locator(".btn-proceed-payment");
        await Assertions.Expect(proceedBtn).ToBeDisabledAsync();

        // Add "Iced Caramel Macchiato" (₱150.00) to cart
        await page.Locator(".product-card").Filter(new() { HasText = "Iced Caramel Macchiato" }).ClickAsync();
        await Assertions.Expect(page.Locator(".cart-item-row")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".cart-item-name")).ToHaveTextAsync("Iced Caramel Macchiato");
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("1");
        await Assertions.Expect(page.Locator(".cart-item-total")).ToHaveTextAsync("₱150.00");
        await Assertions.Expect(proceedBtn).ToBeEnabledAsync();
        await page.GetByTitle("Increase", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("2");
        await page.GetByTitle("Decrease", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("1");
        await proceedBtn.ClickAsync();
        await Assertions.Expect(page.Locator(".swal2-popup")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".swal2-html-container")).ToContainTextAsync("150.00");
        await page.Locator(".swal2-confirm").ClickAsync();
        // Payment remains a preview, not a persisted sale.
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("1");
        await page.Locator(".clear-cart-btn").ClickAsync();
        await page.Locator(".swal2-confirm").ClickAsync();
        await Assertions.Expect(page.Locator(".empty-cart")).ToBeVisibleAsync();
    }

    private static string FindRepository()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PROJECT_CONTEXT.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository not found");
    }

    private static async Task OpenSale(SaleHost host)
    {
        host.Users.Valid = true;
        await host.Page.Keyboard.TypeAsync("1234");
        await Assertions.Expect(host.Page.Locator(".terminal-identity")).ToBeVisibleAsync();
        await host.Page.Locator(".hamburger-btn").ClickAsync();
        await host.Page.GetByRole(AriaRole.Link, new() { Name = "New Sale" }).ClickAsync();
        await Assertions.Expect(host.Page.Locator(".sidebar")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData(null, "not configured")]
    [InlineData("SERVICE", "not supported")]
    [InlineData("HYBRID", "not supported")]
    public async Task Unconfigured_branch_does_not_choose_a_sales_workflow(string? type, string message)
    {
        await using var host = await SaleHost.StartAsync(type);
        await OpenSale(host);
        await Assertions.Expect(host.Page.Locator(".sale-unavailable")).ToContainTextAsync(message);
        await Assertions.Expect(host.Page.Locator(".search-input, .product-card, .order-section")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Retail_scanner_preserves_zeros_and_confirms_selected_quantity_once()
    {
        await using var host = await SaleHost.StartAsync("RETAIL");
        await OpenSale(host);
        var page = host.Page;
        Assert.Equal("RETAIL", host.Session.Outlet?.BusinessType);
        Assert.Equal(7, host.Session.Outlet?.BusinessTypeId);
        await Assertions.Expect(page.Locator(".category-pill, .products-grid, .product-card")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".search-input")).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("001234");
        await Assertions.Expect(page.Locator(".retail-result")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".swal2-popup")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".cart-item-row")).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".swal2-popup")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".swal2-input")).ToBeFocusedAsync();
        await page.Locator(".swal2-input").FillAsync("3");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("3");
        await Assertions.Expect(page.Locator(".cart-item-total")).ToHaveTextAsync("₱450.00");
        await Assertions.Expect(page.Locator(".search-input")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator(".search-input")).ToHaveValueAsync("");
    }

    [Theory]
    [InlineData("1234", "No product")]
    [InlineData("DUP", "Multiple products")]
    [InlineData("missing", "No product")]
    public async Task Retail_missing_or_duplicate_barcode_never_changes_cart(string barcode, string message)
    {
        await using var host = await SaleHost.StartAsync("RETAIL");
        await OpenSale(host);
        await host.Page.Keyboard.TypeAsync(barcode);
        await host.Page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(host.Page.Locator(".scan-error")).ToContainTextAsync(message);
        await Assertions.Expect(host.Page.Locator(".cart-item-row, .swal2-popup")).ToHaveCountAsync(0);
        await Assertions.Expect(host.Page.Locator(".search-input")).ToBeFocusedAsync();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2147483648")]
    public async Task Retail_invalid_quantity_stays_in_dialog_without_cart_mutation(string quantity)
    {
        await using var host = await SaleHost.StartAsync("RETAIL");
        await OpenSale(host);
        await host.Page.Keyboard.TypeAsync("001234");
        await host.Page.Keyboard.PressAsync("Enter");
        await host.Page.Locator(".swal2-input").FillAsync(quantity);
        await host.Page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(host.Page.Locator(".swal2-validation-message")).ToContainTextAsync("positive whole number");
        await Assertions.Expect(host.Page.Locator(".cart-item-row")).ToHaveCountAsync(0);
        await host.Page.Locator(".swal2-cancel").ClickAsync();
        await Assertions.Expect(host.Page.Locator(".search-input")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Retail_search_selection_keyboard_cancel_and_duplicate_events_are_safe()
    {
        await using var host = await SaleHost.StartAsync("RETAIL");
        await OpenSale(host);
        var page = host.Page;
        await page.Keyboard.TypeAsync("COF-1");
        await Assertions.Expect(page.Locator(".retail-result")).ToHaveCountAsync(1);
        await page.Locator(".retail-result").PressAsync("Enter");
        await Assertions.Expect(page.Locator(".swal2-input")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".cart-item-row")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".search-input")).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("Macchiato");
        await page.Locator(".retail-result").DblClickAsync();
        await Assertions.Expect(page.Locator(".swal2-popup")).ToHaveCountAsync(1);
        await page.Locator(".swal2-input").FillAsync("2");
        await page.Locator(".swal2-confirm").DblClickAsync();
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("2");
        await Assertions.Expect(page.Locator(".search-input")).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("001234");
        // Two Enter events queued by a keyboard-wedge scanner must not open two dialogs.
        await page.Locator(".search-input").EvaluateAsync("e => { e.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true})); e.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter', bubbles:true})); }");
        await Assertions.Expect(page.Locator(".swal2-popup")).ToHaveCountAsync(1);
        await page.Locator(".swal2-cancel").ClickAsync();
        await Assertions.Expect(page.Locator(".search-input")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("2");
    }

    [Fact]
    public async Task Lock_and_relogin_different_branch_and_cashier_cannot_reuse_cart()
    {
        await using var host = await SaleHost.StartAsync();
        await OpenSale(host);
        await host.Page.Locator(".product-card").First.ClickAsync();
        await Assertions.Expect(host.Page.Locator(".qty-display")).ToHaveTextAsync("1");
        await host.Page.GetByRole(AriaRole.Button, new() { Name = "Lock Terminal" }).ClickAsync();
        await Assertions.Expect(host.Page.Locator(".hidden-pin-input")).ToBeFocusedAsync();
        host.Outlets.BranchId = 2;
        host.Outlets.BusinessType = "RETAIL";
        host.Users.Username = "Other cashier";
        await OpenSale(host);
        Assert.Equal(2, host.Session.Outlet?.BranchId);
        Assert.Equal("Other cashier", host.Session.Username);
        await Assertions.Expect(host.Page.Locator(".empty-cart")).ToBeVisibleAsync();
        await Assertions.Expect(host.Page.Locator(".category-pill")).ToHaveCountAsync(0);
    }

    private sealed class SaleHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly IPlaywright playwright;
        private readonly IBrowser browser;
        public IPage Page { get; }
        public ClientPinBrowserTests.SyntheticUsers Users { get; }
        public PosSessionService Session => app.Services.GetRequiredService<PosSessionService>();
        public ClientPinBrowserTests.SyntheticOutlets Outlets => app.Services.GetRequiredService<ClientPinBrowserTests.SyntheticOutlets>();

        private SaleHost(WebApplication app, IPlaywright playwright, IBrowser browser, IPage page, ClientPinBrowserTests.SyntheticUsers users)
            => (this.app, this.playwright, this.browser, Page, Users) = (app, playwright, browser, page, users);

        public static async Task<SaleHost> StartAsync(string? businessType = "FNB")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ClientHost.App).Assembly.GetName().Name,
                EnvironmentName = "Development"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var users = new ClientPinBrowserTests.SyntheticUsers();
            builder.Services.AddSingleton<IUserService>(users);
            builder.Services.AddSingleton<ClientPinBrowserTests.SyntheticAddress>();
            builder.Services.AddSingleton<ITerminalAddressProvider>(sp => sp.GetRequiredService<ClientPinBrowserTests.SyntheticAddress>());
            builder.Services.AddSingleton(new ClientPinBrowserTests.SyntheticOutlets { BusinessType = businessType });
            builder.Services.AddSingleton<IOutletService>(sp => sp.GetRequiredService<ClientPinBrowserTests.SyntheticOutlets>());
            builder.Services.AddSingleton<SyntheticCategories>();
            builder.Services.AddSingleton<ICategoryService>(sp => sp.GetRequiredService<SyntheticCategories>());
            builder.Services.AddSingleton<SyntheticProducts>();
            builder.Services.AddSingleton<IProductService>(sp => sp.GetRequiredService<SyntheticProducts>());
            builder.Services.AddSingleton<PosSessionService>();
            builder.Services.AddScoped<CartService>();
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddSweetAlert2();

            var app = builder.Build();
            app.UseAntiforgery();
            var repo = FindRepository();
            app.MapGet("/client-bootstrap.css", () => Results.File(Path.Combine(repo, "POS.Client/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css"), "text/css"));
            app.MapGet("/client-app.css", () => Results.File(Path.Combine(repo, "POS.Client/wwwroot/app.css"), "text/css"));
            app.MapStaticAssets();
            app.MapRazorComponents<ClientHost.App>().AddInteractiveServerRenderMode();
            await app.StartAsync();

            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var host = new SaleHost(app, playwright, browser, page, users);
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
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class SyntheticCategories : ICategoryService
    {
        public Task<List<CategoryDto>> GetCategoriesAsync() => Task.FromResult(new List<CategoryDto>
        {
            new() { CategoryId = 1, Name = "Beverages", IsActive = true },
            new() { CategoryId = 2, Name = "Food", IsActive = true }
        });
        public Task<CategoryDto> CreateCategoryAsync(CategoryDto category) => Task.FromResult(category);
        public Task UpdateCategoryAsync(int id, CategoryDto category) => Task.CompletedTask;
        public Task DeleteCategoryAsync(int id) => Task.CompletedTask;
    }

    private sealed class SyntheticProducts : IProductService
    {
        public Task<List<ProductDto>> GetProductsAsync() => Task.FromResult(new List<ProductDto>
        {
            new() { ProductId = 1, Barcode = "001234", SKU = "COF-1", CategoryId = 1, Name = "Iced Caramel Macchiato", Price = 150m, IsActive = true, ImageUrl = "/uploads/products/macchiato.jpg", UnitOfMeasure = "cup" },
            new() { ProductId = 2, Barcode = "DUP", CategoryId = 1, Name = "Cold Brew Coffee", Price = 130m, IsActive = true, ImageUrl = null, UnitOfMeasure = "bottle" },
            new() { ProductId = 3, Barcode = "DUP", CategoryId = 2, Name = "Blueberry Muffin", Price = 95m, IsActive = true, ImageUrl = null, UnitOfMeasure = "piece" }
        });
        public Task<ProductDto> CreateProductAsync(ProductDto product) => Task.FromResult(product);
        public Task UpdateProductAsync(int id, ProductDto product) => Task.CompletedTask;
        public Task DeleteProductAsync(int id) => Task.CompletedTask;
    }
}
