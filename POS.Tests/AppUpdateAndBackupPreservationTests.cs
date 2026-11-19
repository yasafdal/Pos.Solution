using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Helpers;
using POS.Shared.Models;

namespace POS.Tests;

public partial class Program
{
    private static async Task TestUpdate01_LegacyV0ToV2MigrationPreservesOfflineDataAndCreatesBackup()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var prevSurvivableDir = AppContext.GetData("POS.SurvivableBackupDirectory");
        var prevSimFail = AppContext.GetData("POS.SimulateMigrationFailureAtVersion");

        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd01_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            AppContext.SetData("POS.SurvivableBackupDirectory", Path.Combine(tempDir, "Survivable"));
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", null);
            LocalPosDbContext.ResetInitializationState(dbPath);

            // 1. Seed a legacy V0 database (PRAGMA user_version = 0) with unsynced sales, shift, outbox, sequence, and license
            using (var db = new LocalPosDbContext())
            {
                db.Database.EnsureCreated();
                using (var conn = db.Database.GetDbConnection())
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA user_version = 0;";
                    cmd.ExecuteNonQuery();
                }

                db.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 10,
                    OutletId = 2,
                    BranchId = 1,
                    TerminalCode = "T-10",
                    TerminalName = "Tablet 10",
                    DeviceUuid = "uuid-v0-legacy-10",
                    TerminalIdentifier = "uuid-v0-legacy-10",
                    IsActivated = true,
                    ReceiptPrinterName = "CustomPrinter-80mm"
                });

                db.Orders.Add(new LocalOrder
                {
                    OrderId = "OFFLINE-TX-001",
                    OrderNumber = "0001",
                    BranchId = 1,
                    OutletId = 2,
                    TerminalId = 10,
                    CashierId = "cashier1",
                    Subtotal = 500m,
                    TotalAmount = 500m,
                    PaymentStatus = "SUCCESS", // Legacy status that V1 normalizes to PAID
                    OrderStatus = "COMPLETED",
                    InvoiceNumber = "INV-000105",
                    CreatedAt = DateTime.UtcNow
                });

                db.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "Order",
                    EntityId = "OFFLINE-TX-001",
                    PayloadJson = "{\"ClientTransactionId\":\"OFFLINE-TX-001\"}",
                    Status = "PENDING",
                    CreatedAt = DateTime.UtcNow
                });

                db.InvoiceSequences.Add(new LocalInvoiceSequence
                {
                    SequenceId = 1,
                    TerminalId = 10,
                    SeriesCode = "S1",
                    Prefix = "INV-",
                    StartingNumber = 1,
                    EndingNumber = 10000,
                    LastIssuedNumber = 105,
                    PaddingLength = 6,
                    IsActive = true
                });

                await db.SaveChangesAsync();
            }

            // 2. Simulate APK update startup: ResetInitializationState + InitializeDatabase()
            LocalPosDbContext.ResetInitializationState(dbPath);
            using (var upgradedDb = new LocalPosDbContext())
            {
                upgradedDb.InitializeDatabase();

                var conn = upgradedDb.Database.GetDbConnection();
                int schemaVer = LocalDatabaseMigrator.GetUserVersion(conn);
                Assert(schemaVer == LocalDatabaseMigrator.CurrentSchemaVersion,
                    $"Expected PRAGMA user_version={LocalDatabaseMigrator.CurrentSchemaVersion}, got {schemaVer}");

                var preservedOrder = await upgradedDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == "OFFLINE-TX-001");
                Assert(preservedOrder.PaymentStatus == "PAID", "Legacy SUCCESS status should be normalized to PAID during V1 migration.");
                Assert(preservedOrder.InvoiceNumber == "INV-000105", "Offline InvoiceNumber must be preserved.");

                var preservedOutbox = await upgradedDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityId == "OFFLINE-TX-001");
                Assert(preservedOutbox.Status == "PENDING", "Pending outbox record must be preserved across APK update.");

                var preservedSeq = await upgradedDb.InvoiceSequences.AsNoTracking().SingleAsync(s => s.TerminalId == 10);
                Assert(preservedSeq.LastIssuedNumber == 105, "Invoice sequence counter must be preserved.");

                var preservedCfg = await upgradedDb.TerminalConfigs.AsNoTracking().SingleAsync(t => t.TerminalId == 10);
                Assert(preservedCfg.DeviceUuid == "uuid-v0-legacy-10", "DeviceUuid must be preserved.");
                Assert(preservedCfg.ReceiptPrinterName == "CustomPrinter-80mm", "Local printer config must be preserved.");
            }

            // 3. Verify pre-migration backup was created and passes integrity check
            var backupDir = LocalDatabaseBackupService.GetInternalBackupDirectory(dbPath);
            var backups = Directory.GetFiles(backupDir, "local_pos_pre_mig_*.db");
            Assert(backups.Length >= 1, "Pre-migration backup file must be created before upgrading an existing database.");
            Assert(LocalDatabaseBackupService.VerifySqliteFileIntegrity(backups[0]), "Pre-migration backup must pass PRAGMA quick_check.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            AppContext.SetData("POS.SurvivableBackupDirectory", prevSurvivableDir);
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", prevSimFail);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestUpdate02_FailedMigrationRollbackAndDowngradeProtection()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var prevSimFail = AppContext.GetData("POS.SimulateMigrationFailureAtVersion");

        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd02_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", null);
            LocalPosDbContext.ResetInitializationState(dbPath);

            // 1. Create a V1 database with an offline sale
            using (var db = new LocalPosDbContext())
            {
                db.InitializeDatabase();
                db.Orders.Add(new LocalOrder
                {
                    OrderId = "TX-ROLLBACK-SAFE",
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    TotalAmount = 250m,
                    PaymentStatus = "PAID",
                    OrderStatus = "COMPLETED",
                    InvoiceNumber = "INV-000088",
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();

                using var conn = db.Database.GetDbConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 1; DROP TABLE IF EXISTS LocalSchemaMigrations;";
                cmd.ExecuteNonQuery();
            }

            // 2. Simulate failure at Migration V2 and verify automatic backup restore
            SqliteConnection.ClearAllPools();
            LocalPosDbContext.ResetInitializationState(dbPath);
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", 2);

            bool threwOnFailure = false;
            try
            {
                using var failingDb = new LocalPosDbContext();
                failingDb.InitializeDatabase();
            }
            catch (InvalidOperationException)
            {
                threwOnFailure = true;
            }
            Assert(threwOnFailure, "Migration failure at V2 must throw and trigger backup rollback.");

            // Verify the original V1 database and its offline sale were automatically restored intact
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", null);
            using (var checkConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                checkConn.Open();
                int verAfterRollback = LocalDatabaseMigrator.GetUserVersion(checkConn);
                Assert(verAfterRollback == 1, $"Restored backup should still be at user_version 1 before retry, got {verAfterRollback}");
            }

            // Retry migration without fault injection -> succeeds cleanly to V2 with offline sale intact
            LocalPosDbContext.ResetInitializationState(dbPath);
            using (var recoveredDb = new LocalPosDbContext())
            {
                recoveredDb.InitializeDatabase();
                var order = await recoveredDb.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == "TX-ROLLBACK-SAFE");
                Assert(order != null && order.InvoiceNumber == "INV-000088", "Offline sale must survive failed migration rollback and subsequent retry.");
            }

            // 3. Verify Downgrade Protection blocks opening a database with a newer schema version (e.g. V99)
            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 99;";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            LocalPosDbContext.ResetInitializationState(dbPath);

            bool blockedDowngrade = false;
            try
            {
                using var olderAppDb = new LocalPosDbContext();
                olderAppDb.InitializeDatabase();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("newer than this application version"))
            {
                blockedDowngrade = true;
            }
            Assert(blockedDowngrade, "Downgrade guard must reject opening a database with a newer PRAGMA user_version.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            AppContext.SetData("POS.SimulateMigrationFailureAtVersion", prevSimFail);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestUpdate03_SurvivableEncryptedBackupAndAccidentalUninstallRestore()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var prevSurvivableDir = AppContext.GetData("POS.SurvivableBackupDirectory");

        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd03_{Guid.NewGuid():N}");
        var appInternalDir = Path.Combine(tempDir, "AppInternal");
        var sharedDocsDir = Path.Combine(tempDir, "SharedDocuments_AgresPOS_Backups");
        Directory.CreateDirectory(appInternalDir);
        Directory.CreateDirectory(sharedDocsDir);
        var dbPath = Path.Combine(appInternalDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            AppContext.SetData("POS.SurvivableBackupDirectory", sharedDocsDir);
            LocalPosDbContext.ResetInitializationState(dbPath);

            // 1. Provision terminal and record unsynchronized offline sale
            using (var db = new LocalPosDbContext())
            {
                db.InitializeDatabase();
                db.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 25,
                    OutletId = 3,
                    BranchId = 1,
                    TerminalCode = "T-25",
                    TerminalName = "Tablet 25",
                    DeviceUuid = "survivable-device-uuid-25",
                    TerminalIdentifier = "survivable-device-uuid-25",
                    IsActivated = true
                });
                db.Orders.Add(new LocalOrder
                {
                    OrderId = "OFFLINE-UNINSTALL-TX-99",
                    OrderNumber = "0099",
                    BranchId = 1,
                    OutletId = 3,
                    TerminalId = 25,
                    CashierId = "cashier2",
                    Subtotal = 1250m,
                    TotalAmount = 1250m,
                    PaymentStatus = "PAID",
                    OrderStatus = "COMPLETED",
                    InvoiceNumber = "INV-000555",
                    CreatedAt = DateTime.UtcNow
                });
                db.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "Order",
                    EntityId = "OFFLINE-UNINSTALL-TX-99",
                    PayloadJson = "{\"ClientTransactionId\":\"OFFLINE-UNINSTALL-TX-99\"}",
                    Status = "PENDING",
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // 2. Create encrypted survivable backup in SharedDocuments_AgresPOS_Backups
            var encPath = LocalDatabaseBackupService.CreateEncryptedSurvivableBackup(dbPath);
            Assert(!string.IsNullOrWhiteSpace(encPath) && File.Exists(encPath), "Encrypted survivable backup (.agresbak) must be created.");
            Assert(encPath!.StartsWith(sharedDocsDir, StringComparison.OrdinalIgnoreCase), "Survivable backup must reside in shared storage directory.");

            // 3. Simulate accidental app uninstall on Android tablet: wipe AppInternal directory completely!
            SqliteConnection.ClearAllPools();
            Directory.Delete(appInternalDir, recursive: true);
            Directory.CreateDirectory(appInternalDir);
            LocalPosDbContext.ResetInitializationState(dbPath);
            Assert(!File.Exists(dbPath), "Internal local_pos.db must be gone after simulated uninstall.");

            // 4. Verify tampered backup file is rejected by AES-GCM authentication tag
            var tamperedPath = Path.Combine(sharedDocsDir, "tampered.agresbak");
            var rawBytes = File.ReadAllBytes(encPath);
            rawBytes[^1] ^= 0xFF;
            File.WriteAllBytes(tamperedPath, rawBytes);
            var tamperedRes = LocalDatabaseBackupService.RestoreEncryptedSurvivableBackup(tamperedPath, dbPath);
            Assert(!tamperedRes.Success, "Tampered encrypted backup must be rejected by AES-GCM tag verification.");

            // 5. Restore from authentic survivable backup after reinstall
            var restoreRes = LocalDatabaseBackupService.RestoreEncryptedSurvivableBackup(encPath, dbPath);
            Assert(restoreRes.Success, $"Restore should succeed, got: {restoreRes.Message}");
            Assert(restoreRes.PendingOutboxCount == 1, $"Expected 1 pending outbox item restored, got {restoreRes.PendingOutboxCount}");

            using (var restoredDb = new LocalPosDbContext())
            {
                restoredDb.InitializeDatabase();
                var cfg = await restoredDb.TerminalConfigs.AsNoTracking().SingleAsync(t => t.TerminalId == 25);
                Assert(cfg.DeviceUuid == "survivable-device-uuid-25" && cfg.IsActivated, "Original DeviceUuid and activation state must be restored.");

                var order = await restoredDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == "OFFLINE-UNINSTALL-TX-99");
                Assert(order.TotalAmount == 1250m && order.InvoiceNumber == "INV-000555", "Unsynced offline order must be restored after accidental uninstall.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            AppContext.SetData("POS.SurvivableBackupDirectory", prevSurvivableDir);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static Task TestUpdate04_SignedReleaseManifestVerificationAndAbiSafeDeviceBinding()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var apiDb = new PosDbContext(options);

        var controller = new AppReleasesController(apiDb);
        var actionRes = controller.GetLatestAndroidRelease();
        var okRes = actionRes.Result as Microsoft.AspNetCore.Mvc.OkObjectResult;
        var manifest = okRes?.Value as AppReleaseManifestDto;
        Assert(manifest != null, "AppReleasesController must return signed AppReleaseManifestDto.");

        // 1. Valid manifest for upgrade from versionCode 2 -> 3
        var (isValid, err) = AppUpdateService.ValidateReleaseManifest(manifest, currentVersionCode: 2);
        Assert(isValid, $"Expected valid signed manifest, got error: {err}");

        // 2. Downgrade / same-version protection
        var (sameVerValid, _) = AppUpdateService.ValidateReleaseManifest(manifest, currentVersionCode: 3);
        Assert(!sameVerValid, "ValidateReleaseManifest must block installing same or older versionCode.");

        // 3. Tampered download URL or SHA-256 rejected by ECDSA signature check
        var tamperedUrlManifest = new AppReleaseManifestDto
        {
            PackageId = manifest!.PackageId,
            VersionCode = manifest.VersionCode,
            VersionName = manifest.VersionName,
            Sha256 = manifest.Sha256,
            DownloadUrl = "https://evil.example.com/malicious.apk",
            MinRequiredSchemaVersion = manifest.MinRequiredSchemaVersion,
            SigningCertSha256 = manifest.SigningCertSha256,
            EnvironmentType = manifest.EnvironmentType,
            PublishedAtUtc = manifest.PublishedAtUtc,
            Signature = manifest.Signature
        };
        var (tamperedValid, _) = AppUpdateService.ValidateReleaseManifest(tamperedUrlManifest, currentVersionCode: 1);
        Assert(!tamperedValid, "Tampered download URL must be rejected by ECDSA P-256 signature verification.");

        // 4. VerifyDownloadedApkFile SHA-256 verification
        var tempApk = Path.Combine(Path.GetTempPath(), $"test_apk_{Guid.NewGuid():N}.apk");
        try
        {
            byte[] dummyApkBytes = Encoding.UTF8.GetBytes("DUMMY-APK-BINARY-PAYLOAD-FOR-HASH-TEST");
            File.WriteAllBytes(tempApk, dummyApkBytes);
            string actualSha = Convert.ToHexString(SHA256.HashData(dummyApkBytes));

            var wrongHashCheck = AppUpdateService.VerifyDownloadedApkFile(tempApk, manifest);
            Assert(!wrongHashCheck.IsValid, "APK with mismatched SHA-256 must be rejected.");

            manifest.Sha256 = actualSha;
            var matchingHashCheck = AppUpdateService.VerifyDownloadedApkFile(tempApk, manifest);
            Assert(matchingHashCheck.IsValid, $"APK with matching SHA-256 should pass file digest check: {matchingHashCheck.ErrorMessage}");
        }
        finally
        {
            try { File.Delete(tempApk); } catch { }
        }

        // 5. Verify ABI-safe DeviceBindingHelper.ResolveMatchingDeviceId across 32-bit Arm -> 64-bit Arm64 upgrade on same host
        var prevProvider = DeviceBindingHelper.HardwareFingerprintProvider;
        try
        {
            DeviceBindingHelper.HardwareFingerprintProvider = null;
            string testUuid = "abi-upgrade-device-uuid-01";
            string hostName = Environment.MachineName.ToUpperInvariant();
            int procCount = Environment.ProcessorCount;
            string arm32Fp = $"HOST|{hostName}|{Architecture.Arm}|{procCount}";
            string arm32DeviceId = DeviceBindingHelper.ComputeDeviceId(testUuid, arm32Fp);

            string resolved = DeviceBindingHelper.ResolveMatchingDeviceId(testUuid, arm32DeviceId);
            Assert(resolved == arm32DeviceId, "ResolveMatchingDeviceId must preserve licensed DeviceId across Arm -> Arm64/X64 ABI transitions on the same host.");

            string foreignMachineId = DeviceBindingHelper.ComputeDeviceId(testUuid, "HOST|FOREIGN-TABLET|Arm64|8");
            string foreignResolved = DeviceBindingHelper.ResolveMatchingDeviceId(testUuid, foreignMachineId);
            Assert(foreignResolved != foreignMachineId, "ResolveMatchingDeviceId must NOT match a different host's DeviceId.");
        }
        finally
        {
            DeviceBindingHelper.HardwareFingerprintProvider = prevProvider;
        }

        return Task.CompletedTask;
    }

    private static async Task TestUpdate05_PaymentConditionalCashTenderedAndSplitNonCashIsolation()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd05_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);
            ClientLicenseService.StrictEnforcementMode = false;

            using (var db = new LocalPosDbContext())
            {
                db.InitializeDatabase();
                db.Branches.Add(new LocalBranch
                {
                    BranchId = 1,
                    Name = "Main Branch",
                    Code = "BR-01",
                    TaxType = "VAT-Registered",
                    VatDisplay = true,
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow
                });
                db.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 1,
                    OutletId = 1,
                    BranchId = 1,
                    TerminalCode = "T-01",
                    TerminalName = "POS 01",
                    OutletName = "Main Outlet",
                    BranchName = "Main Branch",
                    BranchCode = "BR-01",
                    BranchTaxType = "VAT-Registered",
                    DeviceUuid = "pay-test-uuid-01",
                    TerminalIdentifier = "pay-test-uuid-01",
                    IsActivated = true,
                    IsInitialized = true
                });
                db.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 101,
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 1000m,
                    ExpectedBalance = 1000m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = DateTime.UtcNow
                });
                db.InvoiceSequences.Add(new LocalInvoiceSequence
                {
                    SequenceId = 1,
                    TerminalId = 1,
                    SeriesCode = "S1",
                    Prefix = "INV-",
                    StartingNumber = 1,
                    EndingNumber = 10000,
                    LastIssuedNumber = 0,
                    PaddingLength = 6,
                    IsActive = true
                });
                db.Products.Add(new LocalProduct
                {
                    ProductId = 1,
                    Name = "Family Platter",
                    Price = 500m,
                    IsActive = true,
                    CategoryId = 1
                });
                await db.SaveChangesAsync();
            }

            var session = new PosSessionService();
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                TerminalCode = "T-01",
                TerminalName = "POS 01",
                OutletName = "Main Outlet",
                BranchName = "Main Branch",
                BranchCode = "BR-01"
            });

            var checkout = new OfflineCheckoutService(new LocalPosDbContext(), session);

            OrderCreateDto MakeOrder(List<PaymentSplitDto>? splits = null) => new()
            {
                ShiftId = 101,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                Splits = splits,
                Items = new()
                {
                    new OrderItemCreateDto
                    {
                        ProductId = 1,
                        ProductName = "Family Platter",
                        Quantity = 1,
                        BasePrice = 500m,
                        TaxName = "VATable"
                    }
                }
            };

            // 1. Cash Payment: Total ₱500.00, Cash Tendered ₱1,000.00 -> Change ₱500.00
            var cashRes = await checkout.ProcessSaleAsync(MakeOrder(), 1000m, "CASH", null, "cashier1");
            Assert(cashRes.Success, $"Cash sale should succeed: {cashRes.ErrorMessage}");
            Assert(cashRes.ChangeAmount == 500m, $"Expected ₱500.00 change for cash sale, got {cashRes.ChangeAmount}");
            Assert(cashRes.Receipt!.Payments.Single().AmountTendered == 1000m && cashRes.Receipt.Payments.Single().ChangeAmount == 500m,
                "Cash receipt payment must record AmountTendered=1000 and ChangeAmount=500.");

            // 2. Non-Cash Payments (CARD, GCASH, OTHER_BANK): do not require cash tendered validation (0m passed) and record 0 change
            foreach (var (code, refNo) in new[] { ("CARD", "APR-1001"), ("GCASH", "GC-2002"), ("OTHER_BANK", "BNK-3003") })
            {
                var nonCashRes = await checkout.ProcessSaleAsync(MakeOrder(), 0m, code, refNo, "cashier1");
                Assert(nonCashRes.Success, $"{code} sale without cash tendered must succeed: {nonCashRes.ErrorMessage}");
                Assert(nonCashRes.ChangeAmount == 0m, $"{code} sale must have 0 change, got {nonCashRes.ChangeAmount}");
                var p = nonCashRes.Receipt!.Payments.Single();
                Assert(p.Amount == 500m && p.AmountTendered == 500m && p.ChangeAmount == 0m && p.ReferenceNumber == refNo,
                    $"{code} payment must record exact charge (500), 0 change, and preserve ReferenceNumber.");
            }

            // 3. Mixed Split Payment: Cash component has Cash Tendered & Change; Card/GCash/Bank components ignore stale AmountTendered
            var mixedSplits = new List<PaymentSplitDto>
            {
                new() { PaymentMethod = "CASH", Amount = 200m, AmountTendered = 500m }, // Change = ₱300.00
                new() { PaymentMethod = "CARD", Amount = 150m, AmountTendered = 999m, ReferenceNumber = "CARD-REF-1" },
                new() { PaymentMethod = "GCASH", Amount = 150m, AmountTendered = 0m, ReferenceNumber = "GCASH-REF-2" }
            };
            var splitRes = await checkout.ProcessSaleAsync(MakeOrder(mixedSplits), 0m, "CASH", null, "cashier1");
            Assert(splitRes.Success, $"Mixed split payment should succeed: {splitRes.ErrorMessage}");
            Assert(splitRes.ChangeAmount == 300m, $"Mixed split change must come strictly from CASH component (₱300.00), got {splitRes.ChangeAmount}");
            Assert(splitRes.CashAmountPaid == 200m, $"CashAmountPaid for drawer balance must be ₱200.00, got {splitRes.CashAmountPaid}");

            var cashSplitPay = splitRes.Receipt!.Payments.Single(p => p.PaymentMethod == "CASH");
            Assert(cashSplitPay.Amount == 200m && cashSplitPay.AmountTendered == 500m && cashSplitPay.ChangeAmount == 300m,
                "Cash split component must preserve AmountTendered=500 and ChangeAmount=300.");

            var cardSplitPay = splitRes.Receipt.Payments.Single(p => p.PaymentMethod == "CARD");
            Assert(cardSplitPay.Amount == 150m && cardSplitPay.AmountTendered == 150m && cardSplitPay.ChangeAmount == 0m,
                "Card split component must strip stale AmountTendered and record exact Amount=150, Change=0.");

            var gcashSplitPay = splitRes.Receipt.Payments.Single(p => p.PaymentMethod == "GCASH");
            Assert(gcashSplitPay.Amount == 150m && gcashSplitPay.AmountTendered == 150m && gcashSplitPay.ChangeAmount == 0m,
                "GCash split component must not require cash tendered and record exact Amount=150, Change=0.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class ToggleableConnectivityService : IPosConnectivityService
    {
        private TerminalConnectivityState _state = TerminalConnectivityState.OnlineAndHealthy;
        public TerminalConnectivityState CurrentState => _state;
        public event EventHandler<TerminalConnectivityState>? ConnectivityChanged;

        public void SetOnline(bool online)
        {
            _state = online
                ? TerminalConnectivityState.OnlineAndHealthy
                : TerminalConnectivityState.InternetConnectedApiUnavailable;
            ConnectivityChanged?.Invoke(this, _state);
        }

        public Task<TerminalConnectivityState> CheckConnectivityAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);
    }

    private static async Task TestUpdate06_Bug1_OfflineTransactionsRemainVisibleOnReconnectAndSync()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd06_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);
            ClientLicenseService.StrictEnforcementMode = false;

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Ensure terminal has UUID for session-token issuance, products, and active invoice sequence
            var serverTerm = await apiDb.Terminals.FindAsync(1);
            serverTerm!.Uuid = "term-uuid-bug1-01";
            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals" });
            apiDb.Products.Add(new Product { ProductId = 1, CategoryId = 1, Name = "Chicken Meal", ProductType = "Standard", Price = 750m, IsActive = true });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                InvoiceSequenceId = 10,
                TerminalId = 1,
                SeriesCode = "S1",
                Prefix = "INV-",
                StartingNumber = 1,
                EndingNumber = 10000,
                LastIssuedNumber = 1,
                PaddingLength = 6,
                IsActive = true,
                UpdatedAt = DateTime.UtcNow
            });
            // Seed an online order (INV-000001) on server for cashier1
            var nowLocal = POS.Shared.Services.BusinessDayClock.DeviceNow();
            apiDb.Orders.Add(new Order
            {
                OrderId = 501,
                OrderNumber = "POS01-000001",
                InvoiceNumber = "INV-000001",
                InvoiceIssuedAt = nowLocal,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                Subtotal = 300m,
                TotalAmount = 300m,
                VatableSales = 267.86m,
                VatAmount = 32.14m,
                CreatedAt = nowLocal
            });
            await apiDb.SaveChangesAsync();

            // Seed local SQLite database for Terminal 1
            using (var localDb = new LocalPosDbContext())
            {
                localDb.InitializeDatabase();
                localDb.Branches.Add(new LocalBranch
                {
                    BranchId = 1,
                    Name = "Main Branch",
                    Code = "BR-01",
                    TaxType = "VAT-Registered",
                    VatDisplay = true,
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow
                });
                localDb.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 1,
                    OutletId = 1,
                    BranchId = 1,
                    TerminalCode = "T-01",
                    TerminalName = "POS 01",
                    OutletName = "Main Outlet",
                    BranchName = "Main Branch",
                    BranchCode = "BR-01",
                    BranchTaxType = "VAT-Registered",
                    DeviceUuid = "term-uuid-bug1-01",
                    TerminalIdentifier = "term-uuid-bug1-01",
                    IsActivated = true,
                    IsInitialized = true
                });
                localDb.Cashiers.Add(new LocalCashier
                {
                    UserId = 1,
                    Username = "cashier1",
                    Role = "Cashier",
                    PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1234")))
                });
                localDb.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 1,
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 2000m,
                    ExpectedBalance = 2300m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = DateTime.UtcNow
                });
                localDb.InvoiceSequences.Add(new LocalInvoiceSequence
                {
                    SequenceId = 10,
                    TerminalId = 1,
                    SeriesCode = "S1",
                    Prefix = "INV-",
                    StartingNumber = 1,
                    EndingNumber = 10000,
                    LastIssuedNumber = 1,
                    PaddingLength = 6,
                    IsActive = true
                });
                localDb.OrderSequences.Add(new LocalOrderSequence
                {
                    TerminalId = 1,
                    LastIssuedNumber = 1,
                    UpdatedAt = DateTime.UtcNow
                });
                localDb.Products.Add(new LocalProduct
                {
                    ProductId = 1,
                    Name = "Chicken Meal",
                    Price = 750m,
                    IsActive = true,
                    CategoryId = 1
                });
                await localDb.SaveChangesAsync();
            }

            var session = new PosSessionService();
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                TerminalCode = "T-01",
                TerminalName = "POS 01",
                OutletName = "Main Outlet",
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                Uuid = "term-uuid-bug1-01"
            });

            // Step 3 & 4: Disconnect internet and process a sale offline
            var checkout = new OfflineCheckoutService(new LocalPosDbContext(), session);
            var offlineSaleRes = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                ShiftId = 1,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                Items = new()
                {
                    new OrderItemCreateDto
                    {
                        ProductId = 1,
                        ProductName = "Chicken Meal",
                        Quantity = 1,
                        BasePrice = 750m,
                        TaxName = "VATable"
                    }
                }
            }, 1000m, "CASH", null, "cashier1");

            Assert(offlineSaleRes.Success, $"Offline sale should succeed: {offlineSaleRes.ErrorMessage}");
            Assert(offlineSaleRes.InvoiceNumber == "INV-000002", $"Expected INV-000002, got {offlineSaleRes.InvoiceNumber}");

            // Step 5: Simulate app restart while offline and verify offline transaction remains in SQLite with cashier1 identity
            LocalPosDbContext.ResetInitializationState(dbPath);
            using (var restartDb = new LocalPosDbContext())
            {
                restartDb.InitializeDatabase();
                var localOrders = await restartDb.Orders.AsNoTracking().Where(o => o.TerminalId == 1 && o.CashierId == "cashier1").ToListAsync();
                Assert(localOrders.Count == 1 && localOrders[0].InvoiceNumber == "INV-000002",
                    "Offline transaction must persist across app restart in LocalOrders with CashierId='cashier1'.");
                var outbox = await restartDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityId == offlineSaleRes.LocalTransactionId);
                Assert(outbox.Status == "PENDING", "Outbox item must remain PENDING while offline.");
            }

            // Step 6: Reconnect internet -> Verify TerminalSessionToken carries cashier1 identity (NOT TerminalCode 'T-01')
            var inMemoryConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "SuperSecretTestKeyForJwtSigning1234567890!",
                    ["Jwt:Issuer"] = "POS.Api",
                    ["Jwt:Audience"] = "POS.Client"
                })
                .Build();
            var termsCtrl = new TerminalsController(apiDb, inMemoryConfig);
            var tokenRes = await termsCtrl.IssueSessionToken(new TerminalSessionTokenRequestDto
            {
                TerminalId = 1,
                DeviceUuid = "term-uuid-bug1-01",
                CashierUsername = "cashier1"
            });
            var tokenOk = tokenRes.Result as Microsoft.AspNetCore.Mvc.OkObjectResult;
            var tokenDto = tokenOk?.Value as TerminalSessionTokenResponseDto;
            Assert(tokenDto != null && tokenDto.Success && !string.IsNullOrWhiteSpace(tokenDto.Token), "IssueSessionToken must return a valid JWT.");

            var jwtHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var parsedJwt = jwtHandler.ReadJwtToken(tokenDto!.Token);
            var nameClaim = parsedJwt.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Name)?.Value;
            var cashierClaim = parsedJwt.Claims.FirstOrDefault(c => c.Type == "cashier_username")?.Value;
            Assert(nameClaim == "cashier1" && cashierClaim == "cashier1",
                $"Session token must preserve cashier identity 'cashier1' instead of TerminalCode, got Name='{nameClaim}', cashier_username='{cashierClaim}'.");

            // Step 7: Upload offline transaction via OfflineSyncController (and test duplicate upload idempotency)
            using var http = CreateInMemoryOfflineSyncHttpClient(apiDb, "cashier1", "Terminal", 1, 1);
            var syncSvc = new OfflineTransactionSyncService(new LocalPosDbContext());
            var pendingList = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pendingList.Count == 1, "Expected 1 pending outbox item before sync.");

            bool syncOk = await syncSvc.UploadTransactionAsync(pendingList[0], http);
            Assert(syncOk, "Offline transaction upload must succeed on reconnect.");

            // Duplicate replay attempt must be idempotent and never create a second Order in PostgreSQL/server DB
            bool replayOk = await syncSvc.UploadTransactionAsync(pendingList[0], http);
            Assert(replayOk, "Idempotent replay of already-synced offline transaction must succeed.");

            int serverOrderCountForInv2 = await apiDb.Orders.CountAsync(o => o.InvoiceNumber == "INV-000002");
            Assert(serverOrderCountForInv2 == 1, $"Offline transaction INV-000002 must exist exactly once on server, found {serverOrderCountForInv2}.");

            // Verify local SQLite record is NOT deleted after synchronization
            using (var postSyncLocalDb = new LocalPosDbContext())
            {
                var stillInLocal = await postSyncLocalDb.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.InvoiceNumber == "INV-000002");
                Assert(stillInLocal != null && stillInLocal.CashierId == "cashier1",
                    "Local SQLite transaction must NEVER be deleted after synchronization.");
            }

            // Step 8: Query TransactionsController using the reconnected Cashier session token -> MUST be Forbidden (RBAC)!
            var txCtrl = new TransactionsController(apiDb, null!)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                {
                    HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                    {
                        User = new System.Security.Claims.ClaimsPrincipal(
                            new System.Security.Claims.ClaimsIdentity(parsedJwt.Claims, "Bearer"))
                    }
                }
            };

            var cashierTxRes = await txCtrl.GetTransactions(
                dateFrom: POS.Shared.Services.BusinessDayClock.GetCurrentBusinessDayStartLocal(),
                dateTo: null, branchId: 1, terminalId: 1, cashierId: null,
                orderNumber: null, invoiceNumber: null, paymentMethod: null,
                orderStatus: null, paymentStatus: null, discountCode: null);
            Assert(cashierTxRes.Result is Microsoft.AspNetCore.Mvc.ForbidResult,
                "Cashier reconnected session token must be forbidden from viewing Transaction History.");

            // Step 9: Authorized Manager/Admin querying TransactionsController sees BOTH online INV-000001 and synced offline INV-000002 with CashierId='cashier1'!
            var managerClaims = new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "manager1"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Manager"),
                new System.Security.Claims.Claim("branch_id", "1"),
                new System.Security.Claims.Claim("terminal_id", "1")
            };
            txCtrl.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(managerClaims, "Bearer"));

            var txActionRes = await txCtrl.GetTransactions(
                dateFrom: POS.Shared.Services.BusinessDayClock.GetCurrentBusinessDayStartLocal(),
                dateTo: null, branchId: 1, terminalId: 1, cashierId: null,
                orderNumber: null, invoiceNumber: null, paymentMethod: null,
                orderStatus: null, paymentStatus: null, discountCode: null);
            var txOk = txActionRes.Result as Microsoft.AspNetCore.Mvc.OkObjectResult;
            var onlineOrders = (txOk?.Value as System.Collections.Generic.IEnumerable<OrderResponseDto>)?.ToList() ?? new();

            Assert(onlineOrders.Any(o => o.InvoiceNumber == "INV-000001"), "Online sale INV-000001 must be visible to Manager.");
            Assert(onlineOrders.Any(o => o.InvoiceNumber == "INV-000002" && o.CashierId == "cashier1" && o.TotalAmount == 750m),
                "Synced offline sale INV-000002 must remain visible in online Transaction History for authorized roles after reconnecting!");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestUpdate07_Bug2_HeldSalesOfflinePersistenceRestartResumeAndCheckoutSync()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_upd07_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);
            ClientLicenseService.StrictEnforcementMode = false;

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals" });
            apiDb.Products.AddRange(
                new Product { ProductId = 1, CategoryId = 1, Name = "Lechon Belly", ProductType = "Standard", Price = 800m, PricingMethod = "WeightBased", UnitOfMeasure = "kg", IsActive = true },
                new Product { ProductId = 2, CategoryId = 1, Name = "Iced Tea Pitcher", ProductType = "Standard", Price = 150m, IsActive = true });
            apiDb.ModifierGroups.Add(new ModifierGroup { ModifierGroupId = 1, Name = "Add-ons", IsActive = true });
            apiDb.ModifierOptions.Add(new ModifierOption { ModifierOptionId = 10, ModifierGroupId = 1, Name = "Extra Lemon", Price = 20m, IsActive = true });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                InvoiceSequenceId = 20,
                TerminalId = 1,
                SeriesCode = "S1",
                Prefix = "INV-",
                StartingNumber = 1,
                EndingNumber = 10000,
                LastIssuedNumber = 0,
                PaddingLength = 6,
                IsActive = true,
                UpdatedAt = DateTime.UtcNow
            });
            await apiDb.SaveChangesAsync();

            using (var localDb = new LocalPosDbContext())
            {
                localDb.InitializeDatabase();
                localDb.Branches.Add(new LocalBranch
                {
                    BranchId = 1,
                    Name = "Main Branch",
                    Code = "BR-01",
                    TaxType = "VAT-Registered",
                    VatDisplay = true,
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow
                });
                localDb.TerminalConfigs.Add(new LocalTerminalConfig
                {
                    TerminalId = 1,
                    OutletId = 1,
                    BranchId = 1,
                    TerminalCode = "T-01",
                    TerminalName = "POS 01",
                    OutletName = "Main Outlet",
                    BranchName = "Main Branch",
                    BranchCode = "BR-01",
                    BranchTaxType = "VAT-Registered",
                    DeviceUuid = "term-uuid-bug2-01",
                    TerminalIdentifier = "term-uuid-bug2-01",
                    IsActivated = true,
                    IsInitialized = true
                });
                localDb.CashDrawerShifts.Add(new LocalCashDrawerShift
                {
                    ShiftId = 1,
                    BranchId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    OpeningFloat = 2000m,
                    ExpectedBalance = 2000m,
                    OpeningConfirmed = true,
                    Status = "OPEN",
                    OpenedAt = DateTime.UtcNow
                });
                localDb.InvoiceSequences.Add(new LocalInvoiceSequence
                {
                    SequenceId = 20,
                    TerminalId = 1,
                    SeriesCode = "S1",
                    Prefix = "INV-",
                    StartingNumber = 1,
                    EndingNumber = 10000,
                    LastIssuedNumber = 0,
                    PaddingLength = 6,
                    IsActive = true
                });
                localDb.Products.Add(new LocalProduct
                {
                    ProductId = 1,
                    Name = "Lechon Belly",
                    Price = 800m,
                    PricingMethod = "WeightBased",
                    UnitOfMeasure = "kg",
                    IsKitchenItem = true,
                    IsActive = true,
                    CategoryId = 1
                });
                localDb.Products.Add(new LocalProduct
                {
                    ProductId = 2,
                    Name = "Iced Tea Pitcher",
                    Price = 150m,
                    PricingMethod = "Fixed",
                    IsBarItem = true,
                    IsActive = true,
                    CategoryId = 1
                });
                await localDb.SaveChangesAsync();
            }

            var session = new PosSessionService();
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                TerminalCode = "T-01",
                TerminalName = "POS 01",
                OutletName = "Main Outlet",
                BranchName = "Main Branch",
                BranchCode = "BR-01"
            });

            var connectivity = new ToggleableConnectivityService();
            using var heldHttp = CreateInMemoryHeldSalesHttpClient(apiDb, "cashier1", "Cashier", 1, 1);
            var heldService = new ClientHeldSaleService(heldHttp, connectivity, session);

            // Step 1 & 2: Create a Held Sale ONLINE and verify it appears in Held Sales and is cached in SQLite
            connectivity.SetOnline(true);
            var onlineHeld = await heldService.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                CustomerName = "Table 5 Online",
                Notes = "Waiting for guest",
                Subtotal = 150m,
                TotalAmount = 150m,
                Items = new()
                {
                    new HeldSaleItemDto
                    {
                        ProductId = 2,
                        ProductName = "Iced Tea Pitcher",
                        Quantity = 1,
                        BasePrice = 150m,
                        LineTotal = 150m,
                        TaxName = "VATable"
                    }
                }
            });
            Assert(onlineHeld != null && onlineHeld.HeldSaleId > 0, "Online Held Sale must be created with positive server HeldSaleId.");

            // Step 3, 4 & 5: Disconnect internet -> Create another Held Sale OFFLINE (with weight-based item, modifiers, notes, discount)
            connectivity.SetOnline(false);
            var offlineHeld = await heldService.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                CustomerName = "Takeout Offline",
                Notes = "Extra crispy",
                Subtotal = 1170m,
                DiscountAmount = 70m,
                DiscountCode = "PROMO70",
                TotalAmount = 1100m,
                Items = new()
                {
                    new HeldSaleItemDto
                    {
                        ProductId = 1,
                        ProductName = "Lechon Belly",
                        Quantity = 1.25m,
                        BasePrice = 800m,
                        LineTotal = 1000m,
                        PricingMethod = "WeightBased",
                        UnitOfMeasure = "kg",
                        TargetAmount = 1000m,
                        IsKitchenItem = true,
                        SpecialInstructions = "Chop small",
                        TaxName = "VATable"
                    },
                    new HeldSaleItemDto
                    {
                        ProductId = 2,
                        ProductName = "Iced Tea Pitcher",
                        Quantity = 1,
                        BasePrice = 150m,
                        LineTotal = 170m,
                        IsBarItem = true,
                        SpecialInstructions = "Less ice",
                        TaxName = "VATable",
                        Modifiers = new()
                        {
                            new HeldSaleModifierDto
                            {
                                ModifierOptionId = 10,
                                ModifierName = "Extra Lemon",
                                AdditionalPrice = 20m
                            }
                        }
                    }
                }
            });
            Assert(offlineHeld != null && offlineHeld.HeldSaleId < 0,
                $"Offline Held Sale must be recorded in SQLite with negative HeldSaleId, got {offlineHeld?.HeldSaleId}");

            // Step 6 & 7: Restart POS.Client while OFFLINE and verify BOTH Held Sales remain accessible!
            LocalPosDbContext.ResetInitializationState(dbPath);
            var restartedHeldService = new ClientHeldSaleService(heldHttp, connectivity, session);
            var allHeldWhileOffline = await restartedHeldService.GetHeldSalesAsync(1, 1, 1);
            Assert(allHeldWhileOffline.Count == 2,
                $"Both online-cached and offline-created Held Sales must be accessible while offline after restart, found {allHeldWhileOffline.Count}.");
            Assert(allHeldWhileOffline.Any(h => h.HoldReference == onlineHeld!.HoldReference), "Online-created Held Sale must remain accessible offline.");
            Assert(allHeldWhileOffline.Any(h => h.HoldReference == offlineHeld!.HoldReference), "Offline-created Held Sale must remain accessible offline.");

            // Step 8: Resume the offline Held Sale while offline and verify CartService restores all state accurately
            var resumedDto = await restartedHeldService.ResumeHeldSaleAsync(offlineHeld!.HeldSaleId, 1);
            Assert(resumedDto != null && resumedDto.Status == "RESUMED", "Offline Held Sale must transition to RESUMED.");

            var cart = new CartService(session);
            cart.RestoreHeldSale(resumedDto!);
            Assert(cart.Items.Count == 2, $"Expected 2 restored cart items, got {cart.Items.Count}");
            Assert(cart.Items[0].IsWeightBased && cart.Items[0].Quantity == 1.25m && cart.Items[0].SpecialInstructions == "Chop small",
                "Weight-based fractional quantity (1.25 kg) and special instructions must be preserved on resume.");
            Assert(cart.Items[1].SelectedModifiers.Count == 1 && cart.Items[1].SelectedModifiers[0].Price == 20m && cart.Items[1].SpecialInstructions == "Less ice",
                "Modifiers and special instructions must be preserved on resume.");
            Assert(cart.ActiveDiscountCode == "PROMO70" && cart.ActiveDiscountAmount == 70m,
                "Discount code and amount must be preserved on resume.");

            // Step 9: Complete checkout of resumed sale while offline
            var checkout = new OfflineCheckoutService(new LocalPosDbContext(), session);
            var saleRes = await checkout.ProcessSaleAsync(new OrderCreateDto
            {
                ShiftId = 1,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "TAKEOUT",
                DiscountCode = cart.ActiveDiscountCode,
                DiscountAmount = cart.ActiveDiscountAmount,
                Items = cart.Items.Select(ci => new OrderItemCreateDto
                {
                    ProductId = ci.ProductId,
                    ProductName = ci.ProductName,
                    Quantity = ci.Quantity,
                    BasePrice = ci.BasePrice,
                    PricingMethod = ci.PricingMethod,
                    UnitOfMeasure = ci.UnitOfMeasure,
                    TargetAmount = ci.TargetAmount,
                    SpecialInstructions = ci.SpecialInstructions,
                    TaxName = ci.TaxName,
                    Modifiers = ci.SelectedModifiers.Select(m => new OrderItemModifierCreateDto
                    {
                        ModifierOptionId = m.ModifierOptionId,
                        ModifierName = m.Name,
                        AdditionalPrice = m.Price
                    }).ToList()
                }).ToList()
            }, 1200m, "CASH", null, "cashier1");
            Assert(saleRes.Success, $"Checkout of resumed offline Held Sale must succeed: {saleRes.ErrorMessage}");

            // Step 10: Reconnect internet -> Verify completed transaction syncs exactly once and remaining Held Sale is not duplicated
            connectivity.SetOnline(true);
            using var syncHttp = CreateInMemoryOfflineSyncHttpClient(apiDb, "cashier1", "Cashier", 1, 1);
            var syncSvc = new OfflineTransactionSyncService(new LocalPosDbContext());
            var pendingOutbox = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pendingOutbox.Count == 1, "Expected 1 pending outbox item from resumed held sale checkout.");
            Assert(await syncSvc.UploadTransactionAsync(pendingOutbox[0], syncHttp), "Resumed held sale transaction must sync cleanly on reconnect.");
            Assert(await apiDb.Orders.CountAsync(o => o.InvoiceNumber == saleRes.InvoiceNumber) == 1,
                "Synced transaction from resumed held sale must exist exactly once in PostgreSQL/server DB.");

            var remainingHeldAfterReconnect = await restartedHeldService.GetHeldSalesAsync(1, 1, 1);
            Assert(remainingHeldAfterReconnect.Count == 1 && remainingHeldAfterReconnect[0].HoldReference == onlineHeld.HoldReference,
                $"Remaining active Held Sale must appear exactly once without duplication after reconnect, found {remainingHeldAfterReconnect.Count}.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static HttpClient CreateInMemoryOfflineSyncHttpClient(
        PosDbContext apiDb,
        string username,
        string role,
        int branchId,
        int terminalId)
    {
        var handler = new InMemoryControllerHttpMessageHandler(async req =>
        {
            var ctrl = new OfflineSyncController(apiDb);
            var claims = new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
                new System.Security.Claims.Claim("cashier_username", username),
                new System.Security.Claims.Claim("branch_id", branchId.ToString()),
                new System.Security.Claims.Claim("terminal_id", terminalId.ToString())
            };
            ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity(claims, "Bearer"))
                }
            };

            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath.EndsWith("/api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase) == true)
            {
                var body = await req.Content!.ReadAsStringAsync();
                var dto = System.Text.Json.JsonSerializer.Deserialize<OfflineTransactionSyncRequest>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var actionRes = await ctrl.SyncTransaction(dto);
                return ConvertActionResultToHttpResponse(actionRes.Result ?? new Microsoft.AspNetCore.Mvc.OkObjectResult(actionRes.Value));
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    }

    private static HttpClient CreateInMemoryHeldSalesHttpClient(
        PosDbContext apiDb,
        string username,
        string role,
        int branchId,
        int terminalId)
    {
        var handler = new InMemoryControllerHttpMessageHandler(async req =>
        {
            var ctrl = new HeldSalesController(apiDb);
            var claims = new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
                new System.Security.Claims.Claim("branch_id", branchId.ToString()),
                new System.Security.Claims.Claim("terminal_id", terminalId.ToString())
            };
            ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity(claims, "Bearer"))
                }
            };

            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (req.Method == HttpMethod.Get && path.Equals("/api/held-sales", StringComparison.OrdinalIgnoreCase))
            {
                var actionRes = await ctrl.GetHeldSales(branchId, terminalId, default, 1);
                return ConvertActionResultToHttpResponse(actionRes.Result!);
            }
            if (req.Method == HttpMethod.Post && path.Equals("/api/held-sales", StringComparison.OrdinalIgnoreCase))
            {
                var body = await req.Content!.ReadAsStringAsync();
                var dto = System.Text.Json.JsonSerializer.Deserialize<HeldSaleCreateDto>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var actionRes = await ctrl.CreateHeldSale(dto);
                return ConvertActionResultToHttpResponse(actionRes.Result!);
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/resume", StringComparison.OrdinalIgnoreCase))
            {
                var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                long id = long.Parse(segs[2]);
                var actionRes = await ctrl.ResumeHeldSale(id, terminalId);
                return ConvertActionResultToHttpResponse(actionRes.Result!);
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                long id = long.Parse(segs[2]);
                var actionRes = await ctrl.CancelHeldSale(id);
                return ConvertActionResultToHttpResponse(actionRes);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    }

    private static HttpResponseMessage ConvertActionResultToHttpResponse(Microsoft.AspNetCore.Mvc.IActionResult result)
    {
        if (result is Microsoft.AspNetCore.Mvc.OkObjectResult ok)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(ok.Value), Encoding.UTF8, "application/json")
            };
        }
        if (result is Microsoft.AspNetCore.Mvc.NoContentResult)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
        if (result is Microsoft.AspNetCore.Mvc.ConflictObjectResult conflict)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
            {
                Content = new StringContent(conflict.Value?.ToString() ?? "Conflict", Encoding.UTF8, "text/plain")
            };
        }
        if (result is Microsoft.AspNetCore.Mvc.BadRequestObjectResult bad)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent(bad.Value?.ToString() ?? "BadRequest", Encoding.UTF8, "text/plain")
            };
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
    }

    private sealed class InMemoryControllerHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            handler(request);
    }
}
