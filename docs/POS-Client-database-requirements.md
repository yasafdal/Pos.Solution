# POS.Client Database Requirements

## Scope

This document inventories the database tables required by `POS.Client` for online and offline operation.

`POS.Client` does not connect directly to PostgreSQL. Online database operations go through `POS.Api`. Offline operations use the local SQLite database configured by `POS.Client/Data/LocalPosDbContext.cs`.

This document describes the current implementation. It does not change code, schemas, migrations, or databases.

### Operation legend

- **R** — Read
- **I** — Insert
- **U** — Update
- **D** — Delete
- **PG → SQLite** — Reference data or server snapshots downloaded to the terminal
- **SQLite → PG** — Offline data uploaded and replayed through the API
- **Local only** — Stored only on the terminal under the current implementation
- **None** — No local table is currently required

---

## 1. SQLite tables used directly by POS.Client

The current local database maps these tables in `POS.Client/Data/LocalPosDbContext.cs`.

| SQLite table | Purpose | Related POS.Client module | Operations | Offline requirement | Synchronization |
|---|---|---|---|---|---|
| `LocalProducts` | Product names, prices, categories, tax labels, images, and kitchen/bar routing. | New Sale, offline checkout, modifiers, receipts | R/I/D | Required | PG → SQLite from `Products` |
| `LocalCategories` | Category names and category colors. | New Sale product menu | R/I/D | Required | PG → SQLite from `Categories` |
| `LocalModifierGroups` | Modifier group rules, required flags, limits, and order. | New Sale modifier modal | R/I/D | Required for offline modifiers | PG → SQLite from `ModifierGroup` |
| `LocalModifierOptions` | Modifier choices and additional prices. | New Sale modifier modal | R/I/D | Required for offline modifiers | PG → SQLite from `ModifierOption` |
| `LocalProductModifierGroups` | Product-to-modifier-group assignments. | New Sale modifier lookup | R/I/D | Required for offline modifiers | PG → SQLite from `ProductModifierGroup` |
| `LocalTaxes` | Cached tax names and rates. | Reference validation and offline catalog | R/I/D | Required by cache validation; rate usage is limited | PG → SQLite from `Taxes` |
| `LocalDiscounts` | Cached discount definitions. | Discount reference cache | I/D; no current offline sale read | Not currently required by the active offline calculator | PG → SQLite from `DiscountTypes`; currently downloaded but not consumed by the offline modal |
| `LocalPaymentMethods` | Payment-method metadata, order, and reference requirements. | Shift declaration and payment configuration | R/I/D | Required for shift reconciliation; offline checkout remains cash-only | PG → SQLite from `PaymentMethods` |
| `LocalTerminalConfigs` | Flattened terminal, outlet, branch, business, tax, menu-display, and printer configuration. | Login, New Sale, receipts, offline checkout, printing | R/I/U/D | Required | PG → SQLite; printer names are device-local and preserved during refresh |
| `LocalCashiers` | Cached usernames, roles, and PIN hashes. | Offline login and supervisor PIN verification | R/I/U/D | Required for cached authentication | PG → SQLite from `Users` |
| `LocalCashMovementCategories` | Cash-in, cash-out, petty-cash, and cash-drop categories. | Cash Drawer | R/I/D | Required for offline movement selection | PG → SQLite from `CashMovementCategories` |
| `LocalInvoiceSequences` | Reserved invoice ranges and local invoice counter. | Offline checkout | R/I/U | Required | Reservation PG → SQLite; local counter update SQLite → PG during replay |
| `LocalOrders` | Offline sale headers, totals, tax summaries, invoice, terminal, shift, and cashier data. | Offline checkout, transaction history, replay | R/I | Required | SQLite → PG through offline transaction replay |
| `LocalOrderItems` | Offline order-line snapshots. | Offline checkout, transaction history, replay | R/I | Required | SQLite → PG through replay |
| `LocalPayments` | Offline payment rows. | Offline checkout, transaction history, replay | R/I | Required | SQLite → PG; current offline checkout creates one CASH payment |
| `LocalSyncOutboxes` | Queued transactions, shift events, cash movements, retries, leases, and errors. | Background sync and Z-readiness | R/I/U | Required | SQLite → PG; not a PostgreSQL mirror |
| `LocalSyncAuditLogs` | Local reference-refresh and sync messages. | Sync diagnostics | I/R | Useful but not financial source data | Local only |
| `LocalHeldSales` | Locally parked sale payloads, including item/modifier JSON. | Held Sales | R/I/U | Required for local held tickets | Local only; no current held-sale synchronization |
| `LocalCashDrawerShifts` | Opening float, sales totals, movements, closing counts, declarations, and recount JSON. | Cash Drawer, My Shift, X-Reading, offline checkout | R/I/U | Required | Server snapshots PG → SQLite; offline opening/closing SQLite → PG |
| `LocalCashDrawerMovements` | Offline paid-in, paid-out, petty-cash, cash-drop, and cash-sale movement rows. | Cash Drawer and replay | R/I/U | Required for offline movements | SQLite → PG through `CASH_MOVEMENT`; incomplete server-history replication |

### SQLite schema details

The current local context defines these mapped tables:

```text
LocalProducts
LocalCategories
LocalModifierGroups
LocalModifierOptions
LocalProductModifierGroups
LocalTaxes
LocalDiscounts
LocalPaymentMethods
LocalTerminalConfigs
LocalInvoiceSequences
LocalOrders
LocalOrderItems
LocalPayments
LocalSyncOutboxes
LocalSyncAuditLogs
LocalHeldSales
LocalCashiers
LocalCashMovementCategories
LocalCashDrawerShifts
LocalCashDrawerMovements
```

---

## 2. PostgreSQL tables accessed indirectly through POS.Api

POS.Client reaches these tables through API endpoints and services.

### 2.1 Identity, hierarchy, catalog, and reference data

| PostgreSQL table | Purpose | Client-triggered operations | Local counterpart and direction |
|---|---|---:|---|
| `Users` | PIN authentication, roles, supervisor verification, and cashier reference data. | R | `LocalCashiers`; PG → SQLite |
| `Branches` | Branch identity, tax type, activity, and receipt metadata. | R | Flattened into `LocalTerminalConfigs`; PG → SQLite |
| `Outlets` | Outlet identity, activity, and ownership validation. | R | Flattened into `LocalTerminalConfigs`; PG → SQLite |
| `Terminals` | Register identity, ownership, activation, fiscal identifiers, and menu display mode. | R; row locks during some transactions | Flattened into `LocalTerminalConfigs`; PG → SQLite |
| `BusinessType` | Retail/F&B behavior selection. | R | Flattened into `LocalTerminalConfigs`; PG → SQLite |
| `Categories` | Online category listing and colors. | R | `LocalCategories`; PG → SQLite |
| `Products` | Online product catalog and routing flags. | R | `LocalProducts`; PG → SQLite |
| `Taxes` | Product tax metadata. | R | `LocalTaxes`; PG → SQLite |
| `ModifierGroup` | Modifier group rules. | R | `LocalModifierGroups`; PG → SQLite |
| `ModifierOption` | Modifier choices and prices. | R | `LocalModifierOptions`; PG → SQLite |
| `ProductModifierGroup` | Product modifier assignments. | R | `LocalProductModifierGroups`; PG → SQLite |
| `DiscountTypes` | Discount chooser and reference data. | R | `LocalDiscounts`; PG → SQLite, but current offline UI uses a hardcoded list |
| `PaymentMethods` | Online payment choices and shift declaration metadata. | R | `LocalPaymentMethods`; PG → SQLite |
| `CashMovementCategories` | Cash-movement categories and approval rules. | R; category GET can seed defaults | `LocalCashMovementCategories`; PG → SQLite |

The reference-data endpoint reads these records and also reserves an invoice block by updating `InvoiceSequences`.

### 2.2 Sales, payments, invoice numbering, and replay

| PostgreSQL table | Purpose | Client-triggered operations | Local counterpart and direction |
|---|---|---:|---|
| `OrderSequences` | Server order-number state. | R/I/U | No local equivalent; server-only numbering state |
| `InvoiceSequences` | Official invoice numbering and offline range reservation. | R/U | `LocalInvoiceSequences`; range PG → SQLite, issued number SQLite → PG |
| `Orders` | Sale header, statuses, totals, invoice, and customer data. | R/I/U | `LocalOrders`; offline sale SQLite → PG |
| `OrderItems` | Server order lines. | R/I | `LocalOrderItems`; SQLite → PG |
| `OrderItemModifiers` | Server modifier selections. | R/I | No normalized local table; selections are preserved in outbox JSON and replayed SQLite → PG |
| `Payments` | Cash, non-cash, split tenders, payment status, and receipt payment data. | R/I/U | `LocalPayments`; offline cash SQLite → PG |
| `DiscountAuditLogs` | Audit of qualifying server-created discounts. | I conditionally | No local equivalent; offline replay does not currently create the same audit |
| `OfflineSyncIdempotency` | Replay deduplication and correlation. | R/I | No same-name SQLite table; local outbox provides terminal-side tracking |
| `BranchInventory` | Stock row used by offline replay and conditional return restocking. | R/U conditionally | No local inventory table |
| `StockAdjustments` | Stock audit for offline-sale replay. | I conditionally | No local equivalent |
| `TerminalRecoveryLogs` | Historical recovery audit for authorized inactive-terminal recovery. | I conditionally | No local equivalent |

### 2.3 Held sales, transaction history, returns, and session audit

| PostgreSQL table | Purpose | Client-triggered operations | Local counterpart and direction |
|---|---|---:|---|
| `HeldSales` | Server-held sale headers and statuses. | R/I/U | `LocalHeldSales` is separate; no synchronization |
| `HeldSaleItems` | Server-held lines. | R/I | Embedded in local held-sale JSON; no sync |
| `HeldSaleItemModifiers` | Server-held modifiers. | R/I | Embedded in local held-sale JSON; no sync |
| `HeldSaleAuditLogs` | Server hold/resume/cancel audit. | I | No local equivalent or replay |
| `CashierSessions` | Online cashier activation and session ownership. | R/I/U | No local equivalent |
| `CashierSwitchLogs` | Online cashier-switch audit. | I | No local equivalent or replay |
| `TransactionVoids` | Void records. | I; related order/payment reads | No local equivalent; current client is online-only |
| `SalesReturns` | Return headers. | R/I/U | No local equivalent; current client is online-only |
| `SalesReturnItems` | Returned lines and quantities. | R/I | No local equivalent |
| `Refunds` | Refund records. | R/I | No local equivalent |
| `ReceiptReprintLogs` | Controlled receipt reprint audit. | I conditionally | No local equivalent; offline reprints are not durably audited |

### 2.4 Drawer and fiscal readings

| PostgreSQL table | Purpose | Client-triggered operations | Local counterpart and direction |
|---|---|---:|---|
| `CashDrawerShifts` | Shift lifecycle, cash totals, closing declarations, and recount history. | R/I/U | `LocalCashDrawerShifts`; snapshots PG → SQLite, offline lifecycle SQLite → PG |
| `CashDrawerMovements` | Cash movement lifecycle, approval, reversal, and cash-sale effects. | R/I/U | `LocalCashDrawerMovements`; offline-origin data SQLite → PG |
| `ZReadingTerminalStates` | Terminal fiscal-period state and concurrency. | R/U during Z generation | None; server-only |
| `ZReadings` | Immutable finalized Z snapshot. | R/I | None; online-only |
| `ZReadingPrintAttempts` | Z print authorization/start audit. | R/I | None; online-only |
| `ZReadingPrintResults` | Z print completion outcome. | R/I | None; online-only |

### X-Reading and denominations

There is no separate database table for:

- X-Reading snapshots
- Denomination master data
- Blind-close declarations
- Payment reconciliation rows
- Recount audit rows

These are derived or stored as JSON fields on `CashDrawerShifts` and `LocalCashDrawerShifts`:

```text
ClosingDenominationsJson
ClosingOriginalDeclarationJson
ClosingPaymentReconciliationsJson
ClosingRecountHistoryJson
```

X-Reading is a non-resetting derived inquiry. Z-Reading is an immutable server-side final snapshot. They must not be treated as the same persistence workflow.

---

## 3. Current offline synchronization matrix

| Event or data | Current direction | Current status |
|---|---|---|
| Catalog, categories, taxes, modifiers, discounts, payment methods | PostgreSQL → SQLite | Implemented through reference refresh |
| Terminal and hierarchy metadata | PostgreSQL → SQLite | Flattened into `LocalTerminalConfigs` |
| Cached cashier identity/PIN hash | PostgreSQL → SQLite | Implemented; cached authorization limitations exist |
| Invoice reservations | PostgreSQL → SQLite | Implemented during reference refresh |
| Offline cash sale | SQLite → PostgreSQL | Implemented through offline transaction replay |
| Offline order items | SQLite → PostgreSQL | Implemented; modifier details come from outbox JSON |
| Offline cash payments | SQLite → PostgreSQL | Implemented |
| Offline shift opening | SQLite → PostgreSQL | Implemented through `SHIFT_OPEN` outbox |
| Offline shift closing | SQLite → PostgreSQL | Implemented through `SHIFT_CLOSE` outbox |
| Offline cash movements | SQLite → PostgreSQL | Implemented through `CASH_MOVEMENT` outbox; shift correlation requires hardening |
| Offline recount | SQLite → PostgreSQL | **Missing**; currently local only |
| Local held sales | SQLite ↔ PostgreSQL | **Missing**; local and server holds are separate |
| Online history into local cache | PostgreSQL → SQLite | **Missing**; online history is not merged locally |
| Returns and refunds | SQLite → PostgreSQL | **Missing**; current client requires online access |
| Voids | SQLite → PostgreSQL | **Missing**; current client requires online access |
| Cashier session/switch audit | SQLite → PostgreSQL | **Missing** |
| Offline receipt reprint audit | SQLite → PostgreSQL | **Missing** |
| Z-Reading generation and print audit | SQLite → PostgreSQL | Intentionally not supported; Z is online-only |
| Product image files | PostgreSQL/server URL → SQLite file cache | Not implemented; only image URLs are cached |

---

## 4. Tables that should not be duplicated locally by default

The following do not need separate local tables under the current architecture:

- `Branches`, `Outlets`, `Terminals`, and `BusinessType`: required fields are flattened into `LocalTerminalConfigs`.
- `HeldSaleItems` and `HeldSaleItemModifiers`: local held-sale contents are stored in `LocalHeldSales.PayloadJson`.
- `ZReadings`, `ZReadingTerminalStates`, `ZReadingPrintAttempts`, and `ZReadingPrintResults`: fiscal finalization and print authorization are intentionally server-only.
- `TransactionVoids`, `SalesReturns`, `SalesReturnItems`, and `Refunds`: no local copies are required while these operations remain online-only.
- Receipt and preparation-ticket documents: currently generated from order/catalog DTOs; no normal receipt table is used.
- `ProductRecipes`, `UnitOfMeasures`, and `ServiceMode`: no standalone POS.Client runtime lookup was found for the current sales path. Product UOM is carried on the product, and service-mode choices are currently coded in the client.

`LocalDiscounts` is different: it is already downloaded but currently not consumed by the offline discount workflow. It should either be wired into an approved discount design or removed only after a separate regression review.

---

## 5. Significant implementation gaps

1. **Uncertain online order creation:** If the order request commits but its response is lost, `NewSale` can fall back to offline checkout without first reconciling the server-side result. This can create stranded or duplicate sales.
2. **Offline recount is not synchronized:** Local recount JSON is updated, but no outbox event is created.
3. **Cash movement shift correlation:** Offline movement upload can fall back to the latest open server shift when the local shift ID is not a valid server ID.
4. **Held-sale synchronization is absent:** Local held sales, server held sales, and their audits are separate.
5. **Offline returns, refunds, and voids are absent:** No local tables or replay protocol exist for these operations.
6. **Offline history is incomplete:** Online sales are not downloaded into SQLite, and local reprint reconstruction omits some original context.
7. **Offline reprint audit is absent:** Local reprints do not create a durable local audit row or upload an equivalent server audit.
8. **Offline non-cash and split payment are absent:** Current offline checkout accepts cash only.
9. **Discount cache is unused offline:** `LocalDiscounts` is synchronized but the active offline chooser uses hardcoded definitions.
10. **Offline image files are not cached:** Image mode depends on normal HTTP/WebView caching; unavailable images show placeholders.
11. **Cached authentication requires review:** Cached PIN fallback can operate after online authentication rejection, and masked PIN values must not be hashed as real credentials.
12. **Fiscal guard readiness requires independent verification:** A readiness response should not be treated as proof that every Z-period trigger is installed.

---

## 6. Source references

- Local table mapping: `POS.Client/Data/LocalPosDbContext.cs`
- Local entities: `POS.Client/Data/LocalPosModels.cs`
- Reference synchronization: `POS.Client/Services/ReferenceDataSyncService.cs`
- Offline checkout: `POS.Client/Services/OfflineCheckoutService.cs`
- Offline replay: `POS.Client/Services/OfflineTransactionSyncService.cs`
- Held sales: `POS.Client/Services/ClientHeldSaleService.cs`
- Shift lifecycle: `POS.Client/Services/ShiftSession.cs`
- Local Z readiness: `POS.Client/Services/ZReadingLocalReadiness.cs`
- API reference snapshot: `POS.Api/Controllers/OfflineReferenceDataController.cs`
- PostgreSQL mappings: `POS.Api/Models/PosDbContext.Extensions.cs`, `PosDbContext.Hierarchy.cs`, and `PosDbContext.ZReading.cs`
- Sales replay side effects: `POS.Api/Controllers/OfflineSyncController.cs`
- Z fiscal persistence: `POS.Api/Services/ZReadingService.cs` and `ZReadingService.Printing.cs`

## Verification limit

This is a static source audit. No application was started, no API request was executed, and no database schema or data was modified for this document.
