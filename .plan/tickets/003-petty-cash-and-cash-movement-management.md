---
id: 003
title: "Implement Petty Cash & Cash Movement Management Across API, Client, Web & Offline Sync"
labels:
  - wayfinder:task
status: RESOLVED
assignee: antigravity
---

## Objective

Implement a complete, commercial-grade **Petty Cash / Cash Paid-In / Cash Paid-Out / Cash Drop Management** module for **Pos.Solution** (`POS.Shared`, `POS.Api`, `POS.Client`, `POS.Web`, and `POS.Tests`) that integrates cleanly with existing cashier shifts, cash drawer reconciliation, sales/refund flows, offline SQLite synchronization, and Philippine BIR compliance controls without duplicating existing tables or breaking revenue/VAT calculations.

---

## Layer 1 — Full Architecture & Database Analysis

### 1.1 Existing Implementation Inventory
1. **Cashier Session Management (`POS.Api/Models/CashierSessionModels.cs`, `POS.Api/Controllers/CashierSessionsController.cs`):**
   - `CashierSessions` and `CashierSwitchLogs` track active terminal ownership (`ACTIVE`, `SWITCHED_OUT`, `LOGGED_OUT`) per `OutletId` and `BranchId`.
2. **Cash Drawer & Shift Management (`POS.Api/Models/CashDrawerModels.cs`, `POS.Api/Controllers/CashDrawerController.cs`, `POS.Client/Components/Pages/CashDrawer.razor`):**
   - `CashDrawerShift` (`"CashDrawerShifts"`) tracks `ShiftId`, `BranchId`, `OutletId`, `CashierId`, `OpeningFloat`, `TotalCashSales`, `TotalCashRefunds`, `TotalCashIn`, `TotalCashOut`, `ExpectedBalance`, `ActualCountedBalance`, `Discrepancy`, `Status` (`OPEN`/`CLOSED`), `OpenedAt`, `ClosedAt`, `Notes`.
   - `CashDrawerMovement` (`"CashDrawerMovements"`) tracks `MovementId`, `ShiftId`, `MovementType`, `Amount`, `Reason`, `AuthorizedBy`, `CreatedAt`.
   - `CashDrawer.razor` provides a basic UI for opening a shift, recording raw `CASH_IN`/`CASH_OUT`, and closing a shift online.
3. **Sales, Payments, Returns & Reporting (`OrdersController.cs`, `PaymentsController.cs`, `ReturnsController.cs`, `SalesReportService.cs`):**
   - `SalesReportService.cs` computes `GrossSales`, `TotalDiscounts`, `NetSales`, `VatableSales`, `VatExemptSales`, `ZeroRatedSales`, `OutputVat`, and `TotalPaymentsCollected` strictly from `Orders` and `Payments`. Cash drawer movements do not touch `Orders` or `InvoiceSequences`.
   - **Gap Identified:** Online `PaymentsController.ProcessPayment` (`CASH` tender) and `ReturnsController` (`VoidTransaction` / `ProcessReturn` with `CASH` refund) do not currently update the open `CashDrawerShift` (`TotalCashSales`, `TotalCashRefunds`, `ExpectedBalance`), whereas `OfflineSyncController.SyncTransaction` does update `TotalCashSales`.
4. **Offline SQLite & Sync Infrastructure (`LocalPosDbContext.cs`, `LocalPosModels.cs`, `ReferenceDataSyncService.cs`, `OfflineTransactionSyncService.cs`, `OfflineSyncCoordinator.cs`, `PosConnectivityService.cs`):**
   - `PosConnectivityService` performs real HTTP health checks against `GET api/offline-sync/health`.
   - `LocalPosDbContext` manages 16 SQLite tables in WAL mode protected by `LocalPosDbContext.WriteLock`.
   - `LocalSyncOutbox` + `OfflineSyncCoordinator` + `OfflineRetryPolicy` provide lease-based background synchronization with bounded retries (max 5 retries) and exponential backoff with jitter.
   - `LocalCashier` caches `UserId`, `Username`, `Role`, and SHA-256 `PinHash` for offline PIN verification.

### 1.2 Missing Functionality & Conflicts to Resolve
1. **No Movement Categories or Approval Workflow:** `CashDrawerMovement` lacks `CategoryId`, `ReferenceNumber` (voucher number), `Status` (`PENDING_APPROVAL`, `POSTED`, `REJECTED`, `REVERSED`), `CreatedBy`, `ApprovedBy`, `ApprovedAt`, `PostedAt`, `ClientMovementId` (idempotency), and reversal tracking (`ReversedBy`, `ReversedAt`, `ReversalReason`, `OriginalMovementId`).
2. **No Drawer Balance Guard on Paid-Out / Petty Cash:** `CashDrawerController.RecordMovement` currently allows `CASH_OUT` even when `Amount > ExpectedBalance` and does not require supervisor PIN/role verification when required by category or policy.
3. **No Cash Drops Bucket in Shift Reconciliation:** `CashDrawerShift` lacks `TotalCashDrops` (`numeric(18,2)`), and online cash sales/refunds are not wired into `CashDrawerShift`.
4. **No Offline Cash Drawer / Cash Movement Support:** `LocalPosDbContext` has no local shift or cash movement tables (`LocalCashDrawerShift`, `LocalCashDrawerMovement`, `LocalCashMovementCategory`), and `OfflineTransactionSyncService` only uploads `LocalOrder` outbox items.
5. **No Printable Cash Movement Voucher or POS.Web Admin Module:** Neither `POS.Client` voucher printing nor `POS.Web` category CRUD, movement history, approval queue, reversal workflow, and summary reporting exist yet.

---

## Layer 2 — Database Design (Reuse First, Non-Destructive Evolution)

Rather than creating a duplicate `CashMovements` table that would conflict with `CashDrawerMovements`, we **extend `CashDrawerMovement` (`"CashDrawerMovements"`) and `CashDrawerShift` (`"CashDrawerShifts"`)** and add **`CashMovementCategory` (`"CashMovementCategories"`)**:

1. **`CashMovementCategory` (`"CashMovementCategories"`):**
   - `CategoryId` (`int`, PK, Identity)
   - `Name` (`varchar(100)`, Required)
   - `Description` (`varchar(255)`)
   - `MovementType` (`varchar(30)`, Required: `PETTY_CASH`, `CASH_OUT`, `CASH_IN`, `CASH_DROP`)
   - `RequiresApproval` (`bool`, default `true` for outflows, `false` for routine change fund in)
   - `ApprovalThreshold` (`numeric(18,2)`, default `0.00` — if `> 0`, requires supervisor approval when `Amount >= ApprovalThreshold` or when `RequiresApproval == true`)
   - `IsActive` (`bool`, default `true`)
   - `CreatedAt` (`timestamp without time zone`, default `UTC`)

2. **Extended `CashDrawerShift` (`"CashDrawerShifts"`):**
   - Existing columns preserved (`ShiftId`, `BranchId`, `OutletId`, `CashierId`, `OpeningFloat`, `TotalCashSales`, `TotalCashRefunds`, `TotalCashIn`, `TotalCashOut`, `ExpectedBalance`, `ActualCountedBalance`, `Discrepancy`, `Status`, `OpenedAt`, `ClosedAt`, `Notes`).
   - Added column:
     - `TotalCashDrops` (`numeric(18,2)`, default `0.00`)
   - Reconciliation invariant:
     $$\text{ExpectedBalance} = \text{OpeningFloat} + \text{TotalCashSales} - \text{TotalCashRefunds} + \text{TotalCashIn} - \text{TotalCashOut} - \text{TotalCashDrops}$$

3. **Extended `CashDrawerMovement` (`"CashDrawerMovements"`):**
   - Existing columns preserved (`MovementId`, `ShiftId`, `MovementType`, `Amount`, `Reason`, `AuthorizedBy`, `CreatedAt`).
   - Added columns:
     - `BranchId` (`int`, indexed)
     - `OutletId` (`int`, indexed)
     - `CategoryId` (`int?`, FK -> `CashMovementCategories.CategoryId` `OnDelete(SetNull)`)
     - `CategoryName` (`varchar(100)`)
     - `ReferenceNumber` (`varchar(50)`, unique index `UX_CashDrawerMovements_ReferenceNumber` — e.g., `PCV-261019-0001`, `CIN-261019-0001`, `CDP-261019-0001`, `REV-261019-0001`)
     - `ClientMovementId` (`varchar(100)`, unique index `UX_CashDrawerMovements_ClientMovementId` when non-null for offline/online idempotency)
     - `Status` (`varchar(30)`, Required: `PENDING_APPROVAL`, `POSTED`, `REJECTED`, `REVERSED`, default `'POSTED'`)
     - `CreatedBy` (`varchar(450)`, Required)
     - `ApprovedBy` (`varchar(450)`)
     - `ApprovedAt` (`timestamp without time zone?`)
     - `PostedAt` (`timestamp without time zone?`)
     - `RejectionReason` (`varchar(255)`)
     - `ReversedBy` (`varchar(450)`)
     - `ReversedAt` (`timestamp without time zone?`)
     - `ReversalReason` (`varchar(255)`)
     - `OriginalMovementId` (`bigint?`)
   - *Decision on `CashMovementApprovals` table:* Excluded as a separate table because storing `CreatedBy`, `ApprovedBy`, `ApprovedAt`, `RejectionReason`, `ReversedBy`, `ReversedAt`, and `ReversalReason` directly on the immutable `CashDrawerMovement` record (plus reversing via an offsetting `REVERSAL` movement row) captures the complete lifecycle without extra join overhead.

4. **Offline SQLite Tables (`POS.Client/Data/LocalPosDbContext.cs`):**
   - `LocalCashMovementCategory` (`LocalCashMovementCategories`)
   - `LocalCashDrawerShift` (`LocalCashDrawerShifts`)
   - `LocalCashDrawerMovement` (`LocalCashDrawerMovements`)
   - Outbox integration via `LocalSyncOutbox` (`EntityType = "CASH_MOVEMENT"`).

---

## Execution Layers Checklist
- [x] Layer 1 — Full Architecture and Database Analysis
- [x] Layer 2 — Database Design
- [ ] Layer 3 — Backend API Implementation (`CashDrawerController`, `OfflineReferenceDataController`, `OfflineSyncController`, `PaymentsController`, `ReturnsController`)
- [ ] Layer 4 — `POS.Client` Cash Management UI & Printable Voucher (`CashDrawer.razor`)
- [ ] Layer 5 — Cashier Shift & Reconciliation Integration
- [ ] Layer 6 — Offline Mode Support (`ReferenceDataSyncService`, `OfflineTransactionSyncService`, `LocalPosDbContext`)
- [ ] Layer 7 — `POS.Web` Management (`CashMovementsManagement.razor`, `NavMenu.razor`)
- [ ] Layer 8 — BIR & Financial Controls Verification
- [ ] Layer 9 — Security & Performance Hardening
- [ ] Layer 10 — Automated Testing & Verification (`POS.Tests`)
- [ ] Layer 11 — Documentation Update (`project_content.md` & `project_context.md`)
