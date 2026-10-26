-- Manual DBA deployment only. No startup execution. Review duplicate OPEN shifts before applying.
BEGIN;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningConfirmed" boolean NOT NULL DEFAULT false;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClientShiftId" character varying(100) NULL;
-- Do not backfill confirmation: existing drawers require explicit cashier confirmation.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_ClientShiftId" ON "CashDrawerShifts" ("ClientShiftId") WHERE "ClientShiftId" IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_OpenTerminal" ON "CashDrawerShifts" ("TerminalId") WHERE "Status" = 'OPEN';
COMMIT;
