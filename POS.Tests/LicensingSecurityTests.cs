using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public static partial class Program
{
    private static LicensingController CreateLicensingController(PosDbContext db, string username = "admin1", string role = "Admin")
    {
        return new LicensingController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, username),
                        new Claim(ClaimTypes.NameIdentifier, username),
                        new Claim(ClaimTypes.Role, role)
                    ], "TestAuth"))
                }
            }
        };
    }

    private static void ResetLicensingTestHooks()
    {
        BusinessDayClock.ResetProviders();
        DeviceBindingHelper.HardwareFingerprintProvider = null;
        ClientLicenseService.ResetClientStateForTesting();
        ClientLicenseService.StrictEnforcementMode = false;
        LicensingService.ResetRateLimitsForTesting();
    }

    private static async Task SeedLocalSqliteForLicensingTestAsync(
        LocalPosDbContext localDb,
        int terminalId = 1,
        int branchId = 1,
        int outletId = 1,
        string deviceUuid = "uuid-term-001",
        long shiftId = 1)
    {
        localDb.InitializeDatabase();
        localDb.Branches.Add(new LocalBranch
        {
            BranchId = branchId,
            Name = "Main Branch",
            Code = "BR-01",
            TaxType = "VAT-Registered",
            VatDisplay = true,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });
        localDb.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = terminalId,
            OutletId = outletId,
            OutletName = "Main Outlet",
            BranchId = branchId,
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            TerminalCode = $"POS{terminalId:D2}",
            TerminalName = $"POS {terminalId}",
            TerminalIdentifier = deviceUuid,
            DeviceUuid = deviceUuid,
            IsActivated = true,
            IsInitialized = true,
            DefaultOpeningFund = 1000m,
            EffectiveOpeningFund = 1000m,
            OpeningFundSource = "OUTLET_DEFAULT",
            LastKnownServerUtc = BusinessDayClock.UtcNow(),
            LastKnownServerTickCount64 = BusinessDayClock.TickCount64()
        });
        localDb.Products.Add(new LocalProduct
        {
            ProductId = 1,
            Name = "Lechon Belly",
            SKU = "LB-01",
            Price = 500m,
            CategoryId = 1,
            CategoryName = "Main",
            TaxName = "VATable",
            IsActive = true
        });
        localDb.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = terminalId,
            SeriesCode = "SER1",
            Prefix = "INV-",
            StartingNumber = 1,
            EndingNumber = 10000,
            LastIssuedNumber = 0,
            PaddingLength = 7,
            IsActive = true
        });
        localDb.CashDrawerShifts.Add(new LocalCashDrawerShift
        {
            ShiftId = shiftId,
            ClientShiftId = $"shift-{terminalId}-{shiftId}",
            BranchId = branchId,
            TerminalId = terminalId,
            CashierId = "cashier1",
            OpeningFloat = 1000m,
            ExpectedBalance = 1000m,
            OpeningConfirmed = true,
            Status = "OPEN",
            OpenedAt = BusinessDayClock.UtcNow()
        });
        await localDb.SaveChangesAsync();
    }

    // LIC-01: Terminal registration and license issuance
    private static async Task TestLic01_TerminalRegistrationAndLicenseIssuance()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var baseUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => baseUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var licController = CreateLicensingController(db);
            var issueRes = await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationYears = 5,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = false
            });
            var ok = issueRes.Result as OkObjectResult;
            Assert(ok != null, "IssueLicense must return 200 OK.");
            var op = (LicenseOperationResponseDto)ok!.Value!;
            Assert(op.Success && op.LicensePackage != null, "IssueLicense must return a signed license package.");
            Assert(op.LicensePackage!.Payload.LicenseStatus == LicenseStatusConstants.PendingActivation,
                "License issued with ActivateImmediatelyIfBound=false must start as PENDING_ACTIVATION.");
            Assert(op.LicensePackage.Payload.ActivatedAtUtc == null && op.LicensePackage.Payload.ExpiresAtUtc == null,
                "Unactivated license must not start its 5-year countdown until official activation.");
            Assert(LicenseCryptoService.VerifySignature(op.LicensePackage.Payload, op.LicensePackage.Signature),
                "Issued license package must have a valid ECDSA P-256 signature.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-02: Terminal activation and device binding
    private static async Task TestLic02_TerminalActivationAndDeviceBinding()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic02_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var baseUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => baseUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licController = CreateLicensingController(db);
            await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationYears = 5,
                ActivateImmediatelyIfBound = false
            });

            var expectedDeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001");
            var actRes = await licController.ActivateTerminalLicense(new LicenseActivateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = expectedDeviceId,
                DeviceClockUtc = baseUtc
            });
            var ok = actRes.Result as OkObjectResult;
            Assert(ok != null, "ActivateTerminalLicense must return 200 OK.");
            var op = (LicenseOperationResponseDto)ok!.Value!;
            Assert(op.Success && op.LicensePackage != null, "Activation must succeed and return signed package.");
            Assert(op.LicensePackage!.Payload.DeviceId == expectedDeviceId, "License must bind to the terminal's hardware DeviceId.");
            Assert(op.LicensePackage.Payload.LicenseStatus == LicenseStatusConstants.Active, "Activated license status must be ACTIVE.");

            // Attempt activation from a second physical machine (Machine B) must be blocked
            var machineBDeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001", "HW-MACHINE-B");
            var actMachineB = await licController.ActivateTerminalLicense(new LicenseActivateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = machineBDeviceId,
                DeviceClockUtc = baseUtc
            });
            var conflict = actMachineB.Result as ConflictObjectResult;
            Assert(conflict != null, "Activation from a different physical device must be rejected with 409 Conflict.");
            var conflictDto = (LicenseOperationResponseDto)conflict!.Value!;
            Assert(conflictDto.ErrorCode == "DEVICE_ALREADY_BOUND", $"Expected DEVICE_ALREADY_BOUND, got {conflictDto.ErrorCode}");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-03: 5-year expiration date calculation
    private static async Task TestLic03_FiveYearExpirationCalculation()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var installUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
            var activationUtc = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.CreatedAt = installUtc;
            await db.SaveChangesAsync();

            BusinessDayClock.UtcNowProvider = () => installUtc;
            var licController = CreateLicensingController(db);
            await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationYears = 5,
                ActivateImmediatelyIfBound = false
            });

            // Official activation happens 13 days after software installation/issuance
            BusinessDayClock.UtcNowProvider = () => activationUtc;
            var actRes = await licController.ActivateTerminalLicense(new LicenseActivateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001")
            });
            var op = (LicenseOperationResponseDto)((OkObjectResult)actRes.Result!).Value!;
            var pkg = op.LicensePackage!;

            var expectedExpires = new DateTime(2031, 11, 14, 10, 30, 0, DateTimeKind.Utc);
            Assert(pkg.Payload.ActivatedAtUtc == activationUtc, $"ActivatedAtUtc must equal activation timestamp {activationUtc:O}, got {pkg.Payload.ActivatedAtUtc:O}");
            Assert(pkg.Payload.ExpiresAtUtc == expectedExpires, $"ExpiresAtUtc must be exactly 5 years from ActivatedAtUtc ({expectedExpires:O}), got {pkg.Payload.ExpiresAtUtc:O}");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-04: Valid active license allows POS sales
    private static async Task TestLic04_ValidActiveLicenseAllowsSales()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic04_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;
            BusinessDayClock.TickCount64Provider = () => 100_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");
            Assert(pkg != null, "License package must be created.");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }

            var clientLic = new ClientLicenseService();
            var saved = await clientLic.SaveSignedLicensePackageAsync(pkg!);
            Assert(saved, "ClientLicenseService must verify and save valid signed license package.");

            var session = new PosSessionService();
            session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                TerminalName = "POS 1",
                BranchName = "Main Branch",
                BranchCode = "BR-01"
            });

            using (var localDb = new LocalPosDbContext())
            {
                var checkout = new OfflineCheckoutService(localDb, session);
                var saleRes = await checkout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                {
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    ShiftId = shift.ShiftId,
                    CashierId = "cashier1",
                    OrderType = "DINING",
                    Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
                }, 500m, "cashier1");
                Assert(saleRes.Success, $"Active license must allow POS sale: {saleRes.ErrorMessage}");
            }
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-05: Expiring soon warnings (90, 60, 30, 7 days)
    private static Task TestLic05_ExpiringSoonWarnings()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var activatedAt = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
            var expiresAt = activatedAt.AddYears(5); // 2031-11-14
            var deviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001");

            var privParams = new LicensingService(null!).GetServerPrivateKeyParameters("Production");
            var payload = new CanonicalLicensePayload
            {
                LicenseId = "lic-warn-01",
                LicenseKey = "POS5Y-WARN-0001",
                TerminalId = 1,
                BranchId = 1,
                OutletId = 1,
                DeviceId = deviceId,
                IssuedAtUtc = activatedAt,
                ActivatedAtUtc = activatedAt,
                ExpiresAtUtc = expiresAt,
                LicenseStatus = LicenseStatusConstants.Active,
                LicenseVersion = 1,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production"
            };
            var sig = LicenseCryptoService.SignPayload(payload, privParams);

            foreach (var (daysBefore, expectedThreshold) in new[] { (85, 90), (45, 60), (20, 30), (5, 7) })
            {
                var checkUtc = expiresAt.AddDays(-daysBefore);
                var eval = LicenseCryptoService.EvaluateLicense(
                    payload, sig, 1, 1, 1, deviceId, "Production",
                    checkUtc, 1000L, checkUtc, 1000L, checkUtc, false);

                Assert(eval.EffectiveStatus == LicenseStatusConstants.ExpiringSoon,
                    $"At {daysBefore} days remaining, expected EXPIRING_SOON, got {eval.EffectiveStatus}");
                Assert(eval.WarningThresholdDays == expectedThreshold,
                    $"At {daysBefore} days remaining, expected threshold {expectedThreshold}, got {eval.WarningThresholdDays}");
                Assert(eval.IsValidForNewSales && eval.IsValidForNewShift,
                    $"Expiring soon warning at {daysBefore} days must still allow POS sales and shifts.");
                Assert(!string.IsNullOrWhiteSpace(eval.WarningMessage),
                    "Expiring soon evaluation must include a clear warning message.");
            }
        }
        finally
        {
            ResetLicensingTestHooks();
        }
        return Task.CompletedTask;
    }

    // LIC-06: Expired license blocks new POS sales and new shifts
    private static async Task TestLic06_ExpiredLicenseBlocksSales()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic06_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var activatedAt = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => activatedAt;
            BusinessDayClock.TickCount64Provider = () => 10_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Fast-forward time past the 5-year expiration date (5 years + 1 second)
            var afterExpiryUtc = activatedAt.AddYears(5).AddSeconds(1);
            BusinessDayClock.UtcNowProvider = () => afterExpiryUtc;

            var eval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(eval.EffectiveStatus == LicenseStatusConstants.Expired && !eval.IsValidForNewSales,
                $"After 5 years, license must be EXPIRED and block sales (got {eval.EffectiveStatus}).");

            var session = new PosSessionService();
            session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "POS 1", BranchName = "Main", BranchCode = "BR-01"
            });

            using (var localDb = new LocalPosDbContext())
            {
                var checkout = new OfflineCheckoutService(localDb, session);
                var saleRes = await checkout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                {
                    BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, CashierId = "cashier1",
                    Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
                }, 500m, "cashier1");
                Assert(!saleRes.Success && saleRes.ErrorMessage!.Contains("expired", StringComparison.OrdinalIgnoreCase),
                    $"Expired license must block OfflineCheckoutService: {saleRes.ErrorMessage}");
            }

            // Server-side OrdersController and CashDrawerController must also block expired terminal
            var ordersController = new OrdersController(db, new SequenceService(db), new DiscountCalculationService())
            {
                ControllerContext = CreateController(db, "cashier1", "Cashier").ControllerContext
            };
            var serverOrderRes = await ordersController.CreateOrder(new OrderCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, CashierId = "cashier1",
                Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
            });
            Assert(serverOrderRes.Result is ConflictObjectResult, "OrdersController.CreateOrder must reject expired terminal license with 409 Conflict.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-07: License renewal restores POS sales
    private static async Task TestLic07_LicenseRenewalRestoresSales()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic07_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var activatedAt = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => activatedAt;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Advance past 5-year expiry
            var expiredTime = activatedAt.AddYears(5).AddDays(2);
            BusinessDayClock.UtcNowProvider = () => expiredTime;
            Assert(!(await clientLic.EvaluateLocalLicenseAsync(1)).IsValidForNewSales, "Must be expired before renewal.");

            // Admin renews for another 5 years
            var licController = CreateLicensingController(db);
            var renewRes = await licController.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                AdditionalYears = 5,
                RenewalNotes = "Renewed for second 5-year term"
            });
            var renewOp = (LicenseOperationResponseDto)((OkObjectResult)renewRes.Result!).Value!;
            Assert(renewOp.Success && renewOp.LicensePackage != null, "Renewal must succeed.");
            Assert(renewOp.LicensePackage!.Payload.LicenseVersion == 2, "Renewal must increment LicenseVersion.");
            Assert(renewOp.LicensePackage.Payload.ExpiresAtUtc == expiredTime.AddYears(5), "Renewal after expiry must add 5 years from renewal date.");

            var history = await licSvc.GetRenewalHistoryAsync(1);
            Assert(history.Count == 1 && history[0].NewLicenseVersion == 2, "LicenseRenewalHistory must record the renewal.");

            // Client receives renewed signed package and resumes sales
            await clientLic.SaveSignedLicensePackageAsync(renewOp.LicensePackage);
            var postRenewEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(postRenewEval.IsValidForNewSales && postRenewEval.EffectiveStatus == LicenseStatusConstants.Active,
                "Renewed license must restore ACTIVE status and allow new sales.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-08: Revoked license blocks POS sales
    private static async Task TestLic08_RevokedLicenseBlocksSales()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic08_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            var licController = CreateLicensingController(db);
            var revRes = await licController.RevokeLicense(new ChangeLicenseStatusRequestDto
            {
                TerminalId = 1,
                Reason = "Decommissioned store terminal"
            });
            var revOp = (LicenseOperationResponseDto)((OkObjectResult)revRes.Result!).Value!;
            Assert(revOp.Success && revOp.LicensePackage!.Payload.LicenseStatus == LicenseStatusConstants.Revoked, "Revoke must set status to REVOKED.");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(revOp.LicensePackage!);

            var eval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(eval.EffectiveStatus == LicenseStatusConstants.Revoked && !eval.IsValidForNewSales && !eval.IsValidForNewShift,
                "Revoked license must block both new sales and new shifts.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-09: Suspended license blocks POS sales and Reactivate restores access
    private static async Task TestLic09_SuspendedLicenseBlocksAndReactivates()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic09_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            var licController = CreateLicensingController(db);
            var suspRes = await licController.SuspendLicense(new ChangeLicenseStatusRequestDto
            {
                TerminalId = 1,
                Reason = "Temporary seasonal closure"
            });
            var suspOp = (LicenseOperationResponseDto)((OkObjectResult)suspRes.Result!).Value!;

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(suspOp.LicensePackage!);

            var evalSusp = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(evalSusp.EffectiveStatus == LicenseStatusConstants.Suspended && !evalSusp.IsValidForNewSales,
                "Suspended license must block new sales.");

            var reactRes = await licController.ReactivateLicense(new ChangeLicenseStatusRequestDto
            {
                TerminalId = 1,
                Reason = "Store reopened"
            });
            var reactOp = (LicenseOperationResponseDto)((OkObjectResult)reactRes.Result!).Value!;
            await clientLic.SaveSignedLicensePackageAsync(reactOp.LicensePackage!);

            var evalActive = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(evalActive.EffectiveStatus == LicenseStatusConstants.Active && evalActive.IsValidForNewSales,
                "Reactivated license must restore sales.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-10: Device replacement workflow
    private static async Task TestLic10_DeviceReplacementWorkflow()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic10_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-OLD-PC";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var initialPkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(initialPkg!);

            // Admin authorizes device replacement to HW-NEW-PC
            var newDeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001", "HW-NEW-PC");
            var licController = CreateLicensingController(db);
            var repRes = await licController.ReplaceDevice(new ReplaceLicenseDeviceRequestDto
            {
                TerminalId = 1,
                NewDeviceId = newDeviceId,
                Reason = "Motherboard replacement on POS 1"
            });
            var repOp = (LicenseOperationResponseDto)((OkObjectResult)repRes.Result!).Value!;
            Assert(repOp.Success && repOp.LicensePackage!.Payload.DeviceId == newDeviceId, "ReplaceDevice must bind to new DeviceId.");

            var devHistory = await licSvc.GetDeviceHistoryAsync(1);
            Assert(devHistory.Any(d => d.ActionType == "DEVICE_REPLACEMENT" && d.NewDeviceId == newDeviceId),
                "LicenseDeviceHistory must log DEVICE_REPLACEMENT.");

            await clientLic.SaveSignedLicensePackageAsync(repOp.LicensePackage!);

            // Old PC must now fail device binding check
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-OLD-PC";
            var oldPcEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!oldPcEval.IsValidForNewSales && oldPcEval.BlockReasonCode == "DEVICE_MISMATCH",
                "Old replaced hardware must be rejected after device replacement.");

            // New PC succeeds
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-NEW-PC";
            var newPcEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(newPcEval.IsValidForNewSales && newPcEval.EffectiveStatus == LicenseStatusConstants.Active,
                "Replacement hardware must be authorized for POS sales.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-11: Manual PostgreSQL license expiration modification rejected
    private static async Task TestLic11_ManualPostgreSqlExpirationModificationRejected()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            // Simulate direct database tampering in PostgreSQL: attacker extends ExpiresAt to 2099 without private key
            await db.Database.ExecuteSqlRawAsync("UPDATE TerminalLicenses SET ExpiresAt = '2099-12-31 00:00:00' WHERE TerminalId = 1;");
            db.ChangeTracker.Clear();

            var valRes = await licSvc.ValidateLicenseAsync(new LicenseValidateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001"),
                DeviceClockUtc = nowUtc
            });

            Assert(!valRes.Success && valRes.ErrorCode == "SIGNATURE_INVALID",
                $"Direct PostgreSQL ExpiresAt tampering must fail signature verification (got {valRes.ErrorCode}).");

            var opGate = await licSvc.VerifyTerminalCanOperateAsync(1);
            Assert(!opGate.Allowed, "VerifyTerminalCanOperateAsync must block terminal when PostgreSQL license row is tampered.");

            var audits = await licSvc.GetAuditLogsAsync(1);
            Assert(audits.Any(a => a.EventType == "TAMPER_DETECTED"),
                "PostgreSQL signature mismatch must record a TAMPER_DETECTED security audit log.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-12: Manual SQLite license expiration modification rejected
    private static async Task TestLic12_ManualSqliteExpirationModificationRejected()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic12_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Attacker modifies ExpiresAtUtc in local SQLite database directly
            using (var localDb = new LocalPosDbContext())
            {
                await localDb.Database.ExecuteSqlRawAsync("UPDATE LocalTerminalLicenses SET ExpiresAtUtc = '2099-12-31 00:00:00' WHERE TerminalId = 1;");
            }

            var eval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!eval.IsSignatureValid && !eval.IsValidForNewSales && eval.BlockReasonCode == "SIGNATURE_INVALID",
                $"Manual SQLite ExpiresAtUtc modification must fail ECDSA signature verification (got {eval.BlockReasonCode}).");

            var gate = await ClientLicenseService.CheckOperationalGateAsync(1);
            Assert(!gate.Allowed, "Operational gate must block sales when SQLite license row is tampered.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-13: Copying SQLite database to another device rejected
    private static async Task TestLic13_CopyingSqliteDatabaseToAnotherDeviceRejected()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic13_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-ORIGINAL-TERMINAL";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            Assert((await clientLic.EvaluateLocalLicenseAsync(1)).IsValidForNewSales, "Original device must be valid.");

            // Simulate copying local_pos.db (including LocalTerminalConfigs.DeviceUuid and LocalTerminalLicenses) to a cloned PC
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-CLONED-UNAUTHORIZED-PC";

            var clonedEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!clonedEval.IsDeviceBoundValid && !clonedEval.IsValidForNewSales && clonedEval.BlockReasonCode == "DEVICE_MISMATCH",
                $"Copied SQLite DB on another physical device must fail hardware binding check (got {clonedEval.BlockReasonCode}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-14: Invalid digital signature rejected
    private static async Task TestLic14_InvalidDigitalSignatureRejected()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic14_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);

            // Attacker generates their own rogue ECDSA P-256 key pair and signs a fake 50-year license
            using var rogueEcdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var fakePayload = new CanonicalLicensePayload
            {
                LicenseId = "forged-lic-001",
                LicenseKey = "POS5Y-FORGED-9999",
                TerminalId = 1,
                BranchId = 1,
                OutletId = 1,
                DeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001"),
                IssuedAtUtc = nowUtc,
                ActivatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddYears(50),
                LicenseStatus = LicenseStatusConstants.Active,
                LicenseVersion = 1,
                OfflineGracePeriodDays = 365,
                EnvironmentType = "Production"
            };
            var forgedSig = LicenseCryptoService.SignPayload(fakePayload, rogueEcdsa.ExportParameters(true));

            Assert(!LicenseCryptoService.VerifySignature(fakePayload, forgedSig),
                "Forged ECDSA signature from non-server private key must be rejected by VerifySignature.");

            var clientLic = new ClientLicenseService();
            bool saved = await clientLic.SaveSignedLicensePackageAsync(new SignedLicensePackageDto
            {
                Payload = fakePayload,
                Signature = forgedSig,
                ServerUtcNow = nowUtc
            });
            Assert(!saved, "ClientLicenseService.SaveSignedLicensePackageAsync must refuse to persist a forged license package.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-15: Device clock rollback detected while preserving local transaction time
    private static async Task TestLic15_DeviceClockRollbackDetected()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic15_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var trustedServerUtc = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => trustedServerUtc;
            BusinessDayClock.TickCount64Provider = () => 50_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Verify normal local transaction time is preserved when clock is valid
            var localFixed = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.FromHours(8));
            BusinessDayClock.DeviceNowOffsetProvider = () => localFixed;
            Assert(BusinessDayClock.DeviceNow().Hour == 18, "Device-local transaction timestamp strategy must remain intact.");

            // Roll device system clock backward by 2 days to bypass expiration
            BusinessDayClock.UtcNowProvider = () => trustedServerUtc.AddDays(-2);
            BusinessDayClock.TickCount64Provider = () => 60_000L;

            var rollbackEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(rollbackEval.IsClockRollbackDetected && !rollbackEval.IsValidForNewSales &&
                   rollbackEval.EffectiveStatus == LicenseStatusConstants.ValidationRequired &&
                   rollbackEval.BlockReasonCode == "CLOCK_ROLLBACK_DETECTED",
                $"Clock rollback of 2 days must trigger CLOCK_ROLLBACK_DETECTED and block sales (got {rollbackEval.BlockReasonCode}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-16: Offline operation within grace period allowed
    private static async Task TestLic16_OfflineWithinGracePeriodAllowed()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic16_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var validatedAtUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => validatedAtUtc;
            BusinessDayClock.TickCount64Provider = () => 10_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // 15 days offline (within 30-day default offline grace period)
            BusinessDayClock.UtcNowProvider = () => validatedAtUtc.AddDays(15);
            BusinessDayClock.TickCount64Provider = () => 5_000L; // simulated reboot while offline

            var eval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(eval.IsValidForNewSales && !eval.IsOfflineGraceExpired && eval.RemainingOfflineGraceDays == 15,
                $"Terminal offline for 15 of 30 grace days must remain valid for sales (RemainingGrace={eval.RemainingOfflineGraceDays}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-17: Offline operation beyond grace period blocked
    private static async Task TestLic17_OfflineBeyondGracePeriodBlocked()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic17_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var validatedAtUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => validatedAtUtc;
            BusinessDayClock.TickCount64Provider = () => 10_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // 31 days offline without online validation (exceeds 30-day grace period)
            BusinessDayClock.UtcNowProvider = () => validatedAtUtc.AddDays(31);
            BusinessDayClock.TickCount64Provider = () => 5_000L;

            var eval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!eval.IsValidForNewSales && eval.IsOfflineGraceExpired &&
                   eval.EffectiveStatus == LicenseStatusConstants.ValidationRequired &&
                   eval.BlockReasonCode == "OFFLINE_GRACE_EXPIRED",
                $"Offline beyond 30-day grace period must require online revalidation (got {eval.BlockReasonCode}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-18: API unavailable during startup falls back to cached license with capped retries
    private static async Task TestLic18_ApiUnavailableDuringStartupFallbackAndCappedRetries()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic18_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;
            BusinessDayClock.TickCount64Provider = () => 10_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            int networkCalls = 0;
            using var unreachableHttp = new HttpClient(new InProcSyncHandler(_ =>
            {
                networkCalls++;
                throw new HttpRequestException("Simulated API offline at startup");
            }))
            {
                BaseAddress = new Uri("http://localhost:5032/")
            };

            // 3 forced failures hit the cap; 4th non-forced call uses cooldown without hitting network
            for (int i = 0; i < 3; i++)
            {
                var res = await clientLic.ValidateOnlineAsync(unreachableHttp, 1, force: true);
                Assert(res.Success && res.ErrorCode == "OFFLINE_FALLBACK",
                    "When API is unreachable at startup, valid cached license within grace period must allow offline operation.");
            }
            var cooldownRes = await clientLic.ValidateOnlineAsync(unreachableHttp, 1, force: false);
            Assert(cooldownRes.Success && networkCalls == 3,
                $"Capped retry cooldown must prevent infinite network retry loops (expected 3 network calls, got {networkCalls}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-19: License expiration during active session / ongoing transaction safe boundary
    private static async Task TestLic19_LicenseExpirationSafeBoundaryPreservesCommittedSales()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic19_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var activatedAt = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            // 10 seconds before 5-year expiration: sale is completed offline
            var beforeExpiryUtc = activatedAt.AddYears(5).AddSeconds(-10);
            BusinessDayClock.UtcNowProvider = () => activatedAt;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var pkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkg!);

            // Validate 1 day before expiry so offline grace is fresh
            BusinessDayClock.UtcNowProvider = () => activatedAt.AddYears(5).AddDays(-1);
            var valRes = await licSvc.ValidateLicenseAsync(new LicenseValidateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001")
            });
            await clientLic.SaveSignedLicensePackageAsync(valRes.LicensePackage!);

            // Complete sale 10 seconds before expiry
            BusinessDayClock.UtcNowProvider = () => beforeExpiryUtc;
            var session = new PosSessionService();
            session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "POS 1", BranchName = "Main", BranchCode = "BR-01"
            });

            OfflineCheckoutResultDto committedSale;
            using (var localDb = new LocalPosDbContext())
            {
                var checkout = new OfflineCheckoutService(localDb, session);
                committedSale = await checkout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                {
                    BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, CashierId = "cashier1",
                    Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
                }, 500m, "cashier1");
                Assert(committedSale.Success, "Sale before expiration must commit cleanly.");
            }

            // Now advance clock past expiration: new sales are blocked, but the already-committed sale in LocalOrders is intact!
            BusinessDayClock.UtcNowProvider = () => activatedAt.AddYears(5).AddMinutes(5);
            using (var localDb = new LocalPosDbContext())
            {
                var savedOrder = await localDb.Orders.FirstOrDefaultAsync(o => o.InvoiceNumber == committedSale.InvoiceNumber);
                Assert(savedOrder != null && savedOrder.PaymentStatus == "PAID",
                    "Already-committed sale must never be rolled back or lost when license expires.");

                var checkout = new OfflineCheckoutService(localDb, session);
                var nextSale = await checkout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                {
                    BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, CashierId = "cashier1",
                    Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
                }, 500m, "cashier1");
                Assert(!nextSale.Success, "Subsequent new sale after expiration must be blocked at the safe pre-sale boundary.");
            }
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-20: Unauthorized API calls and brute-force rate limit rejected
    private static async Task TestLic20_UnauthorizedApiCallsAndRateLimitRejected()
    {
        try
        {
            ResetLicensingTestHooks();
            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Cashier and Manager must be forbidden (403 ForbidResult) from Admin licensing management endpoints
            foreach (var nonAdminRole in new[] { "Cashier", "Manager", "Terminal" })
            {
                var nonAdminCtrl = CreateLicensingController(db, "user1", nonAdminRole);
                var listRes = await nonAdminCtrl.GetLicenses();
                Assert(listRes.Result is ForbidResult, $"Role '{nonAdminRole}' must be rejected with ForbidResult on GetLicenses.");

                var issueRes = await nonAdminCtrl.IssueLicense(new IssueTerminalLicenseRequestDto { TerminalId = 1 });
                Assert(issueRes.Result is ForbidResult, $"Role '{nonAdminRole}' must be rejected with ForbidResult on IssueLicense.");

                var renewRes = await nonAdminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto { TerminalId = 1, RenewalNotes = "Attempt" });
                Assert(renewRes.Result is ForbidResult, $"Role '{nonAdminRole}' must be rejected with ForbidResult on RenewLicense.");

                var revokeRes = await nonAdminCtrl.RevokeLicense(new ChangeLicenseStatusRequestDto { TerminalId = 1, Reason = "Attempt" });
                Assert(revokeRes.Result is ForbidResult, $"Role '{nonAdminRole}' must be rejected with ForbidResult on RevokeLicense.");
            }

            // Brute-force activation rate limiting (>20 attempts/min) must return 429 Too Many Requests
            var anonCtrl = CreateLicensingController(db, "anon", "Anonymous");
            ObjectResult? rateLimitedResult = null;
            for (int i = 0; i < 25; i++)
            {
                var actAttempt = await anonCtrl.ActivateTerminalLicense(new LicenseActivateRequestDto
                {
                    TerminalId = 1,
                    DeviceUuid = "wrong-uuid",
                    DeviceId = "DEV-WRONG"
                });
                if (actAttempt.Result is ObjectResult obj && obj.StatusCode == 429)
                {
                    rateLimitedResult = obj;
                    break;
                }
            }
            Assert(rateLimitedResult != null && rateLimitedResult.StatusCode == 429,
                "Repeated activation attempts must be throttled with HTTP 429 RATE_LIMITED.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-21: Multi-terminal, multi-branch, and multi-outlet isolation
    private static async Task TestLic21_MultiTerminalBranchOutletIsolation()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic21_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term1 = await db.Terminals.FindAsync(1);
            term1!.Uuid = "uuid-term-001";
            await db.SaveChangesAsync();

            // Issue & activate license for Terminal 1 (Branch 1, Outlet 1)
            var licSvc = new LicensingService(db);
            var pkgTerm1 = await licSvc.EnsureOrActivateTerminalLicenseAsync(term1, "uuid-term-001", null, "Admin");

            // Local terminal is configured as Terminal 1, Branch 2 (cross-branch mismatch)
            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, terminalId: 1, branchId: 2, outletId: 1, deviceUuid: "uuid-term-001", shiftId: shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            await clientLic.SaveSignedLicensePackageAsync(pkgTerm1!);

            var evalCrossBranch = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!evalCrossBranch.IsDeviceBoundValid && !evalCrossBranch.IsValidForNewSales && evalCrossBranch.BlockReasonCode == "TERMINAL_MISMATCH",
                $"License for Branch 1 must be rejected on a terminal assigned to Branch 2 (got {evalCrossBranch.BlockReasonCode}).");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // LIC-22: Historical transaction access, receipt reprint after expiration, and UAT vs Production key isolation
    private static async Task TestLic22_HistoricalAccessAfterExpirationAndEnvironmentKeyIsolation()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var activatedAt = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => activatedAt;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";

            // Seed a historical completed order before license expiration
            var historicalOrder = new Order
            {
                OrderNumber = "POS01-000001",
                InvoiceNumber = "INV-0000001",
                InvoiceIssuedAt = activatedAt,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = shift.ShiftId,
                CashierId = "cashier1",
                OrderType = "DINING",
                Subtotal = 500m,
                VatableSales = 446.43m,
                VatAmount = 53.57m,
                TotalAmount = 500m,
                PaymentStatus = "PAID",
                OrderStatus = "COMPLETED",
                CreatedAt = activatedAt
            };
            historicalOrder.OrderItems.Add(new OrderItem
            {
                ProductId = 1,
                ProductName = "Lechon Belly",
                Quantity = 1m,
                BasePrice = 500m,
                UnitPrice = 500m,
                LineTotal = 500m
            });
            historicalOrder.Payments.Add(new Payment
            {
                PaymentMethod = "CASH",
                Amount = 500m,
                AmountTendered = 500m,
                ChangeAmount = 0m,
                PaymentStatus = "PAID",
                ProcessedBy = "cashier1",
                PaidAt = activatedAt
            });
            db.Orders.Add(historicalOrder);
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            await licSvc.EnsureOrActivateTerminalLicenseAsync(term, "uuid-term-001", null, "Admin");

            // Advance time 6 years (license is EXPIRED)
            BusinessDayClock.UtcNowProvider = () => activatedAt.AddYears(6);

            // 1. Verify historical transaction history & receipt reprinting remain accessible when license is expired
            var receiptSvc = new POS.Api.Services.ReceiptService(db);
            var reprinted = await receiptSvc.GetReceiptAsync(historicalOrder.OrderId, isReprint: true);
            Assert(reprinted != null && reprinted.IsReprint && reprinted.InvoiceNumber == "INV-0000001",
                "Historical receipt reprinting must remain accessible after license expiration.");

            var txController = new TransactionsController(db, receiptSvc)
            {
                ControllerContext = CreateController(db, "admin1", "Admin").ControllerContext
            };
            var txHistoryRes = await txController.GetTransactions(null, null, 1, 1, null, null, null, null, null, null, null);
            var txList = (IEnumerable<OrderResponseDto>)((OkObjectResult)txHistoryRes.Result!).Value!;
            Assert(txList.Any(t => t.InvoiceNumber == "INV-0000001"),
                "Historical transaction inquiry must remain accessible after license expiration.");

            // 2. Verify UAT vs Production cryptographic key isolation (Phase 15)
            var licController = CreateLicensingController(db);
            var uatIssueRes = await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationYears = 5,
                EnvironmentType = "UAT",
                ActivateImmediatelyIfBound = true
            });
            var uatPkg = ((LicenseOperationResponseDto)((OkObjectResult)uatIssueRes.Result!).Value!).LicensePackage!;

            Assert(LicenseCryptoService.VerifySignature(uatPkg.Payload, uatPkg.Signature, "UAT"),
                "UAT license must verify against UAT public key.");
            Assert(!LicenseCryptoService.VerifySignature(uatPkg.Payload, uatPkg.Signature, "Production"),
                "UAT license must be rejected when Production environment is required.");

            var prodClientEval = LicenseCryptoService.EvaluateLicense(
                uatPkg.Payload, uatPkg.Signature, 1, 1, 1, uatPkg.Payload.DeviceId,
                expectedEnvironment: "Production",
                deviceUtcNow: activatedAt,
                currentTickCount64: 1000L,
                lastValidatedServerUtc: activatedAt,
                lastValidatedTickCount64: 1000L,
                highWaterMarkUtc: activatedAt);

            Assert(!prodClientEval.IsEnvironmentValid && !prodClientEval.IsValidForNewSales &&
                   prodClientEval.BlockReasonCode == "ENVIRONMENT_MISMATCH",
                "Production POS.Client must reject a UAT license with ENVIRONMENT_MISMATCH.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-23: Configurable duration presets (1Y/2Y/3Y/5Y), custom durations (Y/M/D), leap-year boundaries,
    // strict server-side validation, and delayed activation preserving pre-issued duration
    private static async Task TestLic23_ConfigurableDurationsPresetsCustomLeapAndValidation()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var baseUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => baseUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Add additional bound terminals to test 1Y, 2Y, 3Y, 5Y independently on separate terminals
            for (int tid = 1; tid <= 5; tid++)
            {
                var t = await db.Terminals.FindAsync(tid);
                if (t == null)
                {
                    t = new Terminal
                    {
                        TerminalId = tid,
                        BranchId = 1,
                        OutletId = 1,
                        TerminalCode = $"POS-{tid}",
                        Name = $"Counter {tid}",
                        IsActive = true
                    };
                    db.Terminals.Add(t);
                }
                t.Uuid = $"uuid-term-{tid:D3}";
                t.IsActivationCodeUsed = true;
            }
            await db.SaveChangesAsync();

            var licController = CreateLicensingController(db);

            // 1. Issue 1Y, 2Y, 3Y, 5Y presets on terminals 1..4
            int[] presets = [1, 2, 3, 5];
            for (int i = 0; i < presets.Length; i++)
            {
                int tid = i + 1;
                int years = presets[i];
                var res = await licController.IssueLicense(new IssueTerminalLicenseRequestDto
                {
                    TerminalId = tid,
                    DurationValue = years,
                    DurationUnit = LicenseDurationUnitConstants.Years,
                    OfflineGracePeriodDays = 30,
                    EnvironmentType = "Production",
                    ActivateImmediatelyIfBound = true,
                    ActivationEffectiveAtUtc = baseUtc
                });
                var ok = res.Result as OkObjectResult;
                Assert(ok != null, $"Preset {years}Y issue must succeed.");
                var op = (LicenseOperationResponseDto)ok!.Value!;
                Assert(op.Success && op.LicensePackage != null, $"Preset {years}Y must return signed package.");
                var p = op.LicensePackage!.Payload;
                Assert(p.DurationValue == years && p.DurationUnit == LicenseDurationUnitConstants.Years,
                    $"Expected {years} YEARS on payload.");
                Assert(p.OriginalDurationValue == years && p.OriginalDurationUnit == LicenseDurationUnitConstants.Years,
                    $"Expected original {years} YEARS on payload.");
                Assert(p.ExpiresAtUtc == baseUtc.AddYears(years),
                    $"Expected {years}Y expiration {baseUtc.AddYears(years):O}, got {p.ExpiresAtUtc:O}.");
                Assert(LicenseCryptoService.VerifySignature(p, op.LicensePackage.Signature),
                    $"Signature must verify for {years}Y license.");
            }

            // 2. Custom durations (18 MONTHS, 45 DAYS, leap-year boundary, month-end boundary)
            var cust18M = await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 5,
                DurationValue = 18,
                DurationUnit = LicenseDurationUnitConstants.Months,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = true,
                ActivationEffectiveAtUtc = baseUtc
            });
            var ok18M = (LicenseOperationResponseDto)((OkObjectResult)cust18M.Result!).Value!;
            Assert(ok18M.LicensePackage!.Payload.ExpiresAtUtc == baseUtc.AddMonths(18) &&
                   ok18M.LicensePackage.Payload.DurationValue == 18 &&
                   ok18M.LicensePackage.Payload.DurationUnit == LicenseDurationUnitConstants.Months,
                "Custom 18-MONTHS issuance must compute exact calendar expiration.");

            var leapStart = new DateTime(2024, 2, 29, 8, 0, 0, DateTimeKind.Utc);
            Assert(LicenseCryptoService.CalculateExpirationUtc(leapStart, 1, LicenseDurationUnitConstants.Years) ==
                   new DateTime(2025, 2, 28, 8, 0, 0, DateTimeKind.Utc),
                "Leap-day + 1 year must map safely to Feb 28 of non-leap year.");
            Assert(LicenseCryptoService.CalculateExpirationUtc(leapStart, 4, LicenseDurationUnitConstants.Years) ==
                   new DateTime(2028, 2, 29, 8, 0, 0, DateTimeKind.Utc),
                "Leap-day + 4 years must preserve Feb 29 on leap year.");
            Assert(LicenseCryptoService.CalculateExpirationUtc(new DateTime(2026, 1, 31, 8, 0, 0, DateTimeKind.Utc), 1, LicenseDurationUnitConstants.Months) ==
                   new DateTime(2026, 2, 28, 8, 0, 0, DateTimeKind.Utc),
                "Jan 31 + 1 month must clamp cleanly to Feb 28.");

            // 3. Invalid server-side durations must be rejected (no silent 5Y fallback)
            var badRequests = new[]
            {
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 0, DurationYears = 0, DurationUnit = "YEARS" },
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = -3, DurationYears = 0, DurationUnit = "YEARS" },
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 2, DurationUnit = "HOURS" },
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 51, DurationUnit = "YEARS" },
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 601, DurationUnit = "MONTHS" },
                new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 18251, DurationUnit = "DAYS" }
            };
            foreach (var badReq in badRequests)
            {
                var badRes = await licController.IssueLicense(badReq);
                Assert(badRes.Result is BadRequestObjectResult,
                    $"Invalid duration ({badReq.DurationValue} {badReq.DurationUnit}) must return 400 BadRequest without falling back.");
            }

            // 4. Pre-issued custom duration (PENDING_ACTIVATION) honored on delayed hardware activation
            var term6 = new Terminal { TerminalId = 6, BranchId = 1, OutletId = 1, TerminalCode = "POS-6", Name = "Counter 6", IsActive = true };
            db.Terminals.Add(term6);
            await db.SaveChangesAsync();

            var preIssue = await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 6,
                DurationValue = 90,
                DurationUnit = LicenseDurationUnitConstants.Days,
                ActivateImmediatelyIfBound = false,
                EnvironmentType = "Production"
            });
            Assert(preIssue.Result is OkObjectResult, "Pending pre-issuance of 90 DAYS must succeed.");

            var delayedActUtc = new DateTime(2027, 1, 15, 4, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => delayedActUtc;
            term6.Uuid = "uuid-term-006";
            await db.SaveChangesAsync();

            var licSvc = new LicensingService(db);
            var activatedPkg = await licSvc.EnsureOrActivateTerminalLicenseAsync(term6, "uuid-term-006", null, "Admin");
            Assert(activatedPkg != null, "Delayed activation must succeed.");
            Assert(activatedPkg!.Payload.ActivatedAtUtc == delayedActUtc &&
                   activatedPkg.Payload.ExpiresAtUtc == delayedActUtc.AddDays(90) &&
                   activatedPkg.Payload.DurationValue == 90 &&
                   activatedPkg.Payload.DurationUnit == LicenseDurationUnitConstants.Days &&
                   activatedPkg.Payload.OriginalDurationValue == 90 &&
                   activatedPkg.Payload.OriginalDurationUnit == LicenseDurationUnitConstants.Days,
                "Delayed activation must honor pre-issued 90 DAYS duration rather than defaulting to 5 years.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-24: Renew active license (preserve unused validity), renew expired license, renew with different duration,
    // immutable renewal history & auditable ADJUSTMENT records
    private static async Task TestLic24_RenewalsActiveExpiredDifferentDurationAndHistory()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var startUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => startUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var licController = CreateLicensingController(db);
            var licSvc = new LicensingService(db);

            // Issue initial 2-Year license -> expires 2028-11-14
            await licController.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationValue = 2,
                DurationUnit = LicenseDurationUnitConstants.Years,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = true,
                ActivationEffectiveAtUtc = startUtc
            });

            // 1. Renew ALREADY ACTIVE license 6 months later by 1 Year (must extend from 2028-11-14 -> 2029-11-14)
            var sixMonthsLater = startUtc.AddMonths(6);
            BusinessDayClock.UtcNowProvider = () => sixMonthsLater;
            var activeRenewRes = await licController.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 1,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IdempotencyKey = "ren-active-1y",
                RenewalReason = "Early 1Y extension"
            });
            var op1 = (LicenseOperationResponseDto)((OkObjectResult)activeRenewRes.Result!).Value!;
            Assert(op1.Success && op1.LicensePackage!.Payload.ExpiresAtUtc == startUtc.AddYears(2).AddYears(1),
                $"Active renewal must preserve remaining validity and expire on 2029-11-14 (got {op1.LicensePackage!.Payload.ExpiresAtUtc:O}).");
            Assert(op1.LicensePackage!.Payload.LicenseVersion == 2 && op1.LicensePackage.RenewalCount == 1,
                "Version must increment to 2 and RenewalCount to 1.");

            // 2. Renew with DIFFERENT duration (6 MONTHS) -> extends from 2029-11-14 to 2030-05-14; Original stays 2 YEARS
            var diffDurRes = await licController.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 6,
                RenewalUnit = LicenseDurationUnitConstants.Months,
                IdempotencyKey = "ren-diff-6m",
                RenewalReason = "Bridge 6 months"
            });
            var op2 = (LicenseOperationResponseDto)((OkObjectResult)diffDurRes.Result!).Value!;
            var expectedExp2 = startUtc.AddYears(3).AddMonths(6);
            Assert(op2.LicensePackage!.Payload.ExpiresAtUtc == expectedExp2 &&
                   op2.LicensePackage.Payload.DurationValue == 6 &&
                   op2.LicensePackage.Payload.DurationUnit == LicenseDurationUnitConstants.Months &&
                   op2.LicensePackage.Payload.OriginalDurationValue == 2 &&
                   op2.LicensePackage.Payload.OriginalDurationUnit == LicenseDurationUnitConstants.Years &&
                   op2.LicensePackage.RenewalCount == 2,
                "Second renewal with 6 MONTHS must preserve OriginalDuration (2 YEARS) and update current duration.");

            // 3. Renew EXPIRED license -> expires from authorized effective date
            var postExpiryUtc = expectedExp2.AddDays(18);
            BusinessDayClock.UtcNowProvider = () => postExpiryUtc;
            var expRenewRes = await licController.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 2,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                EffectiveAtUtc = postExpiryUtc,
                IdempotencyKey = "ren-expired-2y",
                RenewalReason = "Post-expiration 2Y renewal"
            });
            var op3 = (LicenseOperationResponseDto)((OkObjectResult)expRenewRes.Result!).Value!;
            Assert(op3.LicensePackage!.Payload.ExpiresAtUtc == postExpiryUtc.AddYears(2) &&
                   op3.LicensePackage.Payload.LicenseStatus == LicenseStatusConstants.Active &&
                   op3.LicensePackage.RenewalCount == 3,
                "Expired license renewal must start from effective date (not old expired date) and restore ACTIVE status.");

            // 4. Create an auditable ADJUSTMENT record & verify prior 3 renewal rows remain immutable
            var adjRes = await licController.AdjustLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 3,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IsAdjustment = true,
                IdempotencyKey = "adj-corr-3y",
                RenewalReason = "Billing contract upgrade correction"
            });
            Assert(adjRes.Result is OkObjectResult, "Admin AdjustLicense must succeed.");

            var history = await licSvc.GetRenewalHistoryAsync(1);
            Assert(history.Count == 4, $"Expected 4 immutable renewal/adjustment records, got {history.Count}.");
            var asc = history.OrderBy(h => h.RenewalId).ToList();
            Assert(asc[0].RenewalDuration == 1 && asc[0].RenewalUnit == LicenseDurationUnitConstants.Years &&
                   asc[0].PreviousExpiresAtUtc == startUtc.AddYears(2) && asc[0].NewExpiresAtUtc == startUtc.AddYears(3) &&
                   !string.IsNullOrWhiteSpace(asc[0].RenewalStatus) && asc[0].RenewalStatus != LicenseRenewalStatusConstants.Adjustment,
                "Record #1 (1Y active extension) must remain intact.");
            Assert(asc[1].RenewalDuration == 6 && asc[1].RenewalUnit == LicenseDurationUnitConstants.Months &&
                   asc[1].PreviousExpiresAtUtc == startUtc.AddYears(3) && asc[1].NewExpiresAtUtc == expectedExp2 &&
                   !string.IsNullOrWhiteSpace(asc[1].RenewalStatus) && asc[1].RenewalStatus != LicenseRenewalStatusConstants.Adjustment,
                "Record #2 (6M extension) must remain intact.");
            Assert(asc[2].RenewalDuration == 2 && asc[2].RenewalUnit == LicenseDurationUnitConstants.Years &&
                   asc[2].PreviousExpiresAtUtc == expectedExp2 && asc[2].NewExpiresAtUtc == postExpiryUtc.AddYears(2) &&
                   !string.IsNullOrWhiteSpace(asc[2].RenewalStatus) && asc[2].RenewalStatus != LicenseRenewalStatusConstants.Adjustment,
                "Record #3 (expired 2Y renewal) must remain intact.");
            Assert(asc[3].RenewalStatus == LicenseRenewalStatusConstants.Adjustment &&
                   asc[3].RenewalReason == "Billing contract upgrade correction",
                "Record #4 must be logged separately as ADJUSTMENT without modifying prior records.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-25: Non-Admin authorization block, idempotency replay & conflict, pre-renewal DB tamper guard,
    // and post-renewal ECDSA P-256 signature verification covering duration fields
    private static async Task TestLic25_RenewalSecurityUnauthorizedIdempotencyAndSignatures()
    {
        try
        {
            ResetLicensingTestHooks();
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";
            var nowUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            await db.SaveChangesAsync();

            var adminCtrl = CreateLicensingController(db, "admin1", "Admin");
            var cashierCtrl = CreateLicensingController(db, "cashier1", "Cashier");
            var managerCtrl = CreateLicensingController(db, "manager1", "Manager");

            await adminCtrl.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationValue = 1,
                DurationUnit = LicenseDurationUnitConstants.Years,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = true,
                ActivationEffectiveAtUtc = nowUtc
            });

            // 1. Unauthorized non-Admin users (Cashier & Manager) rejected from issuing, renewing, and adjusting
            Assert((await cashierCtrl.IssueLicense(new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 5, DurationUnit = "YEARS" })).Result is ForbidResult,
                "Cashier must receive 403 Forbid on IssueLicense.");
            Assert((await managerCtrl.RenewLicense(new RenewTerminalLicenseRequestDto { TerminalId = 1, RenewalDuration = 5, RenewalUnit = "YEARS" })).Result is ForbidResult,
                "Manager must receive 403 Forbid on RenewLicense.");
            Assert((await managerCtrl.AdjustLicense(new RenewTerminalLicenseRequestDto { TerminalId = 1, RenewalDuration = 5, RenewalUnit = "YEARS", IsAdjustment = true, RenewalReason = "Bypass" })).Result is ForbidResult,
                "Manager must receive 403 Forbid on AdjustLicense.");

            // 2. Idempotent duplicate retry vs Idempotency conflict (different parameters on same key)
            var idemKey = "idem-renew-001";
            var r1 = await adminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 2,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IdempotencyKey = idemKey,
                RenewalReason = "2-Year Renewal"
            });
            var op1 = (LicenseOperationResponseDto)((OkObjectResult)r1.Result!).Value!;
            var expectedExpAfterR1 = nowUtc.AddYears(1).AddYears(2);
            Assert(op1.Success && !op1.IsIdempotentReplay && op1.LicensePackage!.Payload.ExpiresAtUtc == expectedExpAfterR1,
                "First renewal request must apply once.");

            // Exact replay -> must succeed with IsIdempotentReplay=true and NOT extend expiration again
            var r2 = await adminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 2,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IdempotencyKey = idemKey,
                RenewalReason = "2-Year Renewal"
            });
            var op2 = (LicenseOperationResponseDto)((OkObjectResult)r2.Result!).Value!;
            Assert(op2.Success && op2.IsIdempotentReplay &&
                   op2.LicensePackage!.Payload.ExpiresAtUtc == expectedExpAfterR1 &&
                   op2.LicensePackage.RenewalCount == 1,
                "Duplicate renewal with same IdempotencyKey must replay safely without double-extending.");

            // Conflicting duration with same IdempotencyKey -> must return 409 Conflict
            var rConflict = await adminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 5,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IdempotencyKey = idemKey,
                RenewalReason = "Conflicting payload"
            });
            Assert(rConflict.Result is ConflictObjectResult conflictObj &&
                   ((LicenseOperationResponseDto)conflictObj.Value!).ErrorCode == "IDEMPOTENCY_CONFLICT",
                "Reusing IdempotencyKey with different duration must return 409 IDEMPOTENCY_CONFLICT.");

            // 3. Post-renewal signature verification covers duration fields
            var validPkg = op1.LicensePackage!;
            Assert(LicenseCryptoService.VerifySignature(validPkg.Payload, validPkg.Signature),
                "Post-renewal signed payload must pass ECDSA P-256 verification.");

            var tamperedDurVal = JsonSerializer.Deserialize<CanonicalLicensePayload>(JsonSerializer.Serialize(validPkg.Payload))!;
            tamperedDurVal.DurationValue = 50;
            Assert(!LicenseCryptoService.VerifySignature(tamperedDurVal, validPkg.Signature),
                "Tampering DurationValue must invalidate post-renewal ECDSA P-256 signature.");

            var tamperedDurUnit = JsonSerializer.Deserialize<CanonicalLicensePayload>(JsonSerializer.Serialize(validPkg.Payload))!;
            tamperedDurUnit.DurationUnit = LicenseDurationUnitConstants.Days;
            Assert(!LicenseCryptoService.VerifySignature(tamperedDurUnit, validPkg.Signature),
                "Tampering DurationUnit must invalidate signature.");

            var tamperedOrig = JsonSerializer.Deserialize<CanonicalLicensePayload>(JsonSerializer.Serialize(validPkg.Payload))!;
            tamperedOrig.OriginalDurationValue = 99;
            Assert(!LicenseCryptoService.VerifySignature(tamperedOrig, validPkg.Signature),
                "Tampering OriginalDurationValue must invalidate signature.");

            // 4. Pre-renewal PostgreSQL tampering guard: corrupt ExpiresAt directly in DB before next renewal
            var dbLic = await db.TerminalLicenses.SingleAsync(l => l.TerminalId == 1);
            dbLic.ExpiresAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();

            var blockedTamperRenew = await adminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 1,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                IdempotencyKey = "ren-after-db-tamper",
                RenewalReason = "Should reject tampered base row"
            });
            Assert(blockedTamperRenew.Result is BadRequestObjectResult,
                "Renewing a database row whose existing signature fails verification must be rejected.");

            // 5. Verify ASP.NET Core DI can activate LicensingController unambiguously
            var diFactory = ActivatorUtilities.CreateFactory(typeof(LicensingController), Type.EmptyTypes);
            Assert(diFactory != null, "LicensingController must resolve cleanly via ActivatorUtilities.");
        }
        finally
        {
            ResetLicensingTestHooks();
        }
    }

    // LIC-26: Offline renewal behavior, clock tamper defense, wrong-device/forged sync rejection,
    // reconnect auto-sync update, and preservation of pending offline transactions
    private static async Task TestLic26_OfflineRenewalReconnectAndPendingTransactionPreservation()
    {
        var prevPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDb = Path.Combine(Path.GetTempPath(), $"pos_lic26_{Guid.NewGuid():N}.db");
        try
        {
            ResetLicensingTestHooks();
            AppContext.SetData("POS.LocalDatabasePath", tempDb);
            ClientLicenseService.StrictEnforcementMode = true;
            DeviceBindingHelper.HardwareFingerprintProvider = () => "HW-MACHINE-A";

            var startUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => startUtc;
            BusinessDayClock.TickCount64Provider = () => 100_000L;

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var term = await db.Terminals.FindAsync(1);
            term!.Uuid = "uuid-term-001";
            term.IsActivationCodeUsed = true;
            db.Terminals.Add(new Terminal
            {
                TerminalId = 2, BranchId = 1, OutletId = 1,
                TerminalCode = "POS-2", Name = "Counter 2", Uuid = "uuid-term-002", IsActivationCodeUsed = true, IsActive = true
            });
            await db.SaveChangesAsync();

            var adminCtrl = CreateLicensingController(db);
            var issueOp = (LicenseOperationResponseDto)((OkObjectResult)(await adminCtrl.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 1,
                DurationValue = 1,
                DurationUnit = LicenseDurationUnitConstants.Years,
                OfflineGracePeriodDays = 30,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = true,
                ActivationEffectiveAtUtc = startUtc
            })).Result!).Value!;

            using (var localDb = new LocalPosDbContext())
            {
                await SeedLocalSqliteForLicensingTestAsync(localDb, 1, 1, 1, "uuid-term-001", shift.ShiftId);
            }
            var clientLic = new ClientLicenseService();
            Assert(await clientLic.SaveSignedLicensePackageAsync(issueOp.LicensePackage!),
                "Initial 1Y license must save into SQLite.");

            // Refresh online validation 2 days before 1Y expiry so 30-day offline grace is active before going offline
            var preExpirySyncUtc = startUtc.AddYears(1).AddDays(-2);
            BusinessDayClock.UtcNowProvider = () => preExpirySyncUtc;
            BusinessDayClock.TickCount64Provider = () => 150_000L;
            var licSvcPre = new LicensingService(db);
            var preValRes = await licSvcPre.ValidateLicenseAsync(new LicenseValidateRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "uuid-term-001",
                DeviceId = DeviceBindingHelper.ComputeDeviceId("uuid-term-001")
            });
            Assert(await clientLic.SaveSignedLicensePackageAsync(preValRes.LicensePackage!),
                "Pre-expiry online validation checkpoint must save into SQLite.");

            // Complete an offline sale 1 hour before 1Y expiry in Manila UTC+8 local time
            // so LocalSyncOutbox.CreatedAt is +8h ahead of UTC (must NOT false-trigger CLOCK_ROLLBACK_DETECTED)
            var preExpirySaleUtc = startUtc.AddYears(1).AddHours(-1);
            BusinessDayClock.UtcNowProvider = () => preExpirySaleUtc;
            BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(
                DateTime.SpecifyKind(preExpirySaleUtc.AddHours(8), DateTimeKind.Unspecified),
                TimeSpan.FromHours(8));
            BusinessDayClock.TickCount64Provider = () => 200_000L;
            var session = new PosSessionService();
            session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "POS 1", BranchName = "Main", BranchCode = "BR-01"
            });
            string pendingInvoice;
            using (var localDb = new LocalPosDbContext())
            {
                var checkout = new OfflineCheckoutService(localDb, session);
                var sale = await checkout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                {
                    BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, CashierId = "cashier1",
                    Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Lechon Belly", Quantity = 1m, BasePrice = 500m, TaxName = "VATable" }]
                }, 500m, "cashier1");
                Assert(sale.Success && !string.IsNullOrEmpty(sale.InvoiceNumber),
                    $"Pre-expiry offline sale must succeed (error: {sale.ErrorMessage}).");
                pendingInvoice = sale.InvoiceNumber!;
            }

            var postLocalSaleEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(postLocalSaleEval.IsValidForNewSales && !postLocalSaleEval.IsClockRollbackDetected,
                $"Local UTC+8 outbox timestamp must not trigger false CLOCK_ROLLBACK_DETECTED (got {postLocalSaleEval.BlockReasonCode}).");

            // Advance past 1Y expiration while still offline -> sales blocked, pending transaction intact
            var expiredUtc = startUtc.AddYears(1).AddDays(2);
            BusinessDayClock.UtcNowProvider = () => expiredUtc;
            BusinessDayClock.TickCount64Provider = () => 300_000L;

            var expiredEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!expiredEval.IsValidForNewSales && expiredEval.BlockReasonCode == "LICENSE_EXPIRED",
                $"Expired 1Y license offline must block sales (got {expiredEval.BlockReasonCode}).");

            // Device clock tampering offline must NOT fake renewal or bypass expiration
            BusinessDayClock.UtcNowProvider = () => startUtc.AddMonths(6);
            var tamperEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(!tamperEval.IsValidForNewSales && tamperEval.IsClockRollbackDetected,
                "Rolling device clock back offline must not unlock expired license.");
            BusinessDayClock.UtcNowProvider = () => expiredUtc;

            // Offline reconnect retry fails gracefully and preserves cached license & pending order
            using var offlineHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline")))
            {
                BaseAddress = new Uri("http://localhost:5032/")
            };
            var offlineRes = await clientLic.ValidateOnlineAsync(offlineHttp, 1, force: true);
            Assert(offlineRes.ErrorCode == "OFFLINE_FALLBACK", "Offline ValidateOnlineAsync must fall back gracefully.");

            // Admin renews Terminal 1 for 2 Years on central POS.Api & issues another license for Terminal 2
            var renewRes = (LicenseOperationResponseDto)((OkObjectResult)(await adminCtrl.RenewLicense(new RenewTerminalLicenseRequestDto
            {
                TerminalId = 1,
                RenewalDuration = 2,
                RenewalUnit = LicenseDurationUnitConstants.Years,
                EffectiveAtUtc = expiredUtc,
                IdempotencyKey = "offline-ren-2y",
                RenewalReason = "Offline terminal 2Y renewal"
            })).Result!).Value!;
            var term2Issue = (LicenseOperationResponseDto)((OkObjectResult)(await adminCtrl.IssueLicense(new IssueTerminalLicenseRequestDto
            {
                TerminalId = 2,
                DurationValue = 3,
                DurationUnit = LicenseDurationUnitConstants.Years,
                EnvironmentType = "Production",
                ActivateImmediatelyIfBound = true,
                ActivationEffectiveAtUtc = expiredUtc
            })).Result!).Value!;

            // Security check 1: forged signature in sync payload must be rejected before replacing SQLite cache
            using var rogueEcdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var forgedOp = new LicenseOperationResponseDto
            {
                Success = true,
                LicensePackage = new SignedLicensePackageDto
                {
                    Payload = renewRes.LicensePackage!.Payload,
                    Signature = LicenseCryptoService.SignPayload(renewRes.LicensePackage.Payload, rogueEcdsa.ExportParameters(true)),
                    ServerUtcNow = expiredUtc
                }
            };
            using var forgedHttp = new HttpClient(new InProcSyncHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(forgedOp, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            }))) { BaseAddress = new Uri("http://localhost:5032/") };
            var forgedSync = await clientLic.ValidateOnlineAsync(forgedHttp, 1, force: true);
            Assert(!forgedSync.Success, "ValidateOnlineAsync must reject forged signature before updating SQLite.");

            // Security check 2: validly signed license belonging to Terminal 2 must be rejected for Terminal 1
            using var wrongTermHttp = new HttpClient(new InProcSyncHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(term2Issue, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            }))) { BaseAddress = new Uri("http://localhost:5032/") };
            var wrongTermSync = await clientLic.ValidateOnlineAsync(wrongTermHttp, 1, force: true);
            Assert(!wrongTermSync.Success, "ValidateOnlineAsync must reject valid license bound to a different terminal/device.");

            // Still has original expired 1Y license (never overwritten by rejected payloads)
            Assert((await clientLic.EvaluateLocalLicenseAsync(1)).DurationValue == 1 &&
                   !(await clientLic.EvaluateLocalLicenseAsync(1)).IsValidForNewSales,
                "Rejected sync payloads must never overwrite cached SQLite license.");

            // Valid connectivity returns -> ValidateOnlineAsync syncs the real 2Y renewed license from server
            var licSvc = new LicensingService(db);
            using var realHttp = new HttpClient(new InProcSyncHandler(async req =>
            {
                var body = await req.Content!.ReadAsStringAsync();
                var vReq = JsonSerializer.Deserialize<LicenseValidateRequestDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var vRes = await licSvc.ValidateLicenseAsync(vReq);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(vRes, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
                };
            })) { BaseAddress = new Uri("http://localhost:5032/") };

            var synced = await clientLic.ValidateOnlineAsync(realHttp, 1, force: true);
            Assert(synced.Success, $"Reconnect ValidateOnlineAsync must succeed: {synced.Message}");

            var postSyncEval = await clientLic.EvaluateLocalLicenseAsync(1);
            Assert(postSyncEval.IsValidForNewSales &&
                   postSyncEval.DurationValue == 2 &&
                   postSyncEval.DurationUnit == LicenseDurationUnitConstants.Years &&
                   postSyncEval.OriginalDurationValue == 1 &&
                   postSyncEval.OriginalDurationUnit == LicenseDurationUnitConstants.Years &&
                   postSyncEval.RenewalCount == 1 &&
                   postSyncEval.ExpiresAtUtc == expiredUtc.AddYears(2),
                "After reconnect sync, client must verify signature, update local SQLite duration/expiration, and unblock sales.");

            // Pending offline transaction created before renewal sync must still be intact in SQLite
            using (var localDb = new LocalPosDbContext())
            {
                var checkout = new OfflineCheckoutService(localDb, session);
                var pendingOrders = await checkout.GetPendingSyncOrdersAsync();
                Assert(pendingOrders.Any(o => o.InvoiceNumber == pendingInvoice),
                    "Pending offline transaction must remain intact after license expiration and online renewal sync.");
            }
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            ResetLicensingTestHooks();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }
}
