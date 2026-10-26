-- Standalone delta from 20261021180419_OrderServiceModeAndTable; pre-existing POS schema required.
-- See confirmed-opening-float.README.md for backup, readiness and rollback procedure.
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026035721_ConfirmedOpeningFloat') THEN
    ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningConfirmed" boolean NOT NULL DEFAULT false;
    ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClientShiftId" character varying(100) NULL;
    DO $opening_readiness$
    BEGIN
        IF EXISTS (SELECT 1 FROM "CashDrawerShifts" WHERE "Status" = 'OPEN' GROUP BY "TerminalId" HAVING count(*) > 1) THEN
            RAISE EXCEPTION 'Opening float migration blocked: reconcile duplicate OPEN shifts per terminal first';
        END IF;
        IF EXISTS (SELECT 1 FROM "CashDrawerShifts" WHERE "ClientShiftId" IS NOT NULL GROUP BY "ClientShiftId" HAVING count(*) > 1) THEN
            RAISE EXCEPTION 'Opening float migration blocked: reconcile duplicate ClientShiftId values first';
        END IF;
    END $opening_readiness$;
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_ClientShiftId" ON "CashDrawerShifts" ("ClientShiftId") WHERE "ClientShiftId" IS NOT NULL;
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_OpenTerminal" ON "CashDrawerShifts" ("TerminalId") WHERE "Status" = 'OPEN';
    -- Do not infer historical confirmation. Retain legacy indexes: strictly additive.
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026035721_ConfirmedOpeningFloat') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026035721_ConfirmedOpeningFloat', '10.0.13');
    END IF;
END $EF$;
COMMIT;

