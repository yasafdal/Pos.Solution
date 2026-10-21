# Branch / Outlet / Terminal refactor

## Examined baseline

The existing `Outlets` rows are machines, not locations: device/IP lookup, PTU/MIN/serial fields, order and invoice sequences, cashier sessions, cash shifts, orders, held tickets, reprints and returns all use the old IDs as terminal identity. Inventory remains branch-owned. The reference inventory is captured under the task scratch directory; generated/vendor files are excluded. Existing regression baseline: 22 passing scenarios; API/Web/Windows Client builds pass with existing warnings. The developer's MauiProgram development IP override is preserved.

No EF migration history exists in this repository. Schema was scaffolded and extended through a startup SQL runner. Migration must therefore explicitly target an existing baseline database, not infer a destructive drop/create from renamed CLR types. Existing API startup migration execution will become opt-in to prevent automatic production alterations.

## Dependency-safe sequence

1. Rename classified machine contracts and references to Terminal/TerminalId, including raw SQL column names, HTTP route/query names and JWT terminal_id scope. Keep BranchDto.Outlets for the new physical locations. Distinguish device identifier text from numeric terminal primary keys.
2. Rename existing PostgreSQL Outlets to Terminals and primary-key column in place. Rename terminal references without changing their values. Create physical Outlets, seed one MAIN location for each branch and backfill Terminals.OutletId. Validate ownership, duplicates and orphaned history before constraints/NOT NULL. Retain terminal BranchId with composite ownership validation during transition.
3. Add validated administrator Outlet/Terminal CRUD and uniqueness constraints. Do not permit terminal relocation after transactional or invoice history, which would change historical location reporting. Keep invoice sequences associated with original terminal IDs and preserve issued numbers/counters.
4. Upgrade SQLite in one transaction: rename machine columns/config table in place, retain pending JSON bytes and idempotency hashes; deserialize old cached held-sale JSON through an explicit compatibility reader. Require an online configuration refresh before creating new sales when a legacy cache has no physical outlet mapping. Preserve invoice counters, pending orders, payments and outbox statuses.
5. Enhance the existing Branch screen with physical Outlet forms and nested Terminal management, preserving existing branch and invoice-series workflow and house styles. Update reports to distinguish terminal versus physical outlet filters.
6. Verify with disposable SQLite and PostgreSQL fixtures, including old-data rename preservation, identity sequence continuity, ownership/uniqueness validation and controller regression tests. Build all projects. Do not run migrations against user databases.

## Deployment, backup and rollback

Stop all cashiers and upload pending offline work where possible. Take a PostgreSQL custom-format backup and a consistent SQLite backup from each terminal (include WAL safely using SQLite backup, not a blind copy during writes). Keep old application binaries. Rehearse on a restored clone first. Inventory old terminal IDs, invoice series/counters, historical invoice numbers, transaction/payment totals and outbox hashes before migration and compare afterward.

Deploy API, Shared consumers, Web and Client together during maintenance; old API contracts and JWT machine claims change. Force reauthentication after deployment. Existing terminals receive a default MAIN outlet under their original branch because the old database contains no physical-location classification. Administrators must review this mapping; the migration cannot infer the real booth/kiosk from a register name.

If validation fails, transaction rollback preserves the baseline. After a successful migration, roll back using the tested pre-migration backups while writes remain stopped. Do not run a destructive Down migration or restore over new transactions. If new sales occurred, reconcile and forward-fix instead of silently discarding them. The EF Down path must refuse unsafe automatic reversal.

## Risks and verification boundaries

IP-based registration is identification, not a device authentication secret. DHCP changes need a reservation or updated registration. Printer hardware is unavailable. Offline refresh now reserves distinct 1000-number blocks under the server invoice lock. This avoids online/offline overlap but can create gaps; obtain operational/BIR approval before production. Preserve counters and reject conflicting invoice replays rather than treating another terminal's invoice as success. Do not claim BIR certification or legal compliance from this structural refactor. Production schema may have external views/triggers/integrations not represented in the source; preflight and backup review are mandatory.
