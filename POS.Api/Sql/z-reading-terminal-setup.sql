-- MANUAL terminal-specific setup template. Run only after schema + period guards deployment,
-- after reconciliation of ALL device outboxes and verification of the fiscal opening balance.
-- Intentionally fails closed until an operator supplies EVERY required variable below.
ROLLBACK; -- Clear an aborted interactive session; this script still fails until explicitly configured.
BEGIN;
DO $$
DECLARE
    terminal_id integer := NULL;              -- REQUIRED registered terminal identity
    first_business_date date := NULL;         -- REQUIRED first supported Z date (Manila 04:00)
    verified_opening_gross numeric(20,2) := NULL; -- REQUIRED independently verified lifetime gross before that date; explicit zero permitted only when evidenced
    verification_reference text := NULL;      -- REQUIRED operator + reconciliation evidence reference
    existing public."ZReadingTerminalStates"%ROWTYPE;
BEGIN
    IF terminal_id IS NULL OR first_business_date IS NULL OR verified_opening_gross IS NULL OR verified_opening_gross < 0
        OR verification_reference IS NULL OR length(btrim(verification_reference)) = 0 THEN
        RAISE EXCEPTION 'Supply reviewed terminal/date/opening gross/evidence; no fabricated historical baseline or reset counter is permitted';
    END IF;
    IF to_regprocedure('public.pos_z_lock_terminal(integer)') IS NULL THEN
        RAISE EXCEPTION 'Deploy z-reading-period-guards.sql before provisioning';
    END IF;
    PERFORM 1 FROM public."Terminals" WHERE "TerminalId" = terminal_id AND "IsActive" = true FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Active registered terminal not found'; END IF;
    SELECT * INTO existing FROM public."ZReadingTerminalStates" WHERE "TerminalId" = terminal_id;
    IF FOUND THEN
        IF existing."FirstBusinessDate" <> first_business_date OR existing."OpeningAccumulatedSales" <> verified_opening_gross OR existing."ConfiguredBy" <> verification_reference THEN
            RAISE EXCEPTION 'Existing provisioned baseline differs; refusing to overwrite fiscal state';
        END IF;
        RAISE NOTICE 'Matching terminal setup already exists; no counters or snapshots changed';
        RETURN;
    END IF;
    INSERT INTO public."ZReadingTerminalStates" ("TerminalId", "FirstBusinessDate", "OpeningAccumulatedSales", "AccumulatedGrossSales", "MutationVersion", "LastZNumber", "ResetCounter", "LastBusinessDate", "ConfiguredBy", "ConfiguredAt")
    VALUES (terminal_id, first_business_date, verified_opening_gross, verified_opening_gross, 0, 0, NULL, NULL, verification_reference, timezone('UTC', now()));
END $$;
COMMIT;
