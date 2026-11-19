using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace POS.Client.Data;

public static class LocalDatabaseMigrator
{
    public const int CurrentSchemaVersion = 2;

    public static void Migrate(DbConnection conn, string dbPath, Action ensureCreatedAction)
    {
        bool closeOnExit = false;
        if (conn.State != ConnectionState.Open)
        {
            conn.Open();
            closeOnExit = true;
        }

        try
        {
            ExecuteNonQuery(conn, null, "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=OFF;");

            int currentVersion = GetUserVersion(conn);
            if (currentVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Local database schema version ({currentVersion}) is newer than this application version supports ({CurrentSchemaVersion}). Please update POS.Client to the latest version.");
            }

            bool hadExistingTables = HasExistingUserTables(conn);
            string? preMigrationBackupPath = null;

            if (hadExistingTables && currentVersion < CurrentSchemaVersion)
            {
                preMigrationBackupPath = LocalDatabaseBackupService.CreateBackupSnapshot(
                    dbPath,
                    $"pre_mig_v{currentVersion}_to_v{CurrentSchemaVersion}");
            }

            try
            {
                LocalHierarchyUpgrade.Apply(conn);
                ensureCreatedAction();

                if (conn.State != ConnectionState.Open)
                {
                    conn.Open();
                }

                if (currentVersion < 1)
                {
                    MaybeSimulateFailure(1);
                    using var tx1 = conn.BeginTransaction();
                    ApplyMigrationV1(conn, tx1);
                    ExecuteNonQuery(conn, tx1, "PRAGMA user_version = 1;");
                    tx1.Commit();
                    currentVersion = 1;
                }

                if (currentVersion < 2)
                {
                    MaybeSimulateFailure(2);
                    using var tx2 = conn.BeginTransaction();
                    ApplyMigrationV2(conn, tx2);
                    ExecuteNonQuery(conn, tx2, "PRAGMA user_version = 2;");
                    tx2.Commit();
                    currentVersion = 2;
                }

                if (!VerifyQuickCheck(conn))
                {
                    throw new InvalidOperationException("Database failed PRAGMA quick_check after migration.");
                }

                ExecuteNonQuery(conn, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=OFF;");
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(preMigrationBackupPath))
                {
                    try
                    {
                        conn.Close();
                        LocalDatabaseBackupService.RestoreFromBackup(preMigrationBackupPath, dbPath);
                    }
                    catch
                    {
                        // Preserve original migration exception
                    }
                }
                throw;
            }
        }
        finally
        {
            if (closeOnExit && conn.State == ConnectionState.Open)
            {
                conn.Close();
            }
        }
    }

    public static int GetUserVersion(DbConnection conn)
    {
        bool close = false;
        if (conn.State != ConnectionState.Open)
        {
            conn.Open();
            close = true;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }
        finally
        {
            if (close && conn.State == ConnectionState.Open)
                conn.Close();
        }
    }

    private static void ApplyMigrationV1(DbConnection conn, DbTransaction tx)
    {
        if (TableExists(conn, tx, "LocalPayments"))
            ExecuteNonQuery(conn, tx, "UPDATE LocalPayments SET PaymentStatus = 'PAID' WHERE PaymentStatus = 'SUCCESS';");
        if (TableExists(conn, tx, "LocalOrders"))
            ExecuteNonQuery(conn, tx, "UPDATE LocalOrders SET PaymentStatus = 'PAID' WHERE PaymentStatus = 'SUCCESS';");

        AddColumnIfMissing(conn, tx, "LocalOrders", "OrderNumber", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "OutletId", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, tx, "LocalOrders", "ShiftId", "INTEGER NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "ClientShiftId", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "OrderType", "TEXT NOT NULL DEFAULT 'DINING'");
        AddColumnIfMissing(conn, tx, "LocalOrders", "TableNumber", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "HeadCount", "INTEGER NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "CustomerCardNumber", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "CustomerName", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "CustomerAddress", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "CustomerTin", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "CustomerBusinessStyle", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "DeviceTimeZoneId", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "DeviceUtcOffsetMinutes", "INTEGER NULL");
        AddColumnIfMissing(conn, tx, "LocalOrders", "ServerReceivedAtUtc", "TEXT NULL");

        AddColumnIfMissing(conn, tx, "LocalPayments", "ReferenceNumber", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalSyncOutboxes", "SyncedAt", "TEXT NULL");
        AddColumnIfMissing(conn, tx, "LocalProducts", "IsBarItem", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, tx, "LocalProducts", "PricingMethod", "TEXT DEFAULT 'Fixed'");
        AddColumnIfMissing(conn, tx, "LocalCategories", "DisplayOrder", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, tx, "LocalProducts", "DisplayOrder", "INTEGER NOT NULL DEFAULT 0");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalOrderSequences (
                TerminalId INTEGER PRIMARY KEY,
                LastIssuedNumber INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalBranches (
                BranchId INTEGER PRIMARY KEY,
                Name TEXT NOT NULL DEFAULT '',
                BranchTagline TEXT NULL,
                Code TEXT NOT NULL DEFAULT '',
                Address TEXT NULL,
                Phone TEXT NULL,
                TaxType TEXT NOT NULL DEFAULT 'VAT-Registered',
                VatDisplay INTEGER NOT NULL DEFAULT 1,
                TrackInventory INTEGER NOT NULL DEFAULT 0,
                BusinessTypeId INTEGER NULL DEFAULT 1,
                BusinessType TEXT NULL DEFAULT 'FNB',
                ServiceModeId INTEGER NULL,
                ReceiptLogoUrl TEXT NULL,
                ReceiptLogoDataUri TEXT NULL,
                ReceiptLogoUpdatedAt TEXT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                UpdatedAt TEXT NOT NULL
            );");

        AddColumnIfMissing(conn, tx, "LocalBranches", "BranchTagline", "TEXT NULL");

        var terminalConfigCols = new (string Name, string Def)[]
        {
            ("BusinessTypeId", "INTEGER"),
            ("BusinessType", "TEXT"),
            ("BranchName", "TEXT"),
            ("BranchTagline", "TEXT NULL"),
            ("BranchCode", "TEXT"),
            ("BranchAddress", "TEXT"),
            ("BranchPhone", "TEXT"),
            ("ReceiptLogoUrl", "TEXT NULL"),
            ("ReceiptLogoDataUri", "TEXT NULL"),
            ("ReceiptLogoUpdatedAt", "TEXT NULL"),
            ("VatDisplay", "INTEGER NOT NULL DEFAULT 1"),
            ("ReceiptPrinterName", "TEXT"),
            ("KitchenPrinterName", "TEXT"),
            ("BarPrinterName", "TEXT"),
            ("IsInitialized", "INTEGER NOT NULL DEFAULT 0"),
            ("LastSyncVersion", "TEXT"),
            ("LastSyncAt", "TEXT"),
            ("DeviceUuid", "TEXT"),
            ("IsActivated", "INTEGER NOT NULL DEFAULT 0"),
            ("AllowSync", "INTEGER NOT NULL DEFAULT 1"),
            ("ActivatedAt", "TEXT"),
            ("DefaultOpeningFund", "TEXT NULL"),
            ("OpeningFundOverride", "TEXT NULL"),
            ("EffectiveOpeningFund", "TEXT NULL"),
            ("OpeningFundSource", "TEXT NULL"),
            ("ClosedBusinessDate", "TEXT NULL"),
            ("ClosingLockedByCashier", "TEXT NULL"),
            ("ClosingLockedAtUtc", "TEXT NULL"),
            ("ClosingUnlockAtUtc", "TEXT NULL"),
            ("ClosingLockedTickCount64", "INTEGER NULL"),
            ("LastKnownServerUtc", "TEXT NULL"),
            ("LastKnownServerTickCount64", "INTEGER NULL")
        };
        foreach (var (name, def) in terminalConfigCols)
        {
            AddColumnIfMissing(conn, tx, "LocalTerminalConfigs", name, def);
        }

        if (TableExists(conn, tx, "LocalTerminalConfigs"))
        {
            ExecuteNonQuery(conn, tx, @"
                INSERT OR IGNORE INTO LocalBranches (
                    BranchId, Name, BranchTagline, Code, Address, Phone, TaxType, VatDisplay, TrackInventory,
                    BusinessTypeId, BusinessType, ServiceModeId, ReceiptLogoUrl, ReceiptLogoDataUri,
                    ReceiptLogoUpdatedAt, IsActive, UpdatedAt
                )
                SELECT
                    BranchId,
                    COALESCE(BranchName, TerminalName, ''),
                    BranchTagline,
                    COALESCE(BranchCode, 'BR-' || printf('%02d', BranchId)),
                    BranchAddress,
                    BranchPhone,
                    COALESCE(BranchTaxType, 'VAT-Registered'),
                    COALESCE(VatDisplay, 1),
                    0,
                    COALESCE(BusinessTypeId, 1),
                    COALESCE(BusinessType, 'FNB'),
                    NULL,
                    ReceiptLogoUrl,
                    ReceiptLogoDataUri,
                    ReceiptLogoUpdatedAt,
                    1,
                    COALESCE(LastSyncAt, CURRENT_TIMESTAMP)
                FROM LocalTerminalConfigs
                WHERE BranchId > 0;");
        }

        var orderItemCols = new (string Name, string Def)[]
        {
            ("ProductShortCode", "TEXT"),
            ("IsKitchenItem", "INTEGER NOT NULL DEFAULT 0"),
            ("IsBarItem", "INTEGER NOT NULL DEFAULT 0"),
            ("SpecialInstructions", "TEXT NULL"),
            ("ModifiersJson", "TEXT"),
            ("PricingMethod", "TEXT NULL"),
            ("TargetAmount", "REAL NULL"),
            ("IsComplimentary", "INTEGER NOT NULL DEFAULT 0"),
            ("ParentProductId", "INTEGER NULL")
        };
        foreach (var (name, def) in orderItemCols)
        {
            AddColumnIfMissing(conn, tx, "LocalOrderItems", name, def);
        }

        AddColumnIfMissing(conn, tx, "LocalModifierOptions", "ProductId", "INTEGER NULL");
        AddColumnIfMissing(conn, tx, "LocalModifierOptions", "ProductName", "TEXT NULL");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalPaymentMethods (
                PaymentMethodId INTEGER PRIMARY KEY,
                Code TEXT NOT NULL,
                Name TEXT NOT NULL,
                RequiresReference INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                DisplayOrder INTEGER NOT NULL DEFAULT 0
            );");

        ExecuteNonQuery(conn, tx, @"
            INSERT INTO LocalPaymentMethods (PaymentMethodId, Code, Name, RequiresReference, IsActive, DisplayOrder)
            SELECT 1, 'CASH', 'Cash', 0, 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalPaymentMethods);
            INSERT INTO LocalPaymentMethods (PaymentMethodId, Code, Name, RequiresReference, IsActive, DisplayOrder)
            SELECT 2, 'GCASH', 'GCash', 1, 1, 2
            WHERE NOT EXISTS (SELECT 1 FROM LocalPaymentMethods WHERE Code = 'GCASH');
            INSERT INTO LocalPaymentMethods (PaymentMethodId, Code, Name, RequiresReference, IsActive, DisplayOrder)
            SELECT 3, 'MAYA', 'Maya', 1, 1, 3
            WHERE NOT EXISTS (SELECT 1 FROM LocalPaymentMethods WHERE Code = 'MAYA');
            INSERT INTO LocalPaymentMethods (PaymentMethodId, Code, Name, RequiresReference, IsActive, DisplayOrder)
            SELECT 4, 'CARD', 'Credit / Debit Card', 1, 1, 4
            WHERE NOT EXISTS (SELECT 1 FROM LocalPaymentMethods WHERE Code = 'CARD');");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalHeldSales (
                HeldSaleId INTEGER PRIMARY KEY,
                BranchId INTEGER NOT NULL,
                TerminalId INTEGER NOT NULL,
                CashierId TEXT NOT NULL,
                HoldReference TEXT NOT NULL,
                Status TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ResumedAt TEXT NULL
            );");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "BranchId", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "TerminalId", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "CashierId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "HoldReference", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "Status", "TEXT NOT NULL DEFAULT 'HELD'");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "PayloadJson", "TEXT NOT NULL DEFAULT '{}'");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "CreatedAt", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, tx, "LocalHeldSales", "ResumedAt", "TEXT NULL");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalCashiers (
                UserId INTEGER PRIMARY KEY,
                Username TEXT NOT NULL,
                Role TEXT NOT NULL,
                PinHash TEXT NOT NULL
            );");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalCashMovementCategories (
                CategoryId INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                MovementType TEXT NOT NULL,
                RequiresApproval INTEGER NOT NULL DEFAULT 1,
                ApprovalThreshold TEXT NOT NULL DEFAULT '0.00',
                IsActive INTEGER NOT NULL DEFAULT 1
            );");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalCashDrawerShifts (
                ShiftId INTEGER PRIMARY KEY,
                BranchId INTEGER NOT NULL,
                TerminalId INTEGER NOT NULL,
                CashierId TEXT NOT NULL,
                OpeningFloat TEXT NOT NULL DEFAULT '0.00',
                TotalCashSales TEXT NOT NULL DEFAULT '0.00',
                TotalCashRefunds TEXT NOT NULL DEFAULT '0.00',
                TotalCashIn TEXT NOT NULL DEFAULT '0.00',
                TotalCashOut TEXT NOT NULL DEFAULT '0.00',
                TotalCashDrops TEXT NOT NULL DEFAULT '0.00',
                ExpectedBalance TEXT NOT NULL DEFAULT '0.00',
                ActualCountedBalance TEXT NULL,
                Discrepancy TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'OPEN',
                OpenedAt TEXT NOT NULL,
                ClosedAt TEXT NULL,
                Notes TEXT NULL
            );");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalCashDrawerMovements (
                MovementId INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientMovementId TEXT NOT NULL UNIQUE,
                ShiftId INTEGER NOT NULL,
                BranchId INTEGER NOT NULL,
                TerminalId INTEGER NOT NULL,
                MovementType TEXT NOT NULL,
                CategoryId INTEGER NULL,
                CategoryName TEXT NULL,
                Amount TEXT NOT NULL,
                Reason TEXT NOT NULL,
                ReferenceNumber TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'POSTED',
                CreatedBy TEXT NOT NULL,
                AuthorizedBy TEXT NOT NULL,
                ApprovedBy TEXT NULL,
                SupervisorPinHash TEXT NULL,
                CreatedAt TEXT NOT NULL,
                ApprovedAt TEXT NULL,
                PostedAt TEXT NULL,
                SyncStatus TEXT NOT NULL DEFAULT 'PENDING',
                SyncError TEXT NULL
            );");

        ExecuteNonQuery(conn, tx, @"
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 1, 'Store Supplies / Ice / Packaging', 'Emergency F&B store consumables and packaging paid from drawer', 'PETTY_CASH', 1, '0.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories);
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 2, 'Delivery / Rider / Transport', 'Rider fee, fuel, or urgent local delivery expense', 'PETTY_CASH', 1, '0.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 2);
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 3, 'Utilities / Minor Repair', 'Urgent store maintenance or utility expense', 'PETTY_CASH', 1, '0.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 3);
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 4, 'Supplier COD / Emergency Ingredient', 'Cash on delivery for urgent kitchen raw materials', 'CASH_OUT', 1, '0.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 4);
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 5, 'Safe Cash Drop / Remittance', 'Mid-shift excess cash transfer to store safe', 'CASH_DROP', 1, '0.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 5);
            INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
            SELECT 6, 'Additional Change Fund (Paid-In)', 'Bills broken into coins or additional float added to drawer', 'CASH_IN', 0, '1000.00', 1
            WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 6);");

        var shiftCols = new (string Name, string Def)[]
        {
            ("OpeningConfirmed", "INTEGER NOT NULL DEFAULT 0"),
            ("ClientShiftId", "TEXT NULL"),
            ("ClientCloseId", "TEXT NULL"),
            ("ClosingDenominationsJson", "TEXT NULL"),
            ("ClosingOriginalDeclarationJson", "TEXT NULL"),
            ("ClosingPaymentReconciliationsJson", "TEXT NULL"),
            ("ClosingRecountHistoryJson", "TEXT NULL"),
            ("ExpectedOpeningFund", "TEXT NULL"),
            ("OpeningDiscrepancy", "TEXT NULL"),
            ("OpeningDenominationsJson", "TEXT NULL")
        };
        foreach (var (name, def) in shiftCols)
        {
            AddColumnIfMissing(conn, tx, "LocalCashDrawerShifts", name, def);
        }

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalTerminalLicenses (
                TerminalId INTEGER PRIMARY KEY,
                LicenseId TEXT NOT NULL DEFAULT '',
                LicenseKey TEXT NOT NULL DEFAULT '',
                DeviceId TEXT NOT NULL DEFAULT '',
                BranchId INTEGER NOT NULL DEFAULT 0,
                OutletId INTEGER NOT NULL DEFAULT 0,
                LicenseStatus TEXT NOT NULL DEFAULT 'PENDING_ACTIVATION',
                IssuedAtUtc TEXT NOT NULL,
                ActivatedAtUtc TEXT NULL,
                ExpiresAtUtc TEXT NULL,
                LicenseVersion INTEGER NOT NULL DEFAULT 1,
                DurationValue INTEGER NOT NULL DEFAULT 5,
                DurationUnit TEXT NOT NULL DEFAULT 'YEARS',
                OriginalDurationValue INTEGER NOT NULL DEFAULT 5,
                OriginalDurationUnit TEXT NOT NULL DEFAULT 'YEARS',
                RenewalCount INTEGER NOT NULL DEFAULT 0,
                LastRenewedAtUtc TEXT NULL,
                OfflineGracePeriodDays INTEGER NOT NULL DEFAULT 30,
                EnvironmentType TEXT NOT NULL DEFAULT 'Production',
                Signature TEXT NOT NULL DEFAULT '',
                LastValidatedServerUtc TEXT NULL,
                LastValidatedTickCount64 INTEGER NULL,
                HighWaterMarkUtc TEXT NULL,
                HighWaterMarkTickCount64 INTEGER NULL,
                RevocationReason TEXT NULL,
                RevokedAtUtc TEXT NULL,
                SuspensionReason TEXT NULL,
                SuspendedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );");

        var licCols = new (string Name, string Def)[]
        {
            ("DurationValue", "INTEGER NOT NULL DEFAULT 5"),
            ("DurationUnit", "TEXT NOT NULL DEFAULT 'YEARS'"),
            ("OriginalDurationValue", "INTEGER NOT NULL DEFAULT 5"),
            ("OriginalDurationUnit", "TEXT NOT NULL DEFAULT 'YEARS'"),
            ("RenewalCount", "INTEGER NOT NULL DEFAULT 0"),
            ("LastRenewedAtUtc", "TEXT NULL")
        };
        foreach (var (name, def) in licCols)
        {
            AddColumnIfMissing(conn, tx, "LocalTerminalLicenses", name, def);
        }

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalPrinterHardwareConfigs (
                TerminalId INTEGER PRIMARY KEY,
                PrinterEnabled INTEGER NOT NULL DEFAULT 0,
                ConnectionType TEXT NOT NULL DEFAULT 'Ethernet',
                CashDrawerEnabled INTEGER NOT NULL DEFAULT 0,
                PrinterIpAddress TEXT NULL,
                PrinterPort INTEGER NOT NULL DEFAULT 9100,
                UsbDeviceId TEXT NULL,
                UsbDeviceName TEXT NULL,
                PaperWidthMm INTEGER NOT NULL DEFAULT 80,
                PrintDensity TEXT NOT NULL DEFAULT 'Normal',
                CharacterEncoding TEXT NOT NULL DEFAULT 'PC437',
                AutoCutEnabled INTEGER NOT NULL DEFAULT 1,
                ConnectTimeoutMs INTEGER NOT NULL DEFAULT 2000,
                KickPin INTEGER NOT NULL DEFAULT 0,
                PulseOnUnits INTEGER NOT NULL DEFAULT 25,
                PulseOffUnits INTEGER NOT NULL DEFAULT 250,
                UpdatedBy TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );");

        var printerCols = new (string Name, string Def)[]
        {
            ("PrinterEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("ConnectionType", "TEXT NOT NULL DEFAULT 'Ethernet'"),
            ("UsbDeviceId", "TEXT NULL"),
            ("UsbDeviceName", "TEXT NULL"),
            ("PaperWidthMm", "INTEGER NOT NULL DEFAULT 80"),
            ("PrintDensity", "TEXT NOT NULL DEFAULT 'Normal'"),
            ("CharacterEncoding", "TEXT NOT NULL DEFAULT 'PC437'"),
            ("AutoCutEnabled", "INTEGER NOT NULL DEFAULT 1")
        };
        foreach (var (name, def) in printerCols)
        {
            AddColumnIfMissing(conn, tx, "LocalPrinterHardwareConfigs", name, def);
        }

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalCashDrawerHardwareEvents (
                EventId INTEGER PRIMARY KEY AUTOINCREMENT,
                TerminalId INTEGER NOT NULL,
                BranchId INTEGER NOT NULL DEFAULT 0,
                TransactionId TEXT NULL,
                InvoiceNumber TEXT NULL,
                ActionType TEXT NOT NULL,
                RequestedBy TEXT NOT NULL,
                AuthorizedBy TEXT NULL,
                AuthorizedRole TEXT NULL,
                Reason TEXT NULL,
                TargetEndpoint TEXT NULL,
                CommandHex TEXT NULL,
                Status TEXT NOT NULL,
                ErrorMessage TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );");
        ExecuteNonQuery(conn, tx, "CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalCashDrawerHardwareEvents_TransactionId ON LocalCashDrawerHardwareEvents (TransactionId) WHERE TransactionId IS NOT NULL;");
        ExecuteNonQuery(conn, tx, "CREATE INDEX IF NOT EXISTS IX_LocalCashDrawerHardwareEvents_Terminal_CreatedAt ON LocalCashDrawerHardwareEvents (TerminalId, CreatedAtUtc DESC);");

        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalReceiptPrintJobs (
                PrintJobId INTEGER PRIMARY KEY AUTOINCREMENT,
                TransactionId TEXT NOT NULL,
                OrderNumber TEXT NULL,
                InvoiceNumber TEXT NULL,
                TerminalId INTEGER NOT NULL,
                BranchId INTEGER NOT NULL DEFAULT 0,
                ConnectionType TEXT NOT NULL DEFAULT 'Ethernet',
                TargetEndpoint TEXT NULL,
                Status TEXT NOT NULL DEFAULT 'PENDING',
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                LastAttemptAtUtc TEXT NULL,
                PrintedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );");
        ExecuteNonQuery(conn, tx, "CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalReceiptPrintJobs_TransactionId ON LocalReceiptPrintJobs (TransactionId);");
        ExecuteNonQuery(conn, tx, "CREATE INDEX IF NOT EXISTS IX_LocalReceiptPrintJobs_Terminal_UpdatedAt ON LocalReceiptPrintJobs (TerminalId, UpdatedAtUtc DESC);");
    }

    private static void ApplyMigrationV2(DbConnection conn, DbTransaction tx)
    {
        ExecuteNonQuery(conn, tx, @"
            CREATE TABLE IF NOT EXISTS LocalSchemaMigrations (
                Version INTEGER PRIMARY KEY,
                Description TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL,
                AppVersion TEXT NULL
            );");

        ExecuteNonQuery(conn, tx, @"
            INSERT OR IGNORE INTO LocalSchemaMigrations (Version, Description, AppliedAtUtc, AppVersion)
            VALUES
                (1, 'Baseline POS offline schema & additive columns', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), '1.0.1'),
                (2, 'Versioned migration ledger & survivable backup support', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), '1.0.1');");
    }

    private static void MaybeSimulateFailure(int targetVersion)
    {
        if (AppContext.GetData("POS.SimulateMigrationFailureAtVersion") is int failAt && failAt == targetVersion)
        {
            throw new InvalidOperationException($"Simulated migration failure at schema version {targetVersion}.");
        }
    }

    private static bool HasExistingUserTables(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var res = cmd.ExecuteScalar();
        return res != null && res != DBNull.Value && Convert.ToInt32(res) > 0;
    }

    private static bool TableExists(DbConnection conn, DbTransaction? tx, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = tableName;
        cmd.Parameters.Add(p);
        var res = cmd.ExecuteScalar();
        return res != null && res != DBNull.Value && Convert.ToInt32(res) > 0;
    }

    private static void AddColumnIfMissing(DbConnection conn, DbTransaction tx, string tableName, string columnName, string columnDef)
    {
        if (!TableExists(conn, tx, tableName))
            return;

        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.FieldCount > 1 && !reader.IsDBNull(1))
                {
                    existingCols.Add(reader.GetString(1));
                }
            }
        }

        if (!existingCols.Contains(columnName))
        {
            ExecuteNonQuery(conn, tx, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};");
        }
    }

    private static void ExecuteNonQuery(DbConnection conn, DbTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool VerifyQuickCheck(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        var res = cmd.ExecuteScalar()?.ToString();
        return string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase);
    }
}
