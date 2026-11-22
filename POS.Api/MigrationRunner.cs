using System;
using System.IO;
using Npgsql;

namespace POS.Api;

public class MigrationRunner
{
    public static void Run(string? configuredConnectionString = null)
    {
        string connString = !string.IsNullOrWhiteSpace(configuredConnectionString)
            ? configuredConnectionString
            : Environment.GetEnvironmentVariable("POS_DB_CONNECTION")
              ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
              ?? throw new InvalidOperationException("PostgreSQL connection string is required for MigrationRunner.");

        string[] scripts = {
            Path.Combine("Database", "ReceiptReprintLogs.sql"),
            Path.Combine("Database", "HeldSales.sql"),
            Path.Combine("Database", "HeldSaleAuditLogs.sql"),
            Path.Combine("Database", "ReturnRefundVoidModule.sql"),
            Path.Combine("Database", "CashierSessionModule.sql"),
            Path.Combine("Database", "CashDrawerModule.sql"),
            Path.Combine("Database", "OfflineSyncIdempotency.sql")
        };

        using var conn = new NpgsqlConnection(connString);
        conn.Open();

        foreach (var path in scripts)
        {
            if (!File.Exists(path)) continue;
            Console.WriteLine($"Executing {path}...");
            string sql = File.ReadAllText(path);
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
            Console.WriteLine($"Successfully executed {path}");
        }

        // Non-destructive schema evolution for Petty Cash & Cash Movement Management
        string cashMovementSchemaSql = @"
            CREATE TABLE IF NOT EXISTS ""CashMovementCategories"" (
                ""CategoryId"" SERIAL PRIMARY KEY,
                ""Name"" VARCHAR(100) NOT NULL,
                ""Description"" VARCHAR(255) NULL,
                ""MovementType"" VARCHAR(30) NOT NULL DEFAULT 'PETTY_CASH',
                ""RequiresApproval"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""ApprovalThreshold"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""CreatedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS ""CashDrawerShifts"" (
                ""ShiftId"" BIGSERIAL PRIMARY KEY,
                ""BranchId"" INTEGER NOT NULL,
                ""TerminalId"" INTEGER NOT NULL,
                ""CashierId"" VARCHAR(450) NOT NULL,
                ""OpeningFloat"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""TotalCashSales"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""TotalCashRefunds"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""TotalCashIn"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""TotalCashOut"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""TotalCashDrops"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""ExpectedBalance"" NUMERIC(18,2) NOT NULL DEFAULT 0.00,
                ""ActualCountedBalance"" NUMERIC(18,2) NULL,
                ""Discrepancy"" NUMERIC(18,2) NULL,
                ""Status"" VARCHAR(30) NOT NULL DEFAULT 'OPEN',
                ""OpenedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""ClosedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""Notes"" VARCHAR(255) NULL
            );

            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""TotalCashDrops"" NUMERIC(18,2) NOT NULL DEFAULT 0.00;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ClientCloseId"" VARCHAR(100) NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ClosingDenominationsJson"" TEXT NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ClosingOriginalDeclarationJson"" TEXT NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ClosingPaymentReconciliationsJson"" TEXT NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ClosingRecountHistoryJson"" TEXT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_CashDrawerShifts_ClientCloseId"" ON ""CashDrawerShifts"" (""ClientCloseId"") WHERE ""ClientCloseId"" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS ""CashDrawerMovements"" (
                ""MovementId"" BIGSERIAL PRIMARY KEY,
                ""ShiftId"" BIGINT NOT NULL REFERENCES ""CashDrawerShifts""(""ShiftId"") ON DELETE CASCADE,
                ""BranchId"" INTEGER NOT NULL DEFAULT 1,
                ""TerminalId"" INTEGER NOT NULL DEFAULT 1,
                ""MovementType"" VARCHAR(30) NOT NULL,
                ""CategoryId"" INTEGER NULL REFERENCES ""CashMovementCategories""(""CategoryId"") ON DELETE SET NULL,
                ""CategoryName"" VARCHAR(100) NULL,
                ""Amount"" NUMERIC(18,2) NOT NULL,
                ""Reason"" VARCHAR(255) NOT NULL,
                ""ReferenceNumber"" VARCHAR(50) NULL,
                ""ClientMovementId"" VARCHAR(100) NULL,
                ""Status"" VARCHAR(30) NOT NULL DEFAULT 'POSTED',
                ""CreatedBy"" VARCHAR(450) NULL,
                ""AuthorizedBy"" VARCHAR(450) NOT NULL,
                ""ApprovedBy"" VARCHAR(450) NULL,
                ""CreatedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""ApprovedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""PostedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""RejectionReason"" VARCHAR(255) NULL,
                ""ReversedBy"" VARCHAR(450) NULL,
                ""ReversedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""ReversalReason"" VARCHAR(255) NULL,
                ""OriginalMovementId"" BIGINT NULL
            );

            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""BranchId"" INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""TerminalId"" INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""CategoryId"" INTEGER NULL REFERENCES ""CashMovementCategories""(""CategoryId"") ON DELETE SET NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""CategoryName"" VARCHAR(100) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ReferenceNumber"" VARCHAR(50) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ClientMovementId"" VARCHAR(100) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""Status"" VARCHAR(30) NOT NULL DEFAULT 'POSTED';
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" VARCHAR(450) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ApprovedBy"" VARCHAR(450) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ApprovedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""PostedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""RejectionReason"" VARCHAR(255) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ReversedBy"" VARCHAR(450) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ReversedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""ReversalReason"" VARCHAR(255) NULL;
            ALTER TABLE ""CashDrawerMovements"" ADD COLUMN IF NOT EXISTS ""OriginalMovementId"" BIGINT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_CashDrawerMovements_ReferenceNumber"" ON ""CashDrawerMovements"" (""ReferenceNumber"") WHERE ""ReferenceNumber"" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_CashDrawerMovements_ClientMovementId"" ON ""CashDrawerMovements"" (""ClientMovementId"") WHERE ""ClientMovementId"" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ""IX_CashDrawerMovements_ShiftId_Status"" ON ""CashDrawerMovements"" (""ShiftId"", ""Status"");
            CREATE INDEX IF NOT EXISTS ""IX_CashDrawerMovements_Branch_Outlet_CreatedAt"" ON ""CashDrawerMovements"" (""BranchId"", ""TerminalId"", ""CreatedAt"");

            INSERT INTO ""CashMovementCategories"" (""Name"", ""Description"", ""MovementType"", ""RequiresApproval"", ""ApprovalThreshold"", ""IsActive"")
            SELECT * FROM (VALUES
                ('Store Supplies / Ice / Packaging', 'Emergency F&B store consumables and packaging paid from drawer', 'PETTY_CASH', TRUE, 0.00::numeric(18,2), TRUE),
                ('Delivery / Rider / Transport', 'Rider fee, fuel, or urgent local delivery expense', 'PETTY_CASH', TRUE, 0.00::numeric(18,2), TRUE),
                ('Utilities / Minor Repair', 'Urgent store maintenance or utility expense', 'PETTY_CASH', TRUE, 0.00::numeric(18,2), TRUE),
                ('Supplier COD / Emergency Ingredient', 'Cash on delivery for urgent kitchen raw materials', 'CASH_OUT', TRUE, 0.00::numeric(18,2), TRUE),
                ('Safe Cash Drop / Remittance', 'Mid-shift excess cash transfer to store safe', 'CASH_DROP', TRUE, 0.00::numeric(18,2), TRUE),
                ('Additional Change Fund (Paid-In)', 'Bills broken into coins or additional float added to drawer', 'CASH_IN', FALSE, 1000.00::numeric(18,2), TRUE),
                ('Petty Cash Replenishment / Return', 'Unused petty cash returned to cash drawer', 'CASH_IN', FALSE, 0.00::numeric(18,2), TRUE)
            ) AS v(""Name"", ""Description"", ""MovementType"", ""RequiresApproval"", ""ApprovalThreshold"", ""IsActive"")
            WHERE NOT EXISTS (SELECT 1 FROM ""CashMovementCategories"");
        ";

        using (var schemaCmd = new NpgsqlCommand(cashMovementSchemaSql, conn))
        {
            schemaCmd.ExecuteNonQuery();
        }

        string terminalLocalFirstSql = @"
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""Uuid"" VARCHAR(36) NULL;
            ALTER TABLE ""Terminals"" ALTER COLUMN ""Uuid"" DROP NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_Terminals_Uuid"" ON ""Terminals"" (""Uuid"");

            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""ActivationCode"" VARCHAR(10) NULL;
            CREATE INDEX IF NOT EXISTS ""IX_Terminals_ActivationCode"" ON ""Terminals"" (""ActivationCode"") WHERE ""ActivationCode"" IS NOT NULL;

            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""IsActivationCodeUsed"" BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""AllowSync"" BOOLEAN NOT NULL DEFAULT TRUE;

            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""SpecialInstructions"" VARCHAR(250) NULL;
            ALTER TABLE ""HeldSaleItems"" ADD COLUMN IF NOT EXISTS ""SpecialInstructions"" VARCHAR(250) NULL;

            ALTER TABLE ""Outlets"" ADD COLUMN IF NOT EXISTS ""DefaultOpeningFund"" NUMERIC(18,2) NULL;
            ALTER TABLE ""Outlets"" ADD COLUMN IF NOT EXISTS ""CashFundUpdatedBy"" VARCHAR(450) NULL;
            ALTER TABLE ""Outlets"" ADD COLUMN IF NOT EXISTS ""CashFundUpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;

            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""OpeningFundOverride"" NUMERIC(18,2) NULL;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""CashFundUpdatedBy"" VARCHAR(450) NULL;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""CashFundUpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;

            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""ExpectedOpeningFund"" NUMERIC(18,2) NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""OpeningDiscrepancy"" NUMERIC(18,2) NULL;
            ALTER TABLE ""CashDrawerShifts"" ADD COLUMN IF NOT EXISTS ""OpeningDenominationsJson"" TEXT NULL;

            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""ClosedBusinessDate"" VARCHAR(20) NULL;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""ClosingLockedByCashier"" VARCHAR(450) NULL;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""ClosingLockedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""Terminals"" ADD COLUMN IF NOT EXISTS ""ClosingUnlockAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL;

            CREATE TABLE IF NOT EXISTS ""CashierClosingRestrictions"" (
                ""RestrictionId"" BIGSERIAL PRIMARY KEY,
                ""BranchId"" INTEGER NOT NULL,
                ""TerminalId"" INTEGER NOT NULL,
                ""CashierId"" VARCHAR(450) NOT NULL,
                ""BusinessDate"" VARCHAR(20) NOT NULL,
                ""ShiftId"" BIGINT NULL,
                ""ClientShiftId"" VARCHAR(100) NULL,
                ""ClientCloseId"" VARCHAR(100) NULL,
                ""ClosedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                ""UnlockAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                ""Status"" VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
                ""ReopenedBy"" VARCHAR(100) NULL,
                ""ReopenedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""ReopenReason"" VARCHAR(500) NULL,
                ""ClientOperationId"" VARCHAR(100) NULL,
                ""CreatedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS ""IX_CashierClosingRestrictions_Terminal_Date_Status""
                ON ""CashierClosingRestrictions"" (""TerminalId"", ""BusinessDate"", ""Status"");
        ";

        using (var terminalCmd = new NpgsqlCommand(terminalLocalFirstSql, conn))
        {
            terminalCmd.ExecuteNonQuery();
        }

        // Non-destructive data migration: Standardize legacy SUCCESS status to PAID
        string paymentStatusMigrationSql = @"
            UPDATE ""Payments"" SET ""PaymentStatus"" = 'PAID' WHERE ""PaymentStatus"" = 'SUCCESS';
            UPDATE ""Orders"" SET ""PaymentStatus"" = 'PAID' WHERE ""PaymentStatus"" = 'SUCCESS';
        ";
        using (var payCmd = new NpgsqlCommand(paymentStatusMigrationSql, conn))
        {
            payCmd.ExecuteNonQuery();
        }

        // Non-destructive schema evolution: Weight-Based Pricing with Amount-Based Ordering
        string weightPricingMigrationSql = @"
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""PricingMethod"" VARCHAR(20) DEFAULT 'Fixed';
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""PricingMethod"" VARCHAR(20) NULL;
            ALTER TABLE ""OrderItems"" ALTER COLUMN ""Quantity"" TYPE NUMERIC(18, 6);
        ";
        using (var weightCmd = new NpgsqlCommand(weightPricingMigrationSql, conn))
        {
            weightCmd.ExecuteNonQuery();
        }

        // Non-destructive schema evolution: ModifierOption and OrderItemModifier Product Linking
        string modifierProductMigrationSql = @"
            ALTER TABLE ""ModifierOption"" ADD COLUMN IF NOT EXISTS ""product_id"" INT NULL;
            DO $$ 
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'FK_ModifierOption_Product'
                ) THEN
                    ALTER TABLE ""ModifierOption"" 
                    ADD CONSTRAINT ""FK_ModifierOption_Product"" 
                    FOREIGN KEY (""product_id"") REFERENCES ""Products""(""ProductId"") 
                    ON DELETE SET NULL;
                END IF;
            END $$;

            ALTER TABLE ""OrderItemModifiers"" ADD COLUMN IF NOT EXISTS ""ProductId"" INT NULL;
            DO $$ 
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'FK_OrderItemModifiers_Products'
                ) THEN
                    ALTER TABLE ""OrderItemModifiers"" 
                    ADD CONSTRAINT ""FK_OrderItemModifiers_Products"" 
                    FOREIGN KEY (""ProductId"") REFERENCES ""Products""(""ProductId"") 
                    ON DELETE SET NULL;
                END IF;
            END $$;

            ALTER TABLE ""HeldSaleItemModifiers"" ADD COLUMN IF NOT EXISTS ""ProductId"" INT NULL;

            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""IsComplimentary"" BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""ParentProductId"" INT NULL;
            DO $$ 
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'FK_OrderItems_ParentProduct'
                ) THEN
                    ALTER TABLE ""OrderItems"" 
                    ADD CONSTRAINT ""FK_OrderItems_ParentProduct"" 
                    FOREIGN KEY (""ParentProductId"") REFERENCES ""Products""(""ProductId"") 
                    ON DELETE SET NULL;
                END IF;
            END $$;

            ALTER TABLE ""HeldSaleItems"" ADD COLUMN IF NOT EXISTS ""PricingMethod"" VARCHAR(20) NULL;
            ALTER TABLE ""HeldSaleItems"" ADD COLUMN IF NOT EXISTS ""TargetAmount"" NUMERIC(18, 2) NULL;
            ALTER TABLE ""HeldSaleItems"" ADD COLUMN IF NOT EXISTS ""IsComplimentary"" BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE ""HeldSaleItems"" ADD COLUMN IF NOT EXISTS ""ParentProductId"" INT NULL;
            ALTER TABLE ""HeldSaleItems"" ALTER COLUMN ""Quantity"" TYPE NUMERIC(18, 6);

            -- Custom Display Order for Categories and Products
            DO $$ 
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Categories') THEN
                    ALTER TABLE ""Categories"" ADD COLUMN IF NOT EXISTS ""display_order"" INT NOT NULL DEFAULT 0;
                ELSIF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'categories') THEN
                    ALTER TABLE ""categories"" ADD COLUMN IF NOT EXISTS ""display_order"" INT NOT NULL DEFAULT 0;
                END IF;

                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Products') THEN
                    ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""display_order"" INT NOT NULL DEFAULT 0;
                ELSIF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'products') THEN
                    ALTER TABLE ""products"" ADD COLUMN IF NOT EXISTS ""display_order"" INT NOT NULL DEFAULT 0;
                END IF;
            END $$;

            ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUrl"" VARCHAR(255) NULL;
            ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""VatDisplay"" BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""BranchTagline"" VARCHAR(200) NULL;

            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ServerReceivedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""DeviceTimeZoneId"" VARCHAR(100) NULL;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""DeviceUtcOffsetMinutes"" INT NULL;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ClockSkewSeconds"" INT NULL;
            UPDATE ""Payments"" p
            SET ""paid_at"" = p.""paid_at"" + (o.""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute')
            FROM ""Orders"" o
            WHERE p.""order_id"" = o.""order_id""
              AND o.""DeviceUtcOffsetMinutes"" IS NOT NULL
              AND ABS(o.""DeviceUtcOffsetMinutes"") >= 180
              AND o.""ServerReceivedAtUtc"" IS NOT NULL
              AND ABS(EXTRACT(EPOCH FROM (o.""created_at"" - o.""ServerReceivedAtUtc""))) < 7200;
            UPDATE ""Orders""
            SET ""created_at"" = ""created_at"" + (""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute'),
                ""invoice_issued_at"" = CASE WHEN ""invoice_issued_at"" IS NOT NULL THEN ""invoice_issued_at"" + (""DeviceUtcOffsetMinutes"" * INTERVAL '1 minute') ELSE NULL END
            WHERE ""DeviceUtcOffsetMinutes"" IS NOT NULL
              AND ABS(""DeviceUtcOffsetMinutes"") >= 180
              AND ""ServerReceivedAtUtc"" IS NOT NULL
              AND ABS(EXTRACT(EPOCH FROM (""created_at"" - ""ServerReceivedAtUtc""))) < 7200;
        ";
        using (var modCmd = new NpgsqlCommand(modifierProductMigrationSql, conn))
        {
            modCmd.ExecuteNonQuery();
        }

        ApplyLicensingSchema(conn);

        Console.WriteLine("All migration scripts applied successfully to pos_db!");
    }

    public static void ApplyLicensingSchema(NpgsqlConnection conn)
    {
        string licensingSchemaSql = @"
            CREATE TABLE IF NOT EXISTS ""TerminalLicenses"" (
                ""LicenseId"" VARCHAR(64) PRIMARY KEY,
                ""LicenseKey"" VARCHAR(80) NOT NULL,
                ""TerminalId"" INTEGER NOT NULL REFERENCES ""Terminals""(""TerminalId"") ON DELETE CASCADE,
                ""DeviceId"" VARCHAR(120) NOT NULL DEFAULT '',
                ""BranchId"" INTEGER NOT NULL REFERENCES ""Branches""(""BranchId"") ON DELETE RESTRICT,
                ""OutletId"" INTEGER NOT NULL REFERENCES ""Outlets""(""OutletId"") ON DELETE RESTRICT,
                ""LicenseStatus"" VARCHAR(40) NOT NULL DEFAULT 'PENDING_ACTIVATION',
                ""DurationYears"" INTEGER NOT NULL DEFAULT 5,
                ""DurationValue"" INTEGER NOT NULL DEFAULT 5,
                ""DurationUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS',
                ""OriginalDurationValue"" INTEGER NOT NULL DEFAULT 5,
                ""OriginalDurationUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS',
                ""RenewalCount"" INTEGER NOT NULL DEFAULT 0,
                ""LastRenewedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""IssuedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""ActivatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""ExpiresAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""LastValidatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""LicenseVersion"" INTEGER NOT NULL DEFAULT 1,
                ""RevokedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""RevocationReason"" VARCHAR(255) NULL,
                ""SuspendedAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""SuspensionReason"" VARCHAR(255) NULL,
                ""OfflineGracePeriodDays"" INTEGER NOT NULL DEFAULT 30,
                ""EnvironmentType"" VARCHAR(30) NOT NULL DEFAULT 'Production',
                ""Signature"" VARCHAR(512) NOT NULL DEFAULT '',
                ""IssuedBy"" VARCHAR(150) NULL,
                ""CreatedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""UpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_TerminalLicenses_LicenseKey"" ON ""TerminalLicenses"" (""LicenseKey"");
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_TerminalLicenses_TerminalId"" ON ""TerminalLicenses"" (""TerminalId"");

            CREATE TABLE IF NOT EXISTS ""LicenseRenewalHistories"" (
                ""RenewalId"" BIGSERIAL PRIMARY KEY,
                ""LicenseId"" VARCHAR(64) NOT NULL,
                ""TerminalId"" INTEGER NOT NULL,
                ""PreviousExpiresAt"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""RenewalDuration"" INTEGER NOT NULL DEFAULT 5,
                ""RenewalUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS',
                ""NewExpiresAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                ""PreviousLicenseVersion"" INTEGER NOT NULL DEFAULT 1,
                ""NewLicenseVersion"" INTEGER NOT NULL DEFAULT 2,
                ""RenewedBy"" VARCHAR(150) NOT NULL,
                ""RenewalReason"" VARCHAR(255) NULL,
                ""RenewalNotes"" VARCHAR(255) NULL,
                ""RenewalStatus"" VARCHAR(30) NOT NULL DEFAULT 'COMPLETED',
                ""IdempotencyKey"" VARCHAR(100) NULL,
                ""RenewedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""DurationValue"" INTEGER NOT NULL DEFAULT 5;
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""DurationUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS';
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""OriginalDurationValue"" INTEGER NOT NULL DEFAULT 5;
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""OriginalDurationUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS';
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""RenewalCount"" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE ""TerminalLicenses"" ADD COLUMN IF NOT EXISTS ""LastRenewedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
            ALTER TABLE ""LicenseRenewalHistories"" ADD COLUMN IF NOT EXISTS ""RenewalDuration"" INTEGER NOT NULL DEFAULT 5;
            ALTER TABLE ""LicenseRenewalHistories"" ADD COLUMN IF NOT EXISTS ""RenewalUnit"" VARCHAR(20) NOT NULL DEFAULT 'YEARS';
            ALTER TABLE ""LicenseRenewalHistories"" ADD COLUMN IF NOT EXISTS ""RenewalReason"" VARCHAR(255) NULL;
            ALTER TABLE ""LicenseRenewalHistories"" ADD COLUMN IF NOT EXISTS ""RenewalStatus"" VARCHAR(30) NOT NULL DEFAULT 'COMPLETED';
            ALTER TABLE ""LicenseRenewalHistories"" ADD COLUMN IF NOT EXISTS ""IdempotencyKey"" VARCHAR(100) NULL;
            CREATE INDEX IF NOT EXISTS ""IX_LicenseRenewalHistories_Terminal_RenewedAt"" ON ""LicenseRenewalHistories"" (""TerminalId"", ""RenewedAt"");
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_LicenseRenewalHistories_Terminal_IdempotencyKey"" ON ""LicenseRenewalHistories"" (""TerminalId"", ""IdempotencyKey"") WHERE ""IdempotencyKey"" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS ""LicenseDeviceHistories"" (
                ""DeviceHistoryId"" BIGSERIAL PRIMARY KEY,
                ""LicenseId"" VARCHAR(64) NOT NULL,
                ""TerminalId"" INTEGER NOT NULL,
                ""OldDeviceId"" VARCHAR(120) NULL,
                ""NewDeviceId"" VARCHAR(120) NULL,
                ""ActionType"" VARCHAR(50) NOT NULL,
                ""AuthorizedBy"" VARCHAR(150) NOT NULL,
                ""Reason"" VARCHAR(255) NULL,
                ""OccurredAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS ""IX_LicenseDeviceHistories_Terminal_OccurredAt"" ON ""LicenseDeviceHistories"" (""TerminalId"", ""OccurredAt"");

            CREATE TABLE IF NOT EXISTS ""LicenseAuditLogs"" (
                ""AuditLogId"" BIGSERIAL PRIMARY KEY,
                ""LicenseId"" VARCHAR(64) NULL,
                ""TerminalId"" INTEGER NULL,
                ""BranchId"" INTEGER NULL,
                ""OutletId"" INTEGER NULL,
                ""DeviceId"" VARCHAR(120) NULL,
                ""EventType"" VARCHAR(50) NOT NULL,
                ""EventStatus"" VARCHAR(30) NOT NULL,
                ""Message"" VARCHAR(500) NOT NULL,
                ""Actor"" VARCHAR(150) NULL,
                ""IpAddress"" VARCHAR(64) NULL,
                ""DeviceClockUtc"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""ServerClockUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ""ClockSkewSeconds"" INTEGER NULL,
                ""OccurredAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS ""IX_LicenseAuditLogs_Terminal_OccurredAt"" ON ""LicenseAuditLogs"" (""TerminalId"", ""OccurredAt"");
        ";
        using var licCmd = new NpgsqlCommand(licensingSchemaSql, conn);
        licCmd.ExecuteNonQuery();
    }

    public static void ApplyAgresIdentitySchema(NpgsqlConnection conn)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS ""Organizations"" (
                ""OrganizationId"" SERIAL PRIMARY KEY,
                ""Code"" VARCHAR(30) NOT NULL,
                ""Name"" VARCHAR(120) NOT NULL,
                ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""CreatedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_Organizations_Code"" ON ""Organizations"" (""Code"");

            INSERT INTO ""Organizations"" (""OrganizationId"", ""Code"", ""Name"", ""IsActive"", ""CreatedAtUtc"")
            SELECT 1, 'DEFAULT', COALESCE((SELECT ""Name"" FROM ""Branches"" ORDER BY ""BranchId"" LIMIT 1), 'Default Organization'), TRUE, NOW() AT TIME ZONE 'UTC'
            WHERE NOT EXISTS (SELECT 1 FROM ""Organizations"" WHERE ""OrganizationId"" = 1);

            SELECT setval(
                pg_get_serial_sequence('""Organizations""', 'OrganizationId'),
                GREATEST((SELECT COALESCE(MAX(""OrganizationId""), 1) FROM ""Organizations""), 1)
            );

            CREATE TABLE IF NOT EXISTS ""AgresSystemAdmins"" (
                ""SystemAdminId"" SERIAL PRIMARY KEY,
                ""Username"" VARCHAR(50) NOT NULL,
                ""DisplayName"" VARCHAR(100) NOT NULL,
                ""PasswordHash"" VARCHAR(255) NOT NULL,
                ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""CreatedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
                ""LastLoginAtUtc"" TIMESTAMP WITHOUT TIME ZONE NULL,
                ""CreatedBy"" VARCHAR(100) NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""UX_AgresSystemAdmins_Username"" ON ""AgresSystemAdmins"" (""Username"");

            CREATE TABLE IF NOT EXISTS ""PlatformAuditLogs"" (
                ""AuditLogId"" BIGSERIAL PRIMARY KEY,
                ""ActorUsername"" VARCHAR(100) NOT NULL,
                ""ActorSystemAdminId"" INTEGER NULL,
                ""Action"" VARCHAR(80) NOT NULL,
                ""TargetType"" VARCHAR(80) NOT NULL,
                ""TargetId"" VARCHAR(100) NULL,
                ""Details"" VARCHAR(1000) NULL,
                ""IpAddress"" VARCHAR(64) NULL,
                ""CreatedAtUtc"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
            );
            CREATE INDEX IF NOT EXISTS ""IX_PlatformAuditLogs_CreatedAtUtc"" ON ""PlatformAuditLogs"" (""CreatedAtUtc"");

            ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""OrganizationId"" INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""OrganizationId"" INTEGER NOT NULL DEFAULT 1;
            CREATE INDEX IF NOT EXISTS ""IX_Users_OrganizationId"" ON ""Users"" (""OrganizationId"");
            CREATE INDEX IF NOT EXISTS ""IX_Branches_OrganizationId"" ON ""Branches"" (""OrganizationId"");

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Users_Organizations') THEN
                    ALTER TABLE ""Users""
                        ADD CONSTRAINT ""FK_Users_Organizations""
                        FOREIGN KEY (""OrganizationId"") REFERENCES ""Organizations"" (""OrganizationId"")
                        ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Branches_Organizations') THEN
                    ALTER TABLE ""Branches""
                        ADD CONSTRAINT ""FK_Branches_Organizations""
                        FOREIGN KEY (""OrganizationId"") REFERENCES ""Organizations"" (""OrganizationId"")
                        ON DELETE RESTRICT;
                END IF;
            END $$;

            DO $$
            DECLARE
                legacy_admin RECORD;
                new_admin_id INTEGER;
            BEGIN
                SELECT ""UserId"", ""Username"", ""PasswordHash""
                INTO legacy_admin
                FROM ""Users""
                WHERE LOWER(""Username"") = 'admin'
                LIMIT 1;

                IF FOUND THEN
                    IF NOT EXISTS (SELECT 1 FROM ""AgresSystemAdmins"" WHERE LOWER(""Username"") = LOWER(legacy_admin.""Username"")) THEN
                        INSERT INTO ""AgresSystemAdmins"" (""Username"", ""DisplayName"", ""PasswordHash"", ""IsActive"", ""CreatedAtUtc"", ""CreatedBy"")
                        VALUES (legacy_admin.""Username"", 'AGRES System Administrator', legacy_admin.""PasswordHash"", TRUE, NOW() AT TIME ZONE 'UTC', 'MIGRATION')
                        RETURNING ""SystemAdminId"" INTO new_admin_id;

                        INSERT INTO ""PlatformAuditLogs"" (""ActorUsername"", ""ActorSystemAdminId"", ""Action"", ""TargetType"", ""TargetId"", ""Details"", ""CreatedAtUtc"")
                        VALUES (
                            'SYSTEM_MIGRATION',
                            new_admin_id,
                            'MIGRATED_SYSTEM_ADMIN',
                            'AgresSystemAdmin',
                            new_admin_id::text,
                            format('Migrated legacy UserId=%s (%s) from Users to AgresSystemAdmins and removed from client Users table.', legacy_admin.""UserId"", legacy_admin.""Username""),
                            NOW() AT TIME ZONE 'UTC'
                        );
                    END IF;

                    DELETE FROM ""Users"" WHERE ""UserId"" = legacy_admin.""UserId"";
                END IF;
            END $$;
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public static void ApplyDiscountSyncSchema(NpgsqlConnection conn)
    {
        const string sql = @"
            ALTER TABLE ""DiscountTypes"" ADD COLUMN IF NOT EXISTS ""UpdatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW();
            UPDATE ""DiscountTypes""
            SET ""UpdatedAt"" = COALESCE(""CreatedAt"", NOW())
            WHERE ""UpdatedAt"" IS NULL;

            CREATE OR REPLACE FUNCTION set_discount_types_updated_at()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.""UpdatedAt"" = NOW();
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_discount_types_updated_at ON ""DiscountTypes"";
            CREATE TRIGGER trg_discount_types_updated_at
                BEFORE UPDATE ON ""DiscountTypes""
                FOR EACH ROW
                EXECUTE FUNCTION set_discount_types_updated_at();
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
