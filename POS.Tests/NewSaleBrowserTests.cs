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
        var paymentDialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(paymentDialog).ToContainTextAsync("150.00");
        await paymentDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        // Cancelling payment preserves the unpaid cart.
        await Assertions.Expect(page.Locator(".qty-display")).ToHaveTextAsync("1");
        await page.Locator(".clear-cart-btn").ClickAsync();
        await page.Locator(".swal2-confirm").ClickAsync();
        await Assertions.Expect(page.Locator(".empty-cart")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Cash_shortcuts_set_tender_change_and_cancel_preserves_cart()
    {
        await using var host = await SaleHost.StartAsync();
        await OpenSale(host);
        var page = host.Page;
        await page.Locator(".product-card").Filter(new() { HasText = "Blueberry Muffin" }).ClickAsync();
        await page.Locator(".btn-proceed-payment").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.Locator("select")).ToHaveCountAsync(0);
        foreach (var amount in new[] { "₱100", "₱500", "₱1,000" })
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = amount, Exact = true })).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "₱500", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.Locator("input[type=number]")).ToHaveValueAsync("500");
        await Assertions.Expect(dialog).ToContainTextAsync("405.00");
        await dialog.Locator("input[type=number]").FillAsync("90");
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Confirm Payment" })).ToBeDisabledAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Exact (₱95.00)", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.Locator("input[type=number]")).ToHaveValueAsync("95");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".cart-item-total")).ToHaveTextAsync("₱95.00");
    }

    [Fact]
    public async Task Modifier_tiles_preserve_customizations_and_enforce_limits()
    {
        await using var host = await SaleHost.StartAsync(withModifiers: true);
        await OpenSale(host);
        var page = host.Page;
        var product = page.Locator(".product-card").Filter(new() { HasText = "Iced Caramel Macchiato" });
        await product.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.Locator("input[type=radio], input[type=checkbox]")).ToHaveCountAsync(0);
        var large = dialog.GetByRole(AriaRole.Button, new() { Name = "Large", Exact = true });
        var pearls = dialog.GetByRole(AriaRole.Button, new() { Name = "Pearls", Exact = true });
        await large.ClickAsync();
        await Assertions.Expect(large).ToHaveAttributeAsync("aria-pressed", "true");
        await pearls.ClickAsync();
        await Assertions.Expect(dialog.Locator(".modal-footer")).ToContainTextAsync("200.00");
        await pearls.ClickAsync();
        await Assertions.Expect(pearls).ToHaveAttributeAsync("aria-pressed", "false");
        await pearls.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Extra Shot", Exact = true }).ClickAsync();
        var syrup = dialog.GetByRole(AriaRole.Button, new() { Name = "Syrup", Exact = true });
        await syrup.ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("at most 2");
        await Assertions.Expect(syrup).ToHaveAttributeAsync("aria-pressed", "false");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Extra Shot", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add to Cart", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("Sugar");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Half Sugar", Exact = true }).PressAsync("Space");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add to Cart", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".cart-item-total")).ToHaveTextAsync("₱200.00");
        await Assertions.Expect(page.Locator(".cart-item-modifiers")).ToContainTextAsync("Large");
        await Assertions.Expect(page.Locator(".cart-item-modifiers")).ToContainTextAsync("Pearls");
        await product.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "No Sugar", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add to Cart", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".cart-item-row")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".cart-item-total")).ToHaveTextAsync(new[] { "₱200.00", "₱150.00" });
        await product.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await product.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".cart-item-row")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task Paid_order_offers_manual_receipt_without_repeating_payment()
    {
        await using var host = await SaleHost.StartAsync();
        await OpenSale(host);
        var page = host.Page;
        await page.Locator(".product-card").Filter(new() { HasText = "Blueberry Muffin" }).ClickAsync();
        await page.Locator(".btn-proceed-payment").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm Payment", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Print Receipt", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Skip Printing", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".empty-cart")).ToBeVisibleAsync();
        Assert.Equal(1, host.Payments.Calls);
        Assert.Equal(0, host.Printer.Calls);
        host.Printer.Fail = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Print Receipt", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Payment Successful — Receipt Not Printed");
        Assert.Equal(1, host.Payments.Calls);
        host.Printer.Fail = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry Print", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Print dialog opened");
        Assert.Equal(2, host.Printer.Calls);
        Assert.Equal(1, host.Payments.Calls);
        await page.GetByRole(AriaRole.Button, new() { Name = "Skip Printing", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".receipt-preview-modal")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Receipt_loading_failure_can_retry_without_another_payment()
    {
        await using var host = await SaleHost.StartAsync();
        host.Receipts.Fail = true;
        await OpenSale(host);
        var page = host.Page;
        await page.Locator(".product-card").Filter(new() { HasText = "Blueberry Muffin" }).ClickAsync();
        await page.Locator(".btn-proceed-payment").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm Payment", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Payment Successful — Receipt Not Printed");
        await Assertions.Expect(page.Locator(".empty-cart")).ToBeVisibleAsync();
        Assert.Equal(1, host.Payments.Calls);
        Assert.Equal(0, host.Printer.Calls);
        host.Receipts.Fail = false;
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry Print", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".receipt-document")).ToContainTextAsync("Blueberry Muffin");
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Print dialog opened");
        Assert.Equal(1, host.Payments.Calls);
        Assert.Equal(1, host.Printer.Calls);
    }

    [Fact]
    public async Task Skipping_receipt_never_calls_printer_or_repeats_payment()
    {
        await using var host = await SaleHost.StartAsync();
        await OpenSale(host);
        var page = host.Page;
        await page.Locator(".product-card").First.ClickAsync();
        await page.Locator(".btn-proceed-payment").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm Payment", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Skip Printing", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".receipt-preview-modal")).ToHaveCountAsync(0);
        Assert.Equal(1, host.Payments.Calls);
        Assert.Equal(0, host.Printer.Calls);
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
        public SyntheticPaymentService Payments => app.Services.GetRequiredService<SyntheticPaymentService>();
        public SyntheticPrinter Printer => app.Services.GetRequiredService<SyntheticPrinter>();
        public SyntheticReceipts Receipts => app.Services.GetRequiredService<SyntheticReceipts>();
        public ClientPinBrowserTests.SyntheticOutlets Outlets => app.Services.GetRequiredService<ClientPinBrowserTests.SyntheticOutlets>();

        private SaleHost(WebApplication app, IPlaywright playwright, IBrowser browser, IPage page, ClientPinBrowserTests.SyntheticUsers users)
            => (this.app, this.playwright, this.browser, Page, Users) = (app, playwright, browser, page, users);

        public static async Task<SaleHost> StartAsync(string? businessType = "FNB", bool withModifiers = false)
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
            builder.Services.AddSingleton(new SyntheticModifierService { Enabled = withModifiers });
            builder.Services.AddSingleton<IModifierService>(sp => sp.GetRequiredService<SyntheticModifierService>());
            builder.Services.AddSingleton<SyntheticOrderService>();
            builder.Services.AddSingleton<IOrderService>(sp => sp.GetRequiredService<SyntheticOrderService>());
            builder.Services.AddSingleton<SyntheticPaymentService>();
            builder.Services.AddSingleton<IPaymentService>(sp => sp.GetRequiredService<SyntheticPaymentService>());
            builder.Services.AddSingleton<SyntheticReceipts>();
            builder.Services.AddSingleton<IReceiptService>(sp => sp.GetRequiredService<SyntheticReceipts>());
            builder.Services.AddSingleton<SyntheticPrinter>();
            builder.Services.AddSingleton<IReceiptPrinter>(sp => sp.GetRequiredService<SyntheticPrinter>());
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
            new() { ProductId = 1, Barcode = "001234", SKU = "COF-1", CategoryId = 1, Name = "Iced Caramel Macchiato", Price = 150m, IsActive = true, ImageUrl = "/uploads/products/macchiato.jpg", UnitOfMeasure = "cup", ProductType = "Standard" },
            new() { ProductId = 2, Barcode = "DUP", CategoryId = 1, Name = "Cold Brew Coffee", Price = 130m, IsActive = true, ImageUrl = null, UnitOfMeasure = "bottle", ProductType = "Standard" },
            new() { ProductId = 3, Barcode = "DUP", CategoryId = 2, Name = "Blueberry Muffin", Price = 95m, IsActive = true, ImageUrl = null, UnitOfMeasure = "piece", ProductType = "Standard" }
        });
        public Task<ProductDto> CreateProductAsync(ProductDto product) => Task.FromResult(product);
        public Task UpdateProductAsync(int id, ProductDto product) => Task.CompletedTask;
        public Task DeleteProductAsync(int id) => Task.CompletedTask;
    }

    private sealed class SyntheticModifierService : IModifierService
    {
        public bool Enabled { get; init; }
        public Task<List<ModifierGroupDto>> GetModifierGroupsAsync() => Task.FromResult(new List<ModifierGroupDto>());
        public Task<ModifierGroupDto?> GetModifierGroupByIdAsync(int id) => Task.FromResult<ModifierGroupDto?>(null);
        public Task<bool> CreateModifierGroupAsync(ModifierGroupDto dto) => Task.FromResult(true);
        public Task<bool> UpdateModifierGroupAsync(int id, ModifierGroupDto dto) => Task.FromResult(true);
        public Task<bool> DeleteModifierGroupAsync(int id) => Task.FromResult(true);
        public Task<List<ModifierOptionDto>> GetOptionsByGroupAsync(int groupId) => Task.FromResult(new List<ModifierOptionDto>());
        public Task<bool> CreateOptionAsync(ModifierOptionDto dto) => Task.FromResult(true);
        public Task<bool> UpdateOptionAsync(int id, ModifierOptionDto dto) => Task.FromResult(true);
        public Task<bool> DeleteOptionAsync(int id) => Task.FromResult(true);
        public Task<List<ProductModifierGroupDto>> GetProductModifierGroupsAsync(int productId) => Task.FromResult(
            !Enabled ? new List<ProductModifierGroupDto>() : new List<ProductModifierGroupDto>
            {
                new() { ModifierGroupId = 1, ModifierGroupName = "Size", IsRequired = true, MinSelections = 1, MaxSelections = 1,
                    Options = new() { new() { ModifierOptionId = 11, Name = "Regular" }, new() { ModifierOptionId = 12, Name = "Large", Price = 30m } } },
                new() { ModifierGroupId = 2, ModifierGroupName = "Add-ons", MinSelections = 0, MaxSelections = 2,
                    Options = new() { new() { ModifierOptionId = 21, Name = "Pearls", Price = 20m }, new() { ModifierOptionId = 22, Name = "Extra Shot", Price = 40m }, new() { ModifierOptionId = 23, Name = "Syrup", Price = 25m } } },
                new() { ModifierGroupId = 3, ModifierGroupName = "Sugar", MinSelections = 1, MaxSelections = 1,
                    Options = new() { new() { ModifierOptionId = 31, Name = "Half Sugar" }, new() { ModifierOptionId = 32, Name = "No Sugar" } } }
            });
        public Task<bool> AssignModifierGroupToProductAsync(int productId, int modifierGroupId, int displayOrder = 0) => Task.FromResult(true);
        public Task<bool> RemoveModifierGroupFromProductAsync(int productId, int modifierGroupId) => Task.FromResult(true);
    }

    private sealed class SyntheticOrderService : IOrderService
    {
        public Task<OrderResponseDto?> CreateOrderAsync(OrderCreateDto dto)
        {
            var items = dto.Items.Select((i, index) =>
            {
                var modifierTotal = i.Modifiers.Sum(m => m.AdditionalPrice);
                var unitPrice = i.BasePrice + modifierTotal;
                return new OrderItemResponseDto
                {
                    OrderItemId = index + 1,
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    BasePrice = i.BasePrice,
                    ModifierTotal = modifierTotal,
                    UnitPrice = unitPrice,
                    LineTotal = unitPrice * i.Quantity
                };
            }).ToList();

            var subtotal = items.Sum(i => i.LineTotal);
            var total = subtotal - dto.DiscountAmount;
            return Task.FromResult<OrderResponseDto?>(new OrderResponseDto
            {
                OrderId = 1,
                OrderNumber = "TEST-ORDER-0001",
                BranchId = dto.BranchId,
                OutletId = dto.OutletId,
                CashierId = dto.CashierId,
                OrderStatus = "PENDING",
                PaymentStatus = "UNPAID",
                Subtotal = subtotal,
                DiscountAmount = dto.DiscountAmount,
                TotalAmount = total,
                Items = items
            });
        }

        public Task<OrderResponseDto?> GetOrderByIdAsync(long orderId) => Task.FromResult<OrderResponseDto?>(null);
    }

    private sealed class SyntheticReceipts : IReceiptService
    {
        public bool Fail { get; set; }
        public Task<ReceiptDto?> GetReceiptAsync(long orderId, bool isReprint = false)
        {
            if (Fail) throw new HttpRequestException("Test receipt retrieval failure");
            return Task.FromResult<ReceiptDto?>(new ReceiptDto
            {
                OrderId = orderId, OrderNumber = "TEST-ORDER-0001", BranchName = "Test Cafe",
                TerminalName = "Counter A", CashierId = "test-cashier", TotalAmount = 95m, Subtotal = 95m,
                Items = new() { new() { OrderItemId = 1, ProductName = "Blueberry Muffin", Quantity = 1, UnitPrice = 95, LineTotal = 95 } },
                Payments = new() { new() { PaymentStatus = "SUCCESS", PaymentMethod = "CASH", Amount = 95m, AmountTendered = 100m, ChangeAmount = 5m } }
            });
        }
    }

    private sealed class SyntheticPrinter : IReceiptPrinter
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task PrintAsync(ReceiptDto receipt)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Test printer offline");
            return Task.CompletedTask;
        }

        public Task<string> SavePdfAsync(ReceiptDto receipt)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Test printer offline");
            return Task.FromResult("/fake/receipt.pdf");
        }
    }

    private sealed class SyntheticPaymentService : IPaymentService
    {
        public int Calls { get; private set; }
        public Task<List<PaymentMethodDto>> GetPaymentMethodsAsync()
        {
            return Task.FromResult(new List<PaymentMethodDto>
            {
                new() { PaymentMethodId = 1, Code = "CASH", Name = "Cash", RequiresReference = false, IsActive = true, DisplayOrder = 1 },
                new() { PaymentMethodId = 2, Code = "CARD", Name = "Visa / Mastercard", RequiresReference = true, IsActive = true, DisplayOrder = 2 },
                new() { PaymentMethodId = 3, Code = "GCASH", Name = "GCash E-Wallet", RequiresReference = true, IsActive = true, DisplayOrder = 3 },
                new() { PaymentMethodId = 4, Code = "MAYA", Name = "Maya E-Wallet", RequiresReference = true, IsActive = true, DisplayOrder = 4 }
            });
        }
        public Task<PaymentResponseDto?> ProcessPaymentAsync(PaymentProcessDto dto)
        {
            Calls++;
            return Task.FromResult<PaymentResponseDto?>(new PaymentResponseDto
        {
            PaymentId = 1,
            OrderId = dto.OrderId,
            PaymentMethod = dto.PaymentMethod,
            Amount = dto.Amount,
            AmountTendered = dto.AmountTendered,
            ChangeAmount = dto.AmountTendered - dto.Amount,
            PaymentStatus = "SUCCESS"
        });
        }
    }
}
