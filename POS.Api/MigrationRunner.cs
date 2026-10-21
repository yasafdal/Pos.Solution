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
            : "Host=localhost;Port=5432;Database=pos_db;Username=postgres;Password=p@$$w0rd";

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

        Console.WriteLine("All migration scripts applied successfully to pos_db!");
    }
}

