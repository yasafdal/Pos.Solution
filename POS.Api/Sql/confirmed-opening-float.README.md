# Confirmed opening float deployment

## Scope and safety

- Migration: `20261026035721_ConfirmedOpeningFloat`, after `20261021180419_OrderServiceModeAndTable`.
- Deploy only to an existing, verified POS schema. This is not a fresh database bootstrap.
- `confirmed-opening-float.ef.sql` is the standalone **delta**, generated using EF `--idempotent` and prefixed with `CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"` because range scripts do not bootstrap history. It records only this migration; it does not pretend earlier migrations ran.
- `confirmed-opening-float.sql` remains unchanged and compatible. Applying manual SQL first, then EF SQL is supported; all additions use `IF NOT EXISTS`.
- No startup/live execution was performed. Design-time tooling uses `PosDbContextFactory` with `POS_MIGRATION_CONNECTION`, not API startup or its legacy migrations.

## Before deployment

1. Stop POS writes and uploads; drain queued requests. Schedule a maintenance window: regular unique-index creation can block writes and needs adequate disk space.
2. Verify target host/database explicitly with `SELECT current_database(), inet_server_addr(), inet_server_port();`. Do not use an implicit/default connection.
3. Take a full custom-format backup: `pg_dump --format=custom --file=before-opening-float.dump "$POS_MIGRATION_CONNECTION"`. List it using `pg_restore --list before-opening-float.dump` and restore it into a separate disposable database to verify recovery. Protect backup credentials/data.
4. Verify the baseline tables, migration history and current indexes. Reconcile duplicate active drawers without deleting audit history:
   ```sql
   SELECT "TerminalId", count(*) FROM "CashDrawerShifts"
   WHERE "Status"='OPEN' GROUP BY "TerminalId" HAVING count(*)>1;
   SELECT indexname, indexdef FROM pg_indexes
   WHERE schemaname=current_schema() AND tablename='CashDrawerShifts';
   ```
   If `ClientShiftId` is already present, also run:
   ```sql
   SELECT "ClientShiftId", count(*) FROM "CashDrawerShifts"
   WHERE "ClientShiftId" IS NOT NULL GROUP BY "ClientShiftId" HAVING count(*)>1;
   ```
   All duplicate queries must return no rows. Migration readiness checks abort rather than resolve duplicates silently. `IF NOT EXISTS` does not validate an existing index's definition: confirm the named indexes are UNIQUE, valid, on the correct columns, with the exact predicates shown in the script. Confirm existing columns have compatible types/nullability/defaults; do not proceed on name alone.
5. Existing OPEN rows remain unconfirmed, even when float is positive. Reconcile/close legacy drawers before accepting new confirmed openings; do not bulk-set confirmation or rewrite historical opening time/amount.

## Apply and verify

```bash
psql "$POS_MIGRATION_CONNECTION" -X -v ON_ERROR_STOP=1 -f POS.Api/Sql/confirmed-opening-float.ef.sql
```

The DDL is transactional, additive and safely repeatable. It does not drop the previous nonunique terminal index. Verify:

```sql
SELECT column_name, data_type, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name='CashDrawerShifts'
  AND column_name IN ('OpeningConfirmed','ClientShiftId');
SELECT indexname, indexdef FROM pg_indexes
WHERE schemaname=current_schema() AND tablename='CashDrawerShifts';
SELECT * FROM "__EFMigrationsHistory"
WHERE "MigrationId"='20261026035721_ConfirmedOpeningFloat';
```

Expected: non-null boolean confirmation default false, nullable varchar(100) identity, unique non-null identity and unique terminal WHERE Status='OPEN', one history row. Compare pre/post legacy shift counts and financial totals. Smoke-test explicit zero opening, duplicate opening conflict, payment, close, and offline replay in staging before resuming writes.

## Rollback

`Down` intentionally throws `NotSupportedException`; automatic downgrade must not erase opening identity/confirmation audit data or remove guards. Normally roll back application binaries **while retaining the additive schema**. If application rollback would bypass confirmation controls, keep checkout stopped until approved. For a full data rollback, stop writes, preserve post-deployment data separately, restore the verified pre-deployment backup to a separate database, validate reconciliation, then perform an explicitly approved DBA cutover. Do not drop columns/indexes or delete migration history as an emergency shortcut.

## Reproduce generation and verification

```bash
dotnet-ef migrations script 20261021180419_OrderServiceModeAndTable 20261026035721_ConfirmedOpeningFloat --idempotent --project POS.Api --output generated-opening-delta.sql
dotnet-ef migrations has-pending-model-changes --project POS.Api --context PosDbContext
dotnet run --project POS.Client.Tests
# PostgreSQL harness DROPS public schema. Only a disposable loopback DB named pos_opening_test_* is accepted.
POS_TEST_POSTGRES_CONNECTION='Host=127.0.0.1;Port=55439;Database=pos_opening_test_verification;Username=pos_test' dotnet run --project POS.Client.Tests
```

The linked-client executable uses real client sources and scratch SQLite files, including a fresh child process restart. It verifies no network use during offline opening/checkout. PostgreSQL tests run real controllers with independent DbContexts, pause the first operation after terminal lock acquisition and observe the second backend in `pg_stat_activity.wait_event_type='Lock'` before release. Both orderings are exercised for CASH and CARD. Full baseline migration-chain bootstrap is outside this additive migration test; controller tests create a disposable schema from the current EF model.
