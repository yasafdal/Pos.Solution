# POS.Client Offline Mode Audit & Gap Analysis

## Executive Summary

`POS.Client` is a .NET MAUI Blazor Hybrid point-of-sale application designed for retail and food & beverage operations on Windows cashier terminals and Android mobile tablets. The local persistence engine is **SQLite** (via Entity Framework Core 10 in WAL journaling mode with a 5000ms busy timeout). POS.Client connects directly to SQLite and reaches the central **PostgreSQL** database exclusively through `POS.Api`.

This audit evaluates the architectural gap between the legacy offline implementation and an enterprise-grade, local-first commercial POS system.

---

## 1. What Currently Works

1. **Local SQLite Storage & Schema**:
   - 20 mapped SQLite tables in `LocalPosDbContext.cs` covering catalog, taxes, discounts, payment methods, terminal configuration, invoice sequences, orders, line items, payments, outboxes, audit logs, held sales, cashiers, shifts, and cash movements.
   - WAL journaling mode (`PRAGMA journal_mode=WAL;`), normal synchronous mode (`PRAGMA synchronous=NORMAL;`), and `PRAGMA foreign_keys = OFF;` to prevent write stalls and deadlock collisions during reference bulk loading.
   - Database integrity validation through direct ADO.NET scalar execution of `PRAGMA integrity_check;`.

2. **Master Reference Data Download**:
   - `ReferenceDataSyncService.SyncReferenceDataAsync` pulls complete catalog snapshots from `POST api/offline/reference-data?terminalId={id}` inside an atomic transaction.
   - Device-local physical printer selections (`ReceiptPrinterName`, `KitchenPrinterName`, `BarPrinterName`) and menu display preferences (`MenuDisplayMode`) are preserved across reference refreshes.

3. **Offline Cash Checkout**:
   - Atomic checkout in `OfflineCheckoutService`: calculates VAT, statutory discounts, increments the pre-allocated local invoice sequence, writes `LocalOrder`, `LocalOrderItem`, `LocalPayment`, updates local shift cash balance, and enqueues the transaction in `LocalSyncOutbox`.

4. **Background Transaction Synchronization**:
   - `OfflineBackgroundSyncManager` polls every 30 seconds, leases pending outbox items via `OfflineSyncCoordinator` with a 2-minute expiration, and uploads transactions via `OfflineTransactionSyncService` with exponential backoff and jitter.

5. **Shift & Movement Operations**:
   - Offline shift opening and blind closing queue `SHIFT_OPEN` and `SHIFT_CLOSE` outbox items with cryptographic ownership verification.
   - Cash drawer movements (paid-in, paid-out, petty cash, cash drop) are saved locally with SHA-256 supervisor PIN verification and queued via `CASH_MOVEMENT`.

---

## 2. Identified Deficiencies and Architecture Gaps

Prior to the enhancements implemented in this milestone, the following critical gaps existed:

1. **Dual Checkout Divergence**:
   - Online checkouts were sent directly to `POS.Api` via `OrderService.CreateOrderAsync` and `PaymentService.ProcessPaymentAsync` without saving to local SQLite first.
   - Offline checkouts were saved to SQLite.
   - **Consequence**: An online sale was invisible in local SQLite. If network dropped during payment, the cart was orphaned. Reconnected terminals could not view online sales in offline transaction history.

2. **Absence of First-Time Initialization Gate**:
   - Cashiers could access `NewSale.razor` before the local database had catalog products, an active invoice sequence, or cashier credentials cached.
   - Operating on an uninitialized database caused unhandled exceptions and blocked sales.

3. **Full Catalog Download on Every Application Launch**:
   - Cashiers logging in on subsequent days were forced to wait for full reference data downloads even when the local database was already populated, healthy, and unchanged.

4. **Non-Cash Payment Support in Local-First Pipeline**:
   - Local checkout only supported cash; non-cash tenders (Card, GCash, Maya) could not be recorded locally.

5. **Modifier Loss on Offline Order Lines**:
   - `LocalOrderItem` did not persist modifier selections or custom charges; reprint of offline receipts in transaction history lost modifier lines.

6. **Status Mismatch in Transaction History**:
   - Offline checkout wrote `PaymentStatus = "SUCCESS"`, while `Transactions.razor` specifically checked `PaymentStatus == "PAID"` to display the green "PAID" badge and enable receipt reprinting. Offline transactions appeared as "UNPAID" and hid the reprint button.

7. **Credential Cache Vulnerability**:
   - If an online user lookup returned a masked supervisor PIN (`PinCode = "****"`), the client previously hashed `"****"` as the valid PIN hash, corrupting the cached credential.

8. **Lack of Visual Sync Dashboard**:
   - The cashier had no persistent indication of outbox queue health, failed sync count, last sync timestamp, or manual "Sync Now" trigger.

---

## 3. Required SQLite Table Inventory & Comparison Matrix

| Table Name | Related Module | Currently in SQLite? | Required Columns / Enhancements | Local Operations | Sync Direction | Priority | Action |
|---|---|---|---|---|---|---|---|
| `LocalProducts` | New Sale & Cart | Yes | Complete (includes `ImageUrl`, `IsKitchenItem`, `IsBarItem`) | R / I / D | PG → SQLite | Critical | Reused |
| `LocalCategories` | Menu Categorization | Yes | Complete (`ColorCode`, `Name`) | R / I / D | PG → SQLite | Critical | Reused |
| `LocalModifierGroups` | Product Customization | Yes | Complete (`IsRequired`, `MinSelections`, `MaxSelections`) | R / I / D | PG → SQLite | High | Reused |
| `LocalModifierOptions` | Product Customization | Yes | Complete (`Price`, `DisplayOrder`, `IsActive`) | R / I / D | PG → SQLite | High | Reused |
| `LocalProductModifierGroups` | Product Customization | Yes | Complete | R / I / D | PG → SQLite | High | Reused |
| `LocalTaxes` | Tax Calculation | Yes | Complete (`Rate`, `Name`) | R / I / D | PG → SQLite | High | Reused |
| `LocalDiscounts` | Discounts & Statutory | Yes | Complete | I / D | PG → SQLite | Medium | Reused |
| `LocalPaymentMethods` | Tenders & Closing | Yes | Complete (`RequiresReference`, `Code`, `Name`) | R / I / D | PG → SQLite | High | Reused |
| `LocalTerminalConfigs` | Terminal Identity | Yes | Added `IsInitialized`, `LastSyncVersion`, `LastSyncAt` | R / I / U / D | PG → SQLite | Critical | Extended |
| `LocalCashiers` | Staff Login & Auth | Yes | Filter out masked `"****"` to protect real PIN hashes | R / I / U / D | PG → SQLite | Critical | Hardened |
| `LocalCashMovementCategories` | Drawer Cash Flow | Yes | Complete (`RequiresApproval`, `ApprovalThreshold`) | R / I / D | PG → SQLite | High | Reused |
| `LocalInvoiceSequences` | Fiscal Invoicing | Yes | Complete (`LastIssuedNumber`, `StartingNumber`, `EndingNumber`) | R / I / U | Coordinated | Critical | Reused |
| `LocalOrders` | Sales & History | Yes | Standardized `PaymentStatus = "PAID"` | R / I | SQLite → PG | Critical | Hardened |
| `LocalOrderItems` | Sales Lines | Yes | Added `ModifiersJson`, `ProductShortCode`, `IsKitchenItem`, `IsBarItem` | R / I | SQLite → PG | Critical | Extended |
| `LocalPayments` | Tenders & Change | Yes | Extended to record non-cash tenders (`CASH`, `CARD`, `GCASH`, `MAYA`) | R / I | SQLite → PG | Critical | Extended |
| `LocalSyncOutboxes` | Sync Engine Queue | Yes | Leases, retry counters, exponential backoff | R / I / U | SQLite → PG | Critical | Reused |
| `LocalSyncAuditLogs` | Sync Diagnostics | Yes | Complete | R / I | Local Only | Low | Reused |
| `LocalHeldSales` | Parked Sales | Yes | Complete (`PayloadJson`) | R / I / U | Local Only | High | Reused |
| `LocalCashDrawerShifts` | Shifts & Balance | Yes | Complete (`ClosingDenominationsJson`, recount history) | R / I / U | Coordinated | Critical | Reused |
| `LocalCashDrawerMovements` | Cash In/Out/Drops | Yes | Complete (`SupervisorPinHash`, `Status`, `SyncStatus`) | R / I / U | SQLite → PG | High | Reused |

---

## 4. Tables That Must NOT Be Cloned into SQLite

To maintain maximum terminal performance and prevent synchronization conflicts, the following tables are intentionally **excluded** from local SQLite:

1. **`Branches`, `Outlets`, `Terminals`, `BusinessType`**:
   - Reason: A physical POS terminal only needs its own assigned branch and terminal settings. Cloning the entire enterprise multi-tier hierarchy creates unnecessary storage overhead and security risks. All required metadata is denormalized into `LocalTerminalConfigs`.

2. **`ZReadings`, `ZReadingTerminalStates`, `ZReadingPrintAttempts`, `ZReadingPrintResults`**:
   - Reason: Fiscal Z-Reading finalization and cumulative BIR tax accounting are strictly server-authoritative, sequential, and tied to registered physical hardware. Finalizing a fiscal period offline violates BIR multi-terminal reconciliation rules. Z-Readings remain online-only.

3. **`ProductRecipes`, `UnitOfMeasures`, `StockAdjustments`**:
   - Reason: Ingredient consumption, inventory stock deductions, and reorder levels are handled centrally on the backend when transactions are synchronized. Terminals sell finished catalog products.

4. **`HeldSaleItems`, `HeldSaleItemModifiers`**:
   - Reason: Local held carts are parked and resumed as self-contained JSON payloads inside `LocalHeldSales.PayloadJson`. Relational sub-tables are unnecessary.

5. **`TransactionVoids`, `SalesReturns`, `SalesReturnItems`, `Refunds`**:
   - Reason: Returns and voids affect completed historical invoices, customer refunds, and fiscal period reports. These require live supervisor authentication and central transaction verification.
