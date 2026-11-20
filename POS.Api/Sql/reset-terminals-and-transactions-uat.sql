-- =============================================================================
-- POS.Solution — UAT / NON-PRODUCTION Full Terminal & Transaction Reset (PostgreSQL)
-- =============================================================================
-- WARNING:
--   FOR UAT / DEV / STAGING TESTING ONLY.
--   NEVER RUN ON A LIVE BIR PRODUCTION DATABASE.
--
-- WHAT THIS RESETS (33 tables):
--   - All Terminals & terminal-scoped configuration:
--       Terminals, OrderSequences, InvoiceSequences, ZReadingTerminalStates,
--       TerminalLicenses, LicenseRenewalHistories, LicenseDeviceHistories,
--       LicenseAuditLogs
--   - All Transactional & Runtime Data (required by TerminalId FKs):
--       Orders, OrderItems, OrderItemModifiers, Payments, DiscountAuditLogs,
--       ReceiptReprintLogs, TransactionVoids, SalesReturns, SalesReturnItems,
--       Refunds, HeldSales, HeldSaleItems, HeldSaleItemModifiers,
--       HeldSaleAuditLogs, CashDrawerShifts, CashDrawerMovements,
--       CashierClosingRestrictions, CashierSessions, CashierSwitchLogs,
--       ZReadings, ZReadingPrintAttempts, ZReadingPrintResults,
--       OfflineSyncIdempotency, TerminalRecoveryLogs, StockAdjustments
--
-- WHAT THIS PRESERVES (18 master / configuration / schema tables):
--   Branches, Outlets, Users, Categories, Products, ProductRecipes,
--   ModifierGroup, ModifierOption, ProductModifierGroup, UnitOfMeasures,
--   Taxes, DiscountTypes, PaymentMethods, CashMovementCategories,
--   BusinessType, ServiceMode, BranchInventory, __EFMigrationsHistory.
--
-- AFTER RUNNING THIS SCRIPT:
--   1. Close POS.Client on every terminal PC and delete its local SQLite DB:
--        %LOCALAPPDATA%\PosSolution\local_pos.db (plus -wal / -shm if present)
--   2. In POS.Web, recreate your Terminals, Invoice Series, and Licenses.
--   3. Open POS.Client and pair each terminal using its new Activation Code.
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

-- 2. Truncate all terminal, license, sequence, Z-Reading, and transaction tables
--    (no CASCADE, so PostgreSQL aborts if any preserved master table references them)
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
    public."ZReadingTerminalStates",
    public."OfflineSyncIdempotency",
    public."TerminalRecoveryLogs",
    public."StockAdjustments",
    public."LicenseAuditLogs",
    public."LicenseDeviceHistories",
    public."LicenseRenewalHistories",
    public."TerminalLicenses",
    public."OrderSequences",
    public."InvoiceSequences",
    public."Terminals"
RESTART IDENTITY;

-- 3. Re-enable Z-Reading immutability / fiscal guards
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
    (SELECT COUNT(*) FROM public."Branches")               AS branches_kept,
    (SELECT COUNT(*) FROM public."Outlets")                AS outlets_kept,
    (SELECT COUNT(*) FROM public."Products")               AS products_kept,
    (SELECT COUNT(*) FROM public."Categories")             AS categories_kept,
    (SELECT COUNT(*) FROM public."Users")                  AS users_kept,
    (SELECT COUNT(*) FROM public."Terminals")              AS terminals_remaining,
    (SELECT COUNT(*) FROM public."InvoiceSequences")       AS invoice_seq_remaining,
    (SELECT COUNT(*) FROM public."TerminalLicenses")       AS licenses_remaining,
    (SELECT COUNT(*) FROM public."ZReadingTerminalStates") AS z_states_remaining,
    (SELECT COUNT(*) FROM public."Orders")                 AS orders_remaining;
