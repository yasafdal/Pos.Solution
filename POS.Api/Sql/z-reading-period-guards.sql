-- MANUAL, additive PostgreSQL deployment AFTER z-reading.sql and application tables.
-- Review README-z-reading-period-guards.md. Default is a dry run: replace only the
-- final ROLLBACK with COMMIT after operator review. Never run at application startup.
ROLLBACK;
BEGIN;
SET LOCAL lock_timeout = '10s';

-- Use the same order as generation: terminal row first, then the real write fence.
-- A mere row/advisory lock does not refresh a SERIALIZABLE transaction's snapshot.
CREATE OR REPLACE FUNCTION public.pos_z_lock_terminal(p_terminal_id integer)
RETURNS void LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    PERFORM 1 FROM public."Terminals" WHERE "TerminalId" = p_terminal_id FOR UPDATE;
    UPDATE public."ZReadingTerminalStates"
       SET "MutationVersion" = "MutationVersion" + 1
     WHERE "TerminalId" = p_terminal_id;
END $$;

-- Resolve BOTH row images, including both sides of reparenting. Parent FOR SHARE
-- prevents a concurrent parent reparent/date change from invalidating this lookup.
-- Historical originals of RETURNS are references, not the adjustment's event time.
CREATE OR REPLACE FUNCTION public.pos_z_row_context(p_table text, r jsonb)
RETURNS TABLE(terminal_id integer, event_at timestamp without time zone)
LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
DECLARE p jsonb; q jsonb; times timestamp without time zone[]; tid integer;
BEGIN
    IF r IS NULL THEN RETURN; END IF;
    CASE p_table
    WHEN 'Orders' THEN
        tid := (r->>'TerminalId')::integer;
        times := ARRAY[(r->>'CreatedAt')::timestamp, (r->>'InvoiceIssuedAt')::timestamp];
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
        -- OpenedAt < the cutoff also rejects shifts overlapping the frozen window,
        -- and freezes subsequent recount/close/cash-total changes to historical shifts.
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
        -- Lock/fence the original terminal too, but never freeze a current return
        -- merely because its original invoice is in a finalized period.
        RETURN QUERY SELECT (p->>'TerminalId')::integer, t FROM unnest(times) t WHERE t IS NOT NULL;
        tid := (r->>'TerminalId')::integer;
    WHEN 'SalesReturnItems' THEN
        SELECT to_jsonb(s) INTO p FROM public."SalesReturns" s WHERE s."ReturnId" = (r->>'ReturnId')::bigint FOR SHARE;
        RETURN QUERY SELECT * FROM public.pos_z_row_context('SalesReturns', p);
        -- Item identity is a historical reference; the return supplies the date.
        SELECT to_jsonb(i) INTO q FROM public."OrderItems" i WHERE i."OrderItemId" = (r->>'OriginalOrderItemId')::bigint FOR SHARE;
        SELECT to_jsonb(o) INTO q FROM public."Orders" o WHERE o."OrderId" = (q->>'OrderId')::bigint FOR SHARE;
        tid := (q->>'TerminalId')::integer;
        times := ARRAY[(p->>'CreatedAt')::timestamp, (p->>'CompletedAt')::timestamp];
    WHEN 'TransactionVoids' THEN
        SELECT to_jsonb(o) INTO p FROM public."Orders" o WHERE o."OrderId" = (r->>'OriginalOrderId')::bigint FOR SHARE;
        -- Existing void workflow mutates the original order/payment. Do not allow
        -- a current void to rewrite a finalized sale; use a new current return.
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
    -- Installed only after the feature schema; no per-terminal state means no
    -- fiscal restriction or state mutation. Existing nonparticipating terminals work.
    IF TG_OP <> 'INSERT' THEN before_row := to_jsonb(OLD); END IF;
    IF TG_OP <> 'DELETE' THEN after_row := to_jsonb(NEW); END IF;
    SELECT jsonb_agg(to_jsonb(c)) INTO contexts FROM (
        SELECT * FROM public.pos_z_row_context(TG_TABLE_NAME, before_row)
        UNION SELECT * FROM public.pos_z_row_context(TG_TABLE_NAME, after_row)
    ) c;
    -- Canonical order for OLD and NEW terminals (including linked parents).
    FOR tid IN SELECT DISTINCT (x->>'terminal_id')::integer
               FROM jsonb_array_elements(contexts) x
               WHERE (x->>'terminal_id') IS NOT NULL ORDER BY 1 LOOP
        PERFORM public.pos_z_lock_terminal(tid);
        IF EXISTS (SELECT 1 FROM public."ZReadingTerminalStates" WHERE "TerminalId" = tid) THEN
            SELECT max("PeriodEndUtc") INTO cutoff FROM public."ZReadings" WHERE "TerminalId" = tid;
            -- Periods are [start,end): an event AT the cutoff belongs to next day.
            -- After first finalization freeze ALL earlier time, even pre-baseline.
            IF cutoff IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(contexts) x
                WHERE (x->>'terminal_id')::integer = tid AND (x->>'event_at')::timestamp < cutoff
            ) THEN
                RAISE EXCEPTION 'Z-Reading finalized period: % % terminal %, events before % are immutable',
                    TG_OP, TG_TABLE_NAME, tid, cutoff USING ERRCODE = '23514';
            END IF;
            -- Enforce reference integrity for configured terminals even where legacy
            -- schema relationships are logical rather than composite foreign keys.
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

-- Direct SQL snapshot insertion must participate in the same version fence too.
CREATE OR REPLACE FUNCTION public.pos_z_snapshot_fence()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    PERFORM public.pos_z_lock_terminal(NEW."TerminalId");
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION public.pos_z_guard_truncate()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    -- TRUNCATE bypasses row triggers. Serialize with in-flight finalizers, then
    -- reject globally once any report exists (TRUNCATE has no terminal predicate).
    PERFORM public.pos_z_lock_terminal("TerminalId") FROM public."ZReadingTerminalStates" ORDER BY "TerminalId";
    IF EXISTS (SELECT 1 FROM public."ZReadings") THEN
        RAISE EXCEPTION 'TRUNCATE of % prohibited after Z-Reading finalization', TG_TABLE_NAME USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;

DO $$ DECLARE t text; BEGIN
    -- Fail atomically if any required application source is absent; never enable
    -- a partially guarded deployment by silently skipping an absent table.
    FOREACH t IN ARRAY ARRAY['Orders','OrderItems','OrderItemModifiers','Payments',
        'CashDrawerShifts','CashDrawerMovements','SalesReturns','SalesReturnItems',
        'Refunds','TransactionVoids','HeldSales','HeldSaleItems','HeldSaleItemModifiers','HeldSaleAuditLogs'] LOOP
        EXECUTE format('CREATE OR REPLACE TRIGGER pos_z_fiscal_row BEFORE INSERT OR UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.pos_z_guard_fiscal_row()', t);
        EXECUTE format('CREATE OR REPLACE TRIGGER pos_z_fiscal_truncate BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.pos_z_guard_truncate()', t);
    END LOOP;
    CREATE OR REPLACE TRIGGER pos_z_snapshot_fence BEFORE INSERT ON public."ZReadings"
        FOR EACH ROW EXECUTE FUNCTION public.pos_z_snapshot_fence();
END $$;

-- Readiness proves every required trigger is enabled and bound to the EXACT
-- expected function OID and event mask. A marker/function name alone is not proof.
CREATE OR REPLACE FUNCTION public.pos_z_guards_ready()
RETURNS boolean LANGUAGE sql STABLE SET search_path = pg_catalog, public AS $$
    WITH fiscal(table_name) AS (VALUES
        ('Orders'),('OrderItems'),('OrderItemModifiers'),('Payments'),
        ('CashDrawerShifts'),('CashDrawerMovements'),('SalesReturns'),
        ('SalesReturnItems'),('Refunds'),('TransactionVoids'),('HeldSales'),
        ('HeldSaleItems'),('HeldSaleItemModifiers'),('HeldSaleAuditLogs')),
    required(table_name, trigger_name, function_name, event_mask) AS (
        SELECT table_name, 'pos_z_fiscal_row', 'public.pos_z_guard_fiscal_row()', 31 FROM fiscal
        UNION ALL SELECT table_name, 'pos_z_fiscal_truncate', 'public.pos_z_guard_truncate()', 34 FROM fiscal
        UNION ALL SELECT 'ZReadings', 'pos_z_snapshot_fence', 'public.pos_z_snapshot_fence()', 7
        UNION ALL SELECT 'ZReadings', 'pos_z_validate_snapshot', 'public.pos_z_validate_snapshot()', 7
        UNION ALL SELECT t, 'pos_z_immutable_row', 'public.pos_z_immutable()', 27
            FROM unnest(ARRAY['ZReadings','ZReadingPrintAttempts','ZReadingPrintResults']) t
        UNION ALL SELECT t, 'pos_z_immutable_truncate', 'public.pos_z_immutable()', 34
            FROM unnest(ARRAY['ZReadings','ZReadingPrintAttempts','ZReadingPrintResults']) t
        UNION ALL SELECT 'ZReadingTerminalStates', 'pos_z_preserve_setup', 'public.pos_z_preserve_setup()', 27
        UNION ALL SELECT 'ZReadingTerminalStates', 'pos_z_preserve_setup_truncate', 'public.pos_z_preserve_setup()', 34
    )
    SELECT to_regprocedure('public.pos_z_lock_terminal(integer)') IS NOT NULL
       AND NOT EXISTS (
           SELECT 1 FROM required r WHERE NOT EXISTS (
               SELECT 1 FROM pg_trigger t
               WHERE t.tgrelid = to_regclass(format('public.%I', r.table_name))
                 AND t.tgname = r.trigger_name AND NOT t.tgisinternal
                 AND t.tgenabled IN ('O','A') AND t.tgtype = r.event_mask
                 AND t.tgqual IS NULL AND t.tgnargs = 0 AND t.tgattr = ''::int2vector
                 AND t.tgfoid = to_regprocedure(r.function_name)
           )
       );
$$;

-- Inspect functions/triggers in this transaction before choosing a reviewed COMMIT.
ROLLBACK;
