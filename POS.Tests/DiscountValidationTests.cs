using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public partial class Program
{
    private static DiscountsController CreateDiscountsControllerForTest(PosDbContext db, string role = "Admin")
    {
        return new DiscountsController(db, new DiscountCalculationService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal("admin", role, branchId: 1, terminalId: 1)
                }
            }
        };
    }

    private static List<CartItemCalculationDto> SingleItem(decimal price, decimal qty = 1m, string taxName = "VATable") =>
        new()
        {
            new CartItemCalculationDto
            {
                ProductId = 1,
                ProductName = "Test Item",
                Quantity = qty,
                UnitPrice = price,
                TaxName = taxName,
                IsEligibleForStatutoryDiscount = true
            }
        };

    private static void AssertThrowsArgument(Action act, string scenario)
    {
        try
        {
            act();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new Exception($"Expected ArgumentException for scenario: {scenario}, but call succeeded.");
    }

    private static async Task TestDiscVal01_ManualPercentageAndAdminDefinitionValidation()
    {
        var localCalc = new LocalDiscountCalculationService();
        var serverCalc = new DiscountCalculationService();

        // Valid manual percentage cases on ₱500.00 eligible item: 0%, 10%, 50%, 100%, decimal 12.5%
        var validCases = new (decimal Pct, decimal ExpectedDiscount, decimal ExpectedTotal)[]
        {
            (0m, 0m, 500.00m),
            (10m, 50.00m, 450.00m),
            (50m, 250.00m, 250.00m),
            (100m, 500.00m, 0.00m),
            (12.5m, 62.50m, 437.50m)
        };

        foreach (var (pct, expDisc, expTotal) in validCases)
        {
            var req = new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered",
                DiscountCode = "MANUAL",
                ManualDiscountPercentage = pct,
                Items = SingleItem(500m)
            };

            var lRes = localCalc.CalculateOrder(req);
            var sRes = serverCalc.CalculateOrder(req);

            Assert(lRes.DiscountAmount == expDisc && lRes.TotalAmount == expTotal,
                $"Manual {pct}% mismatch: expected Disc={expDisc}, Total={expTotal}; got Disc={lRes.DiscountAmount}, Total={lRes.TotalAmount}.");
            Assert(lRes.TotalAmount == sRes.TotalAmount && lRes.DiscountAmount == sRes.DiscountAmount
                   && lRes.VatableSales == sRes.VatableSales && lRes.VatAmount == sRes.VatAmount,
                $"Online and offline calculators must agree identically for Manual {pct}%.");
            Assert(lRes.TotalAmount >= 0m && lRes.VatableSales >= 0m && lRes.VatAmount >= 0m && lRes.CalculatedItems.All(i => i.LineTotal >= 0m),
                $"No negative totals or negative VAT components allowed for Manual {pct}%.");
        }

        // At 100% discount, VatableSales and VatAmount must be 0.00 (never negative)
        var fullDiscRes = localCalc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "MANUAL",
            ManualDiscountPercentage = 100m,
            Items = SingleItem(500m)
        });
        Assert(fullDiscRes.TotalAmount == 0m && fullDiscRes.VatableSales == 0m && fullDiscRes.VatAmount == 0m,
            $"100% discount must yield Total=0, VatableSales=0, VatAmount=0; got Total={fullDiscRes.TotalAmount}, Vatable={fullDiscRes.VatableSales}, Vat={fullDiscRes.VatAmount}.");

        // Invalid manual percentages: 150%, 10,000%, negative -5%
        foreach (var badPct in new[] { 150m, 10000m, -5m, -0.01m, 100.01m })
        {
            var req = new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered",
                DiscountCode = "MANUAL",
                ManualDiscountPercentage = badPct,
                Items = SingleItem(500m)
            };
            AssertThrowsArgument(() => localCalc.CalculateOrder(req), $"Local Manual {badPct}%");
            AssertThrowsArgument(() => serverCalc.CalculateOrder(req), $"Server Manual {badPct}%");
        }

        // API controller validation for CalculateDiscount and Create/Update DiscountType
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var _ = conn;
        var ctrl = CreateDiscountsControllerForTest(db);

        var bad10000Api = await ctrl.CalculateDiscount(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "MANUAL",
            ManualDiscountPercentage = 10000m,
            Items = SingleItem(500m)
        });
        Assert(bad10000Api.Result is BadRequestObjectResult, "POST api/discounts/calculate must return 400 BadRequest for 10,000% manual discount.");

        var badCreatePct = await ctrl.CreateDiscount(new DiscountType
        {
            Code = "BADPCT", Name = "Bad 150%", DiscountCategory = "PROMOTIONAL", DiscountMethod = "PERCENTAGE", DiscountValue = 150m, IsActive = true
        });
        Assert(badCreatePct.Result is BadRequestObjectResult, "CreateDiscount must reject percentage > 100%.");

        var badCreateNeg = await ctrl.CreateDiscount(new DiscountType
        {
            Code = "BADNEG", Name = "Bad Negative", DiscountCategory = "PROMOTIONAL", DiscountMethod = "FIXED", DiscountValue = -50m, IsActive = true
        });
        Assert(badCreateNeg.Result is BadRequestObjectResult, "CreateDiscount must reject negative fixed value.");

        var okCreate = await ctrl.CreateDiscount(new DiscountType
        {
            Code = "PROMO15", Name = "Promo 15%", DiscountCategory = "PROMOTIONAL", DiscountMethod = "PERCENTAGE", DiscountValue = 15m, IsActive = true
        });
        Assert(okCreate.Result is CreatedAtActionResult, "Valid promotional 15% discount creation must succeed.");
    }

    private static async Task TestDiscVal02_ManualAndConfiguredFixedAmountDiscounts()
    {
        var localCalc = new LocalDiscountCalculationService();
        var serverCalc = new DiscountCalculationService();

        // Valid fixed amounts on ₱500.00: ₱0, ₱100, exact ₱500, decimal ₱33.33
        var validFixed = new (decimal Amt, decimal ExpDisc, decimal ExpTotal)[]
        {
            (0m, 0m, 500.00m),
            (100m, 100.00m, 400.00m),
            (500m, 500.00m, 0.00m),
            (33.33m, 33.33m, 466.67m)
        };

        foreach (var (amt, expDisc, expTotal) in validFixed)
        {
            var req = new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered",
                DiscountCode = "MANUAL",
                ManualDiscountAmount = amt,
                Items = SingleItem(500m)
            };
            var lRes = localCalc.CalculateOrder(req);
            var sRes = serverCalc.CalculateOrder(req);
            Assert(lRes.DiscountAmount == expDisc && lRes.TotalAmount == expTotal && lRes.TotalAmount == sRes.TotalAmount,
                $"Manual Fixed ₱{amt} mismatch: got Disc={lRes.DiscountAmount}, Total={lRes.TotalAmount}.");
            Assert(lRes.VatableSales >= 0m && lRes.VatAmount >= 0m, "Tax components must stay non-negative.");
        }

        // Invalid fixed amounts on ₱500.00: > subtotal (₱600), negative (-₱100), huge (₱999,999.99)
        foreach (var badAmt in new[] { 500.01m, 600m, -100m, -0.01m, 999999.99m })
        {
            var req = new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered",
                DiscountCode = "MANUAL",
                ManualDiscountAmount = badAmt,
                Items = SingleItem(500m)
            };
            AssertThrowsArgument(() => localCalc.CalculateOrder(req), $"Manual Fixed ₱{badAmt}");
            AssertThrowsArgument(() => serverCalc.CalculateOrder(req), $"Server Manual Fixed ₱{badAmt}");
        }

        // Configured DB FIXED coupon discount via DiscountsController.CalculateDiscount
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var _ = conn;
        db.DiscountTypes.Add(new DiscountType
        {
            Code = "COUPON100", Name = "₱100 Off Coupon", DiscountCategory = "PROMOTIONAL", DiscountMethod = "FIXED", DiscountValue = 100m, IsActive = true
        });
        await db.SaveChangesAsync();

        var ctrl = CreateDiscountsControllerForTest(db);
        var okCoupon = await ctrl.CalculateDiscount(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "COUPON100",
            Items = SingleItem(250m)
        });
        var okDto = ((OkObjectResult)okCoupon.Result!).Value as OrderCalculationResultDto;
        Assert(okDto != null && okDto.DiscountAmount == 100m && okDto.TotalAmount == 150m,
            $"COUPON100 on ₱250 must yield Discount=100, Total=150; got Disc={okDto?.DiscountAmount}, Total={okDto?.TotalAmount}.");

        // Same ₱100 coupon on ₱80 cart must be rejected (exceeds eligible amount)
        var overCoupon = await ctrl.CalculateDiscount(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "COUPON100",
            Items = SingleItem(80m)
        });
        Assert(overCoupon.Result is BadRequestObjectResult, "COUPON100 (₱100) on ₱80 order must be rejected as exceeding eligible amount.");
    }

    private static Task TestDiscVal03_CombinedStackedStatutoryAndCartRecalculation()
    {
        var calc = new LocalDiscountCalculationService();

        // 1. Item-level + Order-level discount: applies ONLY against remaining eligible amount (no double discounting)
        // Item A: ₱200 with 50% item discount -> ₱100 remaining; Item B: ₱300 with ₱50 item discount -> ₱250 remaining
        // Remaining eligible = ₱350; Order MANUAL 10% = ₱35 -> Total Discount = 100 + 50 + 35 = ₱185, Final Total = ₱315
        var stackedItemsReq = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "MANUAL",
            ManualDiscountPercentage = 10m,
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Item A", Quantity = 1, UnitPrice = 200m, ItemDiscountPercentage = 50m },
                new() { ProductId = 2, ProductName = "Item B", Quantity = 1, UnitPrice = 300m, ItemDiscountAmount = 50m }
            }
        };
        var stackedRes = calc.CalculateOrder(stackedItemsReq);
        Assert(stackedRes.Subtotal == 500m && stackedRes.DiscountAmount == 185m && stackedRes.TotalAmount == 315m,
            $"Item + Order stack expected Sub=500, Disc=185, Total=315; got Sub={stackedRes.Subtotal}, Disc={stackedRes.DiscountAmount}, Total={stackedRes.TotalAmount}.");

        // 2. Manual percentage + Promotional fixed discount (coupon + manual)
        // ₱400 subtotal -> 25% manual (= ₱100 off -> ₱300 remaining) + ₱100 promo coupon = ₱200 total discount, ₱200 payable
        var manualPlusPromo = calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "MANUAL",
            ManualDiscountPercentage = 25m,
            PromoDiscountAmount = 100m,
            Items = SingleItem(400m)
        });
        Assert(manualPlusPromo.DiscountAmount == 200m && manualPlusPromo.TotalAmount == 200m,
            $"Manual 25% + Promo ₱100 on ₱400 expected Disc=200, Total=200; got Disc={manualPlusPromo.DiscountAmount}, Total={manualPlusPromo.TotalAmount}.");

        // Manual 50% (= ₱200 off -> ₱200 remaining) + ₱250 promo coupon EXCEEDS remaining ₱200 -> MUST REJECT!
        AssertThrowsArgument(() => calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "MANUAL",
            ManualDiscountPercentage = 50m,
            PromoDiscountAmount = 250m,
            Items = SingleItem(400m)
        }), "Stacked Manual 50% + Promo ₱250 exceeding remaining eligible ₱200");

        // 3. Statutory Senior Citizen (SC) & PWD preserved verbatim on VAT-Registered and Non-VAT + mixed VAT classifications
        var scMixed = calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "VATable Meal", Quantity = 1, UnitPrice = 112m, TaxName = "VATable", IsEligibleForStatutoryDiscount = true },
                new() { ProductId = 2, ProductName = "Exempt Med", Quantity = 1, UnitPrice = 100m, TaxName = "VAT-Exempt", IsEligibleForStatutoryDiscount = true },
                new() { ProductId = 3, ProductName = "Zero Rated", Quantity = 1, UnitPrice = 50m, TaxName = "Zero-Rated", IsEligibleForStatutoryDiscount = true }
            }
        });
        // VATable 112 -> vatEx 100 -> 20% = 20, lineTotal 80; Exempt 100 -> 20% = 20, lineTotal 80; Zero-Rated 50 -> 0 disc, lineTotal 50
        Assert(scMixed.Subtotal == 262m && scMixed.DiscountAmount == 40m && scMixed.TotalAmount == 210m
               && scMixed.VatExemptSales == 200m && scMixed.ZeroRatedSales == 50m && scMixed.VatAmount == 0m,
            $"SC mixed VAT calculation mismatch: Sub={scMixed.Subtotal}, Disc={scMixed.DiscountAmount}, Total={scMixed.TotalAmount}, Exempt={scMixed.VatExemptSales}, Zero={scMixed.ZeroRatedSales}, Vat={scMixed.VatAmount}.");

        // Statutory SC/PWD combined with extra manual/promo/item discount MUST be rejected
        AssertThrowsArgument(() => calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            ManualDiscountPercentage = 10m,
            Items = SingleItem(112m)
        }), "SC combined with manual percentage");
        AssertThrowsArgument(() => calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "PWD",
            PromoDiscountAmount = 20m,
            Items = SingleItem(112m)
        }), "PWD combined with promo discount");
        AssertThrowsArgument(() => calc.CalculateOrder(new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            Items = new() { new() { ProductId = 1, ProductName = "Meal", Quantity = 1, UnitPrice = 112m, ItemDiscountPercentage = 10m } }
        }), "SC combined with item-level discount");

        // Statutory SP (10%), BNPC (5%), EMPLOYEE (10%)
        Assert(calc.CalculateOrder(new OrderCalculationRequestDto { DiscountCode = "SP", Items = SingleItem(200m) }).TotalAmount == 180m, "SP 10% mismatch.");
        Assert(calc.CalculateOrder(new OrderCalculationRequestDto { DiscountCode = "BNPC", Items = SingleItem(200m) }).TotalAmount == 190m, "BNPC 5% mismatch.");
        Assert(calc.CalculateOrder(new OrderCalculationRequestDto { DiscountCode = "EMPLOYEE", Items = SingleItem(200m) }).TotalAmount == 180m, "EMPLOYEE 10% mismatch.");

        // 4. CartService dynamic recalculation on quantity changes, item removal, and discount removal
        var session = CreateOfflineVoidSession(username: "cashier1", role: "Cashier");
        var cart = new CartService(session);
        var prodA = new ProductDto { ProductId = 1, Name = "Burger", Price = 100m, TaxName = "VATable" };
        var prodB = new ProductDto { ProductId = 2, Name = "Fries", Price = 50m, TaxName = "VATable" };
        cart.AddItem(prodA, 2); // 200
        cart.AddItem(prodB, 1); // 50 -> Subtotal 250

        // Reject negative or > subtotal directly on CartService
        AssertThrowsArgument(() => cart.ApplyDiscount("MANUAL", 10000m), "CartService excessive discount amount");
        AssertThrowsArgument(() => cart.ApplyDiscount("MANUAL", -10m), "CartService negative discount amount");

        // Apply 20% manual discount -> Discount = 50, Total = 200
        cart.ApplyDiscount("MANUAL", 50m, null, "PERCENTAGE", 20m, manualPercentage: 20m);
        Assert(cart.ActiveDiscountAmount == 50m && cart.TotalAmount == 200m, "Initial 20% cart discount mismatch.");

        // Increase quantity -> Subtotal 350 -> 20% = 70, Total = 280
        cart.IncreaseQuantity(1);
        Assert(cart.Subtotal == 350m && cart.ActiveDiscountAmount == 70m && cart.TotalAmount == 280m,
            $"Cart qty increase recalculation failed: Sub={cart.Subtotal}, Disc={cart.ActiveDiscountAmount}, Total={cart.TotalAmount}.");

        // Apply fixed ₱200 discount, then remove Burger so Subtotal drops to ₱50 -> Discount caps to ₱50, Total = ₱0.00 (NEVER negative!)
        cart.ApplyDiscount("MANUAL", 200m, null, "FIXED", 200m, manualAmount: 200m);
        cart.RemoveItem(1);
        Assert(cart.Subtotal == 50m && cart.ActiveDiscountAmount == 50m && cart.TotalAmount == 0m,
            $"Cart fixed discount after item removal must cap cleanly to ₱0.00 total; got Sub={cart.Subtotal}, Disc={cart.ActiveDiscountAmount}, Total={cart.TotalAmount}.");

        // Remove discount -> Total returns to full ₱50
        cart.ClearDiscount();
        Assert(cart.ActiveDiscountCode == "NONE" && cart.ActiveDiscountAmount == 0m && cart.TotalAmount == 50m,
            "Clearing discount must restore full subtotal.");

        return Task.CompletedTask;
    }

    private static async Task TestDiscVal04_EndToEndApiOfflineCheckoutZeroTotalSplitSyncAndHeldSaleGuards()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_disc_val04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Seed server invoice sequence + shift + catalog
            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Food", IsActive = true });
            apiDb.Products.AddRange(
                new Product { ProductId = 1, CategoryId = 1, Name = "Lechon", Price = 600m, PricingMethod = "WeightBased", IsActive = true, ProductType = "Food" },
                new Product { ProductId = 2, CategoryId = 1, Name = "Drink", Price = 50m, IsActive = true, ProductType = "Drink" });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1, SeriesCode = "S1", Prefix = "INV01-",
                StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 7, IsActive = true
            });
            var srvShift = await apiDb.CashDrawerShifts.FirstAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
            srvShift.ClientShiftId = "SHIFT-DISC-01";
            srvShift.OpeningFloat = 1000m;
            await apiDb.SaveChangesAsync();

            // 1. OrdersController.CreateOrder rejects 10,000% manual discount & negative amounts; persists valid 25% manual discount accurately
            var ordersCtrl = new OrdersController(apiDb, new SequenceService(apiDb), new DiscountCalculationService())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = BuildPrincipal("cashier1", "Cashier", 1, 1) }
                }
            };
            var badOrderRes = await ordersCtrl.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "MANUAL", ManualDiscountPercentage = 10000m,
                Items = new() { new() { ProductId = 2, ProductName = "Drink", Quantity = 1, BasePrice = 50m, TaxName = "VATable" } }
            });
            Assert(badOrderRes.Result is BadRequestObjectResult, "OrdersController must reject 10,000% manual discount with 400 BadRequest.");

            var largeOrderRes = await ordersCtrl.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "MANUAL", ManualDiscountPercentage = 25m,
                Items = new() { new() { ProductId = 2, ProductName = "Bulk Catering", Quantity = 100m, BasePrice = 2500m, TaxName = "VATable" } }
            });
            var largeDto = ((OkObjectResult)largeOrderRes.Result!).Value as OrderResponseDto;
            Assert(largeDto != null && largeDto.Subtotal == 250000m && largeDto.DiscountAmount == 62500m && largeDto.TotalAmount == 187500m,
                $"Large order 25% manual discount mismatch: got Sub={largeDto?.Subtotal}, Disc={largeDto?.DiscountAmount}, Total={largeDto?.TotalAmount}.");

            // 2. Seed local SQLite for offline checkout tests
            using (var localDb = new LocalPosDbContext())
            {
                localDb.InitializeDatabase();
                await SeedLocalTerminalAndUsersAsync(localDb, shiftId: srvShift.ShiftId, clientShiftId: "SHIFT-DISC-01", openingFloat: 1000m);
                localDb.InvoiceSequences.Add(new LocalInvoiceSequence
                {
                    SequenceId = 1, TerminalId = 1, SeriesCode = "S1", Prefix = "INV01-",
                    StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 7, IsActive = true
                });
                localDb.OrderSequences.Add(new LocalOrderSequence
                {
                    TerminalId = 1, LastIssuedNumber = 1, UpdatedAt = BusinessDayClock.UtcNow()
                });
                await localDb.SaveChangesAsync();
            }

            var session = CreateOfflineVoidSession(username: "cashier1", role: "Cashier");
            var checkout = new OfflineCheckoutService(new LocalPosDbContext(), session);

            // 2a. Offline checkout rejects 10,000% manual discount without incrementing invoice sequence
            var badOffline = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "MANUAL", ManualDiscountPercentage = 10000m,
                Items = new() { new() { ProductId = 2, ProductName = "Drink", Quantity = 1, BasePrice = 50m, TaxName = "VATable" } }
            }, 50m, "CASH", null, "cashier1");
            Assert(!badOffline.Success, "OfflineCheckoutService must reject 10,000% manual discount.");
            using (var seqCheck = new LocalPosDbContext())
            {
                Assert((await seqCheck.InvoiceSequences.SingleAsync()).LastIssuedNumber == 0,
                    "Rejected discount checkout must NOT increment invoice sequence.");
            }

            // 2b. Offline checkout succeeds for 100% manual discount (TotalAmount == 0.00) with cashTendered == 0.00
            var zeroBill = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "MANUAL", ManualDiscountPercentage = 100m,
                Items = new() { new() { ProductId = 2, ProductName = "Small Item", Quantity = 1, BasePrice = 1.00m, TaxName = "VATable" } }
            }, 0m, "CASH", null, "cashier1");
            Assert(zeroBill.Success && zeroBill.Receipt != null && zeroBill.Receipt.TotalAmount == 0m && zeroBill.ChangeAmount == 0m,
                $"100% manual discount (₱0.00 bill) must succeed cleanly with 0 change; err='{zeroBill.ErrorMessage}'.");

            // 2c. Offline checkout with weight-based product + complimentary item + 20% manual discount + split payment (CASH + GCASH)
            // Lechon 1.25kg TargetAmount=750 + Complimentary Drink 0 -> Subtotal=750 -> 20% = 150 -> Total=600 -> Split CASH 200 + GCASH 400
            var splitSale = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "MANUAL", ManualDiscountPercentage = 20m,
                Items = new()
                {
                    new() { ProductId = 1, ProductName = "Lechon", Quantity = 1.25m, BasePrice = 600m, PricingMethod = "WeightBased", TargetAmount = 750m, TaxName = "VATable" },
                    new() { ProductId = 2, ProductName = "Free Sauce", Quantity = 1m, BasePrice = 0m, IsComplimentary = true, ParentProductId = 1, TaxName = "VATable" }
                },
                Splits = new()
                {
                    new() { PaymentMethod = "CASH", Amount = 200m, AmountTendered = 500m },
                    new() { PaymentMethod = "GCASH", Amount = 400m, AmountTendered = 400m, ReferenceNumber = "GC-888" }
                }
            }, 0m, "SPLIT", null, "cashier1");
            Assert(splitSale.Success && splitSale.Receipt != null
                   && splitSale.Receipt.Subtotal == 750m && splitSale.Receipt.DiscountAmount == 150m
                   && splitSale.Receipt.TotalAmount == 600m && splitSale.ChangeAmount == 300m,
                $"Weight-based split sale with 20% manual discount mismatch: err='{splitSale.ErrorMessage}', Total={splitSale.Receipt?.TotalAmount}, Change={splitSale.ChangeAmount}.");

            // 3. Synchronize offline orders to OfflineSyncController and verify server consistency
            var syncCtrl = CreateOfflineSyncControllerForVoidTest(apiDb, "cashier1", "Cashier", 1, 1);
            using var syncHttp = new HttpClient(new VoidSyncRoutingHandler(syncCtrl)) { BaseAddress = new Uri("http://localhost/") };
            using var syncDb = new LocalPosDbContext();
            var syncSvc = new OfflineTransactionSyncService(syncDb);
            var pending = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pending.Count == 2, $"Expected 2 pending offline orders, got {pending.Count}.");
            foreach (var ob in pending)
            {
                bool uploaded = await syncSvc.UploadTransactionAsync(ob, syncHttp);
                if (!uploaded)
                {
                    using var errDb = new LocalPosDbContext();
                    var failedOb = await errDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.OutboxId == ob.OutboxId);
                    throw new Exception($"Offline order {ob.EntityId} failed to sync: Status={failedOb.Status}, Error={failedOb.LastError}");
                }
            }
            var syncedSplit = await apiDb.Orders.AsNoTracking().SingleAsync(o => o.InvoiceNumber == splitSale.InvoiceNumber);
            Assert(syncedSplit.Subtotal == 750m && syncedSplit.DiscountAmount == 150m && syncedSplit.TotalAmount == 600m,
                "PostgreSQL synced order totals must match local SQLite exactly.");

            // 4. OfflineSyncController rejects tampered negative total / excessive discount payload
            var tamperedSync = await syncCtrl.SyncTransaction(new OfflineTransactionSyncRequest
            {
                LocalTransactionId = "tampered-neg-tx",
                InvoiceNumber = "INV01-0009999",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                Subtotal = 100m,
                DiscountAmount = 10000m,
                TotalAmount = -9900m,
                TransactionDate = BusinessDayClock.DeviceNow(),
                Items = new() { new() { ProductId = 2, ProductName = "Drink", Quantity = 1, UnitPrice = 100m, LineTotal = 100m } },
                Payments = new() { new() { PaymentMethod = "CASH", Amount = -9900m, TenderedAmount = 0m, ChangeAmount = 0m } }
            });
            Assert(tamperedSync.Result is BadRequestObjectResult, "OfflineSyncController must reject negative total / excessive discount payloads with 400 BadRequest.");

            // 5. HeldSalesController & ClientHeldSaleService reject negative total / excessive discount
            var heldCtrl = CreateHeldSalesControllerForTest(apiDb, "cashier1", "Cashier", 1, 1);
            var badHeld = await heldCtrl.CreateHeldSale(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1",
                Subtotal = 100m, DiscountAmount = 10000m, TotalAmount = -9900m,
                Items = new() { new() { ProductId = 2, ProductName = "Drink", Quantity = 1, BasePrice = 100m, LineTotal = 100m } }
            });
            Assert(badHeld.Result is BadRequestObjectResult, "HeldSalesController must reject negative held-sale totals.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
