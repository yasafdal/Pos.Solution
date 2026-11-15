using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public static partial class Program
{
    private static LocalPrinterHardwareConfig CreateReceiptTestPrinterConfig(int paperWidthMm) => new()
    {
        TerminalId = 1,
        PrinterEnabled = true,
        PaperWidthMm = paperWidthMm,
        PrintDensity = "Normal",
        CharacterEncoding = "PC437",
        AutoCutEnabled = true
    };

    private static ReceiptDto CreateBaseMixedReceiptDto(List<OrderItemResponseDto> items, List<PaymentResponseDto>? payments = null)
    {
        decimal subtotal = items.Sum(i => i.LineTotal);
        decimal vatableSales = Math.Round(subtotal / 1.12m, 2, MidpointRounding.AwayFromZero);
        decimal vatAmount = subtotal - vatableSales;

        return new ReceiptDto
        {
            OrderId = 700,
            OrderNumber = "POS01-000700",
            InvoiceNumber = "TXN1-0000700",
            BranchId = 1,
            BranchName = "Main Branch",
            BranchTagline = "Authentic Cebu Lechon",
            BranchCode = "BR-01",
            BranchAddress = "Sta. Rosa, Laguna",
            BranchPhone = "0917-000-0000",
            VatDisplay = true,
            TerminalId = 1,
            TerminalName = "POS1",
            CashierId = "cashier1",
            OrderType = "DINING",
            TableNumber = "4",
            HeadCount = 2,
            CreatedAt = new DateTime(2026, 11, 15, 5, 30, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2026, 11, 15, 5, 30, 0, DateTimeKind.Utc),
            Subtotal = subtotal,
            VatableSales = vatableSales,
            VatAmount = vatAmount,
            TotalAmount = subtotal,
            Items = items,
            Payments = payments ?? new List<PaymentResponseDto>
            {
                new()
                {
                    PaymentMethod = "CASH",
                    Amount = subtotal,
                    AmountTendered = subtotal,
                    ChangeAmount = 0m,
                    PaymentStatus = PaymentStatuses.Paid
                }
            }
        };
    }

    /// <summary>
    /// Decodes ESC/POS control sequences so printable text lines and column widths can be verified directly.
    /// </summary>
    private static List<string> DecodeEscPosLines(byte[] bytes)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            if (b == 0x1B && i + 1 < bytes.Length)
            {
                byte cmd = bytes[i + 1];
                if (cmd == 0x40) // ESC @
                {
                    i += 2;
                    continue;
                }
                if ((cmd == 0x61 || cmd == 0x45 || cmd == 0x64 || cmd == 0x74) && i + 2 < bytes.Length)
                {
                    i += 3;
                    continue;
                }
                if (cmd == 0x70 && i + 4 < bytes.Length) // ESC p m t1 t2
                {
                    i += 5;
                    continue;
                }
            }
            else if (b == 0x1D && i + 1 < bytes.Length)
            {
                byte cmd = bytes[i + 1];
                if (cmd == 0x21 && i + 2 < bytes.Length) // GS ! n
                {
                    i += 3;
                    continue;
                }
                if (cmd == 0x56 && i + 3 < bytes.Length) // GS V B n
                {
                    i += 4;
                    continue;
                }
                if (cmd == 0x28 && i + 4 < bytes.Length && bytes[i + 2] == 0x4B) // GS ( K pL pH ...
                {
                    int len = bytes[i + 3] | (bytes[i + 4] << 8);
                    i += 5 + len;
                    continue;
                }
                if (cmd == 0x76 && i + 7 < bytes.Length && bytes[i + 2] == 0x30) // GS v 0 m xL xH yL yH data
                {
                    int xBytes = bytes[i + 4] | (bytes[i + 5] << 8);
                    int yRows = bytes[i + 6] | (bytes[i + 7] << 8);
                    i += 8 + (xBytes * yRows);
                    continue;
                }
            }

            if (b == (byte)'\n')
            {
                sb.Append('\n');
            }
            else if (b >= 0x20 && b <= 0x7E)
            {
                sb.Append((char)b);
            }

            i++;
        }

        return sb.ToString().Split('\n').ToList();
    }

    private static (string Html, string Esc58, string Esc80, List<string> Lines58, List<string> Lines80) RenderAllFormats(ReceiptDto receipt)
    {
        string html = ReceiptHtmlFormatter.Format(receipt);
        byte[] bytes58 = EscPosReceiptBuilder.BuildReceipt(receipt, CreateReceiptTestPrinterConfig(58));
        byte[] bytes80 = EscPosReceiptBuilder.BuildReceipt(receipt, CreateReceiptTestPrinterConfig(80));
        var lines58 = DecodeEscPosLines(bytes58);
        var lines80 = DecodeEscPosLines(bytes80);
        return (html, string.Join("\n", lines58), string.Join("\n", lines80), lines58, lines80);
    }

    private static void AssertHtmlRow(string html, string label, string value)
    {
        string expected = $"<div class=\"row\"><span class=\"label\">{WebUtility.HtmlEncode(label)}</span><span class=\"amount\">{WebUtility.HtmlEncode(value)}</span></div>";
        Assert(html.Contains(expected, StringComparison.Ordinal),
            $"Expected HTML row '{expected}' not found in receipt HTML.");
    }

    private static void AssertRightAlignedPair(List<string> lines, int expectedCols, string leftPrefix, string rightText)
    {
        string? match = lines.FirstOrDefault(l =>
            l.Length == expectedCols &&
            l.StartsWith(leftPrefix, StringComparison.Ordinal) &&
            l.EndsWith(rightText, StringComparison.Ordinal));
        Assert(match != null,
            $"Expected {expectedCols}-col right-aligned pair starting with '{leftPrefix}' and ending with '{rightText}'.");
    }

    // RCPT-QTY-01: Weight-based product only (+ integer weight, null UOM fallback to kg, and legacy fractional fallback)
    private static Task TestReceiptMixed01_WeightBasedOnlyAndLegacyFallback()
    {
        var item = new OrderItemResponseDto
        {
            OrderItemId = 1,
            ProductId = 20,
            ProductName = "Lechon Bundle",
            Quantity = 0.316m,
            UnitPrice = 950.00m,
            LineTotal = 300.20m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = "kg"
        };

        var receipt = CreateBaseMixedReceiptDto(new() { item });
        var (html, esc58, esc80, lines58, lines80) = RenderAllFormats(receipt);

        Assert(html.Contains("<div class=\"item-subline\">0.316 kg x PHP 950.00/kg</div>", StringComparison.Ordinal),
            "HTML must render weight-based subline '0.316 kg x PHP 950.00/kg'.");
        Assert(esc58.Contains("  0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
               esc80.Contains("  0.316 kg x PHP 950.00/kg", StringComparison.Ordinal),
            "58mm and 80mm ESC/POS must render weight-based subline '0.316 kg x PHP 950.00/kg'.");

        AssertHtmlRow(html, "Total Items", "1");
        AssertHtmlRow(html, "Total Weight", "0.316 kg");
        AssertRightAlignedPair(lines58, 32, "Total Items", "1");
        AssertRightAlignedPair(lines58, 32, "Total Weight", "0.316 kg");
        AssertRightAlignedPair(lines80, 48, "Total Items", "1");
        AssertRightAlignedPair(lines80, 48, "Total Weight", "0.316 kg");
        Assert(!html.Contains("Total Weight (", StringComparison.Ordinal),
            "Single-unit weight summary must use 'Total Weight', not 'Total Weight (kg)'.");

        // Exact integer weight (1.00 kg) with explicit WeightBased PricingMethod & null UOM fallback to "kg"
        var wholeWeightItem = new OrderItemResponseDto
        {
            ProductId = 21,
            ProductName = "Whole Lechon",
            Quantity = 1.00m,
            UnitPrice = 950.00m,
            LineTotal = 950.00m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = null
        };
        var wholeSummary = ReceiptPresentationHelper.SummarizeItems(new[] { wholeWeightItem });
        Assert(ReceiptPresentationHelper.FormatItemSubline(wholeWeightItem) == "1 kg x PHP 950.00/kg",
            "Explicit WeightBased item with null UnitOfMeasure must default display unit to 'kg' even for integer weight 1.");
        Assert(wholeSummary.TotalItemsDisplay == "1" && wholeSummary.WeightTotals.Count == 1 && wholeSummary.WeightTotals[0].FormattedWithUnit == "1 kg",
            "Explicit WeightBased integer weight must count as 1 item and 1 kg Total Weight.");

        // Legacy item with null PricingMethod and non-complimentary fractional quantity + tiny <0.001 formatting
        var legacyFractional = new OrderItemResponseDto
        {
            ProductName = "Legacy Lechon",
            Quantity = 0.0004m,
            UnitPrice = 950m,
            LineTotal = 0.38m,
            PricingMethod = null,
            UnitOfMeasure = null,
            IsComplimentary = false
        };
        Assert(ReceiptPresentationHelper.IsWeightBasedItem(legacyFractional),
            "Non-complimentary fractional item with null PricingMethod must resolve as weight-based.");
        Assert(ReceiptPresentationHelper.FormatItemSubline(legacyFractional) == "<0.001 kg x PHP 950.00/kg",
            "Tiny positive weight must preserve '<0.001 kg' subline.");

        return Task.CompletedTask;
    }

    // RCPT-QTY-02: Regular products only (configured UOM AND null UOM without hardcoded 'pc')
    private static Task TestReceiptMixed02_RegularConfiguredAndNullUom()
    {
        // 1. Configured UOM: 1 pc + 2 box
        var configuredReceipt = CreateBaseMixedReceiptDto(new()
        {
            new OrderItemResponseDto
            {
                OrderItemId = 1,
                ProductId = 30,
                ProductName = "Lechon Paksiw",
                Quantity = 1m,
                UnitPrice = 100.00m,
                LineTotal = 100.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = "pc"
            },
            new OrderItemResponseDto
            {
                OrderItemId = 2,
                ProductId = 31,
                ProductName = "Party Tray",
                Quantity = 2m,
                UnitPrice = 250.00m,
                LineTotal = 500.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = "box"
            }
        });

        var (htmlCfg, esc58Cfg, esc80Cfg, lines58Cfg, lines80Cfg) = RenderAllFormats(configuredReceipt);
        Assert(htmlCfg.Contains("<div class=\"item-subline\">1 pc x PHP 100.00</div>", StringComparison.Ordinal) &&
               htmlCfg.Contains("<div class=\"item-subline\">2 box x PHP 250.00</div>", StringComparison.Ordinal),
            "Configured regular UOMs must appear on HTML sublines without '/unit'.");
        Assert(esc58Cfg.Contains("  1 pc x PHP 100.00", StringComparison.Ordinal) &&
               esc58Cfg.Contains("  2 box x PHP 250.00", StringComparison.Ordinal) &&
               esc80Cfg.Contains("  1 pc x PHP 100.00", StringComparison.Ordinal) &&
               esc80Cfg.Contains("  2 box x PHP 250.00", StringComparison.Ordinal),
            "Configured regular UOMs must appear on 58mm and 80mm ESC/POS sublines.");
        AssertHtmlRow(htmlCfg, "Total Items", "3");
        AssertRightAlignedPair(lines58Cfg, 32, "Total Items", "3");
        AssertRightAlignedPair(lines80Cfg, 48, "Total Items", "3");
        Assert(!htmlCfg.Contains("Total Weight", StringComparison.Ordinal) &&
               !esc58Cfg.Contains("Total Weight", StringComparison.Ordinal) &&
               !esc80Cfg.Contains("Total Weight", StringComparison.Ordinal),
            "Regular-only receipts must NOT render a Total Weight row.");

        // 2. Null / whitespace UOM: must render "1 x PHP 100.00", NEVER hardcoded "1 pc"
        var nullUomReceipt = CreateBaseMixedReceiptDto(new()
        {
            new OrderItemResponseDto
            {
                OrderItemId = 1,
                ProductId = 32,
                ProductName = "Plain Rice",
                Quantity = 1m,
                UnitPrice = 100.00m,
                LineTotal = 100.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = null
            },
            new OrderItemResponseDto
            {
                OrderItemId = 2,
                ProductId = 33,
                ProductName = "Iced Tea",
                Quantity = 2m,
                UnitPrice = 50.00m,
                LineTotal = 100.00m,
                PricingMethod = null,
                UnitOfMeasure = "   "
            }
        });

        var (htmlNull, esc58Null, esc80Null, _, _) = RenderAllFormats(nullUomReceipt);
        Assert(htmlNull.Contains("<div class=\"item-subline\">1 x PHP 100.00</div>", StringComparison.Ordinal) &&
               htmlNull.Contains("<div class=\"item-subline\">2 x PHP 50.00</div>", StringComparison.Ordinal),
            "Null or whitespace UOM regular items must render '<qty> x PHP <price>' in HTML.");
        Assert(esc58Null.Contains("  1 x PHP 100.00", StringComparison.Ordinal) &&
               esc80Null.Contains("  1 x PHP 100.00", StringComparison.Ordinal),
            "Null UOM regular item must render '1 x PHP 100.00' in 58mm and 80mm ESC/POS.");
        Assert(!htmlNull.Contains("1 pc", StringComparison.OrdinalIgnoreCase) &&
               !esc58Null.Contains("1 pc", StringComparison.OrdinalIgnoreCase) &&
               !esc80Null.Contains("1 pc", StringComparison.OrdinalIgnoreCase) &&
               !htmlNull.Contains("2 pc", StringComparison.OrdinalIgnoreCase),
            "Null UOM regular items must NEVER receive a hardcoded 'pc' unit.");
        AssertHtmlRow(htmlNull, "Total Items", "3");
        Assert(!htmlNull.Contains("Total Weight", StringComparison.Ordinal),
            "Null UOM regular-only receipt must NOT render Total Weight.");

        return Task.CompletedTask;
    }

    // RCPT-QTY-03: Mixed weight-based + regular (Total Items: 2, Total Weight: 0.316 kg, never 1.316)
    private static Task TestReceiptMixed03_MixedWeightBasedAndRegular()
    {
        var lechonBundle = new OrderItemResponseDto
        {
            OrderItemId = 1,
            ProductId = 20,
            ProductName = "Lechon Bundle",
            Quantity = 0.316m,
            UnitPrice = 950.00m,
            LineTotal = 300.20m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = "kg"
        };
        var lechonPaksiw = new OrderItemResponseDto
        {
            OrderItemId = 2,
            ProductId = 30,
            ProductName = "Lechon Paksiw",
            Quantity = 1m,
            UnitPrice = 100.00m,
            LineTotal = 100.00m,
            PricingMethod = PricingMethods.Fixed,
            UnitOfMeasure = "pc"
        };

        var receipt = CreateBaseMixedReceiptDto(new() { lechonBundle, lechonPaksiw });
        var (html, esc58, esc80, lines58, lines80) = RenderAllFormats(receipt);

        Assert(html.Contains("0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
               html.Contains("1 pc x PHP 100.00", StringComparison.Ordinal),
            "Mixed HTML receipt must format both weight-based and regular sublines.");
        Assert(esc58.Contains("  0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
               esc58.Contains("  1 pc x PHP 100.00", StringComparison.Ordinal) &&
               esc80.Contains("  0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
               esc80.Contains("  1 pc x PHP 100.00", StringComparison.Ordinal),
            "Mixed 58mm and 80mm ESC/POS receipts must format both sublines.");

        AssertHtmlRow(html, "Total Items", "2");
        AssertHtmlRow(html, "Total Weight", "0.316 kg");
        AssertRightAlignedPair(lines58, 32, "Total Items", "2");
        AssertRightAlignedPair(lines58, 32, "Total Weight", "0.316 kg");
        AssertRightAlignedPair(lines80, 48, "Total Items", "2");
        AssertRightAlignedPair(lines80, 48, "Total Weight", "0.316 kg");

        Assert(!html.Contains("1.316", StringComparison.Ordinal) &&
               !esc58.Contains("1.316", StringComparison.Ordinal) &&
               !esc80.Contains("1.316", StringComparison.Ordinal),
            "Mixed receipt must NEVER sum weight quantity into Total Items (1.316).");

        // Transactions.razor detail modal helper parity
        Assert(ReceiptPresentationHelper.FormatItemSubline(lechonBundle, "₱") == "0.316 kg x ₱950.00/kg",
            "Detail modal Peso subline must match for weight-based item.");
        Assert(ReceiptPresentationHelper.FormatItemSubline(lechonPaksiw, "₱") == "1 pc x ₱100.00",
            "Detail modal Peso subline must match for regular item.");

        return Task.CompletedTask;
    }

    // RCPT-QTY-04: Multiple weight-based products (same unit AND incompatible units never combined)
    private static Task TestReceiptMixed04_MultipleWeightSameAndIncompatibleUnits()
    {
        // 1. Same unit (including case-insensitive/padded "kg" and " KG ")
        var sameUnitReceipt = CreateBaseMixedReceiptDto(new()
        {
            new OrderItemResponseDto
            {
                OrderItemId = 1,
                ProductId = 20,
                ProductName = "Lechon Belly",
                Quantity = 0.316m,
                UnitPrice = 950.00m,
                LineTotal = 300.20m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "kg"
            },
            new OrderItemResponseDto
            {
                OrderItemId = 2,
                ProductId = 21,
                ProductName = "Lechon Ribs",
                Quantity = 0.5m,
                UnitPrice = 900.00m,
                LineTotal = 450.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = " KG "
            }
        });

        var (htmlSame, _, _, lines58Same, lines80Same) = RenderAllFormats(sameUnitReceipt);
        AssertHtmlRow(htmlSame, "Total Items", "2");
        AssertHtmlRow(htmlSame, "Total Weight", "0.816 kg");
        AssertRightAlignedPair(lines58Same, 32, "Total Items", "2");
        AssertRightAlignedPair(lines58Same, 32, "Total Weight", "0.816 kg");
        AssertRightAlignedPair(lines80Same, 48, "Total Items", "2");
        AssertRightAlignedPair(lines80Same, 48, "Total Weight", "0.816 kg");

        // 2. Incompatible units: 0.5 kg + 250 g -> separate rows, NEVER 250.5
        var mixedUnitReceipt = CreateBaseMixedReceiptDto(new()
        {
            new OrderItemResponseDto
            {
                OrderItemId = 1,
                ProductId = 20,
                ProductName = "Lechon Belly",
                Quantity = 0.5m,
                UnitPrice = 950.00m,
                LineTotal = 475.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "kg"
            },
            new OrderItemResponseDto
            {
                OrderItemId = 2,
                ProductId = 22,
                ProductName = "Chicharon",
                Quantity = 250m,
                UnitPrice = 1.20m,
                LineTotal = 300.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "g"
            }
        });

        var (htmlMixed, esc58Mixed, esc80Mixed, lines58Mixed, lines80Mixed) = RenderAllFormats(mixedUnitReceipt);
        AssertHtmlRow(htmlMixed, "Total Items", "2");
        AssertHtmlRow(htmlMixed, "Total Weight (kg)", "0.5 kg");
        AssertHtmlRow(htmlMixed, "Total Weight (g)", "250 g");
        AssertRightAlignedPair(lines58Mixed, 32, "Total Items", "2");
        AssertRightAlignedPair(lines58Mixed, 32, "Total Weight (kg)", "0.5 kg");
        AssertRightAlignedPair(lines58Mixed, 32, "Total Weight (g)", "250 g");
        AssertRightAlignedPair(lines80Mixed, 48, "Total Items", "2");
        AssertRightAlignedPair(lines80Mixed, 48, "Total Weight (kg)", "0.5 kg");
        AssertRightAlignedPair(lines80Mixed, 48, "Total Weight (g)", "250 g");

        Assert(!htmlMixed.Contains("250.5", StringComparison.Ordinal) &&
               !esc58Mixed.Contains("250.5", StringComparison.Ordinal) &&
               !esc80Mixed.Contains("250.5", StringComparison.Ordinal),
            "Incompatible weight units (kg and g) must NEVER be summed into 250.5.");

        return Task.CompletedTask;
    }

    // RCPT-QTY-05: Weight Mode vs Amount Mode (TargetAmount verbatim LineTotal) + unrounded decimal summation
    private static Task TestReceiptMixed05_WeightModeVsAmountModeAndUnroundedSummation()
    {
        var session = CreateOfflineTestSession();
        using var cart = new CartService(session);
        var lechon = new ProductDto
        {
            ProductId = 20,
            CategoryId = 1,
            Name = "Lechon Belly",
            Price = 950.00m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = "kg",
            IsKitchenItem = true,
            IsActive = true
        };

        // Amount Mode: TargetAmount = 300.00, Quantity = 300/950 = 0.315789... -> displays 0.316 kg, LineTotal = 300.00 (not 300.20)
        cart.AddWeightBasedItem(lechon, weight: 300m / 950m, targetAmount: 300.00m);
        // Weight Mode: TargetAmount = null, Quantity = 0.316 -> displays 0.316 kg, LineTotal = Round(0.316 * 950, 2) = 300.20
        cart.AddWeightBasedItem(lechon, weight: 0.316m, targetAmount: null);

        Assert(cart.Items[0].LineTotal == 300.00m && cart.Items[1].LineTotal == 300.20m && cart.Subtotal == 600.20m,
            $"Cart must preserve Amount Mode (300.00) vs Weight Mode (300.20) LineTotals; got {cart.Items[0].LineTotal} and {cart.Items[1].LineTotal}.");

        var amountDto = new OrderItemResponseDto
        {
            OrderItemId = 1,
            ProductId = 20,
            ProductName = "Lechon Belly Amount",
            Quantity = cart.Items[0].Quantity,
            BasePrice = 950.00m,
            UnitPrice = 950.00m,
            LineTotal = cart.Items[0].LineTotal,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = cart.Items[0].UnitOfMeasure,
            TargetAmount = cart.Items[0].TargetAmount
        };
        var weightDto = new OrderItemResponseDto
        {
            OrderItemId = 2,
            ProductId = 20,
            ProductName = "Lechon Belly Weight",
            Quantity = cart.Items[1].Quantity,
            BasePrice = 950.00m,
            UnitPrice = 950.00m,
            LineTotal = cart.Items[1].LineTotal,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = cart.Items[1].UnitOfMeasure,
            TargetAmount = cart.Items[1].TargetAmount
        };

        var receipt = CreateBaseMixedReceiptDto(new() { amountDto, weightDto });
        var (html, _, _, lines58, lines80) = RenderAllFormats(receipt);

        Assert(html.Contains("Lechon Belly Amount</span><span class=\"amount\">PHP 300.00</span>", StringComparison.Ordinal),
            "Amount Mode item must render exact persisted LineTotal PHP 300.00 (not 300.20).");
        Assert(html.Contains("Lechon Belly Weight</span><span class=\"amount\">PHP 300.20</span>", StringComparison.Ordinal),
            "Weight Mode item must render exact persisted LineTotal PHP 300.20.");
        AssertRightAlignedPair(lines58, 32, "Lechon Belly Amount", "PHP 300.00");
        AssertRightAlignedPair(lines58, 32, "Lechon Belly Weight", "PHP 300.20");
        AssertRightAlignedPair(lines80, 48, "Lechon Belly Amount", "PHP 300.00");
        AssertRightAlignedPair(lines80, 48, "Lechon Belly Weight", "PHP 300.20");
        Assert(lines58.Count(l => l == "  0.316 kg x PHP 950.00/kg") == 2,
            "Both Amount Mode and Weight Mode lines must show rounded 0.316 kg x PHP 950.00/kg subline.");

        // Unrounded decimal summation check:
        // Each item = 100 / 950 = 0.1052631578947... kg (individually displays 0.105 kg; 0.105 + 0.105 would be 0.21)
        // Exact sum = 200 / 950 = 0.2105263157894... kg -> rounds to 0.211 kg!
        decimal exactItemQty = 100m / 950m;
        var unroundedItems = new[]
        {
            new OrderItemResponseDto
            {
                ProductId = 20,
                ProductName = "Lechon Mini A",
                Quantity = exactItemQty,
                UnitPrice = 950.00m,
                LineTotal = 100.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "kg",
                TargetAmount = 100.00m
            },
            new OrderItemResponseDto
            {
                ProductId = 20,
                ProductName = "Lechon Mini B",
                Quantity = exactItemQty,
                UnitPrice = 950.00m,
                LineTotal = 100.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "kg",
                TargetAmount = 100.00m
            }
        };

        var prevCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var unroundedSummary = ReceiptPresentationHelper.SummarizeItems(unroundedItems);
            Assert(ReceiptPresentationHelper.FormatItemSubline(unroundedItems[0]) == "0.105 kg x PHP 950.00/kg",
                "Individual 100/950 item must format as '0.105 kg x PHP 950.00/kg' regardless of CurrentCulture.");
            Assert(unroundedSummary.TotalItemsDisplay == "2",
                "Two weight items must yield TotalItemsDisplay == '2'.");
            Assert(unroundedSummary.WeightTotals.Count == 1 &&
                   unroundedSummary.WeightTotals[0].TotalWeight == exactItemQty * 2m &&
                   unroundedSummary.WeightTotals[0].FormattedWithUnit == "0.211 kg",
                $"Exact unrounded summation of 2*(100/950) kg must yield '0.211 kg', got '{unroundedSummary.WeightTotals.FirstOrDefault()?.FormattedWithUnit}'.");
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
        }

        return Task.CompletedTask;
    }

    // RCPT-QTY-06: Split payment (CASH + GCASH) end-to-end through OfflineCheckoutService.ProcessSaleAsync + LoadSavedReceiptFromDbAsync
    private static async Task TestReceiptMixed06_SplitPaymentEndToEndOfflineCheckoutAndRecovery()
    {
        using var scope = new TempLocalDbScope("rcpt_qty06");
        var dayUtc = new DateTime(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc);
        SetDeterministicManilaClock(dayUtc);

        try
        {
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m, lastInvoiceNumber: 0);
            using (var seedLocal = new LocalPosDbContext())
            {
                seedLocal.Products.AddRange(
                    new LocalProduct { ProductId = 20, CategoryId = 1, CategoryName = "Main", Name = "Lechon Belly", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 30, CategoryId = 1, CategoryName = "Main", Name = "Lechon Paksiw", Price = 100m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 31, CategoryId = 1, CategoryName = "Main", Name = "Plain Rice", Price = 35m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = null, TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 25, CategoryId = 1, CategoryName = "Main", Name = "Atchara Cup", Price = 30m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true });
                await seedLocal.SaveChangesAsync();
            }

            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOfflineTestSession(CreateSyntheticJwt("cashier1", "Cashier"), "cashier1", "Cashier");

            var shift = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1000m,
                ClientShiftId = "shift-rcpt-qty06"
            }, "cashier1", offline: true);

            using var cart = new CartService(session);
            cart.AddWeightBasedItem(
                new ProductDto { ProductId = 20, CategoryId = 1, Name = "Lechon Belly", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", IsKitchenItem = true },
                weight: 300m / 950m,
                targetAmount: 300.00m,
                complimentaryItems: new List<CartItem>
                {
                    new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 1m, UnitOfMeasure = "pc", IsKitchenItem = true }
                });
            cart.AddItem(new ProductDto { ProductId = 30, CategoryId = 1, Name = "Lechon Paksiw", Price = 100m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", IsKitchenItem = true }, 1);
            cart.AddItem(new ProductDto { ProductId = 31, CategoryId = 1, Name = "Plain Rice", Price = 35m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = null, IsKitchenItem = true }, 2);

            var splits = new List<PaymentSplitDto>
            {
                new() { PaymentMethod = "CASH", Amount = 250.00m, AmountTendered = 300.00m },
                new() { PaymentMethod = "GCASH", Amount = 220.00m, ReferenceNumber = "GC-SPLIT-316" }
            };

            var createDto = new OrderCreateDto
            {
                ShiftId = shift.ShiftId,
                ClientShiftId = shift.ClientShiftId,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                TableNumber = "T-5",
                HeadCount = 3,
                Splits = splits,
                Items = cart.Items.Select(ci => new OrderItemCreateDto
                {
                    ProductId = ci.ProductId,
                    ProductName = ci.ProductName,
                    ProductShortCode = ci.ProductShortCode,
                    Quantity = ci.Quantity,
                    BasePrice = ci.BasePrice,
                    PricingMethod = ci.PricingMethod,
                    UnitOfMeasure = ci.UnitOfMeasure,
                    TargetAmount = ci.TargetAmount,
                    IsComplimentary = ci.IsComplimentary,
                    ParentProductId = ci.ParentProductId,
                    TaxName = ci.TaxName ?? "VATable",
                    IsKitchenItem = ci.IsKitchenItem,
                    IsBarItem = ci.IsBarItem
                }).ToList()
            };

            OfflineCheckoutResultDto checkoutRes;
            using (var coDb = new LocalPosDbContext())
            {
                checkoutRes = await new OfflineCheckoutService(coDb, session).ProcessSaleAsync(createDto, 470.00m, "SPLIT", null, "cashier1");
            }

            Assert(checkoutRes.Success && checkoutRes.Receipt != null && !string.IsNullOrWhiteSpace(checkoutRes.LocalTransactionId),
                $"Offline split checkout must succeed: {checkoutRes.ErrorMessage}");
            Assert(checkoutRes.ChangeAmount == 50.00m && checkoutRes.CashAmountPaid == 250.00m && checkoutRes.Receipt!.TotalAmount == 470.00m,
                $"Expected Total=470.00, CashPaid=250.00, Change=50.00; got Total={checkoutRes.Receipt?.TotalAmount}, Change={checkoutRes.ChangeAmount}.");

            var restartSession = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier");
            ReceiptDto? savedReceipt;
            using (var readDb = new LocalPosDbContext())
            {
                savedReceipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(readDb, checkoutRes.LocalTransactionId!, restartSession);
            }

            Assert(savedReceipt != null, "LoadSavedReceiptFromDbAsync must reconstruct the split-payment receipt.");
            Assert(savedReceipt!.Items.Count == 4 &&
                   savedReceipt.Items[0].UnitOfMeasure == "kg" && savedReceipt.Items[0].PricingMethod == PricingMethods.WeightBased && savedReceipt.Items[0].LineTotal == 300.00m &&
                   savedReceipt.Items[1].IsComplimentary && savedReceipt.Items[1].UnitOfMeasure == "pc" && savedReceipt.Items[1].LineTotal == 0m &&
                   savedReceipt.Items[2].UnitOfMeasure == "pc" && savedReceipt.Items[2].LineTotal == 100.00m &&
                   savedReceipt.Items[3].UnitOfMeasure == null && savedReceipt.Items[3].LineTotal == 70.00m,
                "Saved receipt items must match checkout items including UnitOfMeasure, null UOM, PricingMethod, IsComplimentary, and verbatim LineTotals.");
            Assert(savedReceipt.Payments.Count == 2 &&
                   savedReceipt.Payments[0].PaymentMethod == "CASH" && savedReceipt.Payments[0].Amount == 250.00m && savedReceipt.Payments[0].ChangeAmount == 50.00m &&
                   savedReceipt.Payments[1].PaymentMethod == "GCASH" && savedReceipt.Payments[1].Amount == 220.00m && savedReceipt.Payments[1].ReferenceNumber == "GC-SPLIT-316",
                "Saved receipt must preserve CASH + GCASH split payments, change, and reference number.");

            var (htmlPreview, esc58Preview, esc80Preview, _, _) = RenderAllFormats(checkoutRes.Receipt!);
            var (htmlSaved, esc58Saved, esc80Saved, _, _) = RenderAllFormats(savedReceipt);

            Assert(htmlPreview == htmlSaved,
                "Preview HTML from ProcessSaleAsync must match HTML from LoadSavedReceiptFromDbAsync verbatim.");
            Assert(esc58Preview == esc58Saved && esc80Preview == esc80Saved,
                "ESC/POS 58mm and 80mm output from ProcessSaleAsync must match LoadSavedReceiptFromDbAsync verbatim.");

            Assert(htmlPreview.Contains("0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
                   htmlPreview.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   htmlPreview.Contains("1 pc x PHP 0.00", StringComparison.Ordinal) &&
                   htmlPreview.Contains("1 pc x PHP 100.00", StringComparison.Ordinal) &&
                   htmlPreview.Contains("2 x PHP 35.00", StringComparison.Ordinal) &&
                   htmlPreview.Contains("Payment: CASH", StringComparison.Ordinal) &&
                   htmlPreview.Contains("Payment: GCASH", StringComparison.Ordinal) &&
                   htmlPreview.Contains("Reference: GC-SPLIT-316", StringComparison.Ordinal),
                "Preview HTML must render all split payment details, exact LineTotals, and UOM/null-UOM sublines.");
            Assert(!htmlPreview.Contains("2 pc x PHP 35.00", StringComparison.Ordinal),
                "Null UOM item in split sale must NOT get hardcoded 'pc'.");
            AssertHtmlRow(htmlPreview, "Total Items", "5");
            AssertHtmlRow(htmlPreview, "Total Weight", "0.316 kg");

            Assert(esc80Preview.Contains("  0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
                   esc80Preview.Contains("  2 x PHP 35.00", StringComparison.Ordinal) &&
                   esc80Preview.Contains("Payment: CASH", StringComparison.Ordinal) &&
                   esc80Preview.Contains("Payment: GCASH", StringComparison.Ordinal) &&
                   esc80Preview.Contains("Reference: GC-SPLIT-316", StringComparison.Ordinal) &&
                   esc80Preview.Contains("Total Items", StringComparison.Ordinal) &&
                   esc80Preview.Contains("0.316 kg", StringComparison.Ordinal),
                "ESC/POS output must match HTML preview values across items, split payments, Total Items (5), and Total Weight (0.316 kg).");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // RCPT-QTY-07: Complimentary items + long product names on BOTH 58mm (32 cols) and 80mm (48 cols) ESC/POS and HTML
    private static Task TestReceiptMixed07_ComplimentaryLongNamesWrappingAndAlignment58And80Mm()
    {
        var items = new List<OrderItemResponseDto>
        {
            new()
            {
                OrderItemId = 1,
                ProductId = 20,
                ProductName = "Artisanal Slow-Roasted Crispy Cebu Lechon Belly Heritage Platter",
                Quantity = 1.578947m,
                UnitPrice = 950.00m,
                LineTotal = 1500.00m,
                PricingMethod = PricingMethods.WeightBased,
                UnitOfMeasure = "kg"
            },
            new()
            {
                OrderItemId = 2,
                ProductId = 25,
                ProductName = "Complimentary Special Homemade Roasted Garlic Vinegar Dipping Sauce Cup",
                Quantity = 2m,
                UnitPrice = 0.00m,
                LineTotal = 0.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = "pc",
                IsComplimentary = true,
                ParentProductId = 20
            },
            new()
            {
                OrderItemId = 3,
                ProductId = 26,
                ProductName = "Steamed Jasmine Rice Bowl",
                Quantity = 1m,
                UnitPrice = 0.00m,
                LineTotal = 0.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = null,
                IsComplimentary = true,
                ParentProductId = 20
            },
            new()
            {
                OrderItemId = 4,
                ProductId = 40,
                ProductName = "Traditional Holiday Feast Takeaway Packaging Box",
                Quantity = 1m,
                UnitPrice = 45.00m,
                LineTotal = 45.00m,
                PricingMethod = PricingMethods.Fixed,
                UnitOfMeasure = "bundle"
            }
        };

        var receipt = CreateBaseMixedReceiptDto(items);
        receipt.DiscountCode = "SC";
        receipt.DiscountAmount = 100.00m;
        receipt.CustomerCardNumber = "SC-2026-99";
        receipt.TotalAmount = receipt.Subtotal - 100.00m;
        receipt.Payments[0].Amount = receipt.TotalAmount;
        receipt.Payments[0].AmountTendered = 1500.00m;
        receipt.Payments[0].ChangeAmount = 1500.00m - receipt.TotalAmount;

        var (html, esc58, esc80, lines58, lines80) = RenderAllFormats(receipt);

        // HTML CSS wrapping + content checks
        string sublineCss = Regex.Match(html, @"\.item-subline\s*\{[^}]*\}").Value;
        Assert(sublineCss.Contains("word-break: break-word;", StringComparison.Ordinal) &&
               sublineCss.Contains("overflow-wrap: break-word;", StringComparison.Ordinal),
            "HTML .item-subline CSS must include word-break: break-word and overflow-wrap: break-word.");
        Assert(html.Contains("Artisanal Slow-Roasted Crispy Cebu Lechon Belly Heritage Platter", StringComparison.Ordinal) &&
               html.Contains("Complimentary Special Homemade Roasted Garlic Vinegar Dipping Sauce Cup [COMPLIMENTARY]", StringComparison.Ordinal) &&
               html.Contains("Steamed Jasmine Rice Bowl [COMPLIMENTARY]", StringComparison.Ordinal) &&
               html.Contains("2 pc x PHP 0.00", StringComparison.Ordinal) &&
               html.Contains("1 x PHP 0.00", StringComparison.Ordinal) &&
               html.Contains("1 bundle x PHP 45.00", StringComparison.Ordinal) &&
               html.Contains("Discount (SC)", StringComparison.Ordinal) &&
               html.Contains("SC/PWD ID: SC-2026-99", StringComparison.Ordinal) &&
               html.Contains("VAT (12%):", StringComparison.Ordinal) &&
               !html.Contains("BR-01", StringComparison.Ordinal),
            "HTML receipt must preserve long names, [COMPLIMENTARY], UOM sublines, discount, VAT, and BranchCode omission.");
        AssertHtmlRow(html, "Total Items", "5");
        AssertHtmlRow(html, "Total Weight", "1.579 kg");

        // 58mm (32-col) and 80mm (48-col) line-width and right-alignment checks
        Assert(lines58.All(l => l.Length <= 32),
            $"Every 58mm line must be <= 32 cols; max was {lines58.Max(l => l.Length)}.");
        Assert(lines80.All(l => l.Length <= 48),
            $"Every 80mm line must be <= 48 cols; max was {lines80.Max(l => l.Length)}.");

        string normalized58 = string.Join(" ", lines58.Select(l => l.Trim()));
        string normalized80 = string.Join(" ", lines80.Select(l => l.Trim()));
        Assert(normalized58.Contains("Artisanal Slow-Roasted Crispy Cebu Lechon Belly Heritage Platter", StringComparison.Ordinal) &&
               normalized80.Contains("Artisanal Slow-Roasted Crispy Cebu Lechon Belly Heritage Platter", StringComparison.Ordinal) &&
               normalized58.Contains("[COMPLIMENTARY]", StringComparison.Ordinal) &&
               normalized80.Contains("[COMPLIMENTARY]", StringComparison.Ordinal) &&
               esc58.Split("[COMPLIMENTARY]").Length - 1 == 2 &&
               esc80.Split("[COMPLIMENTARY]").Length - 1 == 2,
            "Both 58mm and 80mm receipts must wrap long names cleanly and preserve both [COMPLIMENTARY] tags intact.");

        Assert(lines58.Any(l => l.Length == 32 && l.EndsWith("PHP 1,500.00", StringComparison.Ordinal)) &&
               lines58.Any(l => l.Length == 32 && l.EndsWith("PHP 0.00", StringComparison.Ordinal)) &&
               lines58.Any(l => l.Length == 32 && l.EndsWith("PHP 45.00", StringComparison.Ordinal)),
            "58mm long item line totals must be right-aligned to column 32.");
        Assert(lines80.Any(l => l.Length == 48 && l.EndsWith("PHP 1,500.00", StringComparison.Ordinal)) &&
               lines80.Any(l => l.Length == 48 && l.EndsWith("PHP 0.00", StringComparison.Ordinal)) &&
               lines80.Any(l => l.Length == 48 && l.EndsWith("PHP 45.00", StringComparison.Ordinal)),
            "80mm long item line totals must be right-aligned to column 48.");

        AssertRightAlignedPair(lines58, 32, "Subtotal", "PHP 1,545.00");
        AssertRightAlignedPair(lines58, 32, "TOTAL", "PHP 1,445.00");
        AssertRightAlignedPair(lines58, 32, "Total Items", "5");
        AssertRightAlignedPair(lines58, 32, "Total Weight", "1.579 kg");

        AssertRightAlignedPair(lines80, 48, "Subtotal", "PHP 1,545.00");
        AssertRightAlignedPair(lines80, 48, "TOTAL", "PHP 1,445.00");
        AssertRightAlignedPair(lines80, 48, "Total Items", "5");
        AssertRightAlignedPair(lines80, 48, "Total Weight", "1.579 kg");

        Assert(!esc58.Contains("5.579", StringComparison.Ordinal) && !esc80.Contains("5.579", StringComparison.Ordinal) && !html.Contains("5.579", StringComparison.Ordinal),
            "Total Items must be non-fractional '5', never '5.579'.");

        return Task.CompletedTask;
    }

    // RCPT-QTY-08: Online reprint (ReceiptService.GetReceiptAsync(..., isReprint: true)) and offline DB-mapped reprint parity
    private static async Task TestReceiptMixed08_OnlineReprintAndOfflineReprintParity()
    {
        using var scope = new TempLocalDbScope("rcpt_qty08");
        var dayUtc = new DateTime(2026, 11, 15, 7, 0, 0, DateTimeKind.Utc);
        SetDeterministicManilaClock(dayUtc);

        try
        {
            var (conn, serverDb, shift) = await CreateTestEnvAsync();
            await using var cleanup = conn;

            // Server reference data
            var srvBranch = await serverDb.Branches.FirstAsync(b => b.BranchId == 1);
            srvBranch.BranchTagline = "Quality Since 1995";
            srvBranch.Address = "Sta. Rosa, Laguna";
            srvBranch.Phone = "0917-000-0000";

            serverDb.Categories.Add(new Category { CategoryId = 1, Name = "Main", IsActive = true });
            serverDb.Products.AddRange(
                new Product { ProductId = 20, CategoryId = 1, Name = "Lechon Belly", ProductType = "Standard", PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", Price = 950m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 30, CategoryId = 1, Name = "Lechon Paksiw", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", Price = 100m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 31, CategoryId = 1, Name = "Plain Rice", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = null, Price = 35m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 25, CategoryId = 1, Name = "Atchara Cup", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", Price = 30m, IsKitchenItem = true, IsActive = true });

            decimal amtWeight = 300m / 950m;
            var srvOrder = new Order
            {
                OrderNumber = "POS01-000808",
                InvoiceNumber = "TXN1-0000808",
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = shift.ShiftId,
                CashierId = "cashier1",
                OrderType = "DINING",
                TableNumber = "T-08",
                HeadCount = 3,
                OrderStatus = "COMPLETED",
                PaymentStatus = PaymentStatuses.Paid,
                Subtotal = 435.00m,
                VatableSales = 388.39m,
                VatAmount = 46.61m,
                TotalAmount = 435.00m,
                CustomerName = "Maria Santos",
                CustomerAddress = "Laguna",
                CustomerTin = "123-456-789",
                CustomerBusinessStyle = "Food",
                CreatedAt = DateTime.SpecifyKind(dayUtc.AddHours(8), DateTimeKind.Unspecified),
                InvoiceIssuedAt = DateTime.SpecifyKind(dayUtc.AddHours(8), DateTimeKind.Unspecified),
                OrderItems = new List<OrderItem>
                {
                    new()
                    {
                        OrderItemId = 1,
                        ProductId = 20,
                        ProductName = "Lechon Belly",
                        Quantity = amtWeight,
                        BasePrice = 950.00m,
                        UnitPrice = 950.00m,
                        LineTotal = 300.00m,
                        PricingMethod = null, // Verify fallback to Product.PricingMethod when non-complimentary
                        IsComplimentary = false
                    },
                    new()
                    {
                        OrderItemId = 2,
                        ProductId = 25,
                        ProductName = "Atchara Cup",
                        Quantity = 1m,
                        BasePrice = 0m,
                        UnitPrice = 0m,
                        LineTotal = 0m,
                        PricingMethod = null, // Verify complimentary stays null rather than falling back
                        IsComplimentary = true,
                        ParentProductId = 20
                    },
                    new()
                    {
                        OrderItemId = 3,
                        ProductId = 30,
                        ProductName = "Lechon Paksiw",
                        Quantity = 1m,
                        BasePrice = 100.00m,
                        UnitPrice = 100.00m,
                        LineTotal = 100.00m,
                        PricingMethod = PricingMethods.Fixed,
                        IsComplimentary = false
                    },
                    new()
                    {
                        OrderItemId = 4,
                        ProductId = 31,
                        ProductName = "Plain Rice",
                        Quantity = 1m,
                        BasePrice = 35.00m,
                        UnitPrice = 35.00m,
                        LineTotal = 35.00m,
                        PricingMethod = PricingMethods.Fixed,
                        IsComplimentary = false
                    }
                },
                Payments = new List<Payment>
                {
                    new() { PaymentMethod = "CASH", Amount = 235.00m, AmountTendered = 250.00m, ChangeAmount = 15.00m, PaymentStatus = PaymentStatuses.Paid, ProcessedBy = "cashier1", PaidAt = DateTime.SpecifyKind(dayUtc.AddHours(8), DateTimeKind.Unspecified) },
                    new() { PaymentMethod = "GCASH", Amount = 200.00m, AmountTendered = 200.00m, ChangeAmount = 0m, PaymentStatus = PaymentStatuses.Paid, ReferenceNumber = "GC-REPRINT-08", ProcessedBy = "cashier1", PaidAt = DateTime.SpecifyKind(dayUtc.AddHours(8), DateTimeKind.Unspecified) }
                }
            };
            serverDb.Orders.Add(srvOrder);
            await serverDb.SaveChangesAsync();

            var receiptSvc = new POS.Api.Services.ReceiptService(serverDb);
            var onlineReprint = await receiptSvc.GetReceiptAsync(srvOrder.OrderId, isReprint: true);
            Assert(onlineReprint != null && onlineReprint.IsReprint, "ReceiptService.GetReceiptAsync(..., isReprint: true) must return reprint ReceiptDto.");

            // Verify TransactionsController.GetTransactions and GetTransactionReceipt also preserve UOM & PricingMethod
            var txCtrl = new TransactionsController(serverDb, receiptSvc);
            Authenticate(txCtrl, "Manager", "1");
            var txListRes = await txCtrl.GetTransactions(
                dateFrom: dayUtc.Date,
                dateTo: dayUtc.AddDays(2),
                branchId: 1,
                terminalId: 1,
                cashierId: null,
                orderNumber: null,
                invoiceNumber: null,
                paymentMethod: null,
                orderStatus: null,
                paymentStatus: null,
                discountCode: null);
            var txListOk = txListRes.Result as OkObjectResult;
            var txList = (txListOk?.Value as IEnumerable<OrderResponseDto>)?.ToList();
            Assert(txList != null && txList.Count == 1,
                $"TransactionsController.GetTransactions expected 1 order; Result={txListRes.Result?.GetType().Name}, Count={txList?.Count}.");
            var txItemsByPid = txList![0].Items.ToDictionary(i => i.ProductId);
            Assert(txItemsByPid.Count == 4 &&
                   txItemsByPid[20].UnitOfMeasure == "kg" && txItemsByPid[20].PricingMethod == PricingMethods.WeightBased &&
                   txItemsByPid[25].IsComplimentary && txItemsByPid[25].UnitOfMeasure == "pc" && txItemsByPid[25].PricingMethod == null &&
                   txItemsByPid[30].UnitOfMeasure == "pc" && txItemsByPid[30].PricingMethod == PricingMethods.Fixed &&
                   txItemsByPid[31].UnitOfMeasure == null && txItemsByPid[31].PricingMethod == PricingMethods.Fixed,
                "TransactionsController.GetTransactions items mismatch: " + string.Join("; ",
                    txList[0].Items.Select(i => $"PID={i.ProductId},UOM={i.UnitOfMeasure ?? "<null>"},PM={i.PricingMethod ?? "<null>"},Comp={i.IsComplimentary}")));

            var ctrlReprintRes = await txCtrl.GetTransactionReceipt(srvOrder.OrderId, reprint: true);
            var ctrlReprint = (ctrlReprintRes.Result as OkObjectResult)?.Value as ReceiptDto;
            Assert(ctrlReprint != null && ctrlReprint.IsReprint && ctrlReprint.Items.Count == 4,
                $"TransactionsController.GetTransactionReceipt must return reprint ReceiptDto (got {ctrlReprintRes.Result?.GetType().Name}).");

            // Offline SQLite seeded identically -> MapLocalOrderItems (Transactions.razor LoadLocalTransactionsAsync + ReprintInvoice) AND LoadSavedReceiptFromDbAsync
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m, lastInvoiceNumber: 808);
            var session = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Manager");
            session.Terminal!.BranchTagline = "Quality Since 1995";
            session.Terminal.BranchAddress = "Sta. Rosa, Laguna";
            session.Terminal.BranchPhone = "0917-000-0000";

            string localOrderId = Guid.NewGuid().ToString();
            using (var localSeed = new LocalPosDbContext())
            {
                var localBranch = await localSeed.Branches.FirstAsync(b => b.BranchId == 1);
                localBranch.BranchTagline = "Quality Since 1995";
                localBranch.Address = "Sta. Rosa, Laguna";
                localBranch.Phone = "0917-000-0000";

                localSeed.Products.AddRange(
                    new LocalProduct { ProductId = 20, CategoryId = 1, CategoryName = "Main", Name = "Lechon Belly", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 30, CategoryId = 1, CategoryName = "Main", Name = "Lechon Paksiw", Price = 100m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 31, CategoryId = 1, CategoryName = "Main", Name = "Plain Rice", Price = 35m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = null, TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 25, CategoryId = 1, CategoryName = "Main", Name = "Atchara Cup", Price = 30m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true });

                var localCreatedAt = DateTime.SpecifyKind(dayUtc.AddHours(8), DateTimeKind.Unspecified);
                localSeed.Orders.Add(new LocalOrder
                {
                    OrderId = localOrderId,
                    OrderNumber = "POS01-000808",
                    InvoiceNumber = "TXN1-0000808",
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OrderType = "DINING",
                    TableNumber = "T-08",
                    HeadCount = 3,
                    Subtotal = 435.00m,
                    VatableSales = 388.39m,
                    VatAmount = 46.61m,
                    TotalAmount = 435.00m,
                    PaymentStatus = PaymentStatuses.Paid,
                    OrderStatus = "COMPLETED",
                    CustomerName = "Maria Santos",
                    CustomerAddress = "Laguna",
                    CustomerTin = "123-456-789",
                    CustomerBusinessStyle = "Food",
                    CreatedAt = localCreatedAt
                });
                localSeed.OrderItems.AddRange(
                    new LocalOrderItem { OrderItemId = 1, OrderId = localOrderId, ProductId = 20, ProductName = "Lechon Belly", Quantity = amtWeight, UnitPrice = 950.00m, LineTotal = 300.00m, PricingMethod = null, TargetAmount = 300.00m, IsComplimentary = false, IsKitchenItem = true },
                    new LocalOrderItem { OrderItemId = 2, OrderId = localOrderId, ProductId = 25, ProductName = "Atchara Cup", Quantity = 1m, UnitPrice = 0m, LineTotal = 0m, PricingMethod = null, IsComplimentary = true, ParentProductId = 20, IsKitchenItem = true },
                    new LocalOrderItem { OrderItemId = 3, OrderId = localOrderId, ProductId = 30, ProductName = "Lechon Paksiw", Quantity = 1m, UnitPrice = 100.00m, LineTotal = 100.00m, PricingMethod = PricingMethods.Fixed, IsComplimentary = false, IsKitchenItem = true },
                    new LocalOrderItem { OrderItemId = 4, OrderId = localOrderId, ProductId = 31, ProductName = "Plain Rice", Quantity = 1m, UnitPrice = 35.00m, LineTotal = 35.00m, PricingMethod = PricingMethods.Fixed, IsComplimentary = false, IsKitchenItem = true });
                localSeed.Payments.AddRange(
                    new LocalPayment { OrderId = localOrderId, PaymentMethod = "CASH", Amount = 235.00m, TenderedAmount = 250.00m, ChangeAmount = 15.00m, PaymentStatus = PaymentStatuses.Paid, PaidAt = localCreatedAt },
                    new LocalPayment { OrderId = localOrderId, PaymentMethod = "GCASH", Amount = 200.00m, TenderedAmount = 200.00m, ChangeAmount = 0m, ReferenceNumber = "GC-REPRINT-08", PaymentStatus = PaymentStatuses.Paid, PaidAt = localCreatedAt });
                await localSeed.SaveChangesAsync();
            }

            ReceiptDto offlineMappedReprint;
            ReceiptDto? savedDbReceipt;
            using (var localRead = new LocalPosDbContext())
            {
                var localOrder = await localRead.Orders.AsNoTracking().SingleAsync(o => o.OrderId == localOrderId);
                var localItems = await localRead.OrderItems.AsNoTracking().Where(i => i.OrderId == localOrderId).OrderBy(i => i.OrderItemId).ToListAsync();
                var localPayments = await localRead.Payments.AsNoTracking().Where(p => p.OrderId == localOrderId).OrderBy(p => p.PaymentId).ToListAsync();
                var metadata = await OfflineCheckoutService.LoadLocalProductMetadataAsync(localRead, localItems.Select(i => i.ProductId));
                var mappedItems = OfflineCheckoutService.MapLocalOrderItems(localItems, metadata);

                offlineMappedReprint = new ReceiptDto
                {
                    OrderId = 0,
                    OrderNumber = localOrder.OrderNumber ?? string.Empty,
                    InvoiceNumber = localOrder.InvoiceNumber,
                    InvoiceIssuedAt = localOrder.CreatedAt,
                    BranchId = localOrder.BranchId,
                    BranchName = session.Terminal!.BranchName,
                    BranchTagline = session.Terminal.BranchTagline,
                    BranchAddress = session.Terminal.BranchAddress,
                    BranchPhone = session.Terminal.BranchPhone,
                    VatDisplay = session.Terminal.VatDisplay,
                    TerminalId = localOrder.TerminalId,
                    TerminalName = session.Terminal.TerminalName,
                    CashierId = localOrder.CashierId,
                    OrderType = localOrder.OrderType,
                    TableNumber = localOrder.TableNumber,
                    HeadCount = localOrder.HeadCount,
                    CreatedAt = BusinessDayClock.NormalizeLocal(localOrder.CreatedAt),
                    PaidAt = BusinessDayClock.NormalizeLocal(localOrder.CreatedAt),
                    Subtotal = localOrder.Subtotal,
                    VatableSales = localOrder.VatableSales,
                    VatAmount = localOrder.VatAmount,
                    TotalAmount = localOrder.TotalAmount,
                    CustomerName = localOrder.CustomerName,
                    CustomerAddress = localOrder.CustomerAddress,
                    CustomerTin = localOrder.CustomerTin,
                    CustomerBusinessStyle = localOrder.CustomerBusinessStyle,
                    IsReprint = true,
                    Items = mappedItems,
                    Payments = localPayments.Select(p => new PaymentResponseDto
                    {
                        PaymentId = p.PaymentId,
                        PaymentMethod = p.PaymentMethod,
                        Amount = p.Amount,
                        AmountTendered = p.TenderedAmount,
                        ChangeAmount = p.ChangeAmount,
                        ReferenceNumber = p.ReferenceNumber,
                        PaymentStatus = p.PaymentStatus,
                        PaidAt = BusinessDayClock.NormalizeLocal(p.PaidAt)
                    }).ToList()
                };

                savedDbReceipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(localRead, localOrderId, session);
                Assert(savedDbReceipt != null, "LoadSavedReceiptFromDbAsync must hydrate local DB order.");
                savedDbReceipt!.IsReprint = true;
            }

            // Item-level & summary parity assertions across online reprint, offline DB-mapped reprint, and LoadSavedReceiptFromDbAsync
            Assert(onlineReprint!.Items.Count == 4 && offlineMappedReprint.Items.Count == 4 && savedDbReceipt!.Items.Count == 4,
                "All three reprint paths must return 4 items.");

            for (int idx = 0; idx < 4; idx++)
            {
                var on = onlineReprint.Items[idx];
                var off = offlineMappedReprint.Items[idx];
                var sav = savedDbReceipt!.Items[idx];

                Assert(on.ProductId == off.ProductId && on.ProductId == sav.ProductId &&
                       on.UnitOfMeasure == off.UnitOfMeasure && on.UnitOfMeasure == sav.UnitOfMeasure &&
                       on.PricingMethod == off.PricingMethod && on.PricingMethod == sav.PricingMethod &&
                       on.IsComplimentary == off.IsComplimentary && on.IsComplimentary == sav.IsComplimentary &&
                       off.Quantity == sav.Quantity && Math.Abs(on.Quantity - off.Quantity) < 0.000001m &&
                       on.UnitPrice == off.UnitPrice && on.UnitPrice == sav.UnitPrice &&
                       on.LineTotal == off.LineTotal && on.LineTotal == sav.LineTotal,
                    $"Item {idx} parity mismatch: " +
                    $"ON(PID={on.ProductId},UOM={on.UnitOfMeasure ?? "<null>"},PM={on.PricingMethod ?? "<null>"},Comp={on.IsComplimentary},Q={on.Quantity},P={on.UnitPrice},T={on.LineTotal}) vs " +
                    $"OFF(PID={off.ProductId},UOM={off.UnitOfMeasure ?? "<null>"},PM={off.PricingMethod ?? "<null>"},Comp={off.IsComplimentary},Q={off.Quantity},P={off.UnitPrice},T={off.LineTotal}) vs " +
                    $"SAV(PID={sav.ProductId},UOM={sav.UnitOfMeasure ?? "<null>"},PM={sav.PricingMethod ?? "<null>"},Comp={sav.IsComplimentary},Q={sav.Quantity},P={sav.UnitPrice},T={sav.LineTotal})");

                Assert(ReceiptPresentationHelper.FormatItemSubline(on) == ReceiptPresentationHelper.FormatItemSubline(off) &&
                       ReceiptPresentationHelper.FormatItemSubline(on) == ReceiptPresentationHelper.FormatItemSubline(sav),
                    $"Formatted subline mismatch at item {idx}: ON='{ReceiptPresentationHelper.FormatItemSubline(on)}' OFF='{ReceiptPresentationHelper.FormatItemSubline(off)}' SAV='{ReceiptPresentationHelper.FormatItemSubline(sav)}'.");
            }

            var onSummary = ReceiptPresentationHelper.Summarize(onlineReprint);
            var offSummary = ReceiptPresentationHelper.Summarize(offlineMappedReprint);
            var savSummary = ReceiptPresentationHelper.Summarize(savedDbReceipt);
            Assert(onSummary.TotalItemsDisplay == "4" &&
                   offSummary.TotalItemsDisplay == "4" &&
                   savSummary.TotalItemsDisplay == "4" &&
                   onSummary.WeightTotals.Single().FormattedWithUnit == "0.316 kg" &&
                   offSummary.WeightTotals.Single().FormattedWithUnit == "0.316 kg" &&
                   savSummary.WeightTotals.Single().FormattedWithUnit == "0.316 kg",
                "Online and offline reprint summaries must both produce Total Items = 4 and Total Weight = 0.316 kg.");

            var (htmlOnline, esc58Online, esc80Online, _, _) = RenderAllFormats(onlineReprint);
            var (htmlOffline, esc58Offline, esc80Offline, _, _) = RenderAllFormats(offlineMappedReprint);
            var (htmlSaved, esc58Saved, esc80Saved, _, _) = RenderAllFormats(savedDbReceipt);

            Assert(htmlOffline == htmlSaved,
                $"Rendered HTML between offlineMap and LoadSavedReceiptFromDbAsync differed. offLen={htmlOffline.Length}, savLen={htmlSaved.Length}");
            Assert(esc58Offline == esc58Saved && esc80Offline == esc80Saved,
                "Rendered 58mm and 80mm ESC/POS bytes between offlineMap and LoadSavedReceiptFromDbAsync must match identically.");

            // Online and offline receipt bodies (items + totals + summary + footer) must match after aligning header metadata
            Assert(htmlOnline.Contains("** REPRINT **", StringComparison.Ordinal) &&
                   htmlOffline.Contains("** REPRINT **", StringComparison.Ordinal) &&
                   htmlOnline.Contains("0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
                   htmlOffline.Contains("0.316 kg x PHP 950.00/kg", StringComparison.Ordinal) &&
                   htmlOnline.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   htmlOffline.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   htmlOnline.Contains("1 pc x PHP 100.00", StringComparison.Ordinal) &&
                   htmlOffline.Contains("1 pc x PHP 100.00", StringComparison.Ordinal) &&
                   htmlOnline.Contains("1 x PHP 35.00", StringComparison.Ordinal) &&
                   htmlOffline.Contains("1 x PHP 35.00", StringComparison.Ordinal) &&
                   htmlOnline.Contains("DINING | Table: T-08 | 3", StringComparison.Ordinal) &&
                   htmlOffline.Contains("DINING | Table: T-08 | 3", StringComparison.Ordinal) &&
                   htmlOnline.Contains("Maria Santos", StringComparison.Ordinal) &&
                   htmlOffline.Contains("Maria Santos", StringComparison.Ordinal) &&
                   htmlOnline.Contains("Total Items", StringComparison.Ordinal) &&
                   htmlOffline.Contains("Total Items", StringComparison.Ordinal) &&
                   htmlOnline.Contains("Total Weight", StringComparison.Ordinal) &&
                   htmlOffline.Contains("Total Weight", StringComparison.Ordinal),
                "Online and offline reprint HTML must preserve ** REPRINT **, mixed UOMs, [COMPLIMENTARY], Total Items, Total Weight, service info, and customer details.");

            string itemsOnlineEsc80 = esc80Online.Substring(esc80Online.IndexOf("0.316 kg", StringComparison.Ordinal));
            string itemsOfflineEsc80 = esc80Offline.Substring(esc80Offline.IndexOf("0.316 kg", StringComparison.Ordinal));
            Assert(itemsOnlineEsc80 == itemsOfflineEsc80,
                $"80mm ESC/POS items+totals+footer parity mismatch between online and offline reprint.\nON:\n{itemsOnlineEsc80}\nOFF:\n{itemsOfflineEsc80}");

            string itemsOnlineEsc58 = esc58Online.Substring(esc58Online.IndexOf("0.316 kg", StringComparison.Ordinal));
            string itemsOfflineEsc58 = esc58Offline.Substring(esc58Offline.IndexOf("0.316 kg", StringComparison.Ordinal));
            Assert(itemsOnlineEsc58 == itemsOfflineEsc58,
                $"58mm ESC/POS items+totals+footer parity mismatch between online and offline reprint.\nON:\n{itemsOnlineEsc58}\nOFF:\n{itemsOfflineEsc58}");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }
}
