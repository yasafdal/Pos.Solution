-- =============================================================================
-- ClearAllTransactions.sql
-- Clears all sales, payments, shifts, cash movements, voids, returns, held sales,
-- sync idempotency logs, and Z-Reading records in pos_db, and resets order/invoice
-- sequence counters back to 0 while preserving all master/reference data
-- (Branches, Outlets, Terminals, Users, Products, Categories, Modifiers, Taxes,
-- Discounts, PaymentMethods, CashMovementCategories).
-- =============================================================================

BEGIN;

-- 1. Temporarily disable user triggers (such as Z-Reading immutability & period guards)
DO $$
DECLARE
    tbl text;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
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
        'TerminalRecoveryLogs'
    ]
    LOOP
        IF to_regclass(format('public.%I', tbl)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER USER;', tbl);
        END IF;
    END LOOP;
END $$;

-- 2. Truncate all transaction and operational tables and reset their auto-increment IDs
DO $$
DECLARE
    tbl text;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
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
        'TerminalRecoveryLogs'
    ]
    LOOP
        IF to_regclass(format('public.%I', tbl)) IS NOT NULL THEN
            EXECUTE format('TRUNCATE TABLE public.%I RESTART IDENTITY CASCADE;', tbl);
        END IF;
    END LOOP;
END $$;

-- 3. Reset OrderSequences and InvoiceSequences counters back to start
UPDATE public."OrderSequences"
SET last_issued_number = 0,
    updated_at = CURRENT_TIMESTAMP;

UPDATE public."InvoiceSequences"
SET last_issued_number = starting_number - 1,
    updated_at = CURRENT_TIMESTAMP;

-- 4. Re-enable user triggers
DO $$
DECLARE
    tbl text;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'ZReadingPrintResults',
        'ZReadingPrintAttempts',
        'ZReadings',
        'ZReadingTerminalStates',
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
        'TerminalRecoveryLogs'
    ]
    LOOP
        IF to_regclass(format('public.%I', tbl)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE public.%I ENABLE TRIGGER USER;', tbl);
        END IF;
    END LOOP;
END $$;

COMMIT;
