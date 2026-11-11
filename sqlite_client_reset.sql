-- =============================================================================
-- SCRIPT: sqlite_client_reset.sql
-- PURPOSE: Resets a local POS client SQLite terminal database (local_pos.db)
--          when redeploying a physical terminal to a new client.
-- TARGET: SQLite database file (typically at %LOCALAPPDATA%\PosSolution\local_pos.db)
--
-- ALTERNATIVE RECOMMENDED PROCEDURE:
-- Rather than executing SQL manually on client machines, simply close the POS
-- application and delete the local database file:
--   del "%LOCALAPPDATA%\PosSolution\local_pos.db"
-- Upon restart, POS.Client will automatically recreate the database schema and
-- prompt the cashier for the Terminal Activation Code.
-- =============================================================================

BEGIN TRANSACTION;

-- 1. Clear Transactional & Cashier Shift Data
DELETE FROM LocalPayments;
DELETE FROM LocalOrderItems;
DELETE FROM LocalOrders;
DELETE FROM LocalHeldSales;
DELETE FROM LocalCashDrawerMovements;
DELETE FROM LocalCashDrawerShifts;
DELETE FROM LocalSyncOutboxes;
DELETE FROM LocalSyncAuditLogs;

-- 2. Clear Product Catalog & Pricing
DELETE FROM LocalProductModifierGroups;
DELETE FROM LocalModifierOptions;
DELETE FROM LocalModifierGroups;
DELETE FROM LocalProducts;
DELETE FROM LocalCategories;

-- 3. Clear Terminal Numbering Sequences
DELETE FROM LocalOrderSequences;
DELETE FROM LocalInvoiceSequences;

-- 4. Clear Cached Cashiers & Credentials
DELETE FROM LocalCashiers;

-- 5. Reset Terminal Configuration & Activation State
-- This un-pairs the device so POS.Client enters the activation setup screen
UPDATE LocalTerminalConfigs
SET TerminalId = 0,
    OutletId = 0,
    BranchId = 0,
    TerminalCode = '',
    TerminalName = '',
    IsInitialized = 0,
    IsActivated = 0,
    AllowSync = 1,
    DeviceIdentifier = '',
    PairingCode = NULL,
    ServerBaseUrl = '',
    MinNumber = NULL,
    PtuNumber = NULL,
    SerialNumber = NULL,
    ClosedBusinessDate = NULL,
    ClosingLockedByCashier = NULL,
    ClosingLockedAtUtc = NULL,
    ClosingUnlockAtUtc = NULL;

-- If no row exists or preferred fresh, delete the config row:
-- DELETE FROM LocalTerminalConfigs;

-- 6. Clear Cached Master Reference Data (will re-sync from server)
DELETE FROM LocalCashMovementCategories;
DELETE FROM LocalTaxes;
DELETE FROM LocalDiscounts;
DELETE FROM LocalPaymentMethods;

COMMIT;

-- Shrink file size and reclaim disk space
VACUUM;
