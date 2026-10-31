-- MANUAL, additive, idempotent PostgreSQL deployment. Never run at API startup.
-- Read README-z-reading.md; deploy z-reading-period-guards.sql as well before enabling.
-- Does not provision terminals, reset fiscal counters, or change invoice/order sequences.
ROLLBACK; -- Clear an aborted interactive session before starting the reviewed deployment.
BEGIN;
SET LOCAL lock_timeout = '10s';
CREATE TABLE IF NOT EXISTS public."ZReadingTerminalStates" (
    "TerminalId" integer PRIMARY KEY REFERENCES public."Terminals"("TerminalId") ON DELETE RESTRICT,
    "FirstBusinessDate" date NOT NULL,
    "OpeningAccumulatedSales" numeric(20,2) NOT NULL CHECK ("OpeningAccumulatedSales" >= 0),
    "AccumulatedGrossSales" numeric(20,2) NOT NULL CHECK ("AccumulatedGrossSales" >= "OpeningAccumulatedSales"),
    "MutationVersion" bigint NOT NULL DEFAULT 0 CHECK ("MutationVersion" >= 0),
    "LastZNumber" bigint NOT NULL DEFAULT 0 CHECK ("LastZNumber" >= 0),
    "ResetCounter" bigint NULL CHECK ("ResetCounter" IS NULL OR "ResetCounter" >= 0),
    "LastBusinessDate" date NULL,
    "ConfiguredBy" varchar(450) NOT NULL CHECK (length(btrim("ConfiguredBy")) > 0),
    "ConfiguredAt" timestamp without time zone NOT NULL,
    CHECK (("LastBusinessDate" IS NULL AND "LastZNumber" = 0) OR ("LastBusinessDate" >= "FirstBusinessDate" AND "LastZNumber" > 0))
);
CREATE TABLE IF NOT EXISTS public."ZReadings" (
    "Id" uuid PRIMARY KEY,
    "TerminalId" integer NOT NULL REFERENCES public."Terminals"("TerminalId") ON DELETE RESTRICT,
    "BranchId" integer NOT NULL,
    "OutletId" integer NOT NULL,
    "BusinessDate" date NOT NULL,
    "PeriodStartUtc" timestamp without time zone NOT NULL,
    "PeriodEndUtc" timestamp without time zone NOT NULL,
    "GeneratedAt" timestamp without time zone NOT NULL,
    "GeneratedBy" varchar(450) NOT NULL,
    "ClientOperationId" varchar(100) NOT NULL CHECK (length(btrim("ClientOperationId")) > 0),
    "DeviceIdentifier" varchar(100) NOT NULL CHECK (length(btrim("DeviceIdentifier")) > 0),
    "ZNumber" bigint NOT NULL CHECK ("ZNumber" > 0),
    "SnapshotJson" text NOT NULL,
    "SnapshotSha256" varchar(64) NOT NULL CHECK ("SnapshotSha256" ~ '^[A-F0-9]{64}$'),
    CHECK ("PeriodStartUtc" = "BusinessDate"::timestamp - interval '4 hours'),
    CHECK ("PeriodEndUtc" = "PeriodStartUtc" + interval '1 day')
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZReadings_TerminalId_BusinessDate" ON public."ZReadings"("TerminalId", "BusinessDate");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZReadings_TerminalId_ZNumber" ON public."ZReadings"("TerminalId", "ZNumber");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZReadings_ClientOperationId" ON public."ZReadings"("ClientOperationId");
CREATE TABLE IF NOT EXISTS public."ZReadingPrintAttempts" (
    "Id" uuid PRIMARY KEY,
    "ZReadingId" uuid NOT NULL REFERENCES public."ZReadings"("Id") ON DELETE RESTRICT,
    "ClientOperationId" varchar(100) NOT NULL CHECK (length(btrim("ClientOperationId")) > 0),
    "RequestedBy" varchar(450) NOT NULL,
    "StartedAt" timestamp without time zone NOT NULL,
    "PrinterName" varchar(200) NULL,
    "Reason" varchar(500) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ZReadingPrintAttempts_ClientOperationId" ON public."ZReadingPrintAttempts"("ClientOperationId");
CREATE INDEX IF NOT EXISTS "IX_ZReadingPrintAttempts_ZReadingId" ON public."ZReadingPrintAttempts"("ZReadingId");
CREATE TABLE IF NOT EXISTS public."ZReadingPrintResults" (
    "AttemptId" uuid PRIMARY KEY REFERENCES public."ZReadingPrintAttempts"("Id") ON DELETE RESTRICT,
    "Status" varchar(20) NOT NULL CHECK ("Status" IN ('ACCEPTED', 'FAILED', 'UNKNOWN')),
    "ErrorMessage" varchar(2000) NULL,
    "CompletedAt" timestamp without time zone NOT NULL,
    "CompletedBy" varchar(450) NOT NULL
);

-- No extension required: PostgreSQL core sha256(bytea), exact JSON text retained.
CREATE OR REPLACE FUNCTION public.pos_z_validate_snapshot() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE s public."ZReadingTerminalStates"%ROWTYPE; previous_date date; previous_z bigint;
BEGIN
    PERFORM 1 FROM public."Terminals" WHERE "TerminalId" = NEW."TerminalId" FOR UPDATE;
    SELECT * INTO STRICT s FROM public."ZReadingTerminalStates" WHERE "TerminalId" = NEW."TerminalId" FOR UPDATE;
    SELECT "BusinessDate", "ZNumber" INTO previous_date, previous_z FROM public."ZReadings"
        WHERE "TerminalId" = NEW."TerminalId" ORDER BY "BusinessDate" DESC LIMIT 1;
    IF NEW."BusinessDate" <> COALESCE(previous_date + 1, s."FirstBusinessDate") OR NEW."ZNumber" <> COALESCE(previous_z + 1, 1) THEN
        RAISE EXCEPTION 'Z-Reading must follow sequential business date and internal Z number' USING ERRCODE = '23514';
    END IF;
    IF NEW."SnapshotSha256" <> upper(encode(sha256(convert_to(NEW."SnapshotJson", 'UTF8')), 'hex')) THEN
        RAISE EXCEPTION 'Z-Reading SHA-256 mismatch' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public."Terminals" WHERE "TerminalId"=NEW."TerminalId" AND "BranchId"=NEW."BranchId" AND "OutletId"=NEW."OutletId") THEN
        RAISE EXCEPTION 'Z-Reading terminal ownership mismatch' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE OR REPLACE FUNCTION public.pos_z_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Z-Reading snapshots and print audit entries are immutable' USING ERRCODE = '23514'; END $$;
CREATE OR REPLACE FUNCTION public.pos_z_preserve_setup() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'Z-Reading setup deletion/truncation is prohibited' USING ERRCODE = '23514';
    END IF;
    IF (NEW."TerminalId", NEW."FirstBusinessDate", NEW."OpeningAccumulatedSales", NEW."ResetCounter", NEW."ConfiguredBy", NEW."ConfiguredAt")
        IS DISTINCT FROM (OLD."TerminalId", OLD."FirstBusinessDate", OLD."OpeningAccumulatedSales", OLD."ResetCounter", OLD."ConfiguredBy", OLD."ConfiguredAt") THEN
        RAISE EXCEPTION 'Provisioned Z-Reading baseline is immutable; use a reviewed correction procedure, never reset' USING ERRCODE = '23514';
    END IF;
    IF NEW."LastZNumber" < OLD."LastZNumber" OR NEW."AccumulatedGrossSales" < OLD."AccumulatedGrossSales" OR NEW."MutationVersion" < OLD."MutationVersion" THEN
        RAISE EXCEPTION 'Z-Reading cumulative state cannot be rewound' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['ZReadings','ZReadingPrintAttempts','ZReadingPrintResults'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = format('public.%I', t)::regclass AND tgname = 'pos_z_immutable_row') THEN
            EXECUTE format('CREATE TRIGGER pos_z_immutable_row BEFORE UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.pos_z_immutable()', t);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = format('public.%I', t)::regclass AND tgname = 'pos_z_immutable_truncate') THEN
            EXECUTE format('CREATE TRIGGER pos_z_immutable_truncate BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.pos_z_immutable()', t);
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid='public."ZReadings"'::regclass AND tgname='pos_z_validate_snapshot') THEN
        CREATE TRIGGER pos_z_validate_snapshot BEFORE INSERT ON public."ZReadings" FOR EACH ROW EXECUTE FUNCTION public.pos_z_validate_snapshot();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid='public."ZReadingTerminalStates"'::regclass AND tgname='pos_z_preserve_setup') THEN
        CREATE TRIGGER pos_z_preserve_setup BEFORE UPDATE OR DELETE ON public."ZReadingTerminalStates" FOR EACH ROW EXECUTE FUNCTION public.pos_z_preserve_setup();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid='public."ZReadingTerminalStates"'::regclass AND tgname='pos_z_preserve_setup_truncate') THEN
        CREATE TRIGGER pos_z_preserve_setup_truncate BEFORE TRUNCATE ON public."ZReadingTerminalStates" FOR EACH STATEMENT EXECUTE FUNCTION public.pos_z_preserve_setup();
    END IF;
END $$;
COMMIT;
-- REQUIRED NEXT: z-reading-period-guards.sql. No terminal state rows are inserted here.
