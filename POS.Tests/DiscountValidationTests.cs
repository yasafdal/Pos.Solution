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

    private sealed class DiscountSyncInProcHandler(
        OfflineReferenceDataController refCtrl,
        DiscountsController discCtrl,
        Func<bool> isOnline) : HttpMessageHandler
    {
        public bool SimulateLegacyServerWithoutSyncEndpoint { get; set; }
        public int SyncCallCount { get; private set; }
        public int LastReturnedDiscountCount { get; private set; }
        public bool LastWasIncremental { get; private set; }
        public bool LastHadChanges { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            if (!isOnline())
                throw new HttpRequestException("Simulated offline network failure.");

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri?.Query ?? string.Empty);

            if (path.EndsWith("/api/offline/reference-data", StringComparison.OrdinalIgnoreCase))
            {
                int terminalId = query.TryGetValue("terminalId", out var tid) && int.TryParse(tid, out var t) ? t : 1;
                OfflineReferenceDataRequestDto? reqDto = null;
                if (request.Content != null)
                {
                    var json = await request.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(json))
                        reqDto = System.Text.Json.JsonSerializer.Deserialize<OfflineReferenceDataRequestDto>(json,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                var actionResult = await refCtrl.GetReferenceData(terminalId, reqDto);
                if (actionResult.Result is OkObjectResult ok && ok.Value is OfflineReferenceDataResponseDto payload)
                {
                    SyncCallCount++;
                    LastReturnedDiscountCount = payload.Discounts.Count;
                    LastWasIncremental = payload.DiscountsIsIncremental;
                    LastHadChanges = payload.DiscountsHasChanges;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(payload)
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
            }

            if (path.EndsWith("/api/discounts/sync", StringComparison.OrdinalIgnoreCase))
            {
                if (SimulateLegacyServerWithoutSyncEndpoint)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

                DateTime? sinceUtc = query.TryGetValue("sinceUtc", out var sVal)
                    && DateTime.TryParse(sVal, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed : null;
                string? knownHash = query.TryGetValue("knownHash", out var hVal) ? hVal.ToString() : null;

                var actionResult = await discCtrl.SyncDiscountTypes(sinceUtc, knownHash);
                if (actionResult.Result is OkObjectResult ok && ok.Value is OfflineDiscountSyncResponseDto payload)
                {
                    SyncCallCount++;
                    LastReturnedDiscountCount = payload.Discounts.Count;
                    LastWasIncremental = payload.IsIncremental;
                    LastHadChanges = payload.HasChanges;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(payload)
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
            }

            if (path.EndsWith("/api/discounts", StringComparison.OrdinalIgnoreCase))
            {
                var actionResult = await discCtrl.GetDiscountTypes();
                var list = actionResult.Value?.ToList() ?? new List<DiscountType>();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(list)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private static async Task TestDiscSync01_IsActiveSyncIncrementalFilteringOfflineAndApiValidation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_disc_sync01_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Seed catalog, invoice sequence, shift, and 5 Discount Types matching the exact spec matrix
            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals", IsActive = true });
            apiDb.Products.Add(new Product { ProductId = 1, CategoryId = 1, Name = "Chicken Meal", Price = 200m, IsActive = true, ProductType = "Food" });
            apiDb.Taxes.Add(new Taxis { TaxId = 1, Name = "VATable", Rate = 12m });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1, SeriesCode = "S1", Prefix = "INV01-",
                StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 7, IsActive = true
            });
            var srvShift = await apiDb.CashDrawerShifts.FirstAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
            srvShift.ClientShiftId = "SHIFT-DISCSYNC-01";
            srvShift.OpeningFloat = 1000m;

            var t0 = DateTime.UtcNow.AddHours(-2);
            apiDb.DiscountTypes.AddRange(
                new DiscountType { DiscountTypeId = 1, Code = "SC", Name = "Senior Citizen", DiscountCategory = "STATUTORY", DiscountMethod = "PERCENTAGE", DiscountValue = 20m, IsStatutory = true, RequiresIdentification = true, IsActive = true, CreatedAt = t0, UpdatedAt = t0 },
                new DiscountType { DiscountTypeId = 2, Code = "PWD", Name = "PWD", DiscountCategory = "STATUTORY", DiscountMethod = "PERCENTAGE", DiscountValue = 20m, IsStatutory = true, RequiresIdentification = true, IsActive = true, CreatedAt = t0, UpdatedAt = t0 },
                new DiscountType { DiscountTypeId = 3, Code = "EMPLOYEE", Name = "Employee Discount", DiscountCategory = "CORPORATE", DiscountMethod = "PERCENTAGE", DiscountValue = 10m, IsStatutory = false, RequiresIdentification = true, IsActive = false, CreatedAt = t0, UpdatedAt = t0 },
                new DiscountType { DiscountTypeId = 4, Code = "PROMO", Name = "Promotional Discount", DiscountCategory = "PROMOTIONAL", DiscountMethod = "PERCENTAGE", DiscountValue = 15m, IsStatutory = false, RequiresIdentification = false, IsActive = true, CreatedAt = t0, UpdatedAt = t0 },
                new DiscountType { DiscountTypeId = 5, Code = "SPECIAL", Name = "Special Discount", DiscountCategory = "PROMOTIONAL", DiscountMethod = "FIXED", DiscountValue = 25m, IsStatutory = false, RequiresIdentification = false, IsActive = false, CreatedAt = t0, UpdatedAt = t0 }
            );
            await apiDb.SaveChangesAsync();

            var refCtrl = new OfflineReferenceDataController(apiDb)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = BuildPrincipal("cashier1", "Cashier", branchId: 1, terminalId: 1) }
                }
            };
            var discCtrl = CreateDiscountsControllerForTest(apiDb, role: "Admin");
            var ordersCtrl = new OrdersController(apiDb, new SequenceService(apiDb), new DiscountCalculationService())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = BuildPrincipal("cashier1", "Cashier", branchId: 1, terminalId: 1) }
                }
            };
            var heldCtrl = CreateHeldSalesControllerForTest(apiDb, "cashier1", "Cashier", 1, 1);

            bool online = true;
            var handler = new DiscountSyncInProcHandler(refCtrl, discCtrl, () => online);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

            // 1 & 2. Initial full reference sync saves all 5 records with real IsActive; POS.Client shows ONLY active 3
            using var localDbSeed = new LocalPosDbContext();
            var refSync = new ReferenceDataSyncService(localDbSeed);
            Assert(await refSync.SyncReferenceDataAsync(1, http), "Initial reference data sync must succeed.");
            Assert(handler.LastReturnedDiscountCount == 5 && !handler.LastWasIncremental, "Initial sync must download all 5 discount types.");

            using (var checkDb = new LocalPosDbContext())
            {
                var allLocal = await checkDb.Discounts.AsNoTracking().OrderBy(d => d.DiscountId).ToListAsync();
                Assert(allLocal.Count == 5, $"SQLite must preserve all 5 discount records (including inactive), got {allLocal.Count}.");
                Assert(allLocal.Single(d => d.Code == "SC").IsActive, "SC must be active in SQLite.");
                Assert(allLocal.Single(d => d.Code == "PWD").IsActive, "PWD must be active in SQLite.");
                Assert(!allLocal.Single(d => d.Code == "EMPLOYEE").IsActive, "EMPLOYEE must be inactive in SQLite (not hardcoded true).");
                Assert(allLocal.Single(d => d.Code == "PROMO").IsActive, "PROMO must be active in SQLite.");
                Assert(!allLocal.Single(d => d.Code == "SPECIAL").IsActive, "SPECIAL must be inactive in SQLite.");
            }

            var (activeInitial, totalInitial) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(totalInitial == 5 && activeInitial.Count == 3, $"POS.Client must display only 3 active discounts; got {activeInitial.Count}/{totalInitial}.");
            Assert(activeInitial.Select(d => d.Code).SequenceEqual(new[] { "SC", "PWD", "PROMO" }),
                "Visible discounts must be exactly SC, PWD, PROMO.");

            // Incremental sync when unchanged downloads ZERO rows
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "No-change incremental sync must succeed.");
            Assert(handler.LastWasIncremental && !handler.LastHadChanges && handler.LastReturnedDiscountCount == 0,
                $"Unchanged incremental sync must download 0 rows; got count={handler.LastReturnedDiscountCount}, hasChanges={handler.LastHadChanges}.");

            // Create a completed order using PROMO while PROMO is still active (for historical preservation test #10)
            var histOrderRes = await ordersCtrl.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "PROMO",
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, TaxName = "VATable" } }
            });
            var histOrderDto = ((OkObjectResult)histOrderRes.Result!).Value as OrderResponseDto;
            Assert(histOrderDto != null && histOrderDto.DiscountAmount == 30m && histOrderDto.TotalAmount == 170m,
                $"Historical PROMO order expected Disc=30 Total=170; got Disc={histOrderDto?.DiscountAmount} Total={histOrderDto?.TotalAmount}.");

            // 3 & 4. Activate EMPLOYEE in POS.Web (UpdateDiscount) -> incremental sync downloads only changed row -> appears in POS.Client
            var empEntity = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "EMPLOYEE");
            empEntity.IsActive = true;
            var activateRes = await discCtrl.UpdateDiscount(empEntity.DiscountTypeId, empEntity);
            Assert(activateRes is NoContentResult, "Activating EMPLOYEE discount must succeed.");

            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Incremental sync after activation must succeed.");
            Assert(handler.LastWasIncremental && handler.LastHadChanges && handler.LastReturnedDiscountCount == 1,
                $"Incremental sync should download only 1 updated discount; got count={handler.LastReturnedDiscountCount}, incremental={handler.LastWasIncremental}.");

            var (activeAfterActivate, _) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(activeAfterActivate.Any(d => d.Code == "EMPLOYEE") && activeAfterActivate.Count == 4,
                "EMPLOYEE must now appear in POS.Client active discounts.");

            // 5 & 6. Deactivate PROMO in POS.Web -> incremental sync updates SQLite IsActive=false (keeps row) -> disappears from POS.Client
            var promoEntity = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "PROMO");
            promoEntity.IsActive = false;
            var deactivateRes = await discCtrl.UpdateDiscount(promoEntity.DiscountTypeId, promoEntity);
            Assert(deactivateRes is NoContentResult, "Deactivating PROMO discount must succeed.");

            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Incremental sync after deactivation must succeed.");
            using (var checkAfterDeactivate = new LocalPosDbContext())
            {
                var localPromo = await checkAfterDeactivate.Discounts.AsNoTracking().SingleOrDefaultAsync(d => d.Code == "PROMO");
                Assert(localPromo != null && !localPromo.IsActive, "Deactivated PROMO must remain in SQLite with IsActive=false.");
            }
            var (activeAfterDeactivate, totalAfterDeactivate) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(totalAfterDeactivate == 5 && !activeAfterDeactivate.Any(d => d.Code == "PROMO") && activeAfterDeactivate.Count == 3,
                "Deactivated PROMO must disappear from POS.Client while staying in SQLite.");

            // Out-of-band direct DB edit WITHOUT bumping UpdatedAt -> StateHash detects mismatch and self-heals!
            await apiDb.Database.ExecuteSqlRawAsync("UPDATE DiscountTypes SET IsActive = 1 WHERE Code = 'SPECIAL';");
            apiDb.ChangeTracker.Clear();
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Sync after direct DB edit must self-heal via state hash.");
            var (activeAfterDirectDb, _) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(activeAfterDirectDb.Any(d => d.Code == "SPECIAL"),
                "Direct DB activation without timestamp update must still sync via StateHash self-heal.");
            // Restore SPECIAL to inactive for remaining tests
            var specialEntity = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "SPECIAL");
            specialEntity.IsActive = false;
            await discCtrl.UpdateDiscount(specialEntity.DiscountTypeId, specialEntity);
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Restore SPECIAL inactive sync must succeed.");

            // 7. Restart POS.Client (clear pools & reinitialize fresh DbContext) -> exact statuses persist
            SqliteConnection.ClearAllPools();
            LocalPosDbContext.ResetInitializationState(dbPath);
            var (activeAfterRestart, totalAfterRestart) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(totalAfterRestart == 5 && activeAfterRestart.Select(d => d.Code).SequenceEqual(new[] { "SC", "PWD", "EMPLOYEE" }),
                "Restart must preserve synchronized IsActive states (SC, PWD, EMPLOYEE active; PROMO, SPECIAL inactive).");

            // 8. Disconnect internet -> latest synchronized active discounts still displayed & offline checkout blocks inactive discounts
            online = false;
            bool offlineSyncAttempt = await refSync.SyncDiscountTypesIncrementalAsync(http);
            Assert(!offlineSyncAttempt, "Sync while offline must fail gracefully without throwing.");
            var (activeWhileOffline, _) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(activeWhileOffline.Select(d => d.Code).SequenceEqual(new[] { "SC", "PWD", "EMPLOYEE" }),
                "Offline POS.Client must continue displaying latest synchronized active discounts.");

            var session = CreateOfflineVoidSession(username: "cashier1", role: "Cashier");
            var offlineCheckout = new OfflineCheckoutService(new LocalPosDbContext(), session);

            var inactiveOfflineRes = await offlineCheckout.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "PROMO", DiscountMethod = "PERCENTAGE", DiscountValue = 15m,
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, TaxName = "VATable" } }
            }, 200m, "CASH", null, "cashier1");
            Assert(!inactiveOfflineRes.Success && (inactiveOfflineRes.ErrorMessage?.Contains("inactive", StringComparison.OrdinalIgnoreCase) ?? false),
                $"Offline checkout must block inactive PROMO discount; got Success={inactiveOfflineRes.Success}, Err={inactiveOfflineRes.ErrorMessage}.");

            var activeOfflineRes = await offlineCheckout.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountTypeId = 3, DiscountCode = "EMPLOYEE",
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, TaxName = "VATable" } }
            }, 200m, "CASH", null, "cashier1");
            Assert(activeOfflineRes.Success && activeOfflineRes.Receipt != null && activeOfflineRes.Receipt.DiscountAmount == 20m,
                $"Offline checkout with active EMPLOYEE discount must succeed; err='{activeOfflineRes.ErrorMessage}'.");

            // 9. While offline, admin toggles SPECIAL active & EMPLOYEE inactive; reconnect -> sync updates local SQLite
            var specAgain = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "SPECIAL");
            specAgain.IsActive = true;
            await discCtrl.UpdateDiscount(specAgain.DiscountTypeId, specAgain);
            var empAgain = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "EMPLOYEE");
            empAgain.IsActive = false;
            await discCtrl.UpdateDiscount(empAgain.DiscountTypeId, empAgain);

            online = true;
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Reconnect incremental sync must succeed.");
            var (activeAfterReconnect, _) = await ReferenceDataSyncService.LoadActiveDiscountsFromLocalDbAsync();
            Assert(activeAfterReconnect.Select(d => d.Code).SequenceEqual(new[] { "SC", "PWD", "SPECIAL" }),
                $"Reconnect sync must update local active list to SC, PWD, SPECIAL; got {string.Join(",", activeAfterReconnect.Select(d => d.Code))}.");

            // 10. Historical completed transaction using previously active PROMO remains completely unchanged
            var savedHistOrder = await apiDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == histOrderDto!.OrderId);
            Assert(savedHistOrder.DiscountCode == "PROMO" && savedHistOrder.DiscountTypeId == 4 && savedHistOrder.DiscountAmount == 30m && savedHistOrder.TotalAmount == 170m,
                "Historical completed order using previously active PROMO must remain unchanged after deactivation.");

            // 11. Direct API requests cannot apply inactive discounts (by code OR by DiscountTypeId, even with forged method/value)
            var apiCalcInactive = await discCtrl.CalculateDiscount(new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered", DiscountCode = "PROMO", DiscountMethod = "PERCENTAGE", DiscountValue = 15m,
                Items = SingleItem(200m)
            });
            Assert(apiCalcInactive.Result is BadRequestObjectResult, "POST api/discounts/calculate must reject inactive PROMO discount.");

            var apiOrderInactiveByCode = await ordersCtrl.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountCode = "EMPLOYEE", DiscountMethod = "PERCENTAGE", DiscountValue = 10m,
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, TaxName = "VATable" } }
            });
            Assert(apiOrderInactiveByCode.Result is BadRequestObjectResult, "POST api/orders must reject inactive EMPLOYEE discount by code.");

            var apiOrderInactiveById = await ordersCtrl.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", ShiftId = srvShift.ShiftId,
                DiscountTypeId = 4, DiscountCode = "PROMO",
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, TaxName = "VATable" } }
            });
            Assert(apiOrderInactiveById.Result is BadRequestObjectResult, "POST api/orders must reject inactive PROMO discount by DiscountTypeId.");

            var apiHeldInactive = await heldCtrl.CreateHeldSale(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1",
                Subtotal = 200m, DiscountAmount = 30m, DiscountCode = "PROMO", TotalAmount = 170m,
                Items = new() { new() { ProductId = 1, ProductName = "Chicken Meal", Quantity = 1, BasePrice = 200m, LineTotal = 200m } }
            });
            Assert(apiHeldInactive.Result is BadRequestObjectResult, "POST api/held-sales must reject inactive PROMO discount.");

            // 12. Verify SQLite v5 -> v6 safe migration adds IsActive/RequiresApproval/UpdatedAtUtc while preserving existing rows & LocalOrders
            var migDbPath = Path.Combine(tempDir, "legacy_v5.db");
            AppContext.SetData("POS.LocalDatabasePath", migDbPath);
            LocalPosDbContext.ResetInitializationState(migDbPath);
            using (var seedV5 = new LocalPosDbContext())
            {
                seedV5.InitializeDatabase();
                seedV5.Orders.Add(new LocalOrder
                {
                    OrderId = "ord-hist-1", InvoiceNumber = "INV01-0000001", BranchId = 1, OutletId = 1, TerminalId = 1,
                    OrderType = "DINING", CashierId = "cashier1", Subtotal = 200m, DiscountAmount = 30m, DiscountCode = "PROMO",
                    VatableSales = 151.79m, VatAmount = 18.21m, TotalAmount = 170m, PaymentStatus = "PAID", OrderStatus = "COMPLETED",
                    CreatedAt = DateTime.UtcNow
                });
                await seedV5.SaveChangesAsync();
            }
            SqliteConnection.ClearAllPools();
            // Downgrade LocalDiscounts table back to exact v5 schema (without IsActive/RequiresApproval/UpdatedAtUtc) & user_version=5
            await using (var rawConn = new SqliteConnection($"Data Source={migDbPath}"))
            {
                await rawConn.OpenAsync();
                using var cmd = rawConn.CreateCommand();
                cmd.CommandText = @"
                    DROP TABLE LocalDiscounts;
                    CREATE TABLE LocalDiscounts (
                        DiscountId INTEGER PRIMARY KEY,
                        Code TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        DiscountCategory TEXT NOT NULL,
                        DiscountMethod TEXT NOT NULL,
                        DiscountValue TEXT NOT NULL,
                        IsStatutory INTEGER NOT NULL,
                        RequiresIdentification INTEGER NOT NULL
                    );
                    INSERT INTO LocalDiscounts (DiscountId, Code, Name, DiscountCategory, DiscountMethod, DiscountValue, IsStatutory, RequiresIdentification)
                    VALUES (4, 'PROMO', 'Promotional Discount', 'PROMOTIONAL', 'PERCENTAGE', '15.00', 0, 0);
                    DELETE FROM LocalSchemaMigrations WHERE Version >= 6;
                    PRAGMA user_version = 5;";
                await cmd.ExecuteNonQueryAsync();
            }
            LocalPosDbContext.ResetInitializationState(migDbPath);
            using (var upgradedDb = new LocalPosDbContext())
            {
                upgradedDb.InitializeDatabase();
                Assert(LocalDatabaseMigrator.GetUserVersion(upgradedDb.Database.GetDbConnection()) == LocalDatabaseMigrator.CurrentSchemaVersion,
                    $"Legacy v5 SQLite DB must migrate cleanly to v{LocalDatabaseMigrator.CurrentSchemaVersion}.");
                var migratedDisc = await upgradedDb.Discounts.AsNoTracking().SingleAsync(d => d.DiscountId == 4);
                Assert(migratedDisc.Code == "PROMO" && migratedDisc.IsActive && !migratedDisc.UpdatedAtUtc.HasValue,
                    "Migrated legacy LocalDiscounts row must preserve record and leave UpdatedAtUtc null so first sync fetches authoritative PostgreSQL IsActive.");
                var preservedOrder = await upgradedDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == "ord-hist-1");
                Assert(preservedOrder.DiscountCode == "PROMO" && preservedOrder.DiscountAmount == 30m,
                    "Historical LocalOrder discount fields must remain untouched after v5->v6 migration.");
            }
            // First sync on migrated v5->v6 DB pulls authoritative IsActive=false for PROMO immediately!
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "First sync after v5->v6 migration must succeed.");
            using (var postMigDb = new LocalPosDbContext())
            {
                var syncedPromo = await postMigDb.Discounts.AsNoTracking().SingleAsync(d => d.DiscountId == 4);
                Assert(!syncedPromo.IsActive && syncedPromo.UpdatedAtUtc.HasValue,
                    "First sync after v5->v6 migration must update legacy PROMO row to authoritative IsActive=false.");
            }

            // 13. Backwards-compatible fallback when older POS.Api returns 404 on /api/discounts/sync -> falls back to GET /api/discounts
            var promoReEnable = await apiDb.DiscountTypes.AsNoTracking().SingleAsync(d => d.Code == "PROMO");
            promoReEnable.IsActive = true;
            await discCtrl.UpdateDiscount(promoReEnable.DiscountTypeId, promoReEnable);
            handler.SimulateLegacyServerWithoutSyncEndpoint = true;
            Assert(await refSync.SyncDiscountTypesIncrementalAsync(http), "Fallback to GET api/discounts must succeed when /api/discounts/sync returns 404.");
            using (var fallbackDb = new LocalPosDbContext())
            {
                var syncedPromoFallback = await fallbackDb.Discounts.AsNoTracking().SingleAsync(d => d.DiscountId == 4);
                Assert(syncedPromoFallback.IsActive, "Fallback to GET api/discounts must update SQLite IsActive accurately.");
            }
            handler.SimulateLegacyServerWithoutSyncEndpoint = false;
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
