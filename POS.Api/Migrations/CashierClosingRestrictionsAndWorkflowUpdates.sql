-- =============================================================================
-- POS.Api — Database Migration Script (PostgreSQL)
-- Covers:
--   1. HeldSales automatic previous-day expiration audit columns
--   2. TransactionVoids offline void idempotency column & unique index
--   3. Terminals Z-Reading lock columns (idempotent safeguard)
--   4. CashierClosingRestrictions table for Close Cashier vs Z-Reading separation
--      and Supervisor Reopen audit trail
-- Safe, idempotent, and transaction-wrapped.
-- =============================================================================

BEGIN;

-- 1. HeldSales: BusinessDate & expiration cancellation audit columns
ALTER TABLE "HeldSales"
    ADD COLUMN IF NOT EXISTS "BusinessDate" character varying(10) NULL,
    ADD COLUMN IF NOT EXISTS "CancelledAt" timestamp without time zone NULL,
    ADD COLUMN IF NOT EXISTS "CancellationReason" character varying(255) NULL;

CREATE INDEX IF NOT EXISTS "IX_HeldSales_Terminal_Status_BusinessDate"
    ON "HeldSales" ("TerminalId", "Status", "BusinessDate");

-- 2. TransactionVoids: Offline void idempotency key
ALTER TABLE "TransactionVoids"
    ADD COLUMN IF NOT EXISTS "ClientVoidId" character varying(100) NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_TransactionVoids_ClientVoidId"
    ON "TransactionVoids" ("ClientVoidId")
    WHERE "ClientVoidId" IS NOT NULL;

-- 3. Terminals: Z-Reading end-of-day terminal lock columns (idempotent safeguard)
ALTER TABLE "Terminals"
    ADD COLUMN IF NOT EXISTS "ClosedBusinessDate" character varying(20) NULL,
    ADD COLUMN IF NOT EXISTS "ClosingLockedByCashier" character varying(450) NULL,
    ADD COLUMN IF NOT EXISTS "ClosingLockedAtUtc" timestamp without time zone NULL,
    ADD COLUMN IF NOT EXISTS "ClosingUnlockAtUtc" timestamp without time zone NULL;

-- 4. CashierClosingRestrictions: Cashier-level closing restriction (CashierId + TerminalId + BusinessDate)
--    separated from Terminal-level Z-Reading lock, with Supervisor Reopen audit fields
CREATE TABLE IF NOT EXISTS "CashierClosingRestrictions" (
    "RestrictionId" BIGSERIAL PRIMARY KEY,
    "BranchId" INTEGER NOT NULL,
    "TerminalId" INTEGER NOT NULL,
    "CashierId" VARCHAR(450) NOT NULL,
    "BusinessDate" VARCHAR(20) NOT NULL,
    "ShiftId" BIGINT NULL,
    "ClientShiftId" VARCHAR(100) NULL,
    "ClientCloseId" VARCHAR(100) NULL,
    "ClosedAtUtc" TIMESTAMP WITHOUT TIME ZONE NOT NULL,
    "UnlockAtUtc" TIMESTAMP WITHOUT TIME ZONE NOT NULL,
    "Status" VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    "ReopenedBy" VARCHAR(100) NULL,
    "ReopenedAtUtc" TIMESTAMP WITHOUT TIME ZONE NULL,
    "ReopenReason" VARCHAR(500) NULL,
    "ClientOperationId" VARCHAR(100) NULL,
    "CreatedAtUtc" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS "IX_CashierClosingRestrictions_Terminal_Date_Status"
    ON "CashierClosingRestrictions" ("TerminalId", "BusinessDate", "Status");

CREATE INDEX IF NOT EXISTS "IX_CashierClosingRestrictions_ClientCloseId"
    ON "CashierClosingRestrictions" ("TerminalId", "ClientCloseId")
    WHERE "ClientCloseId" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_CashierClosingRestrictions_ClientOperationId"
    ON "CashierClosingRestrictions" ("TerminalId", "ClientOperationId")
    WHERE "ClientOperationId" IS NOT NULL;

-- 5. Record migration in EF history table (if present)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name = '__EFMigrationsHistory'
    ) THEN
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        SELECT '20261120225500_CashierClosingRestrictionsAndWorkflowUpdates', '10.0.13'
        WHERE NOT EXISTS (
            SELECT 1 FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '20261120225500_CashierClosingRestrictionsAndWorkflowUpdates'
        );
    END IF;
END $$;

COMMIT;
