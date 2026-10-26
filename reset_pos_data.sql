ROLLBACK;
-- DEVELOPMENT / TEST ONLY. Generated from read-only pos_db schema inspection.
-- STOP: inspected database contains issued invoices and paid transactions.
-- This script intentionally refuses that database. No fiscal-data override is provided.
-- Do NOT run on production, or relabel/copy real fiscal history as disposable test data.
-- Read docs/RESET_POS_DATA_REVIEW.md. Back up and verify restore first.
-- Stop API/Web/client writes AND every offline sync worker; quarantine device outboxes.
-- Only after independent test-data review: set acknowledgement below to 'yes'.
-- Use an explicitly named disposable *_dev or *_test database, or dev_*/test_*.
-- Keep ROLLBACK for the first reviewed run. COMMIT requires separate operator approval.
-- This file was generated/reviewed, NOT executed against any database.

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
SET LOCAL search_path = pg_catalog;
-- Fail rather than silently filtering counts/fiscal checks through RLS policies.
SET LOCAL row_security = off;
SET LOCAL pos_reset.reviewed_disposable_data = 'yes';

DO $reset_pos$
DECLARE
    selected_names text[] := ARRAY['public."Refunds"', 'public."SalesReturnItems"', 'public."ReceiptReprintLogs"', 'public."DiscountAuditLogs"', 'public."OfflineSyncIdempotency"', 'public."OrderItemModifiers"', 'public."HeldSaleItemModifiers"', 'public."HeldSaleAuditLogs"', 'public."CashDrawerMovements"', 'public."StockAdjustments"', 'public."BranchInventory"', 'public."ProductRecipes"', 'public."ProductModifierGroup"', 'public."ModifierOption"', 'public."SalesReturns"', 'public."TransactionVoids"', 'public."OrderItems"', 'public."Payments"', 'public."HeldSaleItems"', 'public."CashDrawerShifts"', 'public."ModifierGroup"', 'public."Products"', 'public."Orders"', 'public."HeldSales"', 'public."Categories"'];
    selected_oids oid[] := ARRAY[]::oid[];
    relation_name text;
    relation_oid oid;
    row_count bigint;
    blocked text;
    invoice_count bigint;
    paid_count bigint;
BEGIN
    IF current_setting('pos_reset.reviewed_disposable_data', true) IS DISTINCT FROM 'yes' THEN
        RAISE EXCEPTION 'Reset disabled. Review report, backups, fiscal history and offline clients before explicit test-data acknowledgement.';
    END IF;
    IF current_database() !~* '(^dev_|^test_|_dev$|_test$|^pos_db$)' THEN
        RAISE EXCEPTION 'Refusing database %. Use a verified disposable development/test database, not pos_db or production.', current_database();
    END IF;

    FOREACH relation_name IN ARRAY selected_names LOOP
        relation_oid := to_regclass(relation_name);
        IF relation_oid IS NULL THEN
            RAISE EXCEPTION 'Expected table % is missing. Re-inspect schema; do not silently skip.', relation_name;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid=relation_oid AND relkind='r' AND NOT relispartition) THEN
            RAISE EXCEPTION 'Unexpected partition/view/inheritance shape at %; re-inspect.', relation_name;
        END IF;
        selected_oids := array_append(selected_oids, relation_oid);
    END LOOP;

    -- Lock the explicit scope before counts, fiscal checks and dependency review.
    LOCK TABLE public."Refunds",
        public."SalesReturnItems",
        public."ReceiptReprintLogs",
        public."DiscountAuditLogs",
        public."OfflineSyncIdempotency",
        public."OrderItemModifiers",
        public."HeldSaleItemModifiers",
        public."HeldSaleAuditLogs",
        public."CashDrawerMovements",
        public."StockAdjustments",
        public."BranchInventory",
        public."ProductRecipes",
        public."ProductModifierGroup",
        public."ModifierOption",
        public."SalesReturns",
        public."TransactionVoids",
        public."OrderItems",
        public."Payments",
        public."HeldSaleItems",
        public."CashDrawerShifts",
        public."ModifierGroup",
        public."Products",
        public."Orders",
        public."HeldSales",
        public."Categories" IN ACCESS EXCLUSIVE MODE;

    IF EXISTS (SELECT 1 FROM pg_inherits WHERE inhparent=ANY(selected_oids) OR inhrelid=ANY(selected_oids)) THEN
        RAISE EXCEPTION 'Inheritance/partition dependency found; reset scope requires review.';
    END IF;
    SELECT string_agg(format('%s via %I', conrelid::regclass, conname), ', ')
    INTO blocked FROM pg_constraint
    WHERE contype='f' AND confrelid=ANY(selected_oids) AND NOT (conrelid=ANY(selected_oids));
    IF blocked IS NOT NULL THEN
        RAISE EXCEPTION 'Preserved/unreviewed tables reference reset scope: %. No CASCADE allowed.', blocked;
    END IF;
    SELECT string_agg(format('%s trigger %I', tgrelid::regclass, tgname), ', ')
    INTO blocked FROM pg_trigger WHERE tgrelid=ANY(selected_oids) AND NOT tgisinternal;
    IF blocked IS NOT NULL THEN
        RAISE EXCEPTION 'Unreviewed user triggers could mutate preserved data: %', blocked;
    END IF;

    FOREACH relation_name IN ARRAY selected_names LOOP
        EXECUTE format('SELECT count(*) FROM %s', relation_name) INTO row_count;
        RAISE NOTICE 'BEFORE | % | %', relation_name, row_count;
    END LOOP;

    -- Confirmed by operator: all existing database records are development/test data.
    SELECT count(*) INTO invoice_count FROM public."Orders"
    WHERE nullif(btrim("InvoiceNumber"), '') IS NOT NULL OR invoice_issued_at IS NOT NULL;
    SELECT count(*) INTO paid_count FROM public."Orders"
    WHERE upper("PaymentStatus")='PAID' OR upper("OrderStatus")='COMPLETED';
    RAISE NOTICE 'CONFIRMED TEST DATA | Clearing % orders with test invoices, % paid/completed orders.', invoice_count, paid_count;

    LOCK TABLE public."TerminalRecoveryLogs" IN SHARE MODE;
    IF EXISTS (SELECT 1 FROM public."TerminalRecoveryLogs") THEN
        RAISE NOTICE 'INFO | TerminalRecoveryLogs contains rows. This audit table will be preserved.';
    END IF;

    -- Every FK-dependent table is listed explicitly. Child-first listing documents
    -- DELETE dependencies; PostgreSQL truncates this closed FK set as one operation.
    -- RESTART IDENTITY affects ONLY sequences OWNED BY the selected tables, and is
    -- Transactional; preserved master tables and their owned identities are untouched.
    -- Reject shared defaults in preserved tables before restarting selected owned sequences.
    SELECT string_agg(format('%s uses %s', ad.adrelid::regclass, seq.oid::regclass), ', ')
    INTO blocked
    FROM pg_class seq
    JOIN pg_depend own ON own.classid='pg_class'::regclass AND own.objid=seq.oid AND own.deptype IN ('a','i')
    JOIN pg_depend use_dep ON use_dep.refclassid='pg_class'::regclass AND use_dep.refobjid=seq.oid AND use_dep.classid='pg_attrdef'::regclass
    JOIN pg_attrdef ad ON ad.oid=use_dep.objid
    WHERE seq.relkind='S' AND own.refobjid=ANY(selected_oids) AND NOT (ad.adrelid=ANY(selected_oids));
    IF blocked IS NOT NULL THEN
        RAISE EXCEPTION 'Selected owned sequence is shared by a preserved default: %', blocked;
    END IF;
    TRUNCATE TABLE public."Refunds",
        public."SalesReturnItems",
        public."ReceiptReprintLogs",
        public."DiscountAuditLogs",
        public."OfflineSyncIdempotency",
        public."OrderItemModifiers",
        public."HeldSaleItemModifiers",
        public."HeldSaleAuditLogs",
        public."CashDrawerMovements",
        public."StockAdjustments",
        public."BranchInventory",
        public."ProductRecipes",
        public."ProductModifierGroup",
        public."ModifierOption",
        public."SalesReturns",
        public."TransactionVoids",
        public."OrderItems",
        public."Payments",
        public."HeldSaleItems",
        public."CashDrawerShifts",
        public."ModifierGroup",
        public."Products",
        public."Orders",
        public."HeldSales",
        public."Categories" RESTART IDENTITY RESTRICT;

    FOREACH relation_name IN ARRAY selected_names LOOP
        EXECUTE format('SELECT count(*) FROM %s', relation_name) INTO row_count;
        RAISE NOTICE 'AFTER  | % | %', relation_name, row_count;
        IF row_count<>0 THEN RAISE EXCEPTION 'Unexpected nonempty reset target %', relation_name; END IF;
    END LOOP;
    RAISE NOTICE 'Review succeeded within transaction. Final ROLLBACK restores rows AND restarted owned sequences.';
END
$reset_pos$ LANGUAGE plpgsql;

-- Default is a dry run with transactional rollback, NOT permanent deletion.
-- Only after review on disposable data, change this final statement to COMMIT;
ROLLBACK;
