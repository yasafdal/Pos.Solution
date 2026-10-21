# Pos.Solution — Branch / Outlet / Terminal architecture

## Status and scope

The working tree now represents `Branch → Outlet → Terminal`. These changes are not deployed. No application PostgreSQL database or user's `local_pos.db` was migrated or opened during verification. All database execution used disposable fixtures: SQLite in-memory and a separate PostgreSQL 18 cluster bound to `127.0.0.1:55437` in Hermes scratch. Review `docs/BRANCH_OUTLET_TERMINAL_PLAN.md` before rollout.

The actual solution is `Pos.Solution.slnx`. API, Shared, Web and Windows MAUI Client use .NET 10. The existing user development-IP override in `POS.Client/MauiProgram.cs` is preserved. Receipt printer behavior and paper formatting are unchanged by this hierarchy task.

## Responsibilities and data flow

- **POS.Api:** authoritative PostgreSQL business data via EF Core/Npgsql; hierarchy CRUD, JWT authentication, terminal resolution, orders/payments, invoice allocation, cash ledger, held sales, returns/reprints, reporting and offline ingestion.
- **POS.Shared:** JSON DTOs, HTTP adapters, service interfaces and local financial calculations. Numeric `TerminalId` identifies a POS machine; string `DeviceIdentifier` is a registration identifier, not the terminal primary key.
- **POS.Web:** Blazor Interactive Server admin UI using shared HTTP contracts. Features three separated management interfaces: Branch Management (`/branches`), Outlet Management (`/outlets`), and Terminal Management (`/terminals`), with dedicated CRUD tables, cascading branch/outlet filters, device registration details, and integrated terminal invoice series dialogs. Its separate SQL Server Identity database is not the POS business database.
- **POS.Client:** MAUI Blazor Hybrid cashier app. Native device address resolves an active Terminal, Outlet and Branch. Session, sales, shifts, held tickets, receipt requests and offline outbox use `TerminalId`. SQLite caches the terminal's physical-outlet ownership and BIR machine configuration.

```text
Branch (business, tax and inventory ownership)
  Outlet (physical/operational location; MAIN, booth, kiosk, counter, mobile)
    Terminal (POS device; identity, IP, PTU, MIN, serial, invoice series)
      Orders / Payments / Shifts / Sessions / Held sales / Reprints / Returns
```

Inventory remains Branch-based. Existing transactional BranchId fields remain historical ownership. Terminal.BranchId is intentionally retained during transition and checked against the physical Outlet with a composite foreign key. A terminal with transaction or invoice history cannot be reassigned through CRUD; this prevents changing historical outlet reporting by moving a register.

## Central database changes

Migration `20261021141713_BranchOutletTerminal` upgrades an existing legacy schema; it is not an empty-database initializer. The previous source used scaffolded entities and manual SQL rather than EF migration history. The migration therefore uses reviewed embedded PostgreSQL SQL, not a generated DropTable/CreateTable replacement.

1. Rename `Outlets` to `Terminals` in place and `OutletId` to `TerminalId`. Existing records, IDs, PTU/MIN/serial values and identity sequence state remain.
2. Rename machine columns on Orders, CashDrawerShifts, CashDrawerMovements, CashierSessions, CashierSwitchLogs, HeldSales, HeldSaleAuditLogs, ReceiptReprintLogs and SalesReturns. Rename `outlet_id` to `terminal_id` on OrderSequences and InvoiceSequences.
3. Create the real `Outlets` table: OutletId, BranchId, scoped OutletCode, Name, Description, LocationDescription, OutletType, IsActive, CreatedAt and UpdatedAt.
4. Add nullable Terminals.OutletId, seed one MAIN outlet per existing Branch, backfill terminals, validate all ownership and historical references, then enforce NOT NULL and foreign keys.
5. Add TerminalCode, IpAddress and timestamps; generated migrated codes are `POS-<original ID>`. Existing DeviceIdentifier IPs are canonicalized and copied into IpAddress. Equivalent or duplicate identifiers stop the migration instead of deleting records.
6. Enforce unique outlet codes per branch, terminal codes per outlet, device identifiers and IPs. History references restrict terminal deletion. Retain original invoice numbers, sequence associations and counters.

Migration `20261021150226_HierarchyForeignKeyAlignment` records the final EF relationship snapshot. Its schema operations are intentionally empty because constraints are installed atomically in the first migration. Migration `20261021152741_TerminalRecoveryAudit` adds durable supervisor recovery logs, with no automatic destructive downgrade. Migration `20261021163926_TerminalDeactivationTimestamp` adds dedicated `DeactivatedAt` timestamps on Terminals and Outlets to guard historical cutoffs. Migration `20261021171535_OrderOutletRelationship` adds an explicit, indexed `OutletId` foreign key to `Orders`, backfilled from the terminal's physical outlet for all historical and incoming orders. The complete idempotent script is stored in `docs/BRANCH_OUTLET_TERMINAL_MIGRATION.sql`. EF reports no pending model changes.

Timestamp conventions remain the existing UTC values stored in PostgreSQL `timestamp without time zone`; API retains its existing Npgsql legacy timestamp setting. No historical timestamps are rewritten.

The default MAIN assignment is a safe ownership fallback, not knowledge of real booth locations. Administrators must review it before reopening sales.

## Contracts and endpoints

Old machine-oriented `OutletId` contracts are renamed to `TerminalId`. Coordinated API/Web/Client deployment and fresh login are required; an old `outlet_id` JWT claim must not be interpreted as a new physical outlet ID.

- `GET/POST /api/branches`, `GET/PUT/DELETE /api/branches/{id}`: branch management. Branch listing returns physical Outlets with terminal counts and nested Terminals.
- `GET /api/outlets?branchId=...`, `GET/POST /api/outlets`, `GET/PUT/DELETE /api/outlets/{id}`: physical outlet CRUD. PUT includes activation/deactivation. Admin/Manager only; codes normalized and checked within the branch. Child terminals prevent deletion. Active children must be deactivated before their parent.
- `GET /api/terminals?outletId=...`, `GET/POST /api/terminals`, `GET/PUT/DELETE /api/terminals/{id}`: terminal CRUD, Admin/Manager only. Assignment requires an active outlet and branch. Identity and IP uniqueness validated. Linked history prevents deletion/reassignment.
- `GET /api/terminals/resolve-terminal?ipAddress=...`: authenticated registration lookup; returns terminal ID, physical outlet ID/name, branch metadata and a newly scoped JWT. IP identifies a registered terminal but is not an authentication secret.
- `/api/terminals/{terminalId}/invoice-sequences`: existing invoice management now terminal-scoped. Administrator writes use serializable transactions and reject invalid or overlapping prefix/range allocations.
- `POST /api/offline/reference-data?terminalId=...`: authenticated reference/config refresh. POST is deliberate: it reserves an offline invoice block. No longer an anonymous/cacheable GET.
- Inactive terminal reconciliation uses the existing offline POST endpoints with an Admin/Manager bearer token and `X-Terminal-Recovery-Reason` header. It accepts only historical dates before terminal/outlet deactivation and writes `TerminalRecoveryLogs` in the same transaction. It does not reactivate the machine or authorize new sales. An operator must explicitly resubmit held payloads; ordinary cashier retries remain blocked and data is not discarded.
- Existing order, transaction, cash-drawer, cashier-session, held-sale, return and report routes use `terminalId` query parameters where the old value denoted a machine. Sales report/history filters additionally accept physical `outletId`, distinct from `terminalId`.
- `GET /api/offline-sync/health` remains anonymous. Offline write routes require authentication and terminal ownership; background uploads attach the current real session token and pause without one.

## Online and offline workflow

1. Cashier authenticates; native `TerminalAddressProvider` supplies a routable device IP (or existing Debug-only override for localhost development).
2. Terminal lookup verifies active Terminal, Outlet and Branch and issues branch_id, outlet_id and terminal_id claims.
3. Client caches hierarchy/config and refreshes products, tax, modifiers, cashiers, shift state and invoice allocation.
4. Online orders and cash shifts retain machine identity as TerminalId; payments issue terminal-owned invoices, update cash totals and produce persisted receipt DTOs.
5. Offline checkout uses cached ownership and a configured local invoice range. It never invents or extends a range after exhaustion. Cash checkout, payments and outbox are committed atomically in SQLite.
6. Upload preserves original invoice and monetary/item snapshots. Idempotent replay requires the same local transaction, terminal and payload hash. A reused invoice or mismatched payload is a conflict, not acknowledgement. Client holds conflicts for attention instead of marking them synced. Ambiguous status fallback requires an exact local ID, invoice, numeric terminal and original payload-hash match, not merely HTTP 200.

### Invoice reservation behavior

Reference refresh reserves up to 1000 unused numbers under the same PostgreSQL row lock used by online issuing. Server LastIssuedNumber advances to the reserved boundary; online invoices use numbers above that boundary. Client spends only its local reserved range. Refresh reserves a new distinct range; numbers can be skipped if a response is lost or an unused block is replaced. No historical invoice is regenerated. This is an operational numbering change and requires BIR/process review before production. Existing unregistered fallback offline invoices should be reconciled before upgrading; uploads not matching a configured series now stop for review rather than silently assigning a different invoice.

### Local SQLite upgrade

`LocalHierarchyUpgrade` runs before EnsureCreated, in a transaction, once per initialized app process:
- Rename LocalOutletConfigs to LocalTerminalConfigs.
- Rename old machine OutletId columns to TerminalId on config, orders, invoice sequences, held tickets, shifts and movements.
- Preserve original machine names as TerminalName and add physical OutletId/name, TerminalCode, IP and BIR fields.
- Preserve invoice counters, local order IDs, payments, outbox status and raw PayloadJson bytes.
- Read old held/order snapshot JSON with `LegacyTerminalPayload`; it translates the old machine ID in memory only, preserving persisted bytes and idempotency hashes.
- Unknown physical ownership remains 0 until refreshed online. Legacy offline sessions/new sales are blocked until that refresh; pending records remain available for reconciliation/upload.

Do not downgrade an upgraded SQLite file with old binaries. Restore the matching verified local backup while writes are stopped, or forward-fix.

## Verified results

- `dotnet run --project POS.Tests/POS.Tests.csproj --no-restore --verbosity quiet`: **37 passed, 0 failed**. Includes existing cash/approval/idempotency/history tests plus hierarchy CRUD, SQLite in-place upgrade, legacy payload compatibility, terminal resolution, sale/payment/invoice/receipt, upload replay/collision, distinct online/offline invoice allocation, cash-movement scoping, replay correlation, supervisor cross-terminal recovery, and immutable deactivation cutoff enforcement.
- API, Web and Windows Client builds: successful, no compile errors. Client was built to a scratch output directory so a running POS process would not need to be killed.
- `python POS.Tests/verify_hierarchy_postgres.py`: uses the committed synthetic baseline `POS.Tests/Fixtures/legacy_hierarchy.sql` and an explicitly isolated PostgreSQL endpoint; never reads application connection settings. An optional second argument supplies the generated EF script.
- `python POS.Tests/verify_receipt_printer.py`: **6 passed**; source-level checks only, no paper output claim.
- PostgreSQL 18 fixture: seeded original terminals, invoice/order sequences, historical order and cash payment; raw migration preserves IDs, invoice/payment snapshots, BIR configuration and sequence continuation. Invalid ownership fails. Duplicate devices cause transaction-wide rollback.
- Generated EF idempotent SQL applied to another seeded legacy fixture twice successfully.
- Actual EF/Npgsql query probe loaded migrated Terminal → Outlet → Branch, original invoice series and historical receipt/reprint.
- Existing ManageBranches Razor component rendered with a selected Outlet/Terminal using HtmlRenderer and fixture services; asserted location/type/code/IP forms, terminal identity, counts and invoice controls. This is not a full browser interaction test.

## Rollout, backup and rollback

See `docs/BRANCH_OUTLET_TERMINAL_PLAN.md`. Stop writes; reconcile/sync pending terminals; make a PostgreSQL custom-format backup plus consistent per-terminal SQLite backups and retain old binaries. Restore to a clone and compare pre/post machine IDs, invoice strings, counters, payment totals, audit counts and outbox hashes. Apply reviewed EF script manually during maintenance, deploy all consumers together, reauthenticate and refresh terminal caches before offline sales.

Legacy API startup SQL is now opt-in through `Database:RunLegacyMigrations`. Hierarchy migrations are never automatically applied at startup. Do not enable the old runner as a substitute for reviewed migration deployment.

Automatic Down is refused because it could discard new physical locations. If no new writes occurred, restore the matched backups while applications are stopped. If new transactions exist, audit/reconcile and forward-fix; never silently restore over new sales.

## Remaining risks and limits

- No production-data migration or live-device upgrade has been performed. Production views/triggers/custom constraints and external integrations need a restored-clone rehearsal.
- No physical printer is attached. Existing printer code was left unchanged; hardware paper output is unverified.
- No Android, iOS or MacCatalyst build; no end-to-end browser/cashier UI clicking against a real deployment.
- Existing dependency vulnerability warnings, source-embedded connection-string warning and unrelated nullable/UI warnings remain. Secrets are not reproduced here.
- Offline blocks may create numbering gaps; exhausted ranges stop checkout. Legacy fallback invoices require reconciliation. Inactive-device recovery is an audited operator/API workflow, not a new admin screen. This refactor does not establish BIR certification/legal compliance.
- DHCP/IP identification needs stable network assignment; IP is not a secure device credential. Existing API-wide authorization/financial-hardening gaps outside the touched identity boundaries still require a separate security audit.
- First independent review found four blockers: unscoped sync, incorrect fallback acknowledgement, inactive-history recovery, and cross-field address collision. These were corrected with new regression coverage; follow-up independent review remains pending. No approval or production-readiness claim is made.
- PostgreSQL migration uses `pg_input_is_valid` (PostgreSQL 16+); verified on 18.6. Older deployments need compatibility review before rollout.
- Source inspection and passing tests do not prove all runtime/platform behavior.
