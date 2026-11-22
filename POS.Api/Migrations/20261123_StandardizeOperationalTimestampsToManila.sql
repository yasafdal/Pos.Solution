-- =============================================================================
-- Migration: Standardize Operational POS Timestamps to Asia/Manila (UTC+08)
-- Target DB: PostgreSQL (POS.Api)
--
-- Purpose:
--   Aligns previously UTC-stored operational timestamp columns with
--   Orders.CreatedAt, Orders.invoice_issued_at, and Payments.PaidAt (already
--   stored in Asia/Manila wall-clock time), and updates the Z-Reading SQL guard
--   functions (pos_z_row_context / pos_z_guard_fiscal_row) so:
--     1. Orders uses actual column name "invoice_issued_at".
--     2. Cutoff compares PeriodEndUtc + interval '8 hours' (Manila 04:00 AM)
--        against all operational tables' Manila timestamps.
--
-- IMPORTANT:
--   - Run ONCE ONLY on databases that contain historical UTC rows from before
--     this deployment (running the +8h UPDATEs twice would double-shift rows).
--   - If you are resetting UAT transactions with reset-transactions-uat.sql or
--     reset-terminals-and-transactions-uat.sql, you only need Section 2 (SQL
--     guard functions) below.
--   - Existing immutable ZReadings rows (if any) cannot be updated while the
--     pos_z_immutable_row trigger is active; leave them or clear UAT first.
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- SECTION 1: Shift existing historical UTC operational rows to Asia/Manila (+8h)
-- (Skip this section if the database was just cleared/reset.)
-- -----------------------------------------------------------------------------

-- 1a. CashDrawerShifts (OpenedAt, ClosedAt were stored in UTC)
UPDATE public."CashDrawerShifts"
SET "OpenedAt" = "OpenedAt" + interval '8 hours',
    "ClosedAt" = CASE WHEN "ClosedAt" IS NOT NULL THEN "ClosedAt" + interval '8 hours' ELSE NULL END;

-- 1b. CashDrawerMovements: CASH_SALE rows created by OfflineSyncController were
--     already in Manila time; shift only non-CASH_SALE rows (or rows matching UTC shifts).
UPDATE public."CashDrawerMovements"
SET "CreatedAt"  = "CreatedAt" + interval '8 hours',
    "ApprovedAt" = CASE WHEN "ApprovedAt" IS NOT NULL THEN "ApprovedAt" + interval '8 hours' ELSE NULL END,
    "PostedAt"   = CASE WHEN "PostedAt"   IS NOT NULL THEN "PostedAt"   + interval '8 hours' ELSE NULL END,
    "ReversedAt" = CASE WHEN "ReversedAt" IS NOT NULL THEN "ReversedAt" + interval '8 hours' ELSE NULL END
WHERE "MovementType" <> 'CASH_SALE';

-- 1c. SalesReturns, TransactionVoids, Refunds (stored in UTC)
UPDATE public."SalesReturns"
SET "CreatedAt"   = "CreatedAt" + interval '8 hours',
    "CompletedAt" = CASE WHEN "CompletedAt" IS NOT NULL THEN "CompletedAt" + interval '8 hours' ELSE NULL END;

UPDATE public."TransactionVoids"
SET "RequestedAt" = "RequestedAt" + interval '8 hours',
    "ApprovedAt"  = CASE WHEN "ApprovedAt" IS NOT NULL THEN "ApprovedAt" + interval '8 hours' ELSE NULL END,
    "VoidedAt"    = "VoidedAt" + interval '8 hours';

UPDATE public."Refunds"
SET "RequestedAt" = "RequestedAt" + interval '8 hours',
    "CompletedAt" = CASE WHEN "CompletedAt" IS NOT NULL THEN "CompletedAt" + interval '8 hours' ELSE NULL END;

-- 1d. HeldSales & HeldSaleAuditLogs (stored in UTC)
UPDATE public."HeldSales"
SET "CreatedAt"   = "CreatedAt" + interval '8 hours',
    "ResumedAt"   = CASE WHEN "ResumedAt"   IS NOT NULL THEN "ResumedAt"   + interval '8 hours' ELSE NULL END,
    "CancelledAt" = CASE WHEN "CancelledAt" IS NOT NULL THEN "CancelledAt" + interval '8 hours' ELSE NULL END;

UPDATE public."HeldSaleAuditLogs"
SET "CreatedAt" = "CreatedAt" + interval '8 hours';

-- -----------------------------------------------------------------------------
-- SECTION 2: Update Z-Reading SQL guard functions (idempotent)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.pos_z_row_context(p_table text, r jsonb)
RETURNS TABLE(terminal_id integer, event_at timestamp without time zone)
LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
DECLARE p jsonb; q jsonb; times timestamp without time zone[]; tid integer;
BEGIN
    IF r IS NULL THEN RETURN; END IF;
    CASE p_table
    WHEN 'Orders' THEN
        tid := (r->>'TerminalId')::integer;
        times := ARRAY[(r->>'CreatedAt')::timestamp, (r->>'invoice_issued_at')::timestamp];
    WHEN 'OrderItems' THEN
        SELECT to_jsonb(o) INTO p FROM public."Orders" o WHERE o."OrderId" = (r->>'OrderId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('Orders', p); RETURN;
    WHEN 'OrderItemModifiers' THEN
        SELECT to_jsonb(i) INTO p FROM public."OrderItems" i WHERE i."OrderItemId" = (r->>'OrderItemId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('OrderItems', p); RETURN;
    WHEN 'Payments' THEN
        SELECT to_jsonb(o) INTO p FROM public."Orders" o WHERE o."OrderId" = (r->>'OrderId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('Orders', p);
        tid := (p->>'TerminalId')::integer; times := ARRAY[(r->>'PaidAt')::timestamp];
    WHEN 'CashDrawerShifts' THEN
        tid := (r->>'TerminalId')::integer;
        times := ARRAY[(r->>'OpenedAt')::timestamp, (r->>'ClosedAt')::timestamp];
    WHEN 'CashDrawerMovements' THEN
        SELECT to_jsonb(s) INTO p FROM public."CashDrawerShifts" s WHERE s."ShiftId" = (r->>'ShiftId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('CashDrawerShifts', p);
        times := ARRAY[(r->>'CreatedAt')::timestamp, (r->>'ApprovedAt')::timestamp,
                       (r->>'PostedAt')::timestamp, (r->>'ReversedAt')::timestamp];
        RETURN QUERY SELECT (p->>'TerminalId')::integer, t FROM unnest(times) t WHERE t IS NOT NULL;
        tid := (r->>'TerminalId')::integer;
    WHEN 'SalesReturns' THEN
        SELECT to_jsonb(o) INTO p FROM public."Orders" o WHERE o."OrderId" = (r->>'OriginalOrderId')::bigint FOR SHARE;
        times := ARRAY[(r->>'CreatedAt')::timestamp, (r->>'CompletedAt')::timestamp];
        RETURN QUERY SELECT (p->>'TerminalId')::integer, t FROM unnest(times) t WHERE t IS NOT NULL;
        tid := (r->>'TerminalId')::integer;
    WHEN 'SalesReturnItems' THEN
        SELECT to_jsonb(s) INTO p FROM public."SalesReturns" s WHERE s."ReturnId" = (r->>'ReturnId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('SalesReturns', p);
        SELECT to_jsonb(i) INTO q FROM public."OrderItems" i WHERE i."OrderItemId" = (r->>'OriginalOrderItemId')::bigint FOR SHARE;
        SELECT to_jsonb(o) INTO q FROM public."Orders" o WHERE o."OrderId" = (q->>'OrderId')::bigint FOR SHARE;
        tid := (q->>'TerminalId')::integer;
        times := ARRAY[(p->>'CreatedAt')::timestamp, (p->>'CompletedAt')::timestamp];
    WHEN 'TransactionVoids' THEN
        SELECT to_jsonb(o) INTO p FROM public."Orders" o WHERE o."OrderId" = (r->>'OriginalOrderId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('Orders', p);
        tid := (p->>'TerminalId')::integer;
        times := ARRAY[(r->>'RequestedAt')::timestamp, (r->>'ApprovedAt')::timestamp, (r->>'VoidedAt')::timestamp];
    WHEN 'Refunds' THEN
        SELECT to_jsonb(x) INTO p FROM public."Payments" x WHERE x."PaymentId" = (r->>'OriginalPaymentId')::bigint FOR SHARE;
        SELECT to_jsonb(o) INTO q FROM public."Orders" o WHERE o."OrderId" = (p->>'OrderId')::bigint FOR SHARE;
        tid := (q->>'TerminalId')::integer;
        times := ARRAY[(r->>'RequestedAt')::timestamp, (r->>'CompletedAt')::timestamp];
        IF (r->>'ReturnId') IS NOT NULL THEN
            SELECT to_jsonb(s) INTO p FROM public."SalesReturns" s WHERE s."ReturnId" = (r->>'ReturnId')::bigint FOR SHARE;
            RETURN QUERY SELECT * FROM public.pos_z_row_context('SalesReturns', p);
        END IF;
        IF (r->>'VoidId') IS NOT NULL THEN
            SELECT to_jsonb(v) INTO p FROM public."TransactionVoids" v WHERE v."VoidId" = (r->>'VoidId')::bigint FOR SHARE;
            RETURN QUERY SELECT * FROM public.pos_z_row_context('TransactionVoids', p);
        END IF;
    WHEN 'HeldSales' THEN
        tid := (r->>'TerminalId')::integer;
        times := ARRAY[(r->>'CreatedAt')::timestamp, (r->>'ResumedAt')::timestamp];
    WHEN 'HeldSaleItems' THEN
        SELECT to_jsonb(s) INTO p FROM public."HeldSales" s WHERE s."HeldSaleId" = (r->>'HeldSaleId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('HeldSales', p); RETURN;
    WHEN 'HeldSaleItemModifiers' THEN
        SELECT to_jsonb(i) INTO p FROM public."HeldSaleItems" i WHERE i."HeldSaleItemId" = (r->>'HeldSaleItemId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('HeldSaleItems', p); RETURN;
    WHEN 'HeldSaleAuditLogs' THEN
        SELECT to_jsonb(s) INTO p FROM public."HeldSales" s WHERE s."HeldSaleId" = (r->>'HeldSaleId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('HeldSales', p);
        tid := (r->>'TerminalId')::integer; times := ARRAY[(r->>'CreatedAt')::timestamp];
    ELSE
        RAISE EXCEPTION 'Unrecognized Z-Reading guard source %', p_table;
    END CASE;
    RETURN QUERY SELECT tid, t FROM unnest(times) t WHERE tid IS NOT NULL AND t IS NOT NULL;
END $$;

CREATE OR REPLACE FUNCTION public.pos_z_guard_fiscal_row()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
DECLARE before_row jsonb; after_row jsonb; contexts jsonb; tid integer;
        cutoff timestamp without time zone; r jsonb; original_id bigint; parent_id bigint;
BEGIN
    IF TG_OP <> 'INSERT' THEN before_row := to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN after_row := to_jsonb(NEW); END IF;
    SELECT jsonb_agg(to_jsonb(c)) INTO contexts FROM (
        SELECT * FROM public.pos_z_row_context(TG_TABLE_NAME, before_row)
        UNION SELECT * FROM public.pos_z_row_context(TG_TABLE_NAME, after_row)
    ) c;
    FOR tid IN SELECT DISTINCT (x->>'terminal_id')::integer
               FROM jsonb_array_elements(contexts) x
               WHERE (x->>'terminal_id') IS NOT NULL ORDER BY 1 LOOP
        PERFORM public.pos_z_lock_terminal(tid);
        IF EXISTS (SELECT 1 FROM public."ZReadingTerminalStates" WHERE "TerminalId" = tid) THEN
            -- Operational tables store Asia/Manila wall-clock time; PeriodEndUtc is UTC (Manila - 8h).
            SELECT max("PeriodEndUtc") + interval '8 hours' INTO cutoff FROM public."ZReadings" WHERE "TerminalId" = tid;
            IF cutoff IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(contexts) x
                WHERE (x->>'terminal_id')::integer = tid AND (x->>'event_at')::timestamp < cutoff
            ) THEN
                RAISE EXCEPTION 'Z-Reading finalized period: % % terminal %, events before % are immutable',
                    TG_OP, TG_TABLE_NAME, tid, cutoff USING ERRCODE = '23514';
            END IF;
            FOREACH r IN ARRAY ARRAY[before_row, after_row] LOOP
                IF r IS NULL THEN CONTINUE; END IF;
                IF TG_TABLE_NAME = 'SalesReturns' THEN
                    SELECT "TerminalId" INTO parent_id FROM public."Orders" WHERE "OrderId" = (r->>'OriginalOrderId')::bigint;
                    IF parent_id IS DISTINCT FROM (r->>'TerminalId')::bigint THEN
                        RAISE EXCEPTION 'Return terminal must match original sale' USING ERRCODE = '23514';
                    END IF;
                ELSIF TG_TABLE_NAME = 'SalesReturnItems' THEN
                    SELECT "OriginalOrderId" INTO original_id FROM public."SalesReturns" WHERE "ReturnId" = (r->>'ReturnId')::bigint;
                    SELECT "OrderId" INTO parent_id FROM public."OrderItems" WHERE "OrderItemId" = (r->>'OriginalOrderItemId')::bigint;
                    IF original_id IS DISTINCT FROM parent_id THEN
                        RAISE EXCEPTION 'Return item must belong to original sale' USING ERRCODE = '23514';
                    END IF;
                ELSIF TG_TABLE_NAME = 'Refunds' THEN
                    SELECT "OrderId" INTO original_id FROM public."Payments" WHERE "PaymentId" = (r->>'OriginalPaymentId')::bigint;
                    IF ((r->>'ReturnId') IS NULL) = ((r->>'VoidId') IS NULL) THEN
                        RAISE EXCEPTION 'Refund requires exactly one return or void event' USING ERRCODE = '23514';
                    END IF;
                    IF (r->>'ReturnId') IS NOT NULL THEN
                        SELECT "OriginalOrderId" INTO parent_id FROM public."SalesReturns" WHERE "ReturnId" = (r->>'ReturnId')::bigint;
                    ELSE
                        SELECT "OriginalOrderId" INTO parent_id FROM public."TransactionVoids" WHERE "VoidId" = (r->>'VoidId')::bigint;
                    END IF;
                    IF parent_id IS NULL OR original_id IS DISTINCT FROM parent_id THEN
                        RAISE EXCEPTION 'Refund payment must belong to adjustment original sale' USING ERRCODE = '23514';
                    END IF;
                END IF;
            END LOOP;
        END IF;
    END LOOP;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;

COMMIT;
