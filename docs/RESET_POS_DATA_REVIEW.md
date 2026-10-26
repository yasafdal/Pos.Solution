# POS data reset review — development/test only

## Decision: current database is blocked

Read-only inspection of the configured PostgreSQL `pos_db` succeeded through the API-configured connection (client endpoint `127.0.0.1:15432`; database reported PostgreSQL 18.6, backend port 5432). Queries used `default_transaction_read_only=on` and a statement timeout. No reset, DDL, migration, sequence operation, or local SQLite write was executed.

Observed snapshot: **42 public tables, 46 foreign keys, no user-defined triggers**. Additional read-only catalog inspection found **41 owned sequences**, including **25 owned by selected reset tables**, no selected-owned sequence referenced by a preserved column default, and no enabled public-table row-level security. The script also sets `row_security=off` so a filtered execution role fails rather than hiding fiscal records, checks shared sequence defaults, and locks preserved recovery audit during its guard. Counts are a point-in-time observation, not a maintenance-window snapshot.

- `public."Orders"`: **114** rows; **82** have an invoice number, **82** have an issue timestamp; **89** have payment status PAID.
- `public."Payments"`: **93**; refunds **3**; returns **1**; voids **2**; receipt reprints **37**.
- `public."InvoiceSequences"`: one row; `last_issued_number = 14082`. This can include reserved offline blocks; it is not a count of issued invoices.
- All seven repository migration IDs, through `20261026035721_ConfirmedOpeningFloat`, appear in the database history.

**There is no reliable production/test marker. Issued invoices and paid sales must be treated as potentially real fiscal history. They, their dependent records, and shared catalog/inventory/shift context are outside the runnable reset scope pending explicit review.** The supplied script refuses the entire reset when fiscal evidence exists; it does not try to delete only the other orders or silently remove shared products. No fiscal override is supplied. Renaming/cloning this database does not make its fiscal contents disposable.

## Files and validation

- Root `reset_pos_data.sql`: candidate complete development-only scope, explicit transaction and final **ROLLBACK**.
- Table names and FK edges were taken from PostgreSQL catalogs and compared with `POS.Api/Models/PosDbContext.cs`, `.Extensions.cs`, `.Hierarchy.cs`, migrations and snapshot. Models do not replace the actual schema.
- Standalone PostgreSQL grammar parsing and PL/pgSQL parsing were performed without connecting the script to a database. A saved-schema check verifies the selected table names, closed FK set, child-first list and preserved tables. This is **not runtime execution validation**.
- No application configuration, business code, migration or database was changed for this reset task.

## Candidate clear scope

All names below are in schema **public**. Quoted case and singular/plural forms are significant. Counts are from the read-only inspection. Every row in the candidate scope would be cleared on an independently reviewed disposable database **only if safety checks pass**.

| Tables | Observed rows | Reason |
|---|---:|---|
| `"Orders"`, `"OrderItems"`, `"OrderItemModifiers"`, `"Payments"` | 114 / 190 / 70 / 93 | Orders, line/modifier financial snapshots, applied discounts/customer details, invoice number/issue snapshots and payments. Blocked on current data. |
| `"Refunds"`, `"SalesReturns"`, `"SalesReturnItems"`, `"TransactionVoids"` | 3 / 1 / 1 / 2 | Refund/return/void lifecycles and dependencies. Blocked on current data. |
| `"ReceiptReprintLogs"`, `"DiscountAuditLogs"` | 37 / 21 | Transaction-associated receipt and applied-discount audits, not discount definitions. |
| `"OfflineSyncIdempotency"` | 4 | Replay acknowledgements logically reference server order IDs and invoice numbers; no actual FK. Clearing them removes deduplication protection, so clients must be quarantined first. Fiscal evidence blocks current reset. |
| `"HeldSales"`, `"HeldSaleItems"`, `"HeldSaleItemModifiers"`, `"HeldSaleAuditLogs"` | 2 / 4 / 0 / 4 | Held carts and catalog references, including logical IDs and audit snapshots without all enforced FKs. |
| `"CashDrawerMovements"`, `"CashDrawerShifts"` | 3 / 4 | Drawer transactions and opening float/confirmation/lifecycle/balance state. Opening cash is not sales revenue. |
| `"StockAdjustments"`, `"BranchInventory"` | 6 / 21 | Product stock history and per-branch product balances. Rows removed, not branches or tax settings. |
| `"ProductRecipes"`, `"ProductModifierGroup"` | 14 / 10 | Product/raw-material relationships and modifier assignments. |
| `"ModifierOption"`, `"ModifierGroup"` | 8 / 3 | Catalog add-ons/options/groups. Shared options remain catalog data, not global noncatalog configuration. |
| `"Products"`, `"Categories"` | 21 / 6 | Entire product catalog including raw materials and category hierarchy. |

The **25-table candidate set** includes every actual FK child of its members. `TRUNCATE` lists all tables explicitly, in child-first order, with `RESTRICT`; there is no unrestricted CASCADE or disabled trigger/constraint. Truncation occurs as one closed-set operation, not sequential truncates that would fail on empty-but-referencing FK tables. Before/after counts are emitted as PostgreSQL NOTICE messages; save client output. Selected owned identities restart transactionally; `ROLLBACK` restores both rows and sequence restart. No `setval`/nontransactional counter rewrite is used.

## Preserved scope

| Tables / settings | Reason |
|---|---|
| `public."Users"` | User/security master including role information. No separate PostgreSQL roles/permissions application tables were found. PostgreSQL roles, grants and ownership are not changed. |
| `public."BusinessType"`, `public."Branches"`, `public."Outlets"`, `public."Terminals"` | Business/location/device configuration, fiscal registration fields and terminal ownership. |
| `public."Taxes"`, `public."DiscountTypes"` | Tax and discount definitions preserved, unlike applied transaction snapshots. |
| `public."PaymentMethods"`, `public."ServiceMode"`, `public."UnitOfMeasures"`, `public."CashMovementCategories"` | Noncatalog master/configuration definitions. Units of measure remain available for rebuilding products. |
| `public."InvoiceSequences"` | Mixed invoice series configuration, bounds, active status and `last_issued_number`. **Preserve all rows/values/identities**; no automatic counter/range/fiscal reset. |
| `public."OrderSequences"` | Mixed terminal numbering state; preserve counters and identities to avoid identifier reuse. |
| `public."CashierSessions"`, `public."CashierSwitchLogs"` | Login/switch security/audit state, not drawer shifts. Ownership/lifecycle cleanup is a separate review; do not delete security audit just because sales are reset. Require logouts and offline worker shutdown operationally. |
| `public."TerminalRecoveryLogs"` | Recovery audit, currently zero rows; logical entity identifiers have no FK. Nonempty recovery history blocks this script pending manual dependency review. |
| `public."__EFMigrationsHistory"` | Schema deployment history. No migration reset. |
| Printer settings / credentials / Web Identity / all other schemas | No printer settings table was found among the actual public tables. External/local settings are not touched. POS.Web's separate SQL Server Identity store is outside this PostgreSQL script. |

These are **17 preserved public tables**. No other schema is named by a data-changing statement.

## Mixed state and scope uncertainties

1. Invoice series and order counters remain unchanged, even if candidate tables are empty. Do not decrement invoice numbers to close gaps; reserved blocks and issued numbers require fiscal review. `BranchInventory` also mixes product assignment/reorder configuration (`BranchId`, `ProductId`, `ReorderLevel`) with `StockQuantity`/`LastUpdated`: removing the entire product catalog necessarily removes these product-dependent rows, including reorder levels. Export them before reset if needed. For a balances-only reset with products retained, preserve the rows and reorder levels; that is a different script and is not performed here.
2. The actual schema lacks several constraints implied by model/logical references, including `Orders.ShiftId`, product IDs on order/held-sale snapshots, `OfflineSyncIdempotency.ServerOrderId`, and `Orders.invoice_sequence_id` (EF config declares its invoice-sequence FK but the inspected database does not). Logical relationships were included manually; a pure FK-CASCADE script would miss them. This task does not repair schema drift.
3. Whether the existing sales/invoices are genuinely test data is **unresolved**. No amount/date/receipt-footer heuristic makes deletion safe. A separate explicitly reviewed fiscal deletion plan would be required; this script intentionally cannot clear those records.
4. Recovery/audit and login-session retention may be governed by operational requirements. Preserved rather than guessing. Table purposes were identified; retention authorization remains outside scope.
5. A new unknown FK child, user trigger, partition or inheritance dependency causes a refusal/restrict failure, not silent expansion. Stop all writes; do not treat this as an online reset tool.

## Offline SQLite and replay prevention — manual coordinated procedure

**Do not start by resetting PostgreSQL while clients still have queues.** Old `SHIFT_OPEN`, sales and `CASH_MOVEMENT` uploads can recreate deleted state; stale local catalog IDs can reference newly reused product IDs.

1. Stop all POS clients, API upload traffic and background sync workers. Isolate every Windows installation, Android device/emulator and other client connected to the environment. Sign out cashiers. Do not use a temporary network failure as the only protection: workers retry when connectivity returns.
2. Inventory and back up each device's SQLite database plus associated `-wal`/`-shm` files while stopped, or use a consistent SQLite backup. Preserve evidence before considering any local deletion. Never attach old queues to a newly reset server.
3. Inspect `LocalSyncOutboxes` for **all** statuses, not only PENDING: `RETRY_PENDING`, `UPLOADING`, `NEEDS_ATTENTION`, failed/conflict and previously SYNCED records can matter to recovery. Review `SHIFT_OPEN`, order and `CASH_MOVEMENT` payloads, `LocalOrders`, `LocalOrderItems`, `LocalPayments`, `LocalCashDrawerShifts`, `LocalCashDrawerMovements`, `LocalHeldSales` and `LocalSyncAuditLogs`. Unknown/unacknowledged commits require reconciliation, not blind discard or replay. Cash-movement synchronization can resolve a missing historical shift to the terminal's current open shift, so stale queues risk contaminating a newly opened drawer. Legacy installations may contain `LocalOutletConfigs`, upgraded in place to `LocalTerminalConfigs`; review those devices without rewriting preserved payload identities.
4. For real transactions, reconcile/export through the original environment and retain fiscal history. For a separately approved disposable test reset, archive the old local transaction/outbox records and retire them through a separately reviewed, transactional SQLite plan while preserving local configuration, security and reserved invoice-state records. **Do not delete the entire SQLite file as a shortcut.** This deliverable does **not** delete, rename or reset any local database. Selective table deletion needs its own SQLite dependency/transaction review.
5. Local Windows path is derived by `LocalPosDbContext`: `%LOCALAPPDATA%\PosSolution\local_pos.db` (normally `C:\Users\yaser\AppData\Local\PosSolution\local_pos.db`). Android uses its own sandbox value from `Environment.SpecialFolder.LocalApplicationData`; do not assume the Windows path or blindly wipe app data, which may include unrelated settings. Locate and back up each instance explicitly.
6. Local catalog caches: `LocalProducts`, `LocalCategories`, `LocalModifierGroups`, `LocalModifierOptions`, `LocalProductModifierGroups`. Refresh these from the target server before sales. Also refresh taxes/discounts/terminal configuration/cashiers/shift state rather than retaining stale snapshots.
7. `LocalInvoiceSequences` contains reserved offline numbering state. Preserve the existing reservation/counter as evidence; do not rewind it, reuse issued numbers or copy an old block to a different cache/device. Keep server invoice/order sequences unchanged. Any replacement reservation requires controlled operator review: reference synchronization can reserve up to 1000 numbers and advance the server counter, so it is not a read-only inspection or automatic queue cleanup. Review abandoned reservations as gaps, not numbers to reclaim.
8. Re-enable one clean client first; verify target environment, empty approved queues, new valid shift opening and nonreused invoice numbering. Then admit other independently cleaned/verified clients. Reconnecting an unreviewed old client remains a replay risk even if the server reset committed successfully.

## Operator instructions (not executed)

1. Resolve the fiscal-data finding first. **Do not use this file on the inspected pos_db.** Obtain a verified disposable development/test database and no real fiscal history; naming alone is not proof.
2. Stop writers/uploads and back up PostgreSQL using an explicitly selected service/connection, e.g. `pg_dump --format=custom --file=before-reset.dump --dbname=service=pos_dev`. Protect credentials and verify by restoring into a separate isolated database. Back up every client as above.
3. Review scope, actual constraints/triggers and external integrations. Set `pos_reset.reviewed_disposable_data` in the script to `yes` only after review. The database name guard accepts `dev_*`, `test_*`, `*_dev`, `*_test`. Fiscal-evidence checks remain mandatory regardless of the acknowledgement.
4. Execute manually with `psql -X -v ON_ERROR_STOP=1 --dbname=service=pos_dev --file=reset_pos_data.sql` **keeping the final ROLLBACK**. Review saved BEFORE/AFTER NOTICE messages and confirm preserved configuration/counters independently.
5. Only after a successful reviewed dry run and explicit operator authorization, change the **final** `ROLLBACK;` to `COMMIT;` and rerun in the same maintenance conditions. After any error issue `ROLLBACK`/close the session; do not continue an aborted transaction.
6. After commit, verify preserved counts/values, permissions and invoice counters, then perform the controlled client reinitialization above. Recover by coordinated verified backups, not by allowing stale outboxes to repopulate data.
