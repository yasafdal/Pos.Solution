#!/usr/bin/env python
"""Real PostgreSQL guards/barrier tests, ONLY in a new disposable scratch cluster.

No application startup, configured connection, migration, print, or existing cluster.
Requires explicit --run-isolated. Uses stdlib + native PostgreSQL binaries only.
The cluster is stopped in finally; data, SQL transcript and evidence remain in scratch.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path('C:/Users/yaser/AppData/Local/hermes/cache/scratch')
TABLES = ('Orders', 'OrderItems', 'OrderItemModifiers', 'Payments', 'CashDrawerShifts',
          'CashDrawerMovements', 'SalesReturns', 'SalesReturnItems', 'Refunds',
          'TransactionVoids', 'HeldSales', 'HeldSaleItems', 'HeldSaleItemModifiers', 'HeldSaleAuditLogs')

# Exact guard-facing names/types from the models; non-guard columns are deliberately
# omitted. This is NOT an EF schema/migration or application integration test.
SCHEMA = '''
CREATE TABLE public."Terminals" ("TerminalId" integer PRIMARY KEY, "BranchId" integer NOT NULL, "OutletId" integer NOT NULL);
CREATE TABLE public."Orders" ("OrderId" bigint PRIMARY KEY, "TerminalId" integer NOT NULL REFERENCES "Terminals", "CreatedAt" timestamp NOT NULL, "InvoiceIssuedAt" timestamp, "InvoiceNumber" text, "PaymentStatus" text DEFAULT 'PAID', "TotalAmount" numeric DEFAULT 100);
CREATE TABLE public."OrderItems" ("OrderItemId" bigint PRIMARY KEY, "OrderId" bigint NOT NULL REFERENCES "Orders" ON DELETE CASCADE, "ProductName" text DEFAULT 'Historical coffee', "LineTotal" numeric DEFAULT 100);
CREATE TABLE public."OrderItemModifiers" ("OrderItemModifierId" bigint PRIMARY KEY, "OrderItemId" bigint NOT NULL REFERENCES "OrderItems" ON DELETE CASCADE, "AdditionalPrice" numeric DEFAULT 0);
CREATE TABLE public."Payments" ("PaymentId" bigint PRIMARY KEY, "OrderId" bigint NOT NULL REFERENCES "Orders" ON DELETE CASCADE, "PaidAt" timestamp NOT NULL, "PaymentStatus" text DEFAULT 'PAID', "Amount" numeric DEFAULT 100);
CREATE TABLE public."CashDrawerShifts" ("ShiftId" bigint PRIMARY KEY, "TerminalId" integer NOT NULL REFERENCES "Terminals", "OpenedAt" timestamp NOT NULL, "ClosedAt" timestamp, "TotalCashRefunds" numeric DEFAULT 0, "ClosingRecountHistoryJson" text);
CREATE TABLE public."CashDrawerMovements" ("MovementId" bigint PRIMARY KEY, "ShiftId" bigint NOT NULL REFERENCES "CashDrawerShifts" ON DELETE CASCADE, "TerminalId" integer NOT NULL REFERENCES "Terminals", "CreatedAt" timestamp NOT NULL, "ApprovedAt" timestamp, "PostedAt" timestamp, "ReversedAt" timestamp, "Amount" numeric DEFAULT 5);
CREATE TABLE public."SalesReturns" ("ReturnId" bigint PRIMARY KEY, "OriginalOrderId" bigint NOT NULL REFERENCES "Orders", "TerminalId" integer NOT NULL REFERENCES "Terminals", "CreatedAt" timestamp NOT NULL, "CompletedAt" timestamp, "TotalRefundAmount" numeric DEFAULT 10);
CREATE TABLE public."SalesReturnItems" ("ReturnItemId" bigint PRIMARY KEY, "ReturnId" bigint NOT NULL REFERENCES "SalesReturns" ON DELETE CASCADE, "OriginalOrderItemId" bigint NOT NULL REFERENCES "OrderItems", "RefundAmount" numeric DEFAULT 10);
CREATE TABLE public."TransactionVoids" ("VoidId" bigint PRIMARY KEY, "OriginalOrderId" bigint NOT NULL REFERENCES "Orders", "RequestedAt" timestamp NOT NULL, "ApprovedAt" timestamp NOT NULL, "VoidedAt" timestamp NOT NULL, "Status" text DEFAULT 'APPROVED');
CREATE TABLE public."Refunds" ("RefundId" bigint PRIMARY KEY, "ReturnId" bigint REFERENCES "SalesReturns" ON DELETE SET NULL, "VoidId" bigint REFERENCES "TransactionVoids" ON DELETE SET NULL, "OriginalPaymentId" bigint NOT NULL REFERENCES "Payments", "RequestedAt" timestamp NOT NULL, "CompletedAt" timestamp NOT NULL, "RefundAmount" numeric DEFAULT 10);
CREATE TABLE public."HeldSales" ("HeldSaleId" bigint PRIMARY KEY, "TerminalId" integer NOT NULL REFERENCES "Terminals", "CreatedAt" timestamp NOT NULL, "ResumedAt" timestamp, "Status" text DEFAULT 'COMPLETED');
CREATE TABLE public."HeldSaleItems" ("HeldSaleItemId" bigint PRIMARY KEY, "HeldSaleId" bigint NOT NULL REFERENCES "HeldSales" ON DELETE CASCADE, "ProductName" text DEFAULT 'held');
CREATE TABLE public."HeldSaleItemModifiers" ("HeldSaleItemModifierId" bigint PRIMARY KEY, "HeldSaleItemId" bigint NOT NULL REFERENCES "HeldSaleItems" ON DELETE CASCADE, "ModifierName" text DEFAULT 'held modifier');
CREATE TABLE public."HeldSaleAuditLogs" ("AuditId" bigint PRIMARY KEY, "HeldSaleId" bigint, "TerminalId" integer NOT NULL REFERENCES "Terminals", "CreatedAt" timestamp NOT NULL, "Details" text);
INSERT INTO "Terminals" VALUES (1,1,1),(2,1,1),(3,1,1),(4,1,1),(5,1,1),(6,1,1);
'''

SEED = '''
INSERT INTO "ZReadingTerminalStates" ("TerminalId","FirstBusinessDate","OpeningAccumulatedSales","AccumulatedGrossSales","ConfiguredBy","ConfiguredAt")
SELECT t,'2026-10-30',0,0,'isolated-test','2026-10-29' FROM generate_series(1,5) t;
INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt","InvoiceIssuedAt","InvoiceNumber") VALUES (1,1,'2026-10-30 08:00','2026-10-30 08:00','OLD'),(2,1,'2026-10-30 21:00','2026-10-30 21:00','NEW'),(3,2,'2026-10-30 21:00','2026-10-30 21:00','OTHER');
INSERT INTO "OrderItems" ("OrderItemId","OrderId") VALUES (1,1),(2,2),(3,3);
INSERT INTO "OrderItemModifiers" ("OrderItemModifierId","OrderItemId") VALUES (1,1),(2,2);
INSERT INTO "Payments" ("PaymentId","OrderId","PaidAt") VALUES (1,1,'2026-10-30 08:00'),(2,2,'2026-10-30 21:00');
INSERT INTO "CashDrawerShifts" ("ShiftId","TerminalId","OpenedAt","ClosedAt") VALUES (1,1,'2026-10-30 01:00','2026-10-30 09:00'),(2,1,'2026-10-30 20:00',NULL);
INSERT INTO "CashDrawerMovements" ("MovementId","ShiftId","TerminalId","CreatedAt") VALUES (1,1,1,'2026-10-30 02:00'),(2,2,1,'2026-10-30 21:00');
INSERT INTO "SalesReturns" ("ReturnId","OriginalOrderId","TerminalId","CreatedAt","CompletedAt") VALUES (1,1,1,'2026-10-30 09:00','2026-10-30 09:00');
INSERT INTO "SalesReturnItems" ("ReturnItemId","ReturnId","OriginalOrderItemId") VALUES (1,1,1);
INSERT INTO "TransactionVoids" ("VoidId","OriginalOrderId","RequestedAt","ApprovedAt","VoidedAt") VALUES (1,1,'2026-10-30 09:00','2026-10-30 09:00','2026-10-30 09:00');
INSERT INTO "Refunds" ("RefundId","ReturnId","OriginalPaymentId","RequestedAt","CompletedAt") VALUES (1,1,1,'2026-10-30 09:00','2026-10-30 09:00');
INSERT INTO "HeldSales" ("HeldSaleId","TerminalId","CreatedAt") VALUES (1,1,'2026-10-30 08:00'),(2,1,'2026-10-30 21:00');
INSERT INTO "HeldSaleItems" ("HeldSaleItemId","HeldSaleId") VALUES (1,1),(2,2);
INSERT INTO "HeldSaleItemModifiers" ("HeldSaleItemModifierId","HeldSaleItemId") VALUES (1,1),(2,2);
INSERT INTO "HeldSaleAuditLogs" ("AuditId","HeldSaleId","TerminalId","CreatedAt") VALUES (1,1,1,'2026-10-30 08:00'),(2,2,1,'2026-10-30 21:00');
'''


def snapshot(terminal: int, day='2026-10-30', number=1):
    token = str(uuid.uuid4())
    return f'''INSERT INTO "ZReadings" ("Id","TerminalId","BranchId","OutletId","BusinessDate","PeriodStartUtc","PeriodEndUtc","GeneratedAt","GeneratedBy","ClientOperationId","DeviceIdentifier","ZNumber","SnapshotJson","SnapshotSha256")
VALUES ('{token}',{terminal},1,1,'{day}','{day}'::timestamp-interval '4 hours','{day}'::timestamp+interval '20 hours',timezone('UTC',now()),'test','{token}','scratch',{number},'{{"original":"retained"}}',upper(encode(sha256(convert_to('{{"original":"retained"}}','UTF8')),'hex')));'''


class Cluster:
    def __init__(self, pg_bin: Path):
        self.pg_bin = pg_bin
        self.run = SCRATCH / ('pos-z-guards-' + uuid.uuid4().hex)
        self.run.mkdir(parents=True)
        self.data = self.run / 'data'
        self.env = {k: v for k, v in os.environ.items() if not k.upper().startswith('PG')}
        with socket.socket() as s:
            s.bind(('127.0.0.1', 0))
            self.port = s.getsockname()[1]
        self.started = False
        self.results = []
        self.log = self.run / 'verification.log'

    def record(self, text):
        with self.log.open('a', encoding='utf-8') as f:
            f.write(text + '\n')

    def command(self, binary, *args):
        # Windows postmaster inherits pg_ctl pipe handles: capture_output can wait
        # forever after pg_ctl exits. Real files avoid that inherited-pipe deadlock.
        transcript = self.run / (binary + '-command.log')
        with transcript.open('w', encoding='utf-8') as output:
            p = subprocess.run([str(self.pg_bin / (binary + '.exe')), *map(str, args)], env=self.env,
                               text=True, stdout=output, stderr=subprocess.STDOUT, timeout=90)
        result = transcript.read_text(encoding='utf-8')
        self.record(f'{binary}: {result}')
        if p.returncode:
            raise RuntimeError(f'{binary} failed ({p.returncode}): {result}')
        return result

    def start(self):
        self.command('initdb', '-D', self.data, '-U', 'pos_z_test', '--auth=trust', '--encoding=UTF8', '--locale=C')
        # Mark ownership before starting so a partial startup still reaches cleanup.
        self.started = True
        self.command('pg_ctl', '-D', self.data, '-l', self.run / 'postgres.log', '-o',
                     f'-p {self.port} -h 127.0.0.1 -c max_connections=20', '-w', 'start')
        self.record(f'ISOLATED cluster={self.data} host=127.0.0.1 port={self.port} database=postgres')

    def stop(self):
        if self.started:
            self.command('pg_ctl', '-D', self.data, '-m', 'fast', '-w', 'stop')
            self.started = False

    def psql_args(self):
        return [str(self.pg_bin / 'psql.exe'), '-X', '--no-password', '-h', '127.0.0.1', '-p', str(self.port),
                '-U', 'pos_z_test', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose', '-A', '-t']

    def sql(self, text):
        self.record('SQL:\n' + text)
        p = subprocess.run(self.psql_args(), input=text, env=self.env, text=True, capture_output=True, timeout=40)
        self.record(f'OUTPUT ({p.returncode}):\n{p.stdout}{p.stderr}')
        if p.returncode:
            raise AssertionError(f'psql failed: {p.stdout}{p.stderr}')
        return p.stdout.strip()

    def check(self, name, text):
        self.sql(text)
        self.results.append({'name': name, 'passed': True})
        print('PASS ' + name, flush=True)

    def blocked(self, name, statement):
        self.check(name, f'''DO $test$ BEGIN
BEGIN {statement}; RAISE EXCEPTION 'UNEXPECTED ALLOW: {name}';
EXCEPTION WHEN check_violation THEN
    IF SQLERRM NOT LIKE '%Z-Reading%' AND SQLERRM NOT LIKE '%Return%' AND SQLERRM NOT LIKE '%Refund%' THEN RAISE; END IF;
END; END $test$;''')

    def race(self, name, terminal, writer_isolation, finalizer_first):
        """Two actual psql sessions, markers and pg_stat_activity lock wait (no blind sleep)."""
        procs = []
        outputs = [[], []]
        errors = [[], []]
        queues = []
        import queue
        for idx in range(2):
            q = queue.Queue()
            p = subprocess.Popen(self.psql_args(), env=self.env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, text=True, bufsize=1)
            procs.append(p); queues.append(q)
            def pump(stream, dest, q=None):
                for line in stream:
                    dest.append(line)
                    if q is not None: q.put(line.strip())
            threading.Thread(target=pump, args=(p.stdout, outputs[idx], q), daemon=True).start()
            threading.Thread(target=pump, args=(p.stderr, errors[idx]), daemon=True).start()
        def send(i, sql):
            self.record(f'RACE {name} session {i}: {sql}')
            procs[i].stdin.write(sql + '\n'); procs[i].stdin.flush()
        def marker(i, expected):
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                try:
                    if queues[i].get(timeout=.1) == expected: return
                except queue.Empty:
                    if procs[i].poll() is not None: break
            raise AssertionError(f'missing {expected}: {outputs[i]} {errors[i]}')
        def wait_lock(app):
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                if self.sql(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}' AND wait_event_type='Lock';") == '1': return
                time.sleep(.05)
            raise AssertionError('expected actual lock wait for ' + app)
        writer = f'''INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt","InvoiceIssuedAt") VALUES ({terminal * 100},{terminal},'2026-10-30 08:00','2026-10-30 08:00');'''
        try:
            send(0, f"SET application_name='z_writer_{terminal}'; BEGIN ISOLATION LEVEL {writer_isolation}; SELECT count(*) FROM \"ZReadings\"; SELECT 'writer_snapshot';")
            marker(0, 'writer_snapshot')
            send(1, f"SET application_name='z_finalizer_{terminal}'; BEGIN; SELECT public.pos_z_lock_terminal({terminal}); SELECT 'finalizer_locked';" if finalizer_first else f"SET application_name='z_finalizer_{terminal}'; BEGIN;")
            if finalizer_first:
                marker(1, 'finalizer_locked')
                send(0, writer)
                wait_lock(f'z_writer_{terminal}')
                send(1, snapshot(terminal) + " COMMIT; SELECT 'finalized';")
                marker(1, 'finalized')
                procs[0].wait(timeout=15)
                state = '40001' if writer_isolation == 'SERIALIZABLE' else '23514'
                if state not in ''.join(errors[0]):
                    raise AssertionError(f'expected SQLSTATE {state}: {errors[0]}')
                if self.sql(f'SELECT count(*) FROM "Orders" WHERE "OrderId"={terminal * 100};') != '0':
                    raise AssertionError('late writer persisted')
            else:
                send(0, writer + " SELECT 'writer_locked';")
                marker(0, 'writer_locked')
                send(1, f"SELECT public.pos_z_lock_terminal({terminal}); SELECT 'finalizer_after_writer';")
                wait_lock(f'z_finalizer_{terminal}')
                send(0, "COMMIT; SELECT 'writer_committed';")
                marker(0, 'writer_committed'); marker(1, 'finalizer_after_writer')
                send(1, f'''SELECT CASE WHEN (SELECT count(*) FROM "Orders" WHERE "OrderId"={terminal * 100})=1 THEN 'writer_visible' ELSE 'MISSING' END;''')
                marker(1, 'writer_visible')
                send(1, snapshot(terminal) + " COMMIT; SELECT 'finalized';")
                marker(1, 'finalized')
            self.results.append({'name': name, 'passed': True})
            print('PASS ' + name, flush=True)
        finally:
            for p in procs:
                if p.poll() is None:
                    try: p.stdin.write('ROLLBACK;\n\\q\n'); p.stdin.flush(); p.wait(timeout=5)
                    except (OSError, subprocess.TimeoutExpired): p.kill(); p.wait(timeout=5)
            for i in range(2): self.record(f'RACE {name} session {i} stdout={"".join(outputs[i])} stderr={"".join(errors[i])}')


def promote_reviewed_script(path):
    """Scratch-only approval, replacing the script's LAST transaction terminator."""
    sql = path.read_text(encoding='utf-8')
    if sql.rstrip().endswith('ROLLBACK;'):
        idx = sql.rfind('ROLLBACK;')
        sql = sql[:idx] + 'COMMIT;' + sql[idx + len('ROLLBACK;'):]
    return sql


def run(c):
    c.sql(SCHEMA)
    c.sql(promote_reviewed_script(ROOT / 'POS.Api/Sql/z-reading.sql'))
    guards = ROOT / 'POS.Api/Sql/z-reading-period-guards.sql'
    # Deployment dry run must not leave a readiness function installed.
    c.sql(guards.read_text(encoding='utf-8'))
    assert c.sql("SELECT to_regprocedure('public.pos_z_lock_terminal(integer)') IS NULL;") == 't'
    c.check('dry-run rolls back', "SELECT 1;")
    c.sql(promote_reviewed_script(guards))
    c.sql(promote_reviewed_script(guards))
    assert c.sql("SELECT count(*) FROM pg_trigger WHERE tgname IN ('pos_z_fiscal_row','pos_z_fiscal_truncate') AND NOT tgisinternal;") == str(len(TABLES) * 2)
    c.check('idempotent complete trigger installation', 'SELECT 1;')
    c.check('readiness verifies enabled triggers and exact function OID', '''
DO $$ BEGIN IF NOT public.pos_z_guards_ready() THEN RAISE EXCEPTION 'complete guards not ready'; END IF; END $$;
BEGIN;
ALTER TABLE "Orders" DISABLE TRIGGER pos_z_fiscal_row;
DO $$ BEGIN IF public.pos_z_guards_ready() THEN RAISE EXCEPTION 'disabled trigger accepted'; END IF; END $$;
ROLLBACK;
BEGIN;
CREATE OR REPLACE TRIGGER pos_z_fiscal_row BEFORE INSERT OR UPDATE OR DELETE ON "Orders" FOR EACH ROW EXECUTE FUNCTION public.pos_z_snapshot_fence();
DO $$ BEGIN IF public.pos_z_guards_ready() THEN RAISE EXCEPTION 'wrong function OID accepted'; END IF; END $$;
ROLLBACK;
BEGIN;
CREATE OR REPLACE TRIGGER pos_z_fiscal_row BEFORE INSERT OR UPDATE OR DELETE ON "Orders" FOR EACH ROW WHEN (false) EXECUTE FUNCTION public.pos_z_guard_fiscal_row();
DO $$ BEGIN IF public.pos_z_guards_ready() THEN RAISE EXCEPTION 'conditional bypass accepted'; END IF; END $$;
ROLLBACK;
BEGIN;
DROP TRIGGER pos_z_fiscal_truncate ON "HeldSaleAuditLogs";
DO $$ BEGIN IF public.pos_z_guards_ready() THEN RAISE EXCEPTION 'missing held audit trigger accepted'; END IF; END $$;
ROLLBACK;
DO $$ BEGIN IF NOT public.pos_z_guards_ready() THEN RAISE EXCEPTION 'rolled back guards not ready'; END IF; END $$;
''')
    c.sql(SEED)
    c.sql(snapshot(1))
    c.check('current writes and exact cutoff allowed', '''
INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt","InvoiceIssuedAt") VALUES (10,1,'2026-10-30 20:00','2026-10-30 20:00');
UPDATE "Orders" SET "TotalAmount"=101 WHERE "OrderId"=2;
UPDATE "OrderItems" SET "LineTotal"=101 WHERE "OrderItemId"=2;
UPDATE "OrderItemModifiers" SET "AdditionalPrice"=1 WHERE "OrderItemModifierId"=2;
UPDATE "Payments" SET "Amount"=101 WHERE "PaymentId"=2;
UPDATE "CashDrawerShifts" SET "TotalCashRefunds"=1 WHERE "ShiftId"=2;
UPDATE "CashDrawerMovements" SET "Amount"=6 WHERE "MovementId"=2;
UPDATE "HeldSales" SET "Status"='CANCELLED' WHERE "HeldSaleId"=2;
UPDATE "HeldSaleItems" SET "ProductName"='current' WHERE "HeldSaleItemId"=2;
UPDATE "HeldSaleItemModifiers" SET "ModifierName"='current' WHERE "HeldSaleItemModifierId"=2;
UPDATE "HeldSaleAuditLogs" SET "Details"='current' WHERE "AuditId"=2;
DELETE FROM "Orders" WHERE "OrderId"=10;
''')
    for table, pk in zip(TABLES, ('OrderId','OrderItemId','OrderItemModifierId','PaymentId','ShiftId','MovementId','ReturnId','ReturnItemId','RefundId','VoidId','HeldSaleId','HeldSaleItemId','HeldSaleItemModifierId','AuditId')):
        c.blocked('historical update ' + table, f'UPDATE "{table}" SET "{pk}"="{pk}" WHERE "{pk}"=1')
        c.blocked('historical delete ' + table, f'DELETE FROM "{table}" WHERE "{pk}"=1')
    c.blocked('late offline order', '''INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt","InvoiceIssuedAt") VALUES (20,1,'2026-10-30 08:00','2026-10-30 08:00')''')
    c.blocked('pre-baseline backfill', '''INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt") VALUES (20,1,'2020-01-01')''')
    c.blocked('old to new terminal date escape', '''UPDATE "Orders" SET "TerminalId"=6,"CreatedAt"='2026-10-30 21:00',"InvoiceIssuedAt"='2026-10-30 21:00' WHERE "OrderId"=1''')
    c.blocked('new to old event mutation', '''UPDATE "Orders" SET "InvoiceIssuedAt"='2026-10-30 08:00' WHERE "OrderId"=2''')
    c.blocked('item reparent escape', '''UPDATE "OrderItems" SET "OrderId"=2 WHERE "OrderItemId"=1''')
    c.blocked('payment reparent escape', '''UPDATE "Payments" SET "OrderId"=2,"PaidAt"='2026-10-30 21:00' WHERE "PaymentId"=1''')
    c.blocked('late payment on current sale', '''INSERT INTO "Payments" ("PaymentId","OrderId","PaidAt") VALUES (20,2,'2026-10-30 08:00')''')
    c.blocked('shift overlap', '''INSERT INTO "CashDrawerShifts" ("ShiftId","TerminalId","OpenedAt","ClosedAt") VALUES (20,1,'2026-10-30 19:00','2026-10-30 21:00')''')
    c.blocked('historical recount', '''UPDATE "CashDrawerShifts" SET "ClosingRecountHistoryJson"='changed' WHERE "ShiftId"=1''')
    c.blocked('late movement on current shift', '''INSERT INTO "CashDrawerMovements" ("MovementId","ShiftId","TerminalId","CreatedAt") VALUES (20,2,1,'2026-10-30 08:00')''')
    c.blocked('current void of finalized sale', '''INSERT INTO "TransactionVoids" ("VoidId","OriginalOrderId","RequestedAt","ApprovedAt","VoidedAt") VALUES (20,1,'2026-10-30 21:00','2026-10-30 21:00','2026-10-30 21:00')''')
    c.blocked('historical refunded status rewrite', '''UPDATE "Payments" SET "PaymentStatus"='REFUNDED' WHERE "PaymentId"=1''')
    c.check('current cross-day return keeps original untouched', '''BEGIN;
INSERT INTO "SalesReturns" ("ReturnId","OriginalOrderId","TerminalId","CreatedAt","CompletedAt") VALUES (2,1,1,'2026-10-30 21:00','2026-10-30 21:00');
INSERT INTO "SalesReturnItems" ("ReturnItemId","ReturnId","OriginalOrderItemId") VALUES (2,2,1);
INSERT INTO "Refunds" ("RefundId","ReturnId","OriginalPaymentId","RequestedAt","CompletedAt") VALUES (2,2,1,'2026-10-30 21:00','2026-10-30 21:00');
UPDATE "SalesReturns" SET "TotalRefundAmount"=20 WHERE "ReturnId"=2;
UPDATE "CashDrawerShifts" SET "TotalCashRefunds"=20 WHERE "ShiftId"=2;
COMMIT;
DO $$ BEGIN
IF (SELECT "PaymentStatus" FROM "Payments" WHERE "PaymentId"=1) <> 'PAID' OR
   (SELECT "InvoiceNumber" FROM "Orders" WHERE "OrderId"=1) <> 'OLD' OR
   (SELECT "ProductName" FROM "OrderItems" WHERE "OrderItemId"=1) <> 'Historical coffee' OR
   (SELECT "SnapshotJson" FROM "ZReadings" WHERE "TerminalId"=1) <> '{"original":"retained"}'
THEN RAISE EXCEPTION 'historical original changed'; END IF; END $$;
''')
    c.blocked('return cannot change original terminal', '''UPDATE "SalesReturns" SET "TerminalId"=2 WHERE "ReturnId"=2''')
    c.blocked('return item wrong original', '''UPDATE "SalesReturnItems" SET "OriginalOrderItemId"=2 WHERE "ReturnItemId"=2''')
    c.blocked('refund missing adjustment event', '''INSERT INTO "Refunds" ("RefundId","OriginalPaymentId","RequestedAt","CompletedAt") VALUES (20,1,'2026-10-30 21:00','2026-10-30 21:00')''')
    c.check('same-day unfinalized void/refund writes', '''BEGIN;
INSERT INTO "TransactionVoids" ("VoidId","OriginalOrderId","RequestedAt","ApprovedAt","VoidedAt") VALUES (2,2,'2026-10-30 21:00','2026-10-30 21:00','2026-10-30 21:00');
INSERT INTO "Refunds" ("RefundId","VoidId","OriginalPaymentId","RequestedAt","CompletedAt") VALUES (3,2,2,'2026-10-30 21:00','2026-10-30 21:00');
UPDATE "Payments" SET "PaymentStatus"='REFUNDED' WHERE "PaymentId"=2;
COMMIT;''')
    c.check('unconfigured terminal historical writes unaffected', '''
INSERT INTO "Orders" ("OrderId","TerminalId","CreatedAt") VALUES (60,6,'2020-01-01');
INSERT INTO "OrderItems" ("OrderItemId","OrderId") VALUES (60,60);
UPDATE "Orders" SET "TotalAmount"=1 WHERE "OrderId"=60;
DELETE FROM "Orders" WHERE "OrderId"=60;
''')
    for statement in ['UPDATE "ZReadings" SET "SnapshotJson"=\'changed\'', 'DELETE FROM "ZReadings"', 'TRUNCATE "ZReadings" CASCADE']:
        c.blocked('snapshot immutable: ' + statement.split()[0], statement)
    for table in TABLES:
        c.blocked('truncate ' + table, f'TRUNCATE "{table}" CASCADE')
    c.race('stale serializable writer fails 40001 after finalizer', 3, 'SERIALIZABLE', True)
    c.race('read-committed waiter sees finalized cutoff', 4, 'READ COMMITTED', True)
    c.race('finalizer waits for writer then sees committed sale', 5, 'READ COMMITTED', False)


def ef_integration(c):
    """Compile a scratch console referencing API; call services, never API startup."""
    c.command('createdb', '-h', '127.0.0.1', '-p', c.port, '-U', 'pos_z_test', 'z_ef_isolated')
    project = c.run / 'ef-integration'
    project.mkdir()
    api = (ROOT / 'POS.Api/POS.Api.csproj').as_posix()
    (project / 'ZIntegration.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{api}" /></ItemGroup></Project>''', encoding='utf-8')
    (project / 'Program.cs').write_text(EF_SOURCE, encoding='utf-8')
    connection = f'Host=127.0.0.1;Port={c.port};Database=z_ef_isolated;Username=pos_z_test'
    p = subprocess.run(['dotnet', 'run', '--project', str(project / 'ZIntegration.csproj'), '--',
                        connection, str(ROOT), str(c.port)], env=c.env, text=True,
                       capture_output=True, timeout=180)
    (c.run / 'ef-integration.log').write_text(p.stdout + p.stderr, encoding='utf-8')
    c.record(f'EF integration exit={p.returncode}\n{p.stdout}{p.stderr}')
    if p.returncode:
        raise AssertionError(f'EF integration failed: see {c.run / "ef-integration.log"}\n{p.stdout[-9000:]}{p.stderr[-9000:]}')
    if 'PASS EF service sequential generation, idempotence and cross-day return' not in p.stdout:
        raise AssertionError('EF success marker missing')
    c.results.append({'name': 'EF exact-model service generation and cross-day return', 'passed': True})
    print('PASS EF exact-model service generation and cross-day return', flush=True)


EF_SOURCE = r'''
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

// Explicitly configured isolated connection ONLY. No API startup or fallback config.
var cs = new NpgsqlConnectionStringBuilder(args[0]);
if (cs.Host != "127.0.0.1" || cs.Database != "z_ef_isolated" || cs.Username != "pos_z_test" || cs.Port != int.Parse(args[2]))
    throw new Exception("Refusing a non-isolated database");
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(cs.ConnectionString).LogTo(Console.WriteLine).Options;
await using var db = new PosDbContext(options);
await db.Database.EnsureCreatedAsync();
await using (var conn = new NpgsqlConnection(cs.ConnectionString)) {
    await conn.OpenAsync();
    foreach (var name in new[] { "z-reading.sql", "z-reading-period-guards.sql" }) {
        var sql = await File.ReadAllTextAsync(Path.Combine(args[1], "POS.Api", "Sql", name));
        if (sql.TrimEnd().EndsWith("ROLLBACK;")) {
            var index = sql.LastIndexOf("ROLLBACK;", StringComparison.Ordinal);
            sql = sql[..index] + "COMMIT;" + sql[(index + "ROLLBACK;".Length)..];
        }
        await using var command = new NpgsqlCommand(sql, conn);
        await command.ExecuteNonQueryAsync();
    }
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var date = new DateOnly(2026,10,29); var (start,end) = ZReadingService.Period(date);
db.Branches.Add(new Branch { BranchId=1,Name="Scratch",TaxType="VAT-Registered",IsActive=true });
db.Outlets.Add(new Outlet { OutletId=1,BranchId=1,OutletCode="SCRATCH",Name="Scratch",IsActive=true });
db.Terminals.Add(new Terminal { TerminalId=1,BranchId=1,OutletId=1,TerminalCode="SCRATCH",Name="Scratch",IsActive=true });
db.Users.AddRange(new User { UserId=1,Username="cashier1",Role="Cashier",PasswordHash="not-a-login",PinCode="1111" },new User { UserId=2,Username="manager1",Role="Manager",PasswordHash="not-a-login",PinCode="9999" });
db.Categories.Add(new Category { CategoryId=1,Name="Scratch",IsActive=true });
db.Products.Add(new Product { ProductId=1,CategoryId=1,Name="Today's coffee",ProductType="FINISHED",Price=100,IsActive=true });
db.CashDrawerShifts.Add(new CashDrawerShift { ShiftId=1,BranchId=1,TerminalId=1,CashierId="cashier1",OpeningConfirmed=true,Status="CLOSED",OpenedAt=start.AddHours(1),ClosedAt=start.AddHours(8) });
db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId=1,FirstBusinessDate=date,ConfiguredBy="isolated-exact-model",ConfiguredAt=start });
Order Sale(long id, decimal amount) {
    var sale = new Order { OrderId=id,OrderNumber="SCRATCH-"+id,BranchId=1,OutletId=1,TerminalId=1,CashierId="cashier1",ShiftId=1,OrderType="DINE_IN",OrderStatus="COMPLETED",PaymentStatus="PAID",CreatedAt=start.AddHours(2),InvoiceIssuedAt=start.AddHours(2),InvoiceNumber="I-"+id,Subtotal=amount,TotalAmount=amount };
    sale.OrderItems.Add(new OrderItem { OrderItemId=id,ProductId=1,ProductName="Historical coffee",Quantity=1,UnitPrice=amount,LineTotal=amount });
    sale.Payments.Add(new Payment { PaymentId=id,PaymentMethod="CASH",Amount=amount,PaymentStatus="PAID",ProcessedBy="cashier1",PaidAt=start.AddHours(2) });
    return sale;
}
db.Orders.AddRange(Sale(1,100),Sale(2,60));
await db.SaveChangesAsync();
// Nonempty fiscalVoids makes the actual Npgsql Contains translation execute.
db.TransactionVoids.Add(new TransactionVoid { VoidId=1,OriginalOrderId=2,VoidReason="test",RequestedBy="manager1",ApprovedBy="manager1",RequestedAt=start.AddHours(3),ApprovedAt=start.AddHours(3),VoidedAt=start.AddHours(3),Status="APPROVED" });
db.Refunds.Add(new Refund { RefundId=1,VoidId=1,OriginalPaymentId=2,RefundMethod="CASH",RefundAmount=60,RequestedAt=start.AddHours(3),CompletedAt=start.AddHours(3) });
await db.SaveChangesAsync();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ZReading:Enabled"]="true" }).Build();
var service = new ZReadingService(db,config);
GenerateZReadingRequest Request(DateOnly d,string op) => new() { TerminalId=1,BusinessDate=d,ClientOperationId=op,DeviceIdentifier="isolated",AllLocalOutboxesDrained=true,NoUnresolvedLocalTransactions=true,SoleDeviceCompletenessConfirmed=true };
var ready = await service.ReadinessAsync(1,date); Require(ready.IsReady,string.Join(";",ready.Blockers));
var first = await service.GenerateAsync(Request(date,"ef-first"),"manager1");
Require(first.GrossSales==160 && first.Voids==60 && first.NetSales==100,"First-day aggregation mismatch");
var retry = await service.GenerateAsync(Request(date,"ef-first"),"manager1"); Require(retry.Id==first.Id,"Retry created second snapshot");
var saved = await db.ZReadings.AsNoTracking().SingleAsync(); var json = saved.SnapshotJson; var hash = saved.SnapshotSha256;
db.CashDrawerShifts.Add(new CashDrawerShift { ShiftId=2,BranchId=1,TerminalId=1,CashierId="cashier1",OpeningConfirmed=true,Status="CLOSED",OpenedAt=end,ClosedAt=end.AddHours(8) });
var ret = new SalesReturn { ReturnId=1,OriginalOrderId=1,BranchId=1,TerminalId=1,ReturnReference="EF-RETURN",ReturnReason="test",RequestedBy="manager1",ApprovedBy="manager1",Status="COMPLETED",TotalRefundAmount=10,CreatedAt=end.AddHours(2),CompletedAt=end.AddHours(2) };
ret.ReturnItems.Add(new SalesReturnItem { OriginalOrderItemId=1,Quantity=.1m,OriginalUnitPrice=100,RefundAmount=10 });
ret.Refunds.Add(new Refund { OriginalPaymentId=1,RefundMethod="CASH",RefundAmount=10,RequestedAt=end.AddHours(2),CompletedAt=end.AddHours(2) });
db.SalesReturns.Add(ret); await db.SaveChangesAsync();
var second = await service.GenerateAsync(Request(date.AddDays(1),"ef-second"),"manager1");
Require(second.GrossSales==0 && second.Returns==10 && second.NetSales==-10 && second.ReturnProducts.Single().ProductName=="Historical coffee","Cross-day return mismatch");
saved = await db.ZReadings.AsNoTracking().SingleAsync(x=>x.Id==first.Id);
Require(saved.SnapshotJson==json && saved.SnapshotSha256==hash,"Original snapshot changed");
Require(await db.ZReadings.CountAsync()==2 && second.ZNumber==2,"Sequence mismatch");
Console.WriteLine("PASS EF service sequential generation, idempotence and cross-day return");
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-isolated', action='store_true', help='Approve creation/start/stop of a NEW scratch-only cluster')
    parser.add_argument('--pg-bin', type=Path, default=Path('C:/Program Files/PostgreSQL/18/bin'))
    args = parser.parse_args()
    if not args.run_isolated:
        parser.error('Explicit --run-isolated approval is required; no connection was made.')
    c = Cluster(args.pg_bin)
    evidence = {'cluster': str(c.data), 'log': str(c.log), 'passed': False, 'tests': c.results}
    try:
        c.start()
        evidence['postgres_version'] = c.sql('SELECT version();')
        run(c)
        evidence['passed'] = True
    except BaseException as exc:
        evidence['error'] = str(exc)
        raise
    finally:
        try:
            c.stop()
            evidence['cluster_stopped'] = not c.started
        finally:
            evidence['test_count'] = len(c.results)
            (c.run / 'evidence.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
            print('EVIDENCE ' + str(c.run / 'evidence.json'), flush=True)
    print(f'PASS {len(c.results)} PostgreSQL checks; isolated cluster stopped.', flush=True)


if __name__ == '__main__':
    main()
