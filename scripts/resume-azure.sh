#!/usr/bin/env bash
# Bring the dev environment back after a pause.
#
# Usage:
#   scripts/resume-azure.sh <dump-file>            # dry-run
#   scripts/resume-azure.sh <dump-file> --apply    # actually recreate + restore
#
# Requires: az (logged in to the dev subscription), pg_restore on PATH.
# See HANDOFF.md ("Resume procedure") for the reasoning.

set -euo pipefail

RG="flight-assistant-dev"
REGION="westeurope"

DB_SERVER="flightassistantdev-db"
DB_ADMIN="pgadmin"
DB_NAME="flightassistant"
DB_SKU="Standard_B1ms"
DB_TIER="Burstable"
DB_STORAGE_GB=32
DB_PG_VERSION=17
DB_FQDN="flightassistantdev-db.postgres.database.azure.com"

PLAN="ASP-flightassistantdev-88a9"
PLAN_SKU="B1"
API_SITE="flight-assistant-dev-api"
PANEL_SITE="flight-assistant-dev-panel"
MI_NAME="flight-assistant-id-9909"

DUMP_FILE="${1:-}"
APPLY=0
for arg in "${@:2}"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown flag: $arg" >&2; exit 2 ;;
  esac
done

if [[ -z "$DUMP_FILE" || ! -f "$DUMP_FILE" ]]; then
  echo "usage: $0 <dump-file> [--apply]" >&2
  echo "  dump file must exist; pass the path you saved from pause-azure.sh" >&2
  exit 2
fi

say() { printf '\n== %s ==\n' "$*"; }
run() {
  if [[ "$APPLY" -eq 1 ]]; then echo "+ $*"; "$@"
  else echo "  (dry-run) $*"
  fi
}

say "Pre-flight"
az account show --query "{name:name, state:state}" -o table
echo "  dump file: $DUMP_FILE ($(du -h "$DUMP_FILE" | cut -f1))"

say "Step 1: recreate Postgres server (takes ~10 min)"
run az postgres flexible-server create \
  -g "$RG" -n "$DB_SERVER" --location "$REGION" \
  --admin-user "$DB_ADMIN" \
  --sku-name "$DB_SKU" --tier "$DB_TIER" \
  --storage-size "$DB_STORAGE_GB" \
  --version "$DB_PG_VERSION" \
  --public-access None \
  --yes

say "Step 2: temporary firewall rule + create database"
MY_IP=$(curl -s https://api.ipify.org)
run az postgres flexible-server firewall-rule create -g "$RG" -n "$DB_SERVER" \
  --rule-name "resume-script-$(date +%s)" \
  --start-ip-address "$MY_IP" --end-ip-address "$MY_IP"
run az postgres flexible-server db create -g "$RG" -s "$DB_SERVER" -d "$DB_NAME"

say "Step 3: pg_restore the dump"
if [[ "$APPLY" -eq 1 ]]; then
  read -rsp "Postgres password for $DB_ADMIN (the one you set in Step 1): " PGPASSWORD; echo
  export PGPASSWORD
  pg_restore --dbname="host=$DB_FQDN port=5432 dbname=$DB_NAME user=$DB_ADMIN sslmode=require" \
    --no-owner --no-privileges --clean --if-exists "$DUMP_FILE"
  unset PGPASSWORD
else
  echo "  (dry-run) pg_restore into $DB_NAME from $DUMP_FILE"
fi

say "Step 4: recreate App Service Plan + Web Apps"
run az appservice plan create -g "$RG" -n "$PLAN" --sku "$PLAN_SKU" --is-linux --location "$REGION"
run az webapp create -g "$RG" -p "$PLAN" -n "$API_SITE" --runtime "DOTNETCORE:10.0"
run az webapp create -g "$RG" -p "$PLAN" -n "$PANEL_SITE" --runtime "NODE:20-lts"

say "Step 5: reattach managed identity"
MI_ID=$(az identity show -g "$RG" -n "$MI_NAME" --query id -o tsv 2>/dev/null || true)
if [[ -n "$MI_ID" ]]; then
  run az webapp identity assign -g "$RG" -n "$API_SITE" --identities "$MI_ID"
  run az webapp identity assign -g "$RG" -n "$PANEL_SITE" --identities "$MI_ID"
else
  echo "  managed identity $MI_NAME not found — recreate it before granting Key Vault access."
fi

say "Manual follow-ups"
cat <<EOF
  1. Push a commit to 'development' to trigger the API deploy workflow
     (.github/workflows/development_flight-assistant-dev-api.yml).
  2. Push to the panel repo's 'main' to trigger its deploy.
  3. In Azure portal: verify the Web Apps' Key Vault references resolve.
     If they don't, grant the site's managed identity 'get'/'list' on
     flightassistantdev-kv — RBAC role 'Key Vault Secrets User' is enough.
  4. Check /admin/health once the API is up.
EOF

if [[ "$APPLY" -eq 0 ]]; then
  echo; echo "DRY-RUN. Re-run with --apply to execute."
fi
