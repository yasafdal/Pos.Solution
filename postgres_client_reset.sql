-- =============================================================================
-- SCRIPT: postgres_client_reset.sql
-- PURPOSE: Safe, transaction-bracketed cleanup and initialization script for
--          preparing a new client PostgreSQL database (pos_db).
-- TARGET ENVIRONMENT: New Client Deployment / Staging / Development
--
-- SAFETY INSTRUCTIONS:
-- 1. Take a full database backup before running:
--    pg_dump -h <host> -U <user> -d <database> -F c -b -v -f pre_reset_backup.dump
-- 2. By default, this script runs in DRY-RUN mode (ends with ROLLBACK).
-- 3. To apply changes permanently, review the dry-run output, verify row counts,
--    and replace the final ROLLBACK with COMMIT at the very bottom.
-- 4. Never execute this script against an active production database of Client 1.
-- =============================================================================

ROLLBACK; -- Clear any aborted interactive transaction state

BEGIN;

-- Strict session safety settings
SET LOCAL lock_timeout = '10s';
SET LOCAL statement_timeout = '120s';
SET LOCAL row_security = off;

-- =============================================================================
-- STEP 1: SAFETY GUARDS & OPERATOR CONFIRMATION
-- =============================================================================
DO $guard$
DECLARE
    v_db text := current_database();
    v_user text := current_user;
BEGIN
    RAISE NOTICE '=============================================================================';
    RAISE NOTICE 'POS.SOLUTION — NEW CLIENT DATABASE INITIALIZATION SCRIPT';
    RAISE NOTICE 'Connected Database : %', v_db;
    RAISE NOTICE 'Connected User     : %', v_user;
    RAISE NOTICE 'Timestamp (UTC)    : %', CURRENT_TIMESTAMP;
    RAISE NOTICE '=============================================================================';

    -- Guard against accidental execution on known critical production names
    IF v_db ~* '(^prod_|_prod$|production|master_pos)' THEN
        RAISE EXCEPTION 'SAFETY HALT: Execution refused on protected database name "%". If this is genuinely a new client database, rename it or adjust this guard.', v_db;
    END IF;
END $guard$;

-- =============================================================================
-- STEP 2: PRE-CLEANUP INVENTORY AND ROW COUNT PREVIEW (DRY-RUN INSPECTION)
-- =============================================================================
DO $preview$
DECLARE
    rec record;
    v_count bigint;
    v_tables text[] := ARRAY[
        -- Category A: Transactional Data
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
        'TerminalRecoveryLogs',
        'ReceiptReprintLogs',
        'DiscountAuditLogs',
        'SalesReturnItems',
        'SalesReturns',
        'Refunds',
        'TransactionVoids',
        'HeldSaleItemModifiers',
        'HeldSaleItems',
        'HeldSaleAuditLogs',
        'HeldSales',
        'OrderItemModifiers',
        'OrderItems',
        'Payments',
        'Orders',
        'CashDrawerMovements',
        'CashDrawerShifts',
        'CashierSwitchLogs',
        'CashierSessions',
        'OfflineSyncIdempotency',
        'StockAdjustments',
        -- Category B: Product & Inventory Catalog
        'BranchInventory',
        'ProductRecipes',
        'ProductModifierGroup',
        'ModifierOption',
        'ModifierGroup',
        'Products',
        'Categories',
        -- Category C: Terminal Sequences
        'InvoiceSequences',
        'OrderSequences'
    ];
    t text;
BEGIN
    RAISE NOTICE '-----------------------------------------------------------------------------';
    RAISE NOTICE 'PRE-CLEANUP ROW COUNT PREVIEW:';
    RAISE NOTICE '-----------------------------------------------------------------------------';

    FOREACH t IN ARRAY v_tables LOOP
        IF to_regclass(format('public.%I', t)) IS NOT NULL THEN
            EXECUTE format('SELECT count(*) FROM public.%I', t) INTO v_count;
            RAISE NOTICE '  TABLE: %-32 | ROWS: %', t, v_count;
        ELSE
            RAISE NOTICE '  TABLE: %-32 | NOT FOUND (Skipped)', t;
        END IF;
    END LOOP;
    RAISE NOTICE '-----------------------------------------------------------------------------';
END $preview$;

-- =============================================================================
-- STEP 3: TEMPORARILY SUSPEND USER TRIGGERS
-- Necessary to allow clean truncation of fiscal audit tables (Z-Readings, period guards)
-- =============================================================================
DO $disable_triggers$
DECLARE
    tbl text;
    v_trigger_tables text[] := ARRAY[
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
        'Orders',
        'OrderItems',
        'OrderItemModifiers',
        'Payments',
        'CashDrawerShifts',
        'CashDrawerMovements',
        'SalesReturns'
    ];
BEGIN
    FOREACH tbl IN ARRAY v_trigger_tables LOOP
        IF to_regclass(format('public.%I', tbl)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER USER;', tbl);
        END IF;
    END LOOP;
    RAISE NOTICE 'User triggers temporarily disabled for table reset.';
END $disable_triggers$;

-- =============================================================================
-- STEP 4: TRUNCATE TRANSACTIONAL AND CATALOG TABLES (CHILD-FIRST ORDER)
-- =============================================================================
-- PostgreSQL TRUNCATE ... CASCADE / RESTART IDENTITY resets sequences owned by these tables.
TRUNCATE TABLE
    -- Category A: Transactions, Voids, Returns, Held Sales, Logs
    public."ZReadingPrintResults",
    public."ZReadingPrintAttempts",
    public."ZReadings",
    public."ZReadingTerminalStates",
    public."TerminalRecoveryLogs",
    public."ReceiptReprintLogs",
    public."DiscountAuditLogs",
    public."Refunds",
    public."SalesReturnItems",
    public."SalesReturns",
    public."TransactionVoids",
    public."HeldSaleItemModifiers",
    public."HeldSaleItems",
    public."HeldSaleAuditLogs",
    public."HeldSales",
    public."OrderItemModifiers",
    public."OrderItems",
    public."Payments",
    public."Orders",
    public."CashDrawerMovements",
    public."CashDrawerShifts",
    public."CashierSwitchLogs",
    public."CashierSessions",
    public."OfflineSyncIdempotency",
    public."StockAdjustments",

    -- Category B: Product Catalog, Recipes, Modifiers, Inventory Balances
    public."BranchInventory",
    public."ProductRecipes",
    public."ProductModifierGroup",
    public."ModifierOption",
    public."ModifierGroup",
    public."Products",
    public."Categories",

    -- Category C: Numbering Sequences (Specific to previous client's POS terminals)
    public."InvoiceSequences",
    public."OrderSequences"
RESTART IDENTITY CASCADE;

-- =============================================================================
-- STEP 5: RESET CLIENT INFRASTRUCTURE (TERMINALS, OUTLETS, USERS)
-- =============================================================================
-- Ensure terminal columns permit NULL for fresh un-paired state
ALTER TABLE public."Terminals" ALTER COLUMN "Uuid" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "ActivationCode" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "OpeningFundOverride" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "ClosedBusinessDate" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "ClosingLockedByCashier" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "ClosingLockedAtUtc" DROP NOT NULL;
ALTER TABLE public."Terminals" ALTER COLUMN "ClosingUnlockAtUtc" DROP NOT NULL;
ALTER TABLE public."Outlets" ALTER COLUMN "DefaultOpeningFund" DROP NOT NULL;

-- Clear terminal hardware UUID bindings, activation locks, and closing state
UPDATE public."Terminals"
SET "Uuid" = NULL,
    "ActivationCode" = NULL,
    "IsActivationCodeUsed" = FALSE,
    "AllowSync" = TRUE,
    "OpeningFundOverride" = NULL,
    "CashFundUpdatedBy" = NULL,
    "CashFundUpdatedAt" = NULL,
    "ClosedBusinessDate" = NULL,
    "ClosingLockedByCashier" = NULL,
    "ClosingLockedAtUtc" = NULL,
    "ClosingUnlockAtUtc" = NULL,
    "UpdatedAt" = CURRENT_TIMESTAMP;

-- Reset outlet cash fund defaults
UPDATE public."Outlets"
SET "DefaultOpeningFund" = NULL,
    "CashFundUpdatedBy" = NULL,
    "CashFundUpdatedAt" = NULL,
    "UpdatedAt" = CURRENT_TIMESTAMP;

-- Clear previous cashier/staff accounts while strictly preserving Admin & Manager roles
DELETE FROM public."Users"
WHERE "Role" NOT IN ('Admin', 'Manager');

-- =============================================================================
-- STEP 6: RE-ENABLE USER TRIGGERS
-- Restore all immutable and period guard protections
-- =============================================================================
DO $enable_triggers$
DECLARE
    tbl text;
    v_trigger_tables text[] := ARRAY[
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
        'Orders',
        'OrderItems',
        'OrderItemModifiers',
        'Payments',
        'CashDrawerShifts',
        'CashDrawerMovements',
        'SalesReturns'
    ];
BEGIN
    FOREACH tbl IN ARRAY v_trigger_tables LOOP
        IF to_regclass(format('public.%I', tbl)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE public.%I ENABLE TRIGGER USER;', tbl);
        END IF;
    END LOOP;
    RAISE NOTICE 'User triggers successfully re-enabled.';
END $enable_triggers$;

-- =============================================================================
-- STEP 7: POST-CLEANUP VALIDATION & INTEGRITY VERIFICATION
-- =============================================================================
DO $validate$
DECLARE
    v_count bigint;
    v_failed boolean := false;
    v_cleared_tables text[] := ARRAY[
        'Orders', 'OrderItems', 'Payments', 'CashDrawerShifts', 'CashDrawerMovements',
        'HeldSales', 'SalesReturns', 'TransactionVoids', 'Refunds', 'ReceiptReprintLogs',
        'DiscountAuditLogs', 'ZReadings', 'ZReadingTerminalStates', 'OfflineSyncIdempotency',
        'StockAdjustments', 'BranchInventory', 'Products', 'Categories', 'ModifierGroup',
        'InvoiceSequences', 'OrderSequences'
    ];
    v_preserved_tables text[] := ARRAY[
        'PaymentMethods', 'Taxes', 'DiscountTypes', 'BusinessType', 'ServiceMode',
        'CashMovementCategories', 'UnitOfMeasures', 'Users'
    ];
    t text;
BEGIN
    RAISE NOTICE '-----------------------------------------------------------------------------';
    RAISE NOTICE 'POST-CLEANUP VERIFICATION:';
    RAISE NOTICE '-----------------------------------------------------------------------------';

    -- 1. Verify Cleared Tables have 0 rows
    FOREACH t IN ARRAY v_cleared_tables LOOP
        IF to_regclass(format('public.%I', t)) IS NOT NULL THEN
            EXECUTE format('SELECT count(*) FROM public.%I', t) INTO v_count;
            IF v_count <> 0 THEN
                RAISE WARNING 'VALIDATION FAILURE: Table % still contains % rows!', t, v_count;
                v_failed := true;
            ELSE
                RAISE NOTICE '  CLEARED   : %-30 | OK (0 rows)', t;
            END IF;
        END IF;
    END LOOP;

    -- 2. Verify Preserved Tables have records
    FOREACH t IN ARRAY v_preserved_tables LOOP
        IF to_regclass(format('public.%I', t)) IS NOT NULL THEN
            EXECUTE format('SELECT count(*) FROM public.%I', t) INTO v_count;
            IF v_count = 0 THEN
                RAISE WARNING 'VALIDATION WARNING: Preserved Table % has 0 rows!', t;
            ELSE
                RAISE NOTICE '  PRESERVED : %-30 | OK (% rows)', t, v_count;
            END IF;
        END IF;
    END LOOP;

    -- 3. Verify Admin Account Exists
    SELECT count(*) INTO v_count FROM public."Users" WHERE "Role" = 'Admin';
    IF v_count = 0 THEN
        RAISE EXCEPTION 'FATAL: No Admin user found in Users table! Cleanup aborted to prevent lockout.';
    ELSE
        RAISE NOTICE '  SECURITY  : Admin account verified (% active Admin users).', v_count;
    END IF;

    IF v_failed THEN
        RAISE EXCEPTION 'Cleanup verification failed. Transaction will roll back.';
    END IF;

    RAISE NOTICE '=============================================================================';
    RAISE NOTICE 'VALIDATION PASSED: Database is successfully prepared for the new client.';
    RAISE NOTICE '=============================================================================';
END $validate$;

-- =============================================================================
-- FINAL EXECUTION SAFETY VALVE:
-- Default is ROLLBACK (Dry-run mode).
-- To apply changes permanently, replace "ROLLBACK;" with "COMMIT;".
-- =============================================================================
ROLLBACK;
-- COMMIT;
