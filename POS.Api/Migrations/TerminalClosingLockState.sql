-- =============================================================================
-- POS.Api — Cashier Account and POS Terminal Locking After Closing
-- Safe, idempotent PostgreSQL migration script for Terminals lock state columns
-- =============================================================================

BEGIN;

ALTER TABLE "Terminals"
    ADD COLUMN IF NOT EXISTS "ClosedBusinessDate" character varying(20) NULL,
    ADD COLUMN IF NOT EXISTS "ClosingLockedByCashier" character varying(450) NULL,
    ADD COLUMN IF NOT EXISTS "ClosingLockedAtUtc" timestamp without time zone NULL,
    ADD COLUMN IF NOT EXISTS "ClosingUnlockAtUtc" timestamp without time zone NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20261110180000_TerminalClosingLockState', '10.0.13'
WHERE NOT EXISTS (
    SELECT 1 FROM "__EFMigrationsHistory"
    WHERE "MigrationId" = '20261110180000_TerminalClosingLockState'
);

COMMIT;
