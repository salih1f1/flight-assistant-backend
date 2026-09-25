#!/usr/bin/env bash
# Pause the dev Azure resources to $0/month.
#
# Usage:
#   scripts/pause-azure.sh                # dry-run: print what would happen, touch nothing
#   scripts/pause-azure.sh --dump-only    # take the DB dump only, no deletes
#   scripts/pause-azure.sh --apply        # dump + delete Web Apps + plan + DB
#
# Requires: az (logged in to the dev subscription), pg_dump on PATH.
# See HANDOFF.md ("Pausing Azure to $0/month") for the reasoning behind each step.

set -euo pipefail

RG="flight-assistant-dev"
DB_SERVER="flightassistantdev-db"
DB_ADMIN="pgadmin"
DB_NAME="flightassistant"
DB_FQDN="flightassistantdev-db.postgres.database.azure.com"
PLAN="ASP-flightassistantdev-88a9"
API_SITE="flight-assistant-dev-api"
PANEL_SITE="flight-assistant-dev-panel"

DUMP_FILE="flightassistant-$(date -u +%Y-%m-%d).dump"

APPLY=0
DUMP_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    --dump-only) DUMP_ONLY=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown flag: $arg" >&2; exit 2 ;;
  esac
done

say() { printf '\n== %s ==\n' "$*"; }
run() {
  if [[ "$APPLY" -eq 1 || "$DUMP_ONLY" -eq 1 && "$1" == "DUMP" ]]; then
    shift; echo "+ $*"; "$@"
  else
    shift; echo "  (dry-run) $*"
  fi
}

say "Pre-flight"
az account show --query "{name:name, state:state}" -o table

say "Step 1: dump Postgres to $DUMP_FILE"
MY_IP=$(curl -s https://api.ipify.org)
echo "  temp firewall rule for $MY_IP will be created and removed"
if [[ "$APPLY" -eq 1 || "$DUMP_ONLY" -eq 1 ]]; then
  az postgres flexible-server firewall-rule create -g "$RG" -n "$DB_SERVER" \
    --rule-name "pause-script-$(date +%s)" \
    --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" >/dev/null
  trap 'az postgres flexible-server firewall-rule list -g '"$RG"' -n '"$DB_SERVER"' --query "[?starts_with(name, '\''pause-script-'\'')].name" -o tsv | xargs -I{} az postgres flexible-server firewall-rule delete -g '"$RG"' -n '"$DB_SERVER"' --rule-name {} --yes' EXIT
  read -rsp "Postgres password for $DB_ADMIN: " PGPASSWORD; echo
  export PGPASSWORD
  pg_dump "host=$DB_FQDN port=5432 dbname=$DB_NAME user=$DB_ADMIN sslmode=require" \
    --format=custom --no-owner --no-privileges --file="$DUMP_FILE"
  unset PGPASSWORD
  echo "  wrote $(du -h "$DUMP_FILE" | cut -f1) to $DUMP_FILE"
  echo "  MOVE THIS FILE OUT OF THE REPO before committing anything."
else
  echo "  (dry-run) would create firewall rule, pg_dump to $DUMP_FILE, remove firewall rule"
fi

if [[ "$DUMP_ONLY" -eq 1 ]]; then
  echo; echo "dump-only mode: no resources deleted."; exit 0
fi

say "Step 2: delete both Web Apps"
run APPLY az webapp delete -g "$RG" -n "$API_SITE"
run APPLY az webapp delete -g "$RG" -n "$PANEL_SITE"

say "Step 3: delete App Service Plan"
run APPLY az appservice plan delete -g "$RG" -n "$PLAN" --yes

say "Step 4: delete Postgres server"
run APPLY az postgres flexible-server delete -g "$RG" -n "$DB_SERVER" --yes

say "Kept intact"
cat <<EOF
  - Key Vault flightassistantdev-kv   (secrets survive; soft-delete is on)
  - Application Insights component    (empty = \$0)
  - Managed identity flight-assistant-id-9909
  - Action groups
EOF

if [[ "$APPLY" -eq 0 ]]; then
  echo; echo "DRY-RUN. Re-run with --apply to execute."
fi
