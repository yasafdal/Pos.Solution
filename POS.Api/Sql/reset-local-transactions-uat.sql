-- =============================================================================
-- POS.Solution — UAT / NON-PRODUCTION Local Transaction Reset Script (SQLite)
-- =============================================================================
-- Target DB (Windows): %LOCALAPPDATA%\PosSolution\local_pos.db
-- IMPORTANT: Close POS.Client completely BEFORE running this script.
--
-- WHAT THIS RESETS (12 transactional & sync tables):
--   LocalOrderItems, LocalPayments, LocalOrders, LocalHeldSales,
--   LocalTransactionVoids, LocalCashDrawerMovements, LocalCashDrawerShifts,
--   LocalCashierClosingRestrictions, LocalCashDrawerHardwareEvents,
--   LocalReceiptPrintJobs, LocalSyncOutboxes, LocalSyncAuditLogs
--   + resets LocalOrderSequences, LocalInvoiceSequences, and
--     LocalTerminalConfigs closing-lock columns.
--
-- WHAT THIS PRESERVES (17 master / configuration / activation tables):
--   LocalBranches, LocalTerminalConfigs (activation & settings), LocalCashiers,
--   LocalCategories, LocalProducts, LocalModifierGroups, LocalModifierOptions,
--   LocalProductModifierGroups, LocalTaxes, LocalDiscounts,
--   LocalPaymentMethods, LocalCashMovementCategories, LocalTerminalLicenses,
--   LocalPrinterHardwareConfigs, LocalOrderSequences (config),
--   LocalInvoiceSequences (config), LocalSchemaMigrations.
-- =============================================================================

PRAGMA foreign_keys = OFF;

-- 1. Delete all local transactional, shift, hardware-event, and sync records
DELETE FROM LocalOrderItems;
DELETE FROM LocalPayments;
DELETE FROM LocalOrders;
DELETE FROM LocalHeldSales;
DELETE FROM LocalTransactionVoids;
DELETE FROM LocalCashDrawerMovements;
DELETE FROM LocalCashDrawerShifts;
DELETE FROM LocalCashierClosingRestrictions;
DELETE FROM LocalCashDrawerHardwareEvents;
DELETE FROM LocalReceiptPrintJobs;
DELETE FROM LocalSyncOutboxes;
DELETE FROM LocalSyncAuditLogs;

-- 2. Reset local order and BIR invoice sequence counters
UPDATE LocalOrderSequences
SET LastIssuedNumber = 0;

UPDATE LocalInvoiceSequences
SET LastIssuedNumber = CASE
    WHEN StartingNumber > 0 THEN StartingNumber - 1
    ELSE 0
END;

-- 3. Clear runtime closing lock state (preserves terminal activation & config)
UPDATE LocalTerminalConfigs
SET ClosedBusinessDate      = NULL,
    ClosingLockedByCashier  = NULL,
    ClosingLockedAtUtc      = NULL,
    ClosingUnlockAtUtc      = NULL,
    ClosingLockedTickCount64 = NULL;

-- 4. Reset SQLite autoincrement counters for cleared tables (if sqlite_sequence exists)
DELETE FROM sqlite_sequence
WHERE name IN (
    'LocalOrderItems',
    'LocalPayments',
    'LocalCashDrawerMovements',
    'LocalCashDrawerHardwareEvents',
    'LocalReceiptPrintJobs',
    'LocalSyncOutboxes',
    'LocalSyncAuditLogs'
);

PRAGMA foreign_keys = ON;

-- Optional (run separately after clicking "Write Changes" in DB Browser for SQLite):
-- VACUUM;

-- Verification check
SELECT
    (SELECT COUNT(*) FROM LocalProducts)        AS products_kept,
    (SELECT COUNT(*) FROM LocalCategories)      AS categories_kept,
    (SELECT COUNT(*) FROM LocalCashiers)        AS cashiers_kept,
    (SELECT COUNT(*) FROM LocalOrders)          AS orders_remaining,
    (SELECT COUNT(*) FROM LocalSyncOutboxes)    AS outbox_remaining,
    (SELECT COUNT(*) FROM LocalTransactionVoids) AS voids_remaining;
