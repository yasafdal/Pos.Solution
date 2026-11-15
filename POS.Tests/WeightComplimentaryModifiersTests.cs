using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
    private static string TruncateModifierName(int sets, string? optionName)
    {
        var baseName = optionName ?? string.Empty;
        var formatted = sets > 1 ? $"{sets}x {baseName}" : baseName;
        return formatted.Length > 100 ? formatted[..100] : formatted;
    }

    // WM-COMP-01: Truth table for ReceiptPresentationHelper.ShouldShowFreeModifierLabel
    private static Task TestWeightComplimentary01_ShouldShowFreeModifierLabelTruthTable()
    {
        var weightExplicit = new OrderItemResponseDto
        {
            ProductId = 20,
            ProductName = "Lechon Belly",
            Quantity = 1.5m,
            UnitPrice = 950m,
            LineTotal = 1425m,
            PricingMethod = PricingMethods.WeightBased
        };
        var weightIntegerQty = new OrderItemResponseDto
        {
            ProductId = 20,
            ProductName = "Lechon Whole Kg",
            Quantity = 1m,
            UnitPrice = 950m,
            LineTotal = 950m,
            PricingMethod = PricingMethods.WeightBased
        };
        var weightLegacyFractional = new OrderItemResponseDto
        {
            ProductId = 20,
            ProductName = "Legacy Weight",
            Quantity = 0.316m,
            UnitPrice = 950m,
            LineTotal = 300m,
            PricingMethod = null,
            IsComplimentary = false
        };
        var regularFixed = new OrderItemResponseDto
        {
            ProductId = 30,
            ProductName = "Classic Burger",
            Quantity = 1m,
            UnitPrice = 150m,
            LineTotal = 150m,
            PricingMethod = PricingMethods.Fixed
        };
        var regularComplimentary = new OrderItemResponseDto
        {
            ProductId = 25,
            ProductName = "Atchara Cup",
            Quantity = 1m,
            UnitPrice = 0m,
            LineTotal = 0m,
            PricingMethod = null,
            IsComplimentary = true
        };

        var unlinkedFree = new OrderItemModifierResponseDto { ModifierOptionId = 1, ProductId = null, ModifierName = "Coke 1.25L", AdditionalPrice = 0m };
        var regularNoteFree = new OrderItemModifierResponseDto { ModifierOptionId = 2, ProductId = null, ModifierName = "No Onions", AdditionalPrice = 0m };
        var linkedFree = new OrderItemModifierResponseDto { ModifierOptionId = 3, ProductId = 22, ModifierName = "Coke 330ml", AdditionalPrice = 0m };
        var unlinkedPaid = new OrderItemModifierResponseDto { ModifierOptionId = 4, ProductId = null, ModifierName = "Extra Chili", AdditionalPrice = 25m };
        var linkedPaid = new OrderItemModifierResponseDto { ModifierOptionId = 5, ProductId = 25, ModifierName = "Extra Atchara", AdditionalPrice = 30m };
        var negativePrice = new OrderItemModifierResponseDto { ModifierOptionId = 6, ProductId = 25, ModifierName = "Promo", AdditionalPrice = -5m };

        // Null safety
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(null, unlinkedFree), "Null item must return false.");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, null), "Null modifier must return false.");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(null, null), "Null item and modifier must return false.");

        // True cases: weight + zero price (unlinked or linked) AND regular + linked + zero price
        Assert(ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, unlinkedFree),
            "Weight-based item + unlinked zero-price modifier must show (Free).");
        Assert(ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightIntegerQty, unlinkedFree),
            "Weight-based item with integer qty (1 kg) + unlinked zero-price modifier must show (Free).");
        Assert(ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightLegacyFractional, unlinkedFree),
            "Legacy non-complimentary fractional weight item + unlinked zero-price modifier must show (Free).");
        Assert(ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, linkedFree),
            "Weight-based item + linked zero-price modifier must show (Free).");
        Assert(ReceiptPresentationHelper.ShouldShowFreeModifierLabel(regularFixed, linkedFree),
            "Regular item + linked zero-price modifier must show (Free).");

        // False cases: regular + unlinked + zero price ("No Onions") AND any non-zero price
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(regularFixed, regularNoteFree),
            "Regular item + unlinked zero-price modifier ('No Onions') must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(regularComplimentary, unlinkedFree),
            "Complimentary non-weight item + unlinked zero-price modifier must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, unlinkedPaid),
            "Weight-based item + unlinked paid modifier must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, linkedPaid),
            "Weight-based item + linked paid modifier must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(regularFixed, linkedPaid),
            "Regular item + linked paid modifier must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(regularFixed, unlinkedPaid),
            "Regular item + unlinked paid modifier must NOT show (Free).");
        Assert(!ReceiptPresentationHelper.ShouldShowFreeModifierLabel(weightExplicit, negativePrice),
            "Non-zero negative modifier price must NOT show (Free).");

        return Task.CompletedTask;
    }

    // WM-COMP-02: CartService.AddWeightBasedItem + UpdateSpecialInstructions with unlinked & linked complimentary modifiers
    private static Task TestWeightComplimentary02_CartWeightModifiersAndUpdateNotesNoCollapse()
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
            TaxName = "VATable",
            IsKitchenItem = true,
            IsActive = true
        };

        // 1. sets == 0 (< 1.0 kg Amount Mode ₱300 -> 0 complimentary items or modifiers)
        decimal weight300 = 300m / lechon.Price;
        Assert((int)Math.Floor(weight300) == 0, "₱300 at ₱950/kg must yield 0 complimentary sets.");
        cart.AddWeightBasedItem(lechon, weight: weight300, targetAmount: 300.00m, selectedModifiers: null, complimentaryItems: null);
        var line0 = cart.Items.Single();
        Assert(line0.UnitPrice == 950.00m && line0.LineTotal == 300.00m && line0.SelectedModifiers.Count == 0,
            "sets == 0 weight line must keep UnitPrice=950, LineTotal=300, and zero modifiers.");

        // 2. sets == 1 (1.5 kg Weight Mode) with unlinked Coke 1.25L (ProductId == null, Price == 0) + linked Atchara Cup (ProductId == 25)
        var unlinkedSet1 = new List<ModifierOptionDto>
        {
            new()
            {
                ModifierOptionId = 701,
                ModifierGroupId = 30,
                ProductId = null,
                Name = TruncateModifierName(1, "Coke 1.25L"),
                Price = 0m,
                DisplayOrder = 1,
                IsActive = true
            }
        };
        var linkedSet1 = new List<CartItem>
        {
            new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 1m, UnitOfMeasure = "pc", IsKitchenItem = true }
        };
        cart.AddWeightBasedItem(lechon, weight: 1.5m, targetAmount: null, selectedModifiers: unlinkedSet1, specialInstructions: "Chopped", complimentaryItems: linkedSet1);

        var line1 = cart.Items.Single(i => !i.IsComplimentary && i.TargetAmount == null);
        var comp1 = cart.Items.Single(i => i.IsComplimentary && i.ParentCartItemId == line1.CartItemId);
        Assert(line1.UnitPrice == 950.00m && line1.LineTotal == 1425.00m,
            $"Unlinked free modifier must not inflate UnitPrice (950) or LineTotal (1425); got {line1.UnitPrice} / {line1.LineTotal}.");
        Assert(line1.SelectedModifiers.Count == 1 && line1.SelectedModifiers[0].Name == "Coke 1.25L" && line1.SelectedModifiers[0].ProductId == null,
            "sets == 1 unlinked modifier must keep unprefixed name and ProductId == null.");
        Assert(comp1.IsComplimentary && comp1.ProductId == 25 && comp1.UnitPrice == 0m && comp1.LineTotal == 0m && comp1.Quantity == 1m,
            "Linked complimentary item must create separate zero-priced IsComplimentary CartItem without duplicate modifier row.");

        // 3. sets > 1 (Amount Mode ₱2,000 -> 2 sets) with multi-set naming + >100-char truncation + Price == 0
        string longOptionName = new string('A', 45) + " Complimentary Signature Roasted Pork Dipping Sauce " + new string('B', 20);
        Assert(longOptionName.Length > 100, "Test fixture option name must exceed 100 chars.");
        string truncatedMultiSet = TruncateModifierName(2, longOptionName);
        Assert(truncatedMultiSet.Length == 100 && truncatedMultiSet.StartsWith("2x ", StringComparison.Ordinal),
            $"Multi-set long modifier name must start with '2x ' and truncate cleanly to 100 chars (got len={truncatedMultiSet.Length}).");

        var unlinkedSet2 = new List<ModifierOptionDto>
        {
            new()
            {
                ModifierOptionId = 701,
                ModifierGroupId = 30,
                ProductId = null,
                Name = TruncateModifierName(2, "Coke 1.25L"),
                Price = 0m,
                DisplayOrder = 1,
                IsActive = true
            },
            new()
            {
                ModifierOptionId = 702,
                ModifierGroupId = 20,
                ProductId = null,
                Name = truncatedMultiSet,
                Price = 0m,
                DisplayOrder = 2,
                IsActive = true
            }
        };
        var linkedSet2 = new List<CartItem>
        {
            new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 2m, UnitOfMeasure = "pc", IsKitchenItem = true }
        };
        cart.AddWeightBasedItem(
            lechon,
            weight: 2000m / lechon.Price,
            targetAmount: 2000.00m,
            selectedModifiers: unlinkedSet2,
            specialInstructions: "Extra crispy",
            complimentaryItems: linkedSet2);

        var line2000 = cart.Items.Single(i => !i.IsComplimentary && i.TargetAmount == 2000.00m);
        var comp2 = cart.Items.Single(i => i.IsComplimentary && i.ParentCartItemId == line2000.CartItemId);
        Assert(line2000.UnitPrice == 950.00m && line2000.LineTotal == 2000.00m,
            $"Multi-set unlinked free modifiers must not inflate UnitPrice/LineTotal; got {line2000.UnitPrice} / {line2000.LineTotal}.");
        Assert(line2000.SelectedModifiers.Any(m => m.Name == "2x Coke 1.25L" && m.Price == 0m && m.ProductId == null),
            "sets == 2 unlinked modifier must format as '2x Coke 1.25L' with Price=0.");

        // 4. Second WeightBased line with same ProductId=20 and same ModifierOptionIds (701, 702) and targetAmount=1900, note="No chop"
        cart.AddWeightBasedItem(
            lechon,
            weight: 1900m / lechon.Price,
            targetAmount: 1900.00m,
            selectedModifiers: new List<ModifierOptionDto>
            {
                new() { ModifierOptionId = 701, ModifierGroupId = 30, ProductId = null, Name = "2x Coke 1.25L", Price = 0m },
                new() { ModifierOptionId = 702, ModifierGroupId = 20, ProductId = null, Name = truncatedMultiSet, Price = 0m }
            },
            specialInstructions: "No chop");
        var line1900 = cart.Items.Single(i => !i.IsComplimentary && i.TargetAmount == 1900.00m);

        // Editing line2000's notes to match line1900's notes MUST NOT collapse the two weight lines or orphan comp2!
        int countBeforeNoteEdit = cart.Items.Count;
        cart.UpdateSpecialInstructions(line2000, "  No chop  ");
        Assert(cart.Items.Count == countBeforeNoteEdit,
            $"UpdateSpecialInstructions must NEVER collapse weight-based items (expected {countBeforeNoteEdit} rows, got {cart.Items.Count}).");
        Assert(cart.Items.Contains(line2000) && cart.Items.Contains(line1900) && cart.Items.Contains(comp2),
            "Both weight-based lines and complimentary child must remain in cart after note edit.");
        Assert(line2000.SpecialInstructions == "No chop" && line2000.TargetAmount == 2000.00m && line2000.LineTotal == 2000.00m &&
               line1900.TargetAmount == 1900.00m && line1900.LineTotal == 1900.00m,
            "TargetAmount and LineTotal of each weight line must remain untouched after UpdateSpecialInstructions.");

        // Editing notes on two IsComplimentary lines sharing ProductId=25 MUST NOT collapse them either!
        cart.UpdateSpecialInstructions(comp1, "Extra garlic");
        cart.UpdateSpecialInstructions(comp2, "Extra garlic");
        Assert(cart.Items.Count(i => i.IsComplimentary && i.ProductId == 25) == 2 &&
               comp1.Quantity == 1m && comp2.Quantity == 2m,
            "UpdateSpecialInstructions must NEVER collapse IsComplimentary cart items.");

        // Contrast check: two regular non-weight, non-complimentary items with matching modifiers DO collapse on matching notes
        var burger = new ProductDto { ProductId = 30, CategoryId = 1, Name = "Classic Burger", Price = 150m, PricingMethod = PricingMethods.Fixed, IsKitchenItem = true, IsActive = true };
        var noOnions = new List<ModifierOptionDto> { new() { ModifierOptionId = 801, ModifierGroupId = 40, ProductId = null, Name = "No Onions", Price = 0m } };
        cart.AddItem(burger, 1, noOnions, "Light sauce");
        cart.AddItem(burger, 1, new List<ModifierOptionDto>(noOnions), "Extra toasted");
        var burgerLines = cart.Items.Where(i => i.ProductId == 30).ToList();
        Assert(burgerLines.Count == 2, "Distinct notes on regular items must initially create 2 rows.");
        cart.UpdateSpecialInstructions(burgerLines[1], "Light sauce");
        var mergedBurger = cart.Items.Single(i => i.ProductId == 30);
        Assert(mergedBurger.Quantity == 2m && mergedBurger.SpecialInstructions == "Light sauce",
            "Regular non-weight non-complimentary items must still merge when UpdateSpecialInstructions creates a match.");

        return Task.CompletedTask;
    }

    // WM-COMP-03: Receipt generation (HTML, ESC/POS 58mm & 80mm, Prep Tickets), OfflineCheckoutService & Sync pipeline
    private static async Task TestWeightComplimentary03_ReceiptPrepTicketsAndOfflineCheckoutPipeline()
    {
        using var scope = new TempLocalDbScope("wm_comp03");
        var dayUtc = new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc);
        SetDeterministicManilaClock(dayUtc);

        try
        {
            var (conn, serverDb, priorShift) = await CreateTestEnvAsync();
            await using var cleanup = conn;

            priorShift.Status = "CLOSED";
            priorShift.OpenedAt = dayUtc.AddDays(-1);
            priorShift.ClosedAt = dayUtc.AddDays(-1).AddHours(8);

            serverDb.Categories.Add(new Category { CategoryId = 1, Name = "Main", IsActive = true });
            serverDb.Products.AddRange(
                new Product { ProductId = 20, CategoryId = 1, Name = "Lechon Belly", ProductType = "Standard", PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", Price = 950m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 22, CategoryId = 1, Name = "Coke 330ml", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", Price = 50m, IsBarItem = true, IsActive = true },
                new Product { ProductId = 25, CategoryId = 1, Name = "Atchara Cup", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", Price = 30m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 30, CategoryId = 1, Name = "Classic Burger", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", Price = 150m, IsKitchenItem = true, IsActive = true });

            serverDb.BranchInventories.AddRange(
                new BranchInventory { BranchId = 1, ProductId = 20, StockQuantity = 10, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 22, StockQuantity = 20, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 25, StockQuantity = 20, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 30, StockQuantity = 20, LastUpdated = dayUtc });

            serverDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1,
                SeriesCode = "TXN1",
                Prefix = "TXN1-",
                StartingNumber = 1,
                EndingNumber = 10000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            await serverDb.SaveChangesAsync();

            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m, lastInvoiceNumber: 0);
            using (var seedLocal = new LocalPosDbContext())
            {
                seedLocal.Products.AddRange(
                    new LocalProduct { ProductId = 20, CategoryId = 1, CategoryName = "Main", Name = "Lechon Belly", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 22, CategoryId = 1, CategoryName = "Main", Name = "Coke 330ml", Price = 50m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsBarItem = true, IsActive = true },
                    new LocalProduct { ProductId = 25, CategoryId = 1, CategoryName = "Main", Name = "Atchara Cup", Price = 30m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 30, CategoryId = 1, CategoryName = "Main", Name = "Classic Burger", Price = 150m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true });
                await seedLocal.SaveChangesAsync();
            }

            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOfflineTestSession(CreateSyntheticJwt("cashier1", "Cashier"), "cashier1", "Cashier");

            var shift = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1000m,
                ClientShiftId = "shift-wm-comp03"
            }, "cashier1", offline: true);

            // Exact 100-char truncated multi-set unlinked side name
            string longSideSource = "Complimentary Special Slow-Simmered Garlic Vinegar Roast Pork Dripping Gravy Sauce Cup Deluxe Extra";
            string truncatedSideName = TruncateModifierName(2, longSideSource);
            Assert(truncatedSideName.Length == 100 && truncatedSideName.StartsWith("2x ", StringComparison.Ordinal),
                $"Expected 100-char truncated multi-set modifier name, got length {truncatedSideName.Length}.");

            using var cart = new CartService(session);
            var lechonDto = new ProductDto { ProductId = 20, CategoryId = 1, Name = "Lechon Belly", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true };
            var burgerDto = new ProductDto { ProductId = 30, CategoryId = 1, Name = "Classic Burger", Price = 150m, PricingMethod = PricingMethods.Fixed, UnitOfMeasure = "pc", TaxName = "VATable", IsKitchenItem = true, IsActive = true };

            // Weight item: 2.0 kg Lechon (LineTotal=1900) with BOTH unlinked (ProductId == null) modifiers AND linked (ProductId > 0) complimentary items
            cart.AddWeightBasedItem(
                lechonDto,
                weight: 2.0m,
                targetAmount: null,
                selectedModifiers: new List<ModifierOptionDto>
                {
                    new() { ModifierOptionId = 701, ModifierGroupId = 20, ProductId = null, Name = TruncateModifierName(2, "Sarsa Cup"), Price = 0m, DisplayOrder = 1, IsActive = true },
                    new() { ModifierOptionId = 702, ModifierGroupId = 20, ProductId = null, Name = truncatedSideName, Price = 0m, DisplayOrder = 2, IsActive = true }
                },
                specialInstructions: "Chopped medium",
                complimentaryItems: new List<CartItem>
                {
                    new() { ProductId = 22, ProductName = "Coke 330ml", BasePrice = 50m, Quantity = 2m, UnitOfMeasure = "pc", IsBarItem = true },
                    new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 2m, UnitOfMeasure = "pc", IsKitchenItem = true }
                });

            // Regular item with: regular unlinked free ("No Onions"), regular linked free ("Free Extra Atchara", ProductId=25), and paid ("Extra Cheese", +20)
            cart.AddItem(
                burgerDto,
                quantity: 1,
                selectedModifiers: new List<ModifierOptionDto>
                {
                    new() { ModifierOptionId = 801, ModifierGroupId = 40, ProductId = null, Name = "No Onions", Price = 0m, DisplayOrder = 1, IsActive = true },
                    new() { ModifierOptionId = 802, ModifierGroupId = 40, ProductId = 25, Name = "Free Extra Atchara", Price = 0m, DisplayOrder = 2, IsActive = true },
                    new() { ModifierOptionId = 803, ModifierGroupId = 40, ProductId = null, Name = "Extra Cheese", Price = 20m, DisplayOrder = 3, IsActive = true }
                });

            Assert(cart.Subtotal == 2070.00m, $"Expected Cart.Subtotal == 2070.00, got {cart.Subtotal}.");

            var createDto = new OrderCreateDto
            {
                ShiftId = shift.ShiftId,
                ClientShiftId = shift.ClientShiftId,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                TableNumber = "T-12",
                HeadCount = 2,
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
                    IsBarItem = ci.IsBarItem,
                    SpecialInstructions = ci.SpecialInstructions,
                    Modifiers = ci.SelectedModifiers.Select(m => new OrderItemModifierCreateDto
                    {
                        ModifierOptionId = m.ModifierOptionId,
                        ProductId = m.ProductId,
                        ModifierName = m.Name,
                        AdditionalPrice = m.Price
                    }).ToList()
                }).ToList()
            };

            OfflineCheckoutResultDto checkoutRes;
            using (var coDb = new LocalPosDbContext())
            {
                checkoutRes = await new OfflineCheckoutService(coDb, session).ProcessSaleAsync(createDto, 2100.00m, "CASH", null, "cashier1");
            }
            Assert(checkoutRes.Success && checkoutRes.Receipt != null, $"Offline checkout must succeed: {checkoutRes.ErrorMessage}");

            var immediate = checkoutRes.Receipt!;
            Assert(immediate.Subtotal == 2070.00m && immediate.TotalAmount == 2070.00m && immediate.Items.Count == 4,
                $"Immediate receipt totals/count mismatch: Subtotal={immediate.Subtotal}, Count={immediate.Items.Count}.");

            // Immediate ProcessSaleAsync receipt must preserve Modifier.ProductId matching DeserializeModifiers
            var immLechon = immediate.Items[0];
            var immCoke = immediate.Items[1];
            var immAtchara = immediate.Items[2];
            var immBurger = immediate.Items[3];

            Assert(immLechon.UnitPrice == 950.00m && immLechon.LineTotal == 1900.00m &&
                   immLechon.Modifiers.Count == 2 &&
                   immLechon.Modifiers[0].ProductId == null && immLechon.Modifiers[0].ModifierName == "2x Sarsa Cup" && immLechon.Modifiers[0].AdditionalPrice == 0m &&
                   immLechon.Modifiers[1].ProductId == null && immLechon.Modifiers[1].ModifierName == truncatedSideName && immLechon.Modifiers[1].AdditionalPrice == 0m,
                "Immediate weight item must preserve unlinked complimentary modifiers (ProductId==null, AdditionalPrice==0) without inflating UnitPrice/LineTotal.");

            Assert(immCoke.IsComplimentary && immCoke.IsBarItem && !immCoke.IsKitchenItem && immCoke.LineTotal == 0m && immCoke.Quantity == 2m &&
                   immAtchara.IsComplimentary && immAtchara.IsKitchenItem && !immAtchara.IsBarItem && immAtchara.LineTotal == 0m && immAtchara.Quantity == 2m,
                "Linked complimentary items must stay separate IsComplimentary rows with Bar/Kitchen routing intact.");

            Assert(immBurger.Modifiers.Count == 3 &&
                   immBurger.Modifiers[0].ProductId == null && immBurger.Modifiers[0].ModifierName == "No Onions" && immBurger.Modifiers[0].AdditionalPrice == 0m &&
                   immBurger.Modifiers[1].ProductId == 25 && immBurger.Modifiers[1].ModifierName == "Free Extra Atchara" && immBurger.Modifiers[1].AdditionalPrice == 0m &&
                   immBurger.Modifiers[2].ProductId == null && immBurger.Modifiers[2].ModifierName == "Extra Cheese" && immBurger.Modifiers[2].AdditionalPrice == 20m,
                "Immediate ProcessSaleAsync receipt must preserve Modifier.ProductId (25 vs null) matching DeserializeModifiers.");

            // Verify LoadSavedReceiptFromDbAsync + MapLocalOrderItems parity (simulates DeserializeModifiers + Transactions.razor offline load)
            ReceiptDto saved;
            List<OrderItemResponseDto> mappedLocalItems;
            using (var readDb = new LocalPosDbContext())
            {
                saved = (await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(readDb, checkoutRes.LocalTransactionId!, session))!;
                Assert(saved != null, "LoadSavedReceiptFromDbAsync must return saved receipt.");

                var localItems = await readDb.OrderItems.AsNoTracking()
                    .Where(i => i.OrderId == checkoutRes.LocalTransactionId)
                    .OrderBy(i => i.OrderItemId)
                    .ToListAsync();
                var metadata = await OfflineCheckoutService.LoadLocalProductMetadataAsync(readDb, localItems.Select(i => i.ProductId));
                mappedLocalItems = OfflineCheckoutService.MapLocalOrderItems(localItems, metadata);
            }

            Assert(saved!.Items.Count == 4 && mappedLocalItems.Count == 4, "Saved/mapped items count must equal 4.");
            for (int i = 0; i < 4; i++)
            {
                var imItem = immediate.Items[i];
                var svItem = saved.Items[i];
                var mpItem = mappedLocalItems[i];
                Assert(imItem.Modifiers.Count == svItem.Modifiers.Count && imItem.Modifiers.Count == mpItem.Modifiers.Count,
                    $"Modifier count mismatch at item {i}.");
                for (int m = 0; m < imItem.Modifiers.Count; m++)
                {
                    Assert(imItem.Modifiers[m].ProductId == svItem.Modifiers[m].ProductId &&
                           imItem.Modifiers[m].ProductId == mpItem.Modifiers[m].ProductId &&
                           imItem.Modifiers[m].ModifierName == svItem.Modifiers[m].ModifierName &&
                           imItem.Modifiers[m].AdditionalPrice == svItem.Modifiers[m].AdditionalPrice &&
                           ReceiptPresentationHelper.ShouldShowFreeModifierLabel(imItem, imItem.Modifiers[m]) ==
                           ReceiptPresentationHelper.ShouldShowFreeModifierLabel(mpItem, mpItem.Modifiers[m]),
                        $"Immediate vs DeserializeModifiers/MapLocalOrderItems modifier parity mismatch at item {i} mod {m}.");
                }
            }

            // Render HTML, ESC/POS 58mm, and ESC/POS 80mm for BOTH immediate and saved receipts
            var (htmlImm, esc58Imm, esc80Imm, lines58Imm, lines80Imm) = RenderAllFormats(immediate);
            var (htmlSav, esc58Sav, esc80Sav, _, _) = RenderAllFormats(saved);

            Assert(htmlImm == htmlSav, "Immediate and DB-loaded HTML receipts must match identically.");
            Assert(esc58Imm == esc58Sav && esc80Imm == esc80Sav, "Immediate and DB-loaded 58mm/80mm ESC/POS receipts must match identically.");

            // HTML checks: (Free) on unlinked weight modifiers & linked regular modifier, [COMPLIMENTARY] on linked items, NO (Free) on "No Onions"
            Assert(htmlImm.Contains($"<span class=\"mod-name\">+ 2x Sarsa Cup</span><span class=\"mod-price text-muted\">(Free)</span>", StringComparison.Ordinal) &&
                   htmlImm.Contains($"<span class=\"mod-name\">+ {WebUtility.HtmlEncode(truncatedSideName)}</span><span class=\"mod-price text-muted\">(Free)</span>", StringComparison.Ordinal) &&
                   htmlImm.Contains("<span class=\"mod-name\">+ Free Extra Atchara</span><span class=\"mod-price text-muted\">(Free)</span>", StringComparison.Ordinal) &&
                   htmlImm.Contains("<span class=\"mod-name\">+ Extra Cheese</span><span class=\"mod-price\">(PHP 20.00)</span>", StringComparison.Ordinal) &&
                   htmlImm.Contains("<div class=\"modifier-line\"><span class=\"mod-name\">+ No Onions</span></div>", StringComparison.Ordinal) &&
                   htmlImm.Contains("Coke 330ml [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   htmlImm.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal),
                "HTML receipt must show (Free) for unlinked weight & linked modifiers, [COMPLIMENTARY] for linked items, and NO (Free) for 'No Onions'.");

            // ESC/POS 58mm (32-col) and 80mm (48-col) checks
            Assert(lines58Imm.All(l => l.Length <= 32), $"58mm receipt lines must all be <= 32 cols; max={lines58Imm.Max(l => l.Length)}.");
            Assert(lines80Imm.All(l => l.Length <= 48), $"80mm receipt lines must all be <= 48 cols; max={lines80Imm.Max(l => l.Length)}.");

            AssertRightAlignedPair(lines58Imm, 32, "+ 2x Sarsa Cup", "(Free)");
            AssertRightAlignedPair(lines80Imm, 48, "+ 2x Sarsa Cup", "(Free)");
            AssertRightAlignedPair(lines58Imm, 32, "+ Free Extra Atchara", "(Free)");
            AssertRightAlignedPair(lines80Imm, 48, "+ Free Extra Atchara", "(Free)");
            AssertRightAlignedPair(lines58Imm, 32, "+ Extra Cheese", "(PHP 20.00)");
            AssertRightAlignedPair(lines80Imm, 48, "+ Extra Cheese", "(PHP 20.00)");

            string norm58 = string.Join(" ", lines58Imm.Select(l => l.Trim()));
            string norm80 = string.Join(" ", lines80Imm.Select(l => l.Trim()));
            Assert(norm58.Contains(truncatedSideName, StringComparison.Ordinal) && norm80.Contains(truncatedSideName, StringComparison.Ordinal),
                "Both 58mm and 80mm receipts must preserve the 100-char truncated modifier name.");
            Assert(lines58Imm.Any(l => l == "+ No Onions") && !lines58Imm.Any(l => l.Contains("No Onions", StringComparison.Ordinal) && l.Contains("(Free)", StringComparison.Ordinal)),
                "58mm ESC/POS must render '+ No Onions' WITHOUT '(Free)'.");
            Assert(lines80Imm.Any(l => l == "+ No Onions") && !lines80Imm.Any(l => l.Contains("No Onions", StringComparison.Ordinal) && l.Contains("(Free)", StringComparison.Ordinal)),
                "80mm ESC/POS must render '+ No Onions' WITHOUT '(Free)'.");
            Assert(norm58.Contains("Coke 330ml [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   norm58.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal),
                "58mm ESC/POS must preserve both linked [COMPLIMENTARY] items.");
            Assert(esc80Imm.Contains("Coke 330ml [COMPLIMENTARY]", StringComparison.Ordinal) &&
                   esc80Imm.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.Ordinal),
                "80mm ESC/POS must preserve both linked [COMPLIMENTARY] items.");
            Assert(lines58Imm.Count(l => l.Length == 32 && l.EndsWith("(Free)", StringComparison.Ordinal)) == 3 &&
                   lines80Imm.Count(l => l.Length == 48 && l.EndsWith("(Free)", StringComparison.Ordinal)) == 3,
                "58mm and 80mm ESC/POS receipts must each right-align exactly 3 (Free) labels.");

            // PreparationTicketRouter + BuildPreparationTicket + PreparationTicketHtmlFormatter
            var tickets = PreparationTicketRouter.GenerateTickets(immediate, "Kitchen-1", "Bar-1");
            Assert(tickets.Count == 2, $"Expected 2 preparation tickets (KITCHEN + BAR), got {tickets.Count}.");

            var kitchenTicket = tickets.Single(t => t.Station == "KITCHEN");
            var barTicket = tickets.Single(t => t.Station == "BAR");

            Assert(kitchenTicket.Items.Count == 3 &&
                   kitchenTicket.Items[0].ProductId == 20 &&
                   kitchenTicket.Items[0].Modifiers.SequenceEqual(new[] { "2x Sarsa Cup", truncatedSideName }) &&
                   kitchenTicket.Items[1].ProductId == 25 &&
                   kitchenTicket.Items[2].ProductId == 30,
                "Kitchen ticket must include Lechon (with unlinked weight modifiers), linked Atchara Cup, and Classic Burger.");
            Assert(barTicket.Items.Count == 1 && barTicket.Items[0].ProductId == 22 && barTicket.Items[0].Quantity == 2m,
                "Bar ticket must independently route linked complimentary Coke 330ml.");

            var kitchenEsc58 = string.Join("\n", DecodeEscPosLines(EscPosReceiptBuilder.BuildPreparationTicket(kitchenTicket, CreateReceiptTestPrinterConfig(58))));
            var kitchenEsc80 = string.Join("\n", DecodeEscPosLines(EscPosReceiptBuilder.BuildPreparationTicket(kitchenTicket, CreateReceiptTestPrinterConfig(80))));
            var barEsc80 = string.Join("\n", DecodeEscPosLines(EscPosReceiptBuilder.BuildPreparationTicket(barTicket, CreateReceiptTestPrinterConfig(80))));
            var kitchenHtml = PreparationTicketHtmlFormatter.Format(kitchenTicket);

            Assert(kitchenEsc58.Contains("  + 2x Sarsa Cup", StringComparison.Ordinal) &&
                   kitchenEsc80.Contains("  + 2x Sarsa Cup", StringComparison.Ordinal) &&
                   kitchenEsc80.Contains("2x Atchara Cup", StringComparison.Ordinal) &&
                   kitchenHtml.Contains("+ 2x Sarsa Cup", StringComparison.Ordinal) &&
                   barEsc80.Contains("2x Coke 330ml", StringComparison.Ordinal),
                "Kitchen and Bar ESC/POS & HTML prep tickets must render unlinked weight modifiers and linked complimentary routing.");

            // Offline -> Online sync parity via OfflineSyncController + ReceiptService
            var syncCtrl = CreateTerminalSyncController(serverDb);
            var cashCtrl = CreateController(serverDb, "POS-1", "Terminal");
            using var inProcHttp = new HttpClient(new InProcSyncHandler(async req =>
            {
                string path = req.RequestUri!.AbsolutePath;
                var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (path.EndsWith("api/cash-drawer/open-shift", StringComparison.OrdinalIgnoreCase))
                {
                    var dto = JsonSerializer.Deserialize<OpenShiftDto>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                    return ObjectResultToHttpResponse(await cashCtrl.OpenShift(dto));
                }
                if (path.EndsWith("api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase))
                {
                    var dto = JsonSerializer.Deserialize<OfflineTransactionSyncRequest>(await req.Content!.ReadAsStringAsync(), jsonOpts)!;
                    return ActionResultToHttpResponse(await syncCtrl.SyncTransaction(dto));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }))
            {
                BaseAddress = new Uri("http://localhost/")
            };
            inProcHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

            using var syncDb = new LocalPosDbContext();
            var syncSvc = new OfflineTransactionSyncService(syncDb);
            var queue = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(queue.Count == 2, $"Expected SHIFT_OPEN + Order in outbox, got {queue.Count}.");
            Assert(await syncSvc.UploadTransactionAsync(queue[0], inProcHttp), "SHIFT_OPEN sync must succeed.");
            Assert(await syncSvc.UploadTransactionAsync(queue[1], inProcHttp), "Order sync with 100-char unlinked modifier must succeed.");

            var srvOrder = await serverDb.Orders.AsNoTracking().SingleAsync();
            var onlineReceipt = await new POS.Api.Services.ReceiptService(serverDb).GetReceiptAsync(srvOrder.OrderId, isReprint: true);
            Assert(onlineReceipt != null && onlineReceipt.Items.Count == 4, "Server ReceiptService must hydrate all 4 items.");
            Assert(onlineReceipt!.Items[0].Modifiers.Count == 2 &&
                   onlineReceipt.Items[0].Modifiers[0].ProductId == null &&
                   onlineReceipt.Items[0].Modifiers[1].ModifierName == truncatedSideName &&
                   onlineReceipt.Items[3].Modifiers[1].ProductId == 25,
                "Server ReceiptDto must preserve unlinked weight modifiers (ProductId==null, 100-char name) and linked modifier ProductId.");

            string onlineHtml = ReceiptHtmlFormatter.Format(onlineReceipt);
            Assert(onlineHtml.Contains("+ 2x Sarsa Cup</span><span class=\"mod-price text-muted\">(Free)</span>", StringComparison.Ordinal) &&
                   onlineHtml.Contains("<div class=\"modifier-line\"><span class=\"mod-name\">+ No Onions</span></div>", StringComparison.Ordinal),
                "Server-reprinted HTML receipt must match offline (Free) formatting rules.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }
}
