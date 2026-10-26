#!/usr/bin/env bash
# Dedicated scratch PostgreSQL instance only; never invokes the API or uses pos_db.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PG_BIN="${PG_BIN:-C:/Program Files/PostgreSQL/18/bin}"
SCRATCH="${LOCALAPPDATA:?LOCALAPPDATA required}/hermes/cache/scratch"
RUN="$(python -c 'import uuid; print(uuid.uuid4().hex)')"
DATA="$SCRATCH/pos-opening-pg-$RUN"
LOG="$SCRATCH/pos-opening-pg-$RUN.log"
PORT="${POS_TEST_PG_PORT:-55439}"
DB="pos_opening_test_$RUN"
"$PG_BIN/initdb.exe" -D "$DATA" -U pos_test --auth=trust --encoding=UTF8 --locale=C
started=0
cleanup() {
  if [ "$started" = 1 ]; then "$PG_BIN/pg_ctl.exe" -D "$DATA" -m fast -w stop; fi
}
trap cleanup EXIT
"$PG_BIN/pg_ctl.exe" -D "$DATA" -l "$LOG" -o "-p $PORT -h 127.0.0.1" -w start
started=1
"$PG_BIN/createdb.exe" -h 127.0.0.1 -p "$PORT" -U pos_test "$DB"
cd "$ROOT"
export POS_TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=$PORT;Database=$DB;Username=pos_test"
dotnet run --project POS.Client.Tests
# Scratch cluster remains stopped for inspection and is pruned by Hermes scratch retention.
