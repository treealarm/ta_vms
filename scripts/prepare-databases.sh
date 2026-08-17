#!/usr/bin/env bash
# Creates every database named by a POSTGRES_*_DB variable, if it does not exist yet, and
# reports what it found. Idempotent: safe to run on every start and by hand at any time.
#
# Why this exists at all: the .sql files next to this script run through
# docker-entrypoint-initdb.d, which fires *only* when the data directory is initialised from
# empty. On a deployment that already has data — which is every deployment after the first —
# a newly added database is therefore never created. The services then fail obscurely rather
# than loudly: a missing database raises the same NpgsqlException as an unreachable server, so
# DbMigrator retries "Postgres not ready" forever (see web_vms/VmsCfg/Grpc/DbMigrator.cs).
#
# The list of databases is discovered from the environment instead of hardcoded here. Adding a
# service means adding its POSTGRES_<NAME>_DB to .env and nothing else, and stamping a
# per-tenant set of databases is just a different set of values — no code change in this file.
# POSTGRES_DB itself is deliberately not matched: that is the maintenance database we connect
# to in order to issue CREATE DATABASE.

set -euo pipefail

CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --check) CHECK_ONLY=1 ;;
    -h|--help)
      echo "Usage: $0 [--check]"
      echo "  --check   report only; create nothing"
      exit 0
      ;;
    *)
      echo "unknown argument: $arg" >&2
      exit 2
      ;;
  esac
done

PGHOST="${POSTGRES_HOST:-postgres}"
PGPORT="${POSTGRES_PORT:-5432}"
PGUSER="${POSTGRES_USER:-postgres}"
export PGPASSWORD="${POSTGRES_PASSWORD:-postgres}"
MAINTENANCE_DB="${POSTGRES_MAINTENANCE_DB:-postgres}"

psql_main() {
  psql --host "$PGHOST" --port "$PGPORT" --username "$PGUSER" --dbname "$MAINTENANCE_DB" \
       --no-align --tuples-only --quiet "$@"
}

echo "== PostgreSQL preparation"
echo "   server: $PGUSER@$PGHOST:$PGPORT (maintenance db: $MAINTENANCE_DB)"

# Wait for the server. Compose's depends_on only waits for the container, not for postgres
# inside it to accept connections, and on a first start it restarts once after initdb.
deadline=$(( SECONDS + ${POSTGRES_WAIT_SECONDS:-60} ))
until pg_isready --host "$PGHOST" --port "$PGPORT" --username "$PGUSER" --quiet; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "   [fail] postgres did not become ready in ${POSTGRES_WAIT_SECONDS:-60}s" >&2
    exit 1
  fi
  sleep 1
done
echo "   [ ok ] server is accepting connections"

# Every POSTGRES_<SOMETHING>_DB in the environment names a database we are responsible for.
mapfile -t db_vars < <(compgen -A variable | grep -E '^POSTGRES_[A-Z0-9_]+_DB$' | sort)
if [ "${#db_vars[@]}" -eq 0 ]; then
  echo "   [fail] no POSTGRES_*_DB variables in the environment — nothing to prepare." >&2
  echo "          Expected e.g. POSTGRES_CFG_DB, POSTGRES_FS_DB. Is .env being passed in?" >&2
  exit 1
fi

existing="$(psql_main --command "SELECT datname FROM pg_database;")"
db_exists() { printf '%s\n' "$existing" | grep -qx "$1"; }

missing=0
created=0
for var in "${db_vars[@]}"; do
  name="${!var}"
  if [ -z "$name" ]; then
    echo "   [warn] $var is set but empty — skipped"
    continue
  fi

  if db_exists "$name"; then
    echo "   [ ok ] $name already exists ($var)"
    continue
  fi

  if [ "$CHECK_ONLY" -eq 1 ]; then
    echo "   [MISSING] $name ($var)"
    missing=$(( missing + 1 ))
    continue
  fi

  # Quoted so that a tenant-stamped name with mixed case or dashes still works.
  psql_main --command "CREATE DATABASE \"$name\";" >/dev/null
  echo "   [ new] $name created ($var)"
  created=$(( created + 1 ))
done

# A wrong POSTGRES_PORT is the failure this catches: both this stack and multitenant_admin
# publish 5432 on the host, so it is entirely possible to reach the other server, authenticate,
# and find none of our databases. Saying so beats "database does not exist" from every service.
if [ "$missing" -gt 0 ] || { [ "$created" -gt 0 ] && [ "$CHECK_ONLY" -eq 0 ]; }; then
  if printf '%s\n' "$existing" | grep -qx "mapstore"; then
    echo "   [warn] this server also holds 'mapstore' — it looks like multitenant_admin's"
    echo "          postgres, not ours. Both publish port 5432 on the host; check POSTGRES_PORT."
  fi
fi

if [ "$CHECK_ONLY" -eq 1 ]; then
  if [ "$missing" -gt 0 ]; then
    echo "== $missing database(s) missing. Run this script without --check to create them." >&2
    exit 1
  fi
  echo "== All databases present."
  exit 0
fi

echo "== Done: $created created, $(( ${#db_vars[@]} - created )) already present."
