using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static Task TestZReadingFormatter()
    {
        var report = new ZReadingReportDto
        {
            Id = Guid.NewGuid(), ZNumber = 7, BusinessDate = new DateOnly(2026, 10, 29),
            BusinessName = "Cafe <script>alert(1)</script>", BranchName = "Scratch", TerminalName = "POS1",
            PeriodStartUtc = new DateTime(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc = new DateTime(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc),
            GeneratedAt = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc),
            GeneratedBy = "manager", GrossSales = 1500, PreviousAccumulatedSales = 1000, PresentAccumulatedSales = 2500,
            NetSales = 1450, Returns = 50,
            Products = [new() { ProductName = "Beef <b>test</b>", Quantity = 1.5m, Value = 1500 }],
            ReturnProducts = [new() { ProductName = "Rice", Quantity = 1, Value = 50 }],
            Cashiers = [new() { CashierName = "Alice", GrossSales = 1500, Returns = 50, NetSales = 1450, ItemQuantity = 1.5m }],
            Payments = [new() { PaymentMethod = "GCash", PaymentCount = 2, Collections = 1500, RefundCount = 1, Refunds = 50, NetCollections = 1450 }],
            Shifts = [new() { ShiftId = 1, CashierId = "Alice", OpeningFloat = 100, ExpectedBalance = 200, ActualCountedBalance = 200 }]
        };
        var html = ZReadingHtmlFormatter.Format(report, true);
        Assert(html.Contains("REPRINT") && html.Contains("Z-READING REPORT"), "Saved reprint must identify report and copy.");
        Assert(html.Contains("&lt;script&gt;") && !html.Contains("<script>"), "Report must HTML encode historical user data.");
        Assert(html.Contains("1.5") && html.Contains("-PHP 50.00"), "Decimal quantities and negative refund detail must render.");
        Assert(html.Contains("2026-10-29 04:00") && html.Contains("Asia/Manila"), "Period formatting must not depend on host timezone.");
        Assert(html.Contains("Not configured") && html.Contains("Payment entries"), "Never invent reset counter; define payment count semantics.");
        Assert(html.Contains("Cashier") && html.Contains("Alice") && html.Contains("Opening float"), "Cashier and per-shift drawer details must render.");
        Assert(!html.Contains(".Append(") && !html.Contains("https://"), "No template leakage or online assets.");
        Assert(report.ZNumber == 7 && report.NetSales == 1450, "Formatting cannot mutate report state.");
        return Task.CompletedTask;
    }
}
