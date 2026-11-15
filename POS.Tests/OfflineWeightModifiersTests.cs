using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public static partial class Program
{
    // WM-01: Offline weight-based modifier loading & ordering via ReferenceDataSyncService.GetLocalProductModifierGroupsAsync
    private static async Task TestWeightModifiers01_OfflineModifierLoadingAndOrdering()
    {
        using var scope = new TempLocalDbScope("wm01");

        using (var seedDb = new LocalPosDbContext())
        {
            seedDb.InitializeDatabase();

            seedDb.Categories.Add(new LocalCategory { CategoryId = 1, Name = "Main", DisplayOrder = 1 });

            seedDb.Products.AddRange(
                new LocalProduct
                {
                    ProductId = 20,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Lechon",
                    Price = 950m,
                    PricingMethod = PricingMethods.WeightBased,
                    UnitOfMeasure = "kg",
                    IsKitchenItem = true,
                    IsActive = true
                },
                new LocalProduct
                {
                    ProductId = 21,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Plain Rice",
                    Price = 35m,
                    PricingMethod = PricingMethods.Fixed,
                    IsKitchenItem = true,
                    IsActive = true
                },
                new LocalProduct
                {
                    ProductId = 22,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Coke 330ml",
                    Price = 50m,
                    PricingMethod = PricingMethods.Fixed,
                    IsBarItem = true,
                    IsActive = true
                },
                new LocalProduct
                {
                    ProductId = 23,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Sprite 330ml",
                    Price = 50m,
                    PricingMethod = PricingMethods.Fixed,
                    IsBarItem = true,
                    IsActive = true
                },
                new LocalProduct
                {
                    ProductId = 24,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Royal 330ml",
                    Price = 50m,
                    PricingMethod = PricingMethods.Fixed,
                    IsBarItem = true,
                    IsActive = false
                },
                new LocalProduct
                {
                    ProductId = 25,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Atchara Cup",
                    Price = 30m,
                    PricingMethod = PricingMethods.Fixed,
                    IsKitchenItem = true,
                    IsActive = true
                },
                new LocalProduct
                {
                    ProductId = 99,
                    CategoryId = 1,
                    CategoryName = "Main",
                    Name = "Pizza",
                    Price = 350m,
                    PricingMethod = PricingMethods.Fixed,
                    IsKitchenItem = true,
                    IsActive = true
                });

            // ModifierGroup.DisplayOrder matches ModifierGroupId (10 < 20 < 30), while ProductModifierGroup.DisplayOrder reverses it (30, 20, 10)
            seedDb.ModifierGroups.AddRange(
                new LocalModifierGroup
                {
                    ModifierGroupId = 10,
                    Name = "Paid Upgrade Add-ons",
                    IsRequired = false,
                    MinSelections = 0,
                    MaxSelections = 3,
                    DisplayOrder = 10,
                    IsActive = true
                },
                new LocalModifierGroup
                {
                    ModifierGroupId = 20,
                    Name = "Complimentary Sides",
                    IsRequired = false,
                    MinSelections = 0,
                    MaxSelections = 2,
                    DisplayOrder = 20,
                    IsActive = true
                },
                new LocalModifierGroup
                {
                    ModifierGroupId = 30,
                    Name = "Complimentary Drink",
                    IsRequired = true,
                    MinSelections = 1,
                    MaxSelections = 1,
                    DisplayOrder = 30,
                    IsActive = true
                },
                new LocalModifierGroup
                {
                    ModifierGroupId = 99,
                    Name = "Unrelated Pizza Toppings",
                    IsRequired = false,
                    MinSelections = 0,
                    MaxSelections = 5,
                    DisplayOrder = 1,
                    IsActive = true
                });

            seedDb.ProductModifierGroups.AddRange(
                new LocalProductModifierGroup { ProductModifierGroupId = 1, ProductId = 20, ModifierGroupId = 10, DisplayOrder = 30 },
                new LocalProductModifierGroup { ProductModifierGroupId = 2, ProductId = 20, ModifierGroupId = 20, DisplayOrder = 20 },
                new LocalProductModifierGroup { ProductModifierGroupId = 3, ProductId = 20, ModifierGroupId = 30, DisplayOrder = 10 },
                new LocalProductModifierGroup { ProductModifierGroupId = 4, ProductId = 20, ModifierGroupId = 30, DisplayOrder = 40 }, // duplicate mapping must deduplicate keeping first (DisplayOrder=10)
                new LocalProductModifierGroup { ProductModifierGroupId = 5, ProductId = 99, ModifierGroupId = 99, DisplayOrder = 1 });

            // Options with DisplayOrder REVERSED relative to ModifierOptionId, mixing active/inactive and null/non-null ProductId
            seedDb.ModifierOptions.AddRange(
                new LocalModifierOption
                {
                    ModifierOptionId = 101,
                    ModifierGroupId = 30,
                    ProductId = 24,
                    ProductName = "Royal 330ml",
                    Name = "Royal 330ml",
                    Price = 0m,
                    DisplayOrder = 3,
                    IsActive = false
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 102,
                    ModifierGroupId = 30,
                    ProductId = 23,
                    ProductName = "Sprite 330ml",
                    Name = "Sprite 330ml",
                    Price = 0m,
                    DisplayOrder = 2,
                    IsActive = true
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 103,
                    ModifierGroupId = 30,
                    ProductId = 22,
                    ProductName = "Coke 330ml",
                    Name = "Coke 330ml",
                    Price = 0m,
                    DisplayOrder = 1,
                    IsActive = true
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 201,
                    ModifierGroupId = 10,
                    ProductId = null,
                    ProductName = null,
                    Name = "Extra Chili Oil",
                    Price = 25m,
                    DisplayOrder = 2,
                    IsActive = true
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 202,
                    ModifierGroupId = 10,
                    ProductId = null,
                    ProductName = null,
                    Name = "Extra Crispy Skin Pack",
                    Price = 60m,
                    DisplayOrder = 1,
                    IsActive = true
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 301,
                    ModifierGroupId = 20,
                    ProductId = 25,
                    ProductName = "Atchara Cup",
                    Name = "Atchara Cup",
                    Price = 0m,
                    DisplayOrder = 1,
                    IsActive = true
                },
                new LocalModifierOption
                {
                    ModifierOptionId = 991,
                    ModifierGroupId = 99,
                    ProductId = null,
                    Name = "Pepperoni",
                    Price = 45m,
                    DisplayOrder = 1,
                    IsActive = true
                });

            await seedDb.SaveChangesAsync();
        }

        using var readDb = new LocalPosDbContext();

        // 1. Load WeightBased product modifiers via real EF Core SQLite query (no List.IndexOf translation failure)
        var lechonGroups = await ReferenceDataSyncService.GetLocalProductModifierGroupsAsync(readDb, 20);
        Assert(lechonGroups.Count == 3,
            $"Expected 3 deduplicated modifier groups for Lechon (unrelated group 99 excluded), got {lechonGroups.Count}.");

        Assert(lechonGroups.Select(g => g.ModifierGroupId).SequenceEqual(new[] { 30, 20, 10 }),
            $"Groups must follow ProductModifierGroup.DisplayOrder (30, 20, 10), got [{string.Join(",", lechonGroups.Select(g => g.ModifierGroupId))}].");
        Assert(lechonGroups.Select(g => g.DisplayOrder).SequenceEqual(new[] { 10, 20, 30 }),
            $"Group DisplayOrder values must reflect mapping DisplayOrder [10, 20, 30], got [{string.Join(",", lechonGroups.Select(g => g.DisplayOrder))}].");

        // 2. Verify inventory-linked complimentary group options preserve reversed DisplayOrder, IsActive, ProductId, and Price == 0
        var drinkGroup = lechonGroups[0];
        Assert(drinkGroup.ModifierGroupName == "Complimentary Drink" && drinkGroup.IsRequired && drinkGroup.MinSelections == 1 && drinkGroup.MaxSelections == 1,
            "Complimentary Drink group rules must match seeded metadata.");
        Assert(drinkGroup.Options.Select(o => o.ModifierOptionId).SequenceEqual(new[] { 103, 102, 101 }),
            $"Drink options must follow DisplayOrder [1, 2, 3] -> [103, 102, 101], got [{string.Join(",", drinkGroup.Options.Select(o => o.ModifierOptionId))}].");
        Assert(drinkGroup.Options.Select(o => o.IsActive).SequenceEqual(new[] { true, true, false }),
            "Drink options must preserve IsActive flags.");
        Assert(drinkGroup.Options.Select(o => o.ProductId).SequenceEqual(new int?[] { 22, 23, 24 }),
            "Inventory-linked complimentary options must preserve linked ProductIds.");
        Assert(drinkGroup.Options.All(o => o.Price == 0m),
            "Complimentary options must preserve Price == 0m.");

        // 3. Verify non-inventory paid options preserve DisplayOrder, ProductId == null, and Price > 0
        var paidGroup = lechonGroups[2];
        Assert(paidGroup.Options.Select(o => o.ModifierOptionId).SequenceEqual(new[] { 202, 201 }),
            $"Paid add-ons must follow DisplayOrder [1, 2] -> [202, 201], got [{string.Join(",", paidGroup.Options.Select(o => o.ModifierOptionId))}].");
        Assert(paidGroup.Options.All(o => o.ProductId == null && o.ProductName == null && o.IsActive),
            "Non-inventory options must keep ProductId == null.");
        Assert(paidGroup.Options.Select(o => o.Price).SequenceEqual(new[] { 60m, 25m }),
            "Non-inventory options must preserve Prices [60, 25].");

        // 4. Product without modifiers & guard cases return empty list cleanly
        var noModGroups = await ReferenceDataSyncService.GetLocalProductModifierGroupsAsync(readDb, 21);
        Assert(noModGroups.Count == 0, $"Product without modifiers must return empty list (got {noModGroups.Count}).");
        Assert((await ReferenceDataSyncService.GetLocalProductModifierGroupsAsync(readDb, 0)).Count == 0, "Invalid productId <= 0 must return empty list.");
        Assert((await ReferenceDataSyncService.GetLocalProductModifierGroupsAsync(null!, 20)).Count == 0, "Null db must return empty list.");
    }

    // WM-02: Regular multi-select/min-max/required pricing + weight complimentary thresholds, preview alignment & RemoveItem isolation
    private static Task TestWeightModifiers02_CartPricingComplimentaryAndRemoveIsolation()
    {
        var session = CreateOfflineTestSession();
        using var cart = new CartService(session);

        // 1. Fixed product with required (IsRequired=true, MinSelections=1, MaxSelections=1) & optional multi-select (MaxSelections=2) non-inventory modifiers
        var milkTea = new ProductDto
        {
            ProductId = 50,
            CategoryId = 2,
            CategoryName = "Beverages",
            Name = "Signature Milk Tea",
            Price = 120m,
            PricingMethod = PricingMethods.Fixed,
            TaxName = "VATable",
            IsBarItem = true,
            IsActive = true
        };

        var sugarGroup = new ProductModifierGroupDto
        {
            ProductModifierGroupId = 1,
            ProductId = 50,
            ModifierGroupId = 1,
            ModifierGroupName = "Sugar Level",
            IsRequired = true,
            MinSelections = 1,
            MaxSelections = 1,
            DisplayOrder = 1,
            Options = new List<ModifierOptionDto>
            {
                new() { ModifierOptionId = 501, ModifierGroupId = 1, ProductId = null, Name = "50% Sugar", Price = 0m, DisplayOrder = 1, IsActive = true }
            }
        };

        var toppingsGroup = new ProductModifierGroupDto
        {
            ProductModifierGroupId = 2,
            ProductId = 50,
            ModifierGroupId = 2,
            ModifierGroupName = "Toppings",
            IsRequired = false,
            MinSelections = 0,
            MaxSelections = 2,
            DisplayOrder = 2,
            Options = new List<ModifierOptionDto>
            {
                new() { ModifierOptionId = 502, ModifierGroupId = 2, ProductId = null, Name = "Boba Pearls", Price = 20m, DisplayOrder = 1, IsActive = true },
                new() { ModifierOptionId = 503, ModifierGroupId = 2, ProductId = null, Name = "Cream Cheese Foam", Price = 30m, DisplayOrder = 2, IsActive = true },
                new() { ModifierOptionId = 504, ModifierGroupId = 2, ProductId = null, Name = "Pudding", Price = 25m, DisplayOrder = 3, IsActive = true }
            }
        };

        static bool ValidateSelection(List<ProductModifierGroupDto> groups, Dictionary<int, HashSet<int>> selected)
        {
            foreach (var group in groups)
            {
                int count = selected.TryGetValue(group.ModifierGroupId, out var set) ? set.Count : 0;
                int requiredMin = group.IsRequired ? Math.Max(1, group.MinSelections) : group.MinSelections;
                if (requiredMin > 0 && count < requiredMin) return false;
                if (group.MaxSelections > 0 && count > group.MaxSelections) return false;
            }
            return true;
        }

        var fixedGroups = new List<ProductModifierGroupDto> { sugarGroup, toppingsGroup };
        Assert(!ValidateSelection(fixedGroups, new Dictionary<int, HashSet<int>> { [2] = new() { 502 } }),
            "Missing required group selection (MinSelections=1) must fail validation.");
        Assert(!ValidateSelection(fixedGroups, new Dictionary<int, HashSet<int>> { [1] = new() { 501 }, [2] = new() { 502, 503, 504 } }),
            "Exceeding optional group MaxSelections=2 must fail validation.");
        Assert(ValidateSelection(fixedGroups, new Dictionary<int, HashSet<int>> { [1] = new() { 501 }, [2] = new() { 502, 503 } }),
            "Required group satisfied + 2 optional selections within MaxSelections=2 must pass validation.");

        var selectedBothToppings = new List<ModifierOptionDto> { sugarGroup.Options[0], toppingsGroup.Options[0], toppingsGroup.Options[1] };
        cart.AddItem(milkTea, 2, selectedBothToppings, "  Less ice  ");
        cart.AddItem(milkTea, 1, new List<ModifierOptionDto> { sugarGroup.Options[0], toppingsGroup.Options[0] }, "Less ice");

        Assert(cart.Items.Count == 2, $"Distinct modifier sets must create 2 cart lines (got {cart.Items.Count}).");
        var lineBoth = cart.Items[0];
        var lineOneTopping = cart.Items[1];
        Assert(lineBoth.UnitPrice == 170.00m && lineBoth.LineTotal == 340.00m && lineBoth.SpecialInstructions == "Less ice",
            $"Expected UnitPrice=170.00, LineTotal=340.00 for qty 2 + 50m extras (got UnitPrice={lineBoth.UnitPrice}, LineTotal={lineBoth.LineTotal}).");
        Assert(lineBoth.SelectedModifiers.All(m => m.ProductId == null),
            "Non-inventory regular modifiers must keep ProductId == null.");
        Assert(lineOneTopping.UnitPrice == 140.00m && lineOneTopping.LineTotal == 140.00m && cart.Subtotal == 480.00m,
            $"Expected second line 140.00 and cart Subtotal 480.00 (got {lineOneTopping.LineTotal} / {cart.Subtotal}).");

        // 2. WeightBased < 1.0 kg Amount Mode (₱300 -> 0 sets, TargetAmount=300) and >= 1.0 kg Weight & Amount Modes + preview parity
        cart.Clear();
        var lechon = new ProductDto
        {
            ProductId = 20,
            CategoryId = 1,
            CategoryName = "Main",
            Name = "Lechon",
            Price = 950m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = "kg",
            TaxName = "VATable",
            IsKitchenItem = true,
            IsActive = true
        };

        decimal wt300 = 300m / lechon.Price;
        int sets300 = (int)Math.Floor(wt300);
        Assert(sets300 == 0, $"₱300 at ₱950/kg must yield 0 complimentary sets (got {sets300}).");
        cart.AddWeightBasedItem(lechon, wt300, targetAmount: 300m, complimentaryItems: null);

        var line300 = cart.Items.Single();
        Assert(line300.PricingMethod == PricingMethods.WeightBased && line300.TargetAmount == 300m && line300.LineTotal == 300.00m,
            $"<1.0kg Amount Mode must preserve TargetAmount=300 and LineTotal=300.00 (got Target={line300.TargetAmount}, LineTotal={line300.LineTotal}).");

        // Weight Mode 1.5 kg -> 1 complimentary set
        decimal wt15 = 1.5m;
        int sets15 = (int)Math.Floor(wt15);
        Assert(sets15 == 1, $"1.5 kg must yield 1 complimentary set (got {sets15}).");
        cart.AddWeightBasedItem(
            lechon,
            wt15,
            targetAmount: null,
            specialInstructions: "Chopped",
            complimentaryItems: new List<CartItem>
            {
                new() { ProductId = 22, ProductName = "Coke 330ml", BasePrice = 50m, Quantity = sets15, IsBarItem = true },
                new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = sets15, IsKitchenItem = true }
            });

        // Amount Mode ₱2,500 -> 2 complimentary sets
        decimal wt2500 = 2500m / lechon.Price;
        int sets2500 = (int)Math.Floor(wt2500);
        Assert(sets2500 == 2, $"₱2,500 at ₱950/kg must yield 2 complimentary sets (got {sets2500}).");
        cart.AddWeightBasedItem(
            lechon,
            wt2500,
            targetAmount: 2500m,
            specialInstructions: "Whole skin",
            complimentaryItems: new List<CartItem>
            {
                new() { ProductId = 23, ProductName = "Sprite 330ml", BasePrice = 50m, Quantity = sets2500, IsBarItem = true },
                new() { ProductId = 26, ProductName = "Sarsa Cup", BasePrice = 20m, Quantity = sets2500, IsKitchenItem = true }
            });

        var weightLine15 = cart.Items.Single(i => !i.IsComplimentary && i.TargetAmount == null);
        var amountLine2500 = cart.Items.Single(i => !i.IsComplimentary && i.TargetAmount == 2500m);

        var line15Children = cart.Items.Where(i => i.IsComplimentary && i.ParentCartItemId == weightLine15.CartItemId).ToList();
        var line2500Children = cart.Items.Where(i => i.IsComplimentary && i.ParentCartItemId == amountLine2500.CartItemId).ToList();
        Assert(line15Children.Count == 2 && line15Children.All(c => c.ParentProductId == 20 && c.UnitPrice == 0m && c.LineTotal == 0m),
            "1.5 kg Lechon must bind 2 zero-priced complimentary children via ParentCartItemId and ParentProductId=20.");
        Assert(line2500Children.Count == 2 && line2500Children.All(c => c.ParentProductId == 20 && c.UnitPrice == 0m && c.LineTotal == 0m && c.Quantity == 2m),
            "₱2,500 Lechon must bind 2 zero-priced complimentary children with qty 2.");

        // 3. CartItemCalculationDto preview alignment (matching NewSale.razor ProceedToPayment projection)
        var previewReq = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NONE",
            Items = cart.Items.Select(i => new CartItemCalculationDto
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.IsComplimentary ? 0m : i.BasePrice,
                TargetAmount = i.IsComplimentary ? 0m : i.TargetAmount,
                ModifierTotal = i.IsComplimentary ? 0m : (i.SelectedModifiers?.Sum(m => m.Price) ?? 0m),
                IsComplimentary = i.IsComplimentary,
                TaxName = i.TaxName ?? "VATable",
                IsEligibleForStatutoryDiscount = true
            }).ToList()
        };
        var previewRes = new LocalDiscountCalculationService().CalculateOrder(previewReq);
        Assert(cart.Subtotal == 4225.00m && previewRes.Subtotal == cart.Subtotal && previewRes.TotalAmount == 4225.00m,
            $"Preview calculation ({previewRes.Subtotal}) must match Cart.Subtotal ({cart.Subtotal} == 4225.00) with zero rounding drift.");

        // 4. RemoveItem removes ONLY the targeted weight line's complimentary items, never a sibling line sharing ParentProductId
        cart.RemoveItem(weightLine15);
        Assert(!cart.Items.Any(i => i.CartItemId == weightLine15.CartItemId || i.ParentCartItemId == weightLine15.CartItemId),
            "Removed 1.5 kg line and its complimentary items must be deleted from cart.");
        Assert(cart.Items.Contains(line300) && cart.Items.Contains(amountLine2500),
            "Sibling ₱300 and ₱2,500 Lechon lines must remain in cart.");
        Assert(cart.Items.Count(i => i.IsComplimentary && i.ParentCartItemId == amountLine2500.CartItemId) == 2,
            "Sibling ₱2,500 Lechon complimentary items sharing ParentProductId=20 must NOT be deleted.");
        Assert(cart.Subtotal == 2800.00m,
            $"Cart Subtotal after removing 1.5 kg line must be ₱2,800.00 (got {cart.Subtotal}).");

        return Task.CompletedTask;
    }

    // WM-03: Offline SQLite sale persistence -> app restart recovery -> reconnect sync against real OfflineSyncController
    private static async Task TestWeightModifiers03_OfflinePersistenceRestartRecoveryAndReconnectSync()
    {
        using var scope = new TempLocalDbScope("wm03");
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
                new Product { ProductId = 20, CategoryId = 1, Name = "Lechon", ProductType = "Standard", PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", Price = 950m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 22, CategoryId = 1, Name = "Sprite 330ml", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 50m, IsBarItem = true, IsActive = true },
                new Product { ProductId = 25, CategoryId = 1, Name = "Atchara Cup", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 30m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 50, CategoryId = 1, Name = "Signature Milk Tea", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 120m, IsBarItem = true, IsActive = true });

            serverDb.BranchInventories.AddRange(
                new BranchInventory { BranchId = 1, ProductId = 20, StockQuantity = 10, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 22, StockQuantity = 20, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 25, StockQuantity = 30, LastUpdated = dayUtc },
                new BranchInventory { BranchId = 1, ProductId = 50, StockQuantity = 15, LastUpdated = dayUtc });

            serverDb.ModifierGroups.Add(new ModifierGroup { ModifierGroupId = 2, Name = "Toppings", MaxSelections = 2, IsActive = true });
            serverDb.ModifierOptions.AddRange(
                new ModifierOption { ModifierOptionId = 502, ModifierGroupId = 2, ProductId = null, Name = "Boba Pearls", Price = 20m, DisplayOrder = 1, IsActive = true },
                new ModifierOption { ModifierOptionId = 503, ModifierGroupId = 2, ProductId = null, Name = "Cream Cheese Foam", Price = 30m, DisplayOrder = 2, IsActive = true });

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
                    new LocalProduct { ProductId = 20, CategoryId = 1, CategoryName = "Main", Name = "Lechon", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 22, CategoryId = 1, CategoryName = "Main", Name = "Sprite 330ml", Price = 50m, PricingMethod = PricingMethods.Fixed, TaxName = "VATable", IsBarItem = true, IsActive = true },
                    new LocalProduct { ProductId = 25, CategoryId = 1, CategoryName = "Main", Name = "Atchara Cup", Price = 30m, PricingMethod = PricingMethods.Fixed, TaxName = "VATable", IsKitchenItem = true, IsActive = true },
                    new LocalProduct { ProductId = 50, CategoryId = 1, CategoryName = "Main", Name = "Signature Milk Tea", Price = 120m, PricingMethod = PricingMethods.Fixed, TaxName = "VATable", IsBarItem = true, IsActive = true });
                await seedLocal.SaveChangesAsync();
            }

            // 1. Open offline shift and build cart with WeightBased + complimentary items + instructions AND Fixed + multi-select paid modifiers (ProductId == null)
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline"))) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOfflineTestSession(CreateSyntheticJwt("cashier1", "Cashier"), "cashier1", "Cashier");

            var openedShift = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1000m,
                ClientShiftId = "shift-wm03-1"
            }, "cashier1", offline: true);
            Assert(openedShift.ShiftId < 0, "Offline shift must receive negative temporary ShiftId.");

            using var cart = new CartService(session);
            var lechonDto = new ProductDto { ProductId = 20, CategoryId = 1, Name = "Lechon", Price = 950m, PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", TaxName = "VATable", IsKitchenItem = true, IsActive = true };
            var milkTeaDto = new ProductDto { ProductId = 50, CategoryId = 1, Name = "Signature Milk Tea", Price = 120m, PricingMethod = PricingMethods.Fixed, TaxName = "VATable", IsBarItem = true, IsActive = true };

            cart.AddWeightBasedItem(
                lechonDto,
                weight: 1500m / 950m,
                targetAmount: 1500m,
                selectedModifiers: null,
                specialInstructions: "Crispy skin, chop small",
                complimentaryItems: new List<CartItem>
                {
                    new() { ProductId = 22, ProductName = "Sprite 330ml", BasePrice = 50m, Quantity = 1m, IsBarItem = true },
                    new() { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 1m, IsKitchenItem = true }
                });

            cart.AddItem(
                milkTeaDto,
                quantity: 2,
                selectedModifiers: new List<ModifierOptionDto>
                {
                    new() { ModifierOptionId = 502, ModifierGroupId = 2, ProductId = null, Name = "Boba Pearls", Price = 20m, DisplayOrder = 1, IsActive = true },
                    new() { ModifierOptionId = 503, ModifierGroupId = 2, ProductId = null, Name = "Cream Cheese Foam", Price = 30m, DisplayOrder = 2, IsActive = true }
                },
                specialInstructions: "Less ice");

            var orderCreate = new OrderCreateDto
            {
                ShiftId = openedShift.ShiftId,
                ClientShiftId = openedShift.ClientShiftId,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                Items = cart.Items.Select(ci => new OrderItemCreateDto
                {
                    ProductId = ci.ProductId,
                    ProductName = ci.ProductName,
                    ProductShortCode = ci.ProductShortCode,
                    Quantity = ci.Quantity,
                    BasePrice = ci.BasePrice,
                    PricingMethod = ci.PricingMethod,
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
                checkoutRes = await new OfflineCheckoutService(coDb, session).ProcessSaleAsync(orderCreate, 2000m, "CASH", null, "cashier1");
            }
            Assert(checkoutRes.Success && !string.IsNullOrWhiteSpace(checkoutRes.LocalTransactionId),
                $"Offline checkout must succeed: {checkoutRes.ErrorMessage}");
            Assert(checkoutRes.Receipt != null && checkoutRes.Receipt.TotalAmount == 1840.00m && checkoutRes.ChangeAmount == 160.00m,
                $"Expected TotalAmount=1840.00 and Change=160.00 (got Total={checkoutRes.Receipt?.TotalAmount}, Change={checkoutRes.ChangeAmount}).");

            // 2. Verify SQLite LocalOrderItems persistence directly
            using (var sqliteAudit = new LocalPosDbContext())
            {
                var localItems = await sqliteAudit.OrderItems.AsNoTracking()
                    .Where(i => i.OrderId == checkoutRes.LocalTransactionId)
                    .OrderBy(i => i.OrderItemId)
                    .ToListAsync();
                Assert(localItems.Count == 4, $"Expected 4 LocalOrderItems, got {localItems.Count}.");

                var localLechon = localItems[0];
                var localSprite = localItems[1];
                var localAtchara = localItems[2];
                var localTea = localItems[3];

                Assert(localLechon.ProductId == 20 && localLechon.PricingMethod == PricingMethods.WeightBased &&
                       localLechon.TargetAmount == 1500m && localLechon.LineTotal == 1500m &&
                       !localLechon.IsComplimentary && localLechon.SpecialInstructions == "Crispy skin, chop small",
                    "Local Lechon OrderItem must persist WeightBased PricingMethod, TargetAmount=1500, LineTotal=1500, and SpecialInstructions.");

                Assert(localSprite.ProductId == 22 && localSprite.IsComplimentary && localSprite.ParentProductId == 20 && localSprite.LineTotal == 0m,
                    "Local complimentary Sprite must persist IsComplimentary=true, ParentProductId=20, LineTotal=0.");
                Assert(localAtchara.ProductId == 25 && localAtchara.IsComplimentary && localAtchara.ParentProductId == 20 && localAtchara.LineTotal == 0m,
                    "Local complimentary Atchara must persist IsComplimentary=true, ParentProductId=20, LineTotal=0.");

                Assert(localTea.ProductId == 50 && localTea.UnitPrice == 170m && localTea.LineTotal == 340m &&
                       localTea.SpecialInstructions == "Less ice" && !string.IsNullOrWhiteSpace(localTea.ModifiersJson),
                    "Local Milk Tea OrderItem must persist UnitPrice=170, LineTotal=340, SpecialInstructions, and ModifiersJson.");

                var savedMods = JsonSerializer.Deserialize<List<OrderItemModifierCreateDto>>(localTea.ModifiersJson!);
                Assert(savedMods != null && savedMods.Count == 2 && savedMods.All(m => m.ProductId == null) &&
                       savedMods.Sum(m => m.AdditionalPrice) == 50m,
                    "ModifiersJson must preserve both paid non-inventory modifiers with ProductId == null.");
            }

            // 3. Simulate offline app restart with fresh PosSessionService + fresh LocalPosDbContext
            var restartSession = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier");
            using (var restartDb = new LocalPosDbContext())
            {
                var recoveredReceipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(
                    restartDb, checkoutRes.LocalTransactionId!, restartSession);

                Assert(recoveredReceipt != null, "Restart recovery LoadSavedReceiptFromDbAsync must hydrate saved receipt.");
                Assert(recoveredReceipt!.InvoiceNumber == checkoutRes.InvoiceNumber &&
                       recoveredReceipt.Subtotal == 1840.00m &&
                       recoveredReceipt.TotalAmount == 1840.00m &&
                       recoveredReceipt.Items.Count == 4,
                    "Recovered receipt totals and item count must match pre-restart sale.");

                Assert(recoveredReceipt.Items.Select(i => i.ProductId).SequenceEqual(new[] { 20, 22, 25, 50 }),
                    "Recovered receipt item order must match saved SQLite order.");
                Assert(recoveredReceipt.Items[0].PricingMethod == PricingMethods.WeightBased &&
                       recoveredReceipt.Items[0].LineTotal == 1500.00m &&
                       recoveredReceipt.Items[0].SpecialInstructions == "Crispy skin, chop small",
                    "Recovered Lechon item must preserve WeightBased metadata and instructions.");
                Assert(recoveredReceipt.Items[1].IsComplimentary && recoveredReceipt.Items[1].ParentProductId == 20 &&
                       recoveredReceipt.Items[2].IsComplimentary && recoveredReceipt.Items[2].ParentProductId == 20,
                    "Recovered complimentary items must preserve flags and ParentProductId.");
                Assert(recoveredReceipt.Items[3].BasePrice == 120m &&
                       recoveredReceipt.Items[3].ModifierTotal == 50m &&
                       recoveredReceipt.Items[3].UnitPrice == 170m &&
                       recoveredReceipt.Items[3].LineTotal == 340m &&
                       recoveredReceipt.Items[3].SpecialInstructions == "Less ice" &&
                       recoveredReceipt.Items[3].Modifiers.Count == 2,
                    "Recovered Milk Tea item must preserve BasePrice, ModifierTotal, UnitPrice, LineTotal, instructions, and modifiers.");
            }

            // 4. Reconnect & upload outbox through OfflineTransactionSyncService -> real OfflineSyncController.SyncTransaction
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
            Assert(queue.Count == 2 && queue[0].EntityType == "SHIFT_OPEN" && queue[1].EntityType == "Order",
                $"Expected SHIFT_OPEN then Order in outbox (got {string.Join(",", queue.Select(q => q.EntityType))}).");

            Assert(await syncSvc.UploadTransactionAsync(queue[0], inProcHttp), "SHIFT_OPEN outbox sync must succeed.");
            Assert(await syncSvc.UploadTransactionAsync(queue[1], inProcHttp), "Order outbox sync must succeed.");

            using (var postSyncDb = new LocalPosDbContext())
            {
                var outboxes = await postSyncDb.SyncOutboxes.AsNoTracking().ToListAsync();
                Assert(outboxes.All(o => o.Status == "SYNCED"), "All outbox items must be marked SYNCED after upload.");
            }

            var srvOrder = await serverDb.Orders.AsNoTracking().SingleAsync();
            Assert(srvOrder.Subtotal == 1840.00m && srvOrder.TotalAmount == 1840.00m && srvOrder.InvoiceNumber == checkoutRes.InvoiceNumber,
                $"Server order must preserve Subtotal=1840.00, Total=1840.00, Invoice={checkoutRes.InvoiceNumber}.");

            var srvItems = await serverDb.OrderItems
                .AsNoTracking()
                .Include(i => i.OrderItemModifiers)
                .Where(i => i.OrderId == srvOrder.OrderId)
                .OrderBy(i => i.OrderItemId)
                .ToListAsync();
            Assert(srvItems.Select(i => i.ProductId).SequenceEqual(new[] { 20, 22, 25, 50 }),
                $"Server OrderItems order must match local sequence [20,22,25,50], got [{string.Join(",", srvItems.Select(i => i.ProductId))}].");

            Assert(srvItems[0].PricingMethod == PricingMethods.WeightBased &&
                   srvItems[0].LineTotal == 1500.00m &&
                   srvItems[0].SpecialInstructions == "Crispy skin, chop small",
                "Server Lechon OrderItem must retain WeightBased PricingMethod, LineTotal=1500, and instructions.");
            Assert(srvItems[1].IsComplimentary && srvItems[1].ParentProductId == 20 && srvItems[1].LineTotal == 0m &&
                   srvItems[2].IsComplimentary && srvItems[2].ParentProductId == 20 && srvItems[2].LineTotal == 0m,
                "Server complimentary items must retain IsComplimentary, ParentProductId=20, and LineTotal=0.");

            var srvTea = srvItems[3];
            var srvTeaMods = srvTea.OrderItemModifiers.OrderBy(m => m.ModifierOptionId).ToList();
            Assert(srvTea.BasePrice == 120m && srvTea.ModifierTotal == 50m && srvTea.UnitPrice == 170m && srvTea.LineTotal == 340m &&
                   srvTea.SpecialInstructions == "Less ice" && srvTeaMods.Count == 2 &&
                   srvTeaMods.All(m => m.ProductId == null) &&
                   srvTeaMods.Select(m => m.ModifierOptionId).SequenceEqual(new[] { 502, 503 }) &&
                   srvTeaMods.Select(m => m.AdditionalPrice).SequenceEqual(new[] { 20m, 30m }),
                "Server Milk Tea OrderItem & OrderItemModifiers must preserve multi-select prices and ProductId == null.");

            // Verify inventory deductions on server BranchInventories
            Assert((await serverDb.BranchInventories.AsNoTracking().SingleAsync(bi => bi.ProductId == 20)).StockQuantity == 8,
                "Lechon inventory must deduct 2 (rounded from 1.578947 kg): 10 -> 8.");
            Assert((await serverDb.BranchInventories.AsNoTracking().SingleAsync(bi => bi.ProductId == 22)).StockQuantity == 19,
                "Complimentary Sprite inventory must deduct 1: 20 -> 19.");
            Assert((await serverDb.BranchInventories.AsNoTracking().SingleAsync(bi => bi.ProductId == 25)).StockQuantity == 29,
                "Complimentary Atchara inventory must deduct 1: 30 -> 29.");
            Assert((await serverDb.BranchInventories.AsNoTracking().SingleAsync(bi => bi.ProductId == 50)).StockQuantity == 13,
                "Milk Tea inventory must deduct 2: 15 -> 13.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // WM-04: Online ProductModifiersController & Offline ReferenceDataSync parity
    private static async Task TestWeightModifiers04_OnlineOfflineModifierParity()
    {
        using var scope = new TempLocalDbScope("wm04");
        var dayUtc = new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc);
        SetDeterministicManilaClock(dayUtc);

        try
        {
            var (conn, serverDb, _) = await CreateTestEnvAsync();
            await using var cleanup = conn;

            serverDb.Categories.Add(new Category { CategoryId = 1, Name = "Main", IsActive = true });
            serverDb.Products.AddRange(
                new Product { ProductId = 20, CategoryId = 1, Name = "Lechon", ProductType = "Standard", PricingMethod = PricingMethods.WeightBased, UnitOfMeasure = "kg", Price = 950m, IsKitchenItem = true, IsActive = true },
                new Product { ProductId = 22, CategoryId = 1, Name = "Coke 330ml", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 50m, IsBarItem = true, IsActive = true },
                new Product { ProductId = 23, CategoryId = 1, Name = "Sprite 330ml", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 50m, IsBarItem = true, IsActive = true },
                new Product { ProductId = 25, CategoryId = 1, Name = "Atchara Cup", ProductType = "Standard", PricingMethod = PricingMethods.Fixed, Price = 30m, IsKitchenItem = true, IsActive = true });

            serverDb.Taxes.Add(new Taxis { TaxId = 1, Name = "VATable", Rate = 12m });
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

            serverDb.ModifierGroups.AddRange(
                new ModifierGroup { ModifierGroupId = 10, Name = "Paid Add-ons", IsRequired = false, MinSelections = 0, MaxSelections = 2, DisplayOrder = 10, IsActive = true },
                new ModifierGroup { ModifierGroupId = 20, Name = "Complimentary Drink", IsRequired = true, MinSelections = 1, MaxSelections = 1, DisplayOrder = 20, IsActive = true });

            serverDb.ProductModifierGroups.AddRange(
                new ProductModifierGroup { ProductModifierGroupId = 1, ProductId = 20, ModifierGroupId = 10, DisplayOrder = 20 },
                new ProductModifierGroup { ProductModifierGroupId = 2, ProductId = 20, ModifierGroupId = 20, DisplayOrder = 10 });

            serverDb.ModifierOptions.AddRange(
                new ModifierOption { ModifierOptionId = 101, ModifierGroupId = 20, ProductId = 23, Name = "Sprite 330ml", Price = 0m, DisplayOrder = 2, IsActive = true },
                new ModifierOption { ModifierOptionId = 102, ModifierGroupId = 20, ProductId = 22, Name = "Coke 330ml", Price = 0m, DisplayOrder = 1, IsActive = true },
                new ModifierOption { ModifierOptionId = 201, ModifierGroupId = 10, ProductId = null, Name = "Extra Chili", Price = 25m, DisplayOrder = 2, IsActive = false },
                new ModifierOption { ModifierOptionId = 202, ModifierGroupId = 10, ProductId = 25, Name = "Extra Atchara", Price = 30m, DisplayOrder = 1, IsActive = true });

            await serverDb.SaveChangesAsync();

            var modCtrl = new ProductModifiersController(serverDb);
            Authenticate(modCtrl, "Cashier", "1");
            var onlineAction = await modCtrl.GetProductModifiers(20);
            var onlineOk = onlineAction.Result as OkObjectResult;
            Assert(onlineOk != null && onlineOk.Value is IEnumerable<ProductModifierGroupDto>,
                "ProductModifiersController.GetProductModifiers must return OkObjectResult.");
            var onlineGroups = ((IEnumerable<ProductModifierGroupDto>)onlineOk!.Value!).ToList();

            var refCtrl = new OfflineReferenceDataController(serverDb);
            Authenticate(refCtrl, "Terminal", "1");
            using var inProcHttp = new HttpClient(new InProcSyncHandler(async req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("api/offline/reference-data", StringComparison.OrdinalIgnoreCase))
                {
                    return ActionResultToHttpResponse(await refCtrl.GetReferenceData(1, null));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }))
            {
                BaseAddress = new Uri("http://localhost/")
            };

            using var syncDb = new LocalPosDbContext();
            syncDb.InitializeDatabase();
            var refSync = new ReferenceDataSyncService(syncDb);
            Assert(await refSync.SyncReferenceDataAsync(1, inProcHttp),
                "ReferenceDataSyncService.SyncReferenceDataAsync must succeed against OfflineReferenceDataController.");

            using var readLocalDb = new LocalPosDbContext();
            var offlineGroups = await ReferenceDataSyncService.GetLocalProductModifierGroupsAsync(readLocalDb, 20);

            Assert(onlineGroups.Count == offlineGroups.Count && onlineGroups.Count == 2,
                $"Online ({onlineGroups.Count}) and offline ({offlineGroups.Count}) group counts must both equal 2.");

            for (int i = 0; i < onlineGroups.Count; i++)
            {
                var og = onlineGroups[i];
                var lg = offlineGroups[i];
                Assert(og.ModifierGroupId == lg.ModifierGroupId &&
                       og.ModifierGroupName == lg.ModifierGroupName &&
                       og.IsRequired == lg.IsRequired &&
                       og.MinSelections == lg.MinSelections &&
                       og.MaxSelections == lg.MaxSelections &&
                       og.DisplayOrder == lg.DisplayOrder,
                    $"Group mismatch at index {i}: online=({og.ModifierGroupId},{og.DisplayOrder}) vs offline=({lg.ModifierGroupId},{lg.DisplayOrder}).");

                Assert(og.Options.Count == lg.Options.Count,
                    $"Option count mismatch for group {og.ModifierGroupId}: online={og.Options.Count}, offline={lg.Options.Count}.");

                for (int j = 0; j < og.Options.Count; j++)
                {
                    var oo = og.Options[j];
                    var lo = lg.Options[j];
                    Assert(oo.ModifierOptionId == lo.ModifierOptionId &&
                           oo.Name == lo.Name &&
                           oo.ProductId == lo.ProductId &&
                           oo.ProductName == lo.ProductName &&
                           oo.Price == lo.Price &&
                           oo.DisplayOrder == lo.DisplayOrder &&
                           oo.IsActive == lo.IsActive,
                        $"Option parity mismatch in group {og.ModifierGroupId} index {j}: online=({oo.ModifierOptionId}, order={oo.DisplayOrder}, prod={oo.ProductId}) vs offline=({lo.ModifierOptionId}, order={lo.DisplayOrder}, prod={lo.ProductId}).");
                }
            }
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // WM-05: CartItem quantity and product card badge formatting & underlying precision preservation
    private static Task TestWeightModifiers05_QuantityAndBadgeFormatting()
    {
        decimal repeatingWeight = 900m / 950m;

        // 1. Direct CartItem.FormatQuantity assertions
        Assert(CartItem.FormatQuantity(repeatingWeight) == "0.947",
            $"Expected 900/950 formatted to '0.947', got '{CartItem.FormatQuantity(repeatingWeight)}'.");
        Assert(CartItem.FormatQuantity(1.5m) == "1.5",
            $"Expected 1.5 formatted to '1.5', got '{CartItem.FormatQuantity(1.5m)}'.");
        Assert(CartItem.FormatQuantity(2m) == "2",
            $"Expected 2 formatted to '2', got '{CartItem.FormatQuantity(2m)}'.");
        Assert(CartItem.FormatQuantity(0.0004m) == "<0.001",
            $"Expected 0.0004 formatted to '<0.001', got '{CartItem.FormatQuantity(0.0004m)}'.");
        Assert(CartItem.FormatQuantity(0.0005m) == "0.001",
            $"Expected 0.0005 rounded away from zero to '0.001', got '{CartItem.FormatQuantity(0.0005m)}'.");
        Assert(CartItem.FormatQuantity(0m) == "0",
            $"Expected 0 formatted to '0', got '{CartItem.FormatQuantity(0m)}'.");

        // 2. Direct CartItem.FormatProductCardQuantity assertions
        Assert(CartItem.FormatProductCardQuantity(repeatingWeight, PricingMethods.WeightBased) == "0.947 kg",
            $"Expected WeightBased 900/950 badge '0.947 kg', got '{CartItem.FormatProductCardQuantity(repeatingWeight, PricingMethods.WeightBased)}'.");
        Assert(CartItem.FormatProductCardQuantity(1.5m, PricingMethods.WeightBased) == "1.5 kg",
            $"Expected WeightBased 1.5 badge '1.5 kg', got '{CartItem.FormatProductCardQuantity(1.5m, PricingMethods.WeightBased)}'.");
        Assert(CartItem.FormatProductCardQuantity(1.5m, "weightbased") == "1.5 kg",
            "Expected case-insensitive WeightBased pricing method to append ' kg'.");
        Assert(CartItem.FormatProductCardQuantity(2m, PricingMethods.Fixed) == "2",
            $"Expected Fixed 2 badge '2', got '{CartItem.FormatProductCardQuantity(2m, PricingMethods.Fixed)}'.");
        Assert(CartItem.FormatProductCardQuantity(2m, null) == "2",
            $"Expected null pricingMethod 2 badge '2', got '{CartItem.FormatProductCardQuantity(2m, null)}'.");
        Assert(CartItem.FormatProductCardQuantity(0.0004m, PricingMethods.WeightBased) == "<0.001 kg",
            $"Expected WeightBased 0.0004 badge '<0.001 kg', got '{CartItem.FormatProductCardQuantity(0.0004m, PricingMethods.WeightBased)}'.");
        Assert(CartItem.FormatProductCardQuantity(0m, PricingMethods.Fixed) == "0",
            $"Expected Fixed 0 badge '0', got '{CartItem.FormatProductCardQuantity(0m, PricingMethods.Fixed)}'.");
        Assert(CartItem.FormatProductCardQuantity(0m, PricingMethods.WeightBased) == "0 kg",
            $"Expected WeightBased 0 badge '0 kg', got '{CartItem.FormatProductCardQuantity(0m, PricingMethods.WeightBased)}'.");

        // 3. InvariantCulture check (must ignore comma-decimal ambient culture)
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert(CartItem.FormatQuantity(1.5m) == "1.5",
                "FormatQuantity must use InvariantCulture decimal separator regardless of CurrentCulture.");
            Assert(CartItem.FormatProductCardQuantity(repeatingWeight, PricingMethods.WeightBased) == "0.947 kg",
                "FormatProductCardQuantity must use InvariantCulture decimal separator regardless of CurrentCulture.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        // 4. CartService integration: badge formatting must NOT truncate underlying CartItem.Quantity or LineTotal precision
        var session = CreateOfflineTestSession();
        using var cart = new CartService(session);
        var lechon = new ProductDto
        {
            ProductId = 20,
            CategoryId = 1,
            CategoryName = "Main",
            Name = "Lechon",
            Price = 950m,
            PricingMethod = PricingMethods.WeightBased,
            UnitOfMeasure = "kg",
            IsKitchenItem = true,
            IsActive = true
        };
        var rice = new ProductDto
        {
            ProductId = 21,
            CategoryId = 1,
            CategoryName = "Main",
            Name = "Plain Rice",
            Price = 35m,
            PricingMethod = PricingMethods.Fixed,
            IsKitchenItem = true,
            IsActive = true
        };

        cart.AddWeightBasedItem(lechon, repeatingWeight, targetAmount: 900m);
        cart.AddItem(rice, 2);

        var lechonLine = cart.Items.Single(i => i.ProductId == 20);
        Assert(lechonLine.Quantity == repeatingWeight && lechonLine.LineTotal == 900.00m,
            $"Underlying CartItem.Quantity ({lechonLine.Quantity}) and LineTotal ({lechonLine.LineTotal}) must retain exact precision.");

        decimal lechonCardQty = cart.Items.Where(i => i.ProductId == 20).Sum(i => i.Quantity);
        decimal riceCardQty = cart.Items.Where(i => i.ProductId == 21).Sum(i => i.Quantity);
        Assert(CartItem.FormatProductCardQuantity(lechonCardQty, lechon.PricingMethod) == "0.947 kg",
            $"Expected Lechon card badge '0.947 kg', got '{CartItem.FormatProductCardQuantity(lechonCardQty, lechon.PricingMethod)}'.");
        Assert(CartItem.FormatProductCardQuantity(riceCardQty, rice.PricingMethod) == "2",
            $"Expected Rice card badge '2', got '{CartItem.FormatProductCardQuantity(riceCardQty, rice.PricingMethod)}'.");
        Assert(CartItem.FormatQuantity(cart.TotalItems) == "2.947",
            $"Expected formatted Cart.TotalItems '2.947', got '{CartItem.FormatQuantity(cart.TotalItems)}'.");

        return Task.CompletedTask;
    }
}
