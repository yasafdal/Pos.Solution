-- Idempotent PostgreSQL migration for Supervisor-Assisted Overnight Shift Recovery
-- Preserves all historical CashDrawerShifts, Orders, and CashDrawerMovements intact.

ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OutletId" INTEGER NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "BusinessDate" VARCHAR(10) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosingType" VARCHAR(40) NOT NULL DEFAULT 'NORMAL';
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosedBySupervisorId" VARCHAR(450) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "RecoveryReason" VARCHAR(500) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "RecoveryStatus" VARCHAR(40) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "UnverifiedCashReason" VARCHAR(500) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "UnverifiedCashReportedBy" VARCHAR(450) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "UnverifiedCashReportedAtUtc" TIMESTAMP WITHOUT TIME ZONE NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "RecoveryOperationId" VARCHAR(100) NULL;

UPDATE "CashDrawerShifts" s
SET "OutletId" = t."OutletId"
FROM "Terminals" t
WHERE s."TerminalId" = t."TerminalId"
  AND s."OutletId" IS NULL;

UPDATE "CashDrawerShifts"
SET "BusinessDate" = TO_CHAR(((("OpenedAt" AT TIME ZONE 'UTC') AT TIME ZONE 'Asia/Manila') - INTERVAL '4 hours')::date, 'YYYY-MM-DD')
WHERE "BusinessDate" IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_RecoveryOperationId"
    ON "CashDrawerShifts" ("RecoveryOperationId")
    WHERE "RecoveryOperationId" IS NOT NULL;
