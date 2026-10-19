---
id: 005
title: "Scope POS.Client Transaction History to Today, This Terminal's Branch and Outlet"
labels:
  - wayfinder:task
status: RESOLVED
assignee: antigravity
---

## Question / Objective

Cashier on `POS.Client` must see only today's transactions for their own branch/outlet. No past days, no other POS/branch.

## Issues Found

1. [`Transactions.razor`](../../POS.Client/Components/Pages/Transactions.razor): no `DateFrom`, so every past transaction was listed.
2. Only `OutletId` was sent, never `BranchId`. If `Session.Outlet` was null, the filter was empty and returned every terminal's sales.
3. `PageSize` stayed at 20, so a busy day was truncated.
4. API failure left `transactions == null` (spinner forever, error only in console).
5. [`TransactionsController.GetTransactionById`](../../POS.Api/Controllers/TransactionsController.cs): any cashier could open any order by id (other cashier/outlet/branch).
6. `GetTransactionSummary` accepted `dateFrom` but never applied it.
7. Reprint audit log wrote hardcoded printer name `"EPSON TM-T88IV Receipt"`.
8. `Orders.CreatedAt` is UTC `timestamp without time zone`; incoming `Local`-kind dates were compared unconverted.

## Resolution

- Client: `ApplyTerminalScope()` forces `BranchId`, `OutletId`, `DateFrom = local midnight (UTC)`, `PageSize = 100` on every load/reset; user fields (invoice #, status) cannot override it. No outlet -> empty list + message. Failure -> visible banner, no spinner lock. Header shows today's date.
- API: ordinary cashiers (non Admin/Manager/SeniorCashier) are capped to the last 24h and own transactions only; detail endpoint enforces ownership; `ToUtcUnspecified` normalises date kinds; summary now honours `dateFrom`; reprint log uses `SYSTEM_DEFAULT_PRINTER`.

## Not Changed

- Offline mode: Transactions page needs the API; offline now shows a message instead of hanging. Local SQLite order list is a future ticket.
- `[AllowAnonymous]` + username-as-PIN on void/return stays in Track 2 (security).
