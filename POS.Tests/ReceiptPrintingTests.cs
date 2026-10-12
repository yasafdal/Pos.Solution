using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using POS.Client.Services;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public class ReceiptPrintingTests
{
    [Fact]
    public void Format_includes_persisted_sale_and_encodes_untrusted_text()
    {
        var receipt = SampleReceipt();
        receipt.BranchName = "<script>alert('branch')</script>";
        receipt.Items[0].ProductName = "<img src=x onerror=alert(1)>";
        receipt.Items[0].Modifiers[0].ModifierName = "Pearls & <extra>";
        var html = ReceiptHtmlFormatter.Format(receipt);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("Pearls &amp; &lt;extra&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SALE-000042", html);
        Assert.Contains("PHP 95.00", html);
        Assert.Contains("PHP 100.00", html);
        Assert.Contains("PHP 5.00", html);
        Assert.Contains("Cashier: cashier-1", html);
        Assert.Contains("Terminal: TILL-01", html);
        Assert.Contains("NOT A BIR TAX INVOICE", html);
    }

    [Fact]
    public void Format_uses_invariant_currency_and_rejects_null()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Contains("PHP 95.00", ReceiptHtmlFormatter.Format(SampleReceipt()));
            Assert.Throws<ArgumentNullException>(() => ReceiptHtmlFormatter.Format(null!));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public async Task Receipt_html_renders_to_a_real_80mm_pdf_without_scripts_or_network()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 303, Height = 1000 } });
        var requests = new List<string>();
        page.Request += (_, request) => requests.Add(request.Url);
        await page.SetContentAsync(ReceiptHtmlFormatter.Format(SampleReceipt()));
        Assert.Equal(0, await page.Locator("script,img,iframe").CountAsync());
        Assert.Contains("Milk tea", await page.Locator("body").InnerTextAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        Assert.Empty(requests);
        var directory = Environment.GetEnvironmentVariable("POS_RECEIPT_ARTIFACT_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "receipt-artifacts");
        Directory.CreateDirectory(directory);
        var pdf = await page.PdfAsync(new()
        {
            Path = Path.Combine(directory, "receipt-sample.pdf"),
            Width = "80mm", Height = "200mm", PrintBackground = true,
            Margin = new() { Top = "0", Bottom = "0", Left = "0", Right = "0" }
        });
        Assert.True(pdf.Length > 1000);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 8));
        await File.WriteAllTextAsync(Path.Combine(directory, "receipt-sample.html"), ReceiptHtmlFormatter.Format(SampleReceipt()));
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "receipt-sample.png"), FullPage = true });
    }

    private static ReceiptDto SampleReceipt() => new()
    {
        OrderId = 42, OrderNumber = "SALE-000042", BranchId = 1, BranchName = "Sample Tea Shop",
        BranchCode = "MAIN", BranchAddress = "123 Example Street", BranchPhone = "02 1234 5678",
        OutletId = 7, TerminalName = "TILL-01", CashierId = "cashier-1",
        CreatedAt = new DateTime(2026, 10, 12, 10, 30, 0), PaidAt = new DateTime(2026, 10, 12, 10, 31, 0),
        Subtotal = 100m, DiscountAmount = 5m, TotalAmount = 95m,
        Items = [new() { ProductName = "Milk tea", Quantity = 1, BasePrice = 90m, ModifierTotal = 10m,
            UnitPrice = 100m, LineTotal = 100m, Modifiers = [new() { ModifierName = "Pearls", AdditionalPrice = 10m }] }],
        Payments = [new() { PaymentMethod = "Cash", PaymentStatus = "Completed", Amount = 95m,
            AmountTendered = 100m, ChangeAmount = 5m, PaidAt = new DateTime(2026, 10, 12, 10, 31, 0) }]
    };
}
