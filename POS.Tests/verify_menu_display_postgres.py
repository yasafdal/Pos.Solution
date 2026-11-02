"""Exercise real PostgreSQL in a NEW private cluster; never read app configuration.
Usage: python POS.Tests/verify_menu_display_postgres.py [generated-incremental-ef.sql]
Requires PostgreSQL binaries (PG_BIN or default PostgreSQL 18 Windows install).
Creates only menu_mode_test in a UUID scratch cluster, then stops/deletes it.
"""
from pathlib import Path
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
PG = Path(os.environ.get("PG_BIN", "C:/Program Files/PostgreSQL/18/bin"))
SCRATCH = Path("C:/Users/yaser/AppData/Local/hermes/cache/scratch")
SQL = ROOT / "POS.Api/Migrations/TerminalMenuDisplayMode.sql"


def main():
    cluster = SCRATCH / ("menu-pg-" + uuid.uuid4().hex)
    cluster.mkdir(parents=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith("PG")}
    env["LC_ALL"] = "C"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]

    def run(exe, args, *, text=None, expected=0):
        result = subprocess.run([str(PG / (exe + ".exe")), *args], input=text, text=True, capture_output=True, env=env)
        if result.returncode != expected:
            raise AssertionError(f"{exe} exit {result.returncode}: {result.stdout}\n{result.stderr}")
        return result.stdout.strip()

    # New cluster and unique test-only superuser, not credentials for any user database.
    run("initdb", ["-D", str(cluster / "data"), "-A", "trust", "-U", "menu_test_owner", "--no-locale", "-E", "UTF8"])
    log = (cluster / "postgres.log").open("w")
    server = subprocess.Popen([str(PG / "postgres.exe"), "-D", str(cluster / "data"), "-h", "127.0.0.1", "-p", str(port)], stdout=log, stderr=log, env=env)
    args = ["-h", "127.0.0.1", "-p", str(port), "-U", "menu_test_owner"]
    try:
        for _ in range(100):
            ready = subprocess.run([str(PG / "pg_isready.exe"), *args], capture_output=True, env=env)
            if ready.returncode == 0:
                break
            if server.poll() is not None:
                raise AssertionError("Private PostgreSQL failed to start: " + (cluster / "postgres.log").read_text())
            time.sleep(0.1)
        else:
            raise AssertionError("Private PostgreSQL did not become ready")
        run("createdb", [*args, "menu_mode_test"])

        def query(sql, *, fail=False):
            return run("psql", [*args, "-d", "menu_mode_test", "-X", "-v", "ON_ERROR_STOP=1", "-At"], text=sql, expected=3 if fail else 0)

        query('''CREATE TABLE "Terminals" ("TerminalId" integer PRIMARY KEY, "Name" text, "PTUNumber" text, "IpAddress" text);
            INSERT INTO "Terminals" VALUES (7,'Keep A','PTU-KEEP','10.0.0.7'),(8,'Keep B','PTU-B','10.0.0.8');
            CREATE TABLE "InvoiceSequences" (id integer, last_issued bigint); INSERT INTO "InvoiceSequences" VALUES (7,1234);
            CREATE TABLE "Orders" (invoice text, total numeric); INSERT INTO "Orders" VALUES ('KEEP-1234',112.00);
            CREATE TABLE "Payments" (invoice text, amount numeric); INSERT INTO "Payments" VALUES ('KEEP-1234',112.00);''')

        def snapshot():
            return query('''SELECT json_build_object('terminals',(SELECT json_agg(json_build_array("TerminalId","Name","PTUNumber","IpAddress") ORDER BY "TerminalId") FROM "Terminals"),'sequences',(SELECT json_agg(t) FROM "InvoiceSequences" t),'orders',(SELECT json_agg(t) FROM "Orders" t),'payments',(SELECT json_agg(t) FROM "Payments" t));''')

        before = json.loads(snapshot())
        manual = SQL.read_text()
        query(manual)
        assert query("SELECT count(*) FROM information_schema.columns WHERE table_name='Terminals' AND column_name='MenuDisplayMode';") == "0", "Dry run must roll back"
        committed = manual.replace("\nROLLBACK;", "\nCOMMIT;")
        query(committed)
        assert query('SELECT string_agg("MenuDisplayMode"::text,\',\' ORDER BY "TerminalId") FROM "Terminals";') == "0,0", "Legacy terminals default to text"
        query('UPDATE "Terminals" SET "MenuDisplayMode"=1 WHERE "TerminalId"=7;')
        query(committed)
        assert query('SELECT string_agg("MenuDisplayMode"::text,\',\' ORDER BY "TerminalId") FROM "Terminals";') == "1,0", "Repeat deployment must preserve per-terminal choices"
        assert json.loads(snapshot()) == before, "Identity, BIR metadata, fiscal history and sequences must be preserved"
        query('INSERT INTO "Terminals" ("TerminalId","Name") VALUES (9,\'New\');')
        assert query('SELECT "MenuDisplayMode" FROM "Terminals" WHERE "TerminalId"=9;') == "0", "New rows default to text"
        for value in ("-1", "2", "99", "NULL"):
            query(f'UPDATE "Terminals" SET "MenuDisplayMode"={value} WHERE "TerminalId"=7;', fail=True)
        assert query('SELECT "MenuDisplayMode" FROM "Terminals" WHERE "TerminalId"=7;') == "1", "Invalid updates must not persist"
        print("PASS PostgreSQL: dry-run rollback, old/new default 0, 0/1 constraint, null rejection, sibling isolation, fiscal/identity preservation, repeat deployment")
        if len(sys.argv) > 1:
            query('''CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('20261026150000_ShiftClosingReconciliation','10.0.13');''')
            ef = Path(sys.argv[1]).read_text(encoding="utf-8-sig")
            query(ef)
            query(ef)
            assert query('SELECT count(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE \'%_TerminalMenuDisplayMode\';') == "1"
            assert query('SELECT "MenuDisplayMode" FROM "Terminals" WHERE "TerminalId"=7;') == "1"
            print("PASS PostgreSQL: incremental EF idempotent SQL adopts manual-first schema and repeats without mode loss")
        # Preexisting incompatible values abort rather than silently repairing operator data.
        query('ALTER TABLE "Terminals" DROP CONSTRAINT "CK_Terminals_MenuDisplayMode"; UPDATE "Terminals" SET "MenuDisplayMode"=99 WHERE "TerminalId"=7;')
        query(committed, fail=True)
        assert query('SELECT "MenuDisplayMode" FROM "Terminals" WHERE "TerminalId"=7;') == "99"
        print("PASS PostgreSQL: incompatible preexisting data rejected without rewriting it")
    finally:
        subprocess.run([str(PG / "pg_ctl.exe"), "-D", str(cluster / "data"), "-m", "immediate", "-w", "stop"], capture_output=True, env=env)
        server.wait(timeout=15)
        log.close()
        shutil.rmtree(cluster)
        print("Private PostgreSQL cluster stopped and deleted; no app connection strings or pos_db used")


if __name__ == "__main__":
    main()
