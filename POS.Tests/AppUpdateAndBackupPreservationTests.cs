using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        // 1. Valid manifest for upgrade from versionCode 1 -> 2
        var (isValid, err) = AppUpdateService.ValidateReleaseManifest(manifest, currentVersionCode: 1);
        Assert(isValid, $"Expected valid signed manifest, got error: {err}");

        // 2. Downgrade / same-version protection
        var (sameVerValid, _) = AppUpdateService.ValidateReleaseManifest(manifest, currentVersionCode: 2);
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
}
