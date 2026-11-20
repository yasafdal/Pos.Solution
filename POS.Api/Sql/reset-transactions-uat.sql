-- =============================================================================
-- POS.Solution — UAT / NON-PRODUCTION Transaction Reset Script (PostgreSQL)
-- =============================================================================
-- WARNING:
--   FOR UAT / DEV / STAGING TESTING ONLY.
--   NEVER RUN ON A LIVE BIR PRODUCTION DATABASE.
--
-- WHAT THIS RESETS (25 transactional & runtime tables):
--   - Sales: Orders, OrderItems, OrderItemModifiers, Payments, DiscountAuditLogs,
--            ReceiptReprintLogs, TransactionVoids, SalesReturns,
--            SalesReturnItems, Refunds
--   - Held Sales: HeldSales, HeldSaleItems, HeldSaleItemModifiers,
--                 HeldSaleAuditLogs
--   - Shifts & Sessions: CashDrawerShifts, CashDrawerMovements,
--                        CashierClosingRestrictions, CashierSessions,
--                        CashierSwitchLogs
--   - Z-Readings: ZReadings, ZReadingPrintAttempts, ZReadingPrintResults
--   - Sync / Recovery / Stock Ledger: OfflineSyncIdempotency,
--                                     TerminalRecoveryLogs, StockAdjustments
--   - Runtime counters/flags reset (rows preserved):
--       * Terminals: clears ClosedBusinessDate / ClosingLocked* columns
--       * OrderSequences: resets last_issued_number = 0
--       * InvoiceSequences: resets last_issued_number = GREATEST(starting_number - 1, 0)
--       * ZReadingTerminalStates: resets cumulative counters to OpeningAccumulatedSales
--
-- WHAT THIS PRESERVES (26 master / configuration / schema tables):
--   Branches, Outlets, Terminals (config & activation), Users, Categories,
--   Products, ProductRecipes, ModifierGroup, ModifierOption,
--   ProductModifierGroup, UnitOfMeasures, Taxes, DiscountTypes, PaymentMethods,
--   CashMovementCategories, BusinessType, ServiceMode, BranchInventory,
--   TerminalLicenses, LicenseRenewalHistories, LicenseDeviceHistories,
--   LicenseAuditLogs, OrderSequences (config), InvoiceSequences (config),
--   ZReadingTerminalStates (provisioned baseline), __EFMigrationsHistory.
--
-- IMPORTANT:
--   Close all POS.Client apps first, run this PostgreSQL script, then run
--   reset-local-transactions-uat.sql on each terminal's local_pos.db BEFORE
--   reopening POS.Client (otherwise old SQLite outbox items and sequence
--   high-water marks will re-sync back into PostgreSQL).
-- =============================================================================

BEGIN;

-- 1. Temporarily disable Z-Reading immutability / fiscal guards (if installed)
DO $$
DECLARE
    rec record;
BEGIN
    FOR rec IN
        SELECT c.relname AS tbl, t.tgname AS trg
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND NOT t.tgisinternal
          AND t.tgname IN (
              'pos_z_immutable_row',
              'pos_z_immutable_truncate',
              'pos_z_preserve_setup',
              'pos_z_preserve_setup_truncate',
              'pos_z_fiscal_row',
              'pos_z_fiscal_truncate'
          )
    LOOP
        EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER %I', rec.tbl, rec.trg);
    END LOOP;
END $$;

-- 2. Truncate all transactional & runtime tables (no CASCADE, so PostgreSQL
--    aborts automatically if any preserved master table has an unexpected FK)
TRUNCATE TABLE
    public."OrderItemModifiers",
    public."OrderItems",
    public."Payments",
    public."DiscountAuditLogs",
    public."ReceiptReprintLogs",
    public."Refunds",
    public."SalesReturnItems",
    public."SalesReturns",
    public."TransactionVoids",
    public."Orders",
    public."HeldSaleItemModifiers",
    public."HeldSaleItems",
    public."HeldSaleAuditLogs",
    public."HeldSales",
    public."CashDrawerMovements",
    public."CashierClosingRestrictions",
    public."CashDrawerShifts",
    public."CashierSessions",
    public."CashierSwitchLogs",
    public."ZReadingPrintResults",
    public."ZReadingPrintAttempts",
    public."ZReadings",
    public."OfflineSyncIdempotency",
    public."TerminalRecoveryLogs",
    public."StockAdjustments"
RESTART IDENTITY;

-- 3. Clear runtime terminal closing lock columns (preserves all terminal config)
UPDATE public."Terminals"
SET
    "ClosedBusinessDate"     = NULL,
    "ClosingLockedByCashier" = NULL,
    "ClosingLockedAtUtc"     = NULL,
    "ClosingUnlockAtUtc"     = NULL,
    "UpdatedAt"              = NOW() AT TIME ZONE 'UTC';

-- 4. Reset order and BIR invoice sequence counters (preserves allocated series)
UPDATE public."OrderSequences"
SET
    "last_issued_number" = 0,
    "updated_at"         = NOW() AT TIME ZONE 'UTC';

UPDATE public."InvoiceSequences"
SET
    "last_issued_number" = GREATEST("starting_number" - 1, 0),
    "updated_at"         = NOW() AT TIME ZONE 'UTC';

-- 5. Reset Z-Reading cumulative counters to provisioned baseline (preserves setup)
UPDATE public."ZReadingTerminalStates"
SET
    "AccumulatedGrossSales" = "OpeningAccumulatedSales",
    "MutationVersion"       = 0,
    "LastZNumber"           = 0,
    "ResetCounter"          = 0,
    "LastBusinessDate"      = NULL;

-- 6. Re-enable Z-Reading immutability / fiscal guards
DO $$
DECLARE
    rec record;
BEGIN
    FOR rec IN
        SELECT c.relname AS tbl, t.tgname AS trg
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND NOT t.tgisinternal
          AND t.tgname IN (
              'pos_z_immutable_row',
              'pos_z_immutable_truncate',
              'pos_z_preserve_setup',
              'pos_z_preserve_setup_truncate',
              'pos_z_fiscal_row',
              'pos_z_fiscal_truncate'
          )
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE TRIGGER %I', rec.tbl, rec.trg);
    END LOOP;
END $$;

COMMIT;

-- Verification check (read-only)
SELECT
    (SELECT COUNT(*) FROM public."Products")         AS products_kept,
    (SELECT COUNT(*) FROM public."Categories")       AS categories_kept,
    (SELECT COUNT(*) FROM public."Users")            AS users_kept,
    (SELECT COUNT(*) FROM public."Terminals")        AS terminals_kept,
    (SELECT COUNT(*) FROM public."Orders")           AS orders_remaining,
    (SELECT COUNT(*) FROM public."CashDrawerShifts") AS shifts_remaining,
    (SELECT COUNT(*) FROM public."ZReadings")        AS zreadings_remaining;
