"""Run only against an explicitly supplied disposable PostgreSQL endpoint, never application configuration."""
import json
from pathlib import Path
import subprocess
import sys

PG = Path('C:/Program Files/PostgreSQL/18/bin')
ROOT = Path(__file__).resolve().parents[1]
BASELINE = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "POS.Tests/Fixtures/legacy_hierarchy.sql"
SQL = ROOT / 'POS.Api/Migrations/BranchOutletTerminal.sql'
ARGS = ['-h', '127.0.0.1', '-p', '55437', '-U', 'pos_test']


def run(executable, args, **kwargs):
    result = subprocess.run([str(PG / (executable + '.exe')), *args], text=True, capture_output=True, **kwargs)
    if result.returncode:
        print(result.stderr.strip(), file=sys.stderr)
        result.check_returncode()
    return result


def query(db, sql):
    return run('psql', [*ARGS, '-d', db, '-X', '-v', 'ON_ERROR_STOP=1', '-At', '-c', sql]).stdout.strip()


def setup(db):
    run('createdb', [*ARGS, db])
    run('psql', [*ARGS, '-d', db, '-X', '-v', 'ON_ERROR_STOP=1', '-f', str(BASELINE)])
    query(db, '''INSERT INTO "Branches" ("BranchId","Name","Code","TaxType","TrackInventory") VALUES (7,'Legacy branch','BR7','VAT-Registered',false),(8,'Empty branch','BR8','Non-VAT',false);
        INSERT INTO "Outlets" ("BranchId","Name","DeviceIdentifier","PTUNumber","MINNumber","SerialNumber") VALUES (7,'Register A','192.168.1.42','PTU-KEEP','MIN-KEEP','SN-KEEP'),(7,'Register B','192.168.1.43',NULL,NULL,NULL);
        INSERT INTO "InvoiceSequences" ("outlet_id","series_code","prefix","starting_number","ending_number","last_issued_number","padding_length","is_active","created_at","updated_at") VALUES (1,'KEEP-SER','KEEP-',1,99999,17,6,true,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);
        INSERT INTO "OrderSequences" ("outlet_id","last_issued_number","created_at","updated_at") VALUES (1,29,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);
        INSERT INTO "Orders" ("OrderNumber","BranchId","OutletId","CashierId","OrderType","OrderStatus","PaymentStatus","Subtotal","TotalAmount","VatableSales","VatExemptSales","ZeroRatedSales","VatAmount","DiscountPercentage","InvoiceNumber","invoice_sequence_id") VALUES ('LEGACY-ORDER',7,1,'cashier','RETAIL','COMPLETED','PAID',112,112,100,0,0,12,0,'KEEP-000017',1);
        INSERT INTO "Payments" ("OrderId","PaymentMethod","Amount","AmountTendered","ChangeAmount","PaymentStatus","ProcessedBy") VALUES (1,'CASH',112,120,8,'SUCCESS','cashier');''')


def snapshot(db, terminal_column):
    return json.loads(query(db, f'''SELECT json_build_object('invoices',(SELECT json_agg(json_build_array("InvoiceNumber","Subtotal","TotalAmount",{terminal_column})) FROM "Orders"),'payments',(SELECT json_agg(json_build_array("Amount","AmountTendered","ChangeAmount")) FROM "Payments"),'sequence',(SELECT json_agg(json_build_array("series_code","prefix","last_issued_number")) FROM "InvoiceSequences"))'''))


def migrate(db):
    return run('psql', [*ARGS, '-d', db, '-X', '--single-transaction', '-v', 'ON_ERROR_STOP=1', '-f', str(SQL)])


import uuid
name = 'hierarchy_preserve_' + uuid.uuid4().hex[:8]
setup(name)
before = snapshot(name, '"OutletId"')
migrate(name)
assert snapshot(name, '"TerminalId"') == before, 'Historical financial/numbering snapshots changed'
assert query(name, 'SELECT count(*) FROM "Orders" o JOIN "Outlets" out ON out."OutletId"=o."OutletId"') == '1', 'Orders.OutletId foreign key not backfilled'
assert query(name, 'SELECT count(*) FROM "Outlets"') == '2'
assert query(name, 'SELECT count(*) FROM "Terminals" t JOIN "Outlets" o USING ("OutletId","BranchId")') == '2'
assert query(name, '''SELECT "PTUNumber"||'/'||"MINNumber"||'/'||"SerialNumber" FROM "Terminals" WHERE "TerminalId"=1''') == 'PTU-KEEP/MIN-KEEP/SN-KEEP'
assert query(name, '''INSERT INTO "Terminals" ("BranchId","OutletId","TerminalCode","Name") SELECT 7,"OutletId",'NEW','New terminal' FROM "Outlets" WHERE "BranchId"=7 RETURNING "TerminalId"''').splitlines()[0] == '3', 'Terminal identity sequence reset'
try:
    query(name, '''INSERT INTO "Terminals" ("BranchId","OutletId","TerminalCode","Name") SELECT 8,"OutletId",'WRONG','Invalid' FROM "Outlets" WHERE "BranchId"=7''')
    raise AssertionError('Cross-branch ownership was accepted')
except subprocess.CalledProcessError:
    pass

try:
    query(name, '''INSERT INTO "Terminals" ("BranchId","OutletId","TerminalCode","Name","DeviceIdentifier","IpAddress") SELECT 7,"OutletId",'CROSS-IP','Invalid alias','192.168.1.42','192.168.1.99' FROM "Outlets" WHERE "BranchId"=7''')
    raise AssertionError('Cross-field IP alias was accepted')
except subprocess.CalledProcessError:
    pass

bad = 'hierarchy_rollback_' + uuid.uuid4().hex[:8]
setup(bad)
query(bad, '''UPDATE "Outlets" SET "DeviceIdentifier"='192.168.1.42' WHERE "OutletId"=2''')
try:
    migrate(bad)
    raise AssertionError('Duplicate devices should stop the migration')
except subprocess.CalledProcessError:
    pass
assert query(bad, '''SELECT count(*) FROM "Outlets"''') == '2', 'Failed migration did not restore legacy table'
assert query(bad, '''SELECT to_regclass('"Terminals"') IS NULL''') == 't'
assert snapshot(bad, '"OutletId"') == before
print('PASS: PostgreSQL table/PK rename preserves IDs, invoices, payments, BIR fields and sequence counters.')
print('PASS: Default outlet backfill and composite ownership constraint.')
print('PASS: Terminal identity continues at the next value, without resetting.')
print('PASS: Duplicate-device preflight failure rolls back the complete migration.')
ef_script = Path(sys.argv[2]) if len(sys.argv) > 2 else BASELINE.with_name('hierarchy-ef.sql')
if ef_script.exists():
    efdb = 'hierarchy_ef_' + uuid.uuid4().hex[:8]
    setup(efdb)
    for _ in range(2):
        run('psql', [*ARGS, '-d', efdb, '-X', '-v', 'ON_ERROR_STOP=1', '-f', str(ef_script)])
    assert snapshot(efdb, '"TerminalId"') == before
    print('PASS: Generated EF idempotent script applies to seeded legacy schema and safely repeats.')
    ef_script.with_name('last-ef-test-database.txt').write_text(efdb)
print('Disposable databases retained for inspection on 127.0.0.1:55437 only.')
