using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using POS.Web.Auth;
using Xunit;

namespace POS.Tests;

public class BusinessTypeBrowserTests
{
    [Fact]
    public async Task Business_type_crud_and_branch_assignment_work_in_browser()
    {
        using var api = LoginBrowserTests.CreateApi();
        api.UseKestrel(0);
        using var apiClient = api.CreateClient();
        await LoginBrowserTests.SeedAsync(api);
        using var web = new WebApplicationFactory<CustomAuthStateProvider>().WithWebHostBuilder(builder =>
            builder.UseUrls("http://127.0.0.1:0").UseEnvironment("Development").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = apiClient.BaseAddress!.ToString() })));
        web.UseKestrel(0);
        using var webClient = web.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15000);
        async Task Go(string path) => await page.GotoAsync(new Uri(webClient.BaseAddress!, path).ToString());
        async Task Confirm() => await page.Locator(".swal2-confirm").ClickAsync();
        await Go("/login");
        await page.Locator("input:not([type=hidden]):not([type=password])").FillAsync("test-cashier");
        await page.Locator("input[type=password]").FillAsync(LoginBrowserTests.TestPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Logout" })).ToBeVisibleAsync();
        await Go("/business-type");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add Business Type" }).ClickAsync();
        await page.GetByLabel("Type", new() { Exact = true }).FillAsync(" retail ");
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync("Product sales");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Confirm();
        await Assertions.Expect(page.Locator("tbody")).ToContainTextAsync("RETAIL");
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Business Type" }).ClickAsync();
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync("Edited sales");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Confirm();
        await Assertions.Expect(page.Locator("tbody")).ToContainTextAsync("Edited sales");
        await Go("/branches");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add New Branch" }).ClickAsync();
        await page.Locator("input[placeholder='e.g. Main Branch']").FillAsync("Browser branch");
        await page.GetByLabel("Business Type", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "RETAIL" });
        await page.Locator("button[type=submit]").ClickAsync();
        await Confirm();
        await Assertions.Expect(page.Locator("tbody")).ToContainTextAsync("RETAIL");
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Branch", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Business Type", new() { Exact = true })).Not.ToHaveValueAsync("");
        await page.GetByLabel("Business Type", new() { Exact = true }).SelectOptionAsync("");
        await page.Locator("button[type=submit]").ClickAsync();
        await Confirm();
        await Go("/business-type");
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete Business Type" }).ClickAsync();
        await Confirm();
        await Confirm();
        await Assertions.Expect(page.Locator("tbody")).Not.ToContainTextAsync("RETAIL");
    }
}
