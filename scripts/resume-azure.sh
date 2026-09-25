#!/usr/bin/env bash
# Brings the paused FlightAssistant dev environment back — the one command.
#
#   scripts/resume-azure.sh                 dry run: checks the current state, shows the plan, changes nothing
#   scripts/resume-azure.sh --apply         does it (asks once up front, again before anything destructive)
#   scripts/resume-azure.sh --guided        explains each step and asks y/n/q before running it
#   scripts/resume-azure.sh --from-step 4   resume after a failure (a letter works too: --from-step d)
#   scripts/resume-azure.sh --local-test    proves the restore against local PostgreSQL 17 (port 5433); no Azure at all
#
# Every step checks the current state first and skips what is already done, so re-running after a
# failure is always safe. Needs: az (logged in), gh (logged in), jq, curl, PostgreSQL 17 client
# tools, and the encrypted backup kit (mounted automatically; macOS asks for its password).
#
# Steps:
#   1 (a) preflight         tools, subscription, backup kit, dump sha256, Key Vault password
#   2 (b) plan              App Service plan F1 -> B1
#   3 (c) postgres          recreate the server from 02-db-dump/server.json with the Key Vault password,
#                           its database, changed parameters, firewall rules, and a temporary rule for you
#   4 (d) restore           pg_restore the dump, verify row counts + jsonb checksums, drop the temporary rule
#   5 (e) apps              Always On on, start both web apps
#   6 (f) workflows         re-enable both deploy workflows
#   7 (g) checks            /health/ready (and /admin/health with FA_ADMIN_TOKEN), expiring secrets, your IP on the panel
#   (h)   every step appends a row to the kit's LOG.md; a summary is printed at the end
set -Eeo pipefail
set -u

# shellcheck source-path=SCRIPTDIR source=lib/azure-dev.sh
. "$(cd "$(dirname "$0")" && pwd)/lib/azure-dev.sh"

fa_usage() { sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; }

fa_parse_args "$@"
trap 'fa_on_error $LINENO' ERR

KIT_DB="" DUMP="" MY_IP="" SERVER_EXISTS=0

# ---------------------------------------------------------------------------------------------
step_preflight() {
  fa_step 1 "Preflight" "" || return 0
  fa_need jq "brew install jq"
  fa_need curl ""
  fa_find_pg_bin
  fa_ok "PostgreSQL client $(fa_pg_major "$PG_RESTORE") at $PG_BIN"
  fa_mount_kit
  KIT_DB="$FA_KIT_DIR/02-db-dump"
  local f
  for f in server.json firewall-rules.json server-params-changed.json databases.json \
           live-rowcounts.txt live-checksums.txt rowcounts.sql checksums.sql; do
    [ -f "$KIT_DB/$f" ] || fa_die "Backup kit is missing 02-db-dump/$f."
  done
  DUMP=$(fa_kit_dump)
  fa_verify_dump_sha "$DUMP"
  [ "$(jq -r '.name' "$KIT_DB/server.json")" = "$FA_PG_SERVER" ] ||
    fa_die "02-db-dump/server.json describes $(jq -r .name "$KIT_DB/server.json"), not $FA_PG_SERVER."

  if [ "$FA_MODE" = "local-test" ]; then
    fa_step_done "ok (local test)"
    return 0
  fi
  fa_check_az
  fa_check_gh
  fa_load_pg_secret
  [ "$FA_PG_ADMIN_USER" = "$(jq -r .administratorLogin "$KIT_DB/server.json")" ] ||
    fa_die "Key Vault user $FA_PG_ADMIN_USER differs from server.json's administratorLogin."
  MY_IP=$(fa_my_ip)
  fa_ok "Your public IP: $MY_IP"
  if fa_az_exists az postgres flexible-server show -g "$FA_RG" --name "$FA_PG_SERVER"; then SERVER_EXISTS=1; fi
  fa_step_done "ok"
}

# ---------------------------------------------------------------------------------------------
step_plan() {
  fa_step 2 "App Service plan $FA_PLAN_PAUSED_SKU -> $FA_PLAN_RUNNING_SKU" \
"The plan both web apps share was moved to the free F1 tier. B1 (~13 USD/month) is needed for
Always On and to run the API properly. Changes Azure: az appservice plan update --sku $FA_PLAN_RUNNING_SKU." || return 0
  local sku
  sku=$(az appservice plan show -g "$FA_RG" -n "$FA_PLAN" --query sku.name -o tsv)
  if [ "$sku" = "$FA_PLAN_RUNNING_SKU" ]; then
    fa_ok "Plan is already $sku"; fa_step_done "already $sku"; return 0
  fi
  fa_info "Plan is $sku"
  fa_act az appservice plan update -g "$FA_RG" -n "$FA_PLAN" --sku "$FA_PLAN_RUNNING_SKU" -o none
  fa_step_done "$sku -> $FA_PLAN_RUNNING_SKU" "az appservice plan update -g $FA_RG -n $FA_PLAN --sku $sku"
}

# ---------------------------------------------------------------------------------------------
step_postgres() {
  fa_step 3 "Recreate PostgreSQL server $FA_PG_SERVER" \
"Creates the server exactly as 02-db-dump/server.json recorded it (same name, SKU, version, storage,
admin), using the password already in Key Vault so PostgresConnectionString needs no change. Then
creates the database, reapplies changed server parameters and firewall rules, and adds a temporary
rule ($FA_TEMP_FW_RULE) for your IP so step 4 can restore. Changes Azure; starts billing
(Burstable B1ms ~13 USD/month compute + ~4 USD/month for 32 GB storage). Creating the server takes 5-10 minutes." || return 0
  local j="$KIT_DB/server.json" state location version tier sku storage retention geo autogrow ha entra
  location=$(jq -r '.location | ascii_downcase | gsub(" "; "")' "$j")
  version=$(jq -r '.version' "$j")
  tier=$(jq -r '.sku.tier' "$j")
  sku=$(jq -r '.sku.name' "$j")
  storage=$(jq -r '.storage.storageSizeGb' "$j")
  retention=$(jq -r '.backup.backupRetentionDays // 7' "$j")
  geo=$(jq -r '.backup.geoRedundantBackup // "Disabled"' "$j")
  autogrow=$(jq -r '.storage.autoGrow // "Disabled"' "$j")
  ha=$(jq -r '.highAvailability.mode // "Disabled"' "$j")
  entra=$(jq -r '.authConfig.activeDirectoryAuth // "Disabled"' "$j")
  [ "$ha" = "Disabled" ] || fa_warn "server.json had high availability '$ha'; recreating without it (dev never needs it)."
  [ "$entra" = "Disabled" ] || fa_warn "server.json had Entra auth enabled; this recreates password auth only — add the Entra admin by hand."

  if [ "$SERVER_EXISTS" = 1 ]; then
    state=$(az postgres flexible-server show -g "$FA_RG" --name "$FA_PG_SERVER" --query state -o tsv)
    fa_ok "Server exists (state $state)"
    [ "$(az postgres flexible-server show -g "$FA_RG" --name "$FA_PG_SERVER" --query version -o tsv)" = "$version" ] ||
      fa_warn "Existing server is not PostgreSQL $version."
    if [ "$state" = "Stopped" ]; then
      fa_act az postgres flexible-server start -g "$FA_RG" --name "$FA_PG_SERVER" -o none
    fi
  else
    fa_info "Server does not exist; creating $sku ($tier), PostgreSQL $version, ${storage} GB, admin $FA_PG_ADMIN_USER, $location"
    # --public-access None = public network access on, but no firewall rule; the recorded rules follow.
    # --zonal-resiliency replaces the deprecated --high-availability.
    fa_act az postgres flexible-server create -g "$FA_RG" --name "$FA_PG_SERVER" --location "$location" \
      --version "$version" --tier "$tier" --sku-name "$sku" --storage-size "$storage" \
      --storage-auto-grow "$autogrow" --backup-retention "$retention" --geo-redundant-backup "$geo" \
      --zonal-resiliency Disabled --admin-user "$FA_PG_ADMIN_USER" --admin-password "$FA_PG_PASSWORD" \
      --public-access None --yes -o none
    fa_changes_things && SERVER_EXISTS=1
  fi

  if [ "$SERVER_EXISTS" != 1 ]; then
    fa_info "(dry run: the database, $(jq length "$KIT_DB/firewall-rules.json") firewall rule(s), changed parameters and the temporary rule for $MY_IP follow once the server exists)"
    fa_step_done "would create"
    return 0
  fi

  # Database
  if fa_az_exists az postgres flexible-server db show -g "$FA_RG" --server-name "$FA_PG_SERVER" --name "$FA_PG_DB"; then
    fa_ok "Database $FA_PG_DB exists"
  else
    fa_db_create
  fi

  # Server parameters that differed from Azure's defaults when paused.
  local name value current changed=0
  while IFS="$(printf '\t')" read -r name value; do
    [ -n "$name" ] || continue
    current=$(az postgres flexible-server parameter show -g "$FA_RG" --server-name "$FA_PG_SERVER" --name "$name" --query value -o tsv)
    if [ "$current" = "$value" ]; then
      fa_ok "Parameter $name = $value"
    else
      fa_info "Parameter $name is '$current', recorded '$value'"
      fa_act az postgres flexible-server parameter set -g "$FA_RG" --server-name "$FA_PG_SERVER" --name "$name" --value "$value" -o none
      changed=1
    fi
  done <<EOF
$(jq -r 'if type == "array" then .[] | [.name, .value] else to_entries[] | [.key, (.value | if type == "object" then .value else . end)] end | @tsv' "$KIT_DB/server-params-changed.json")
EOF
  if [ "$changed" = 1 ] && fa_changes_things; then
    if [ -n "$(az postgres flexible-server parameter list -g "$FA_RG" --server-name "$FA_PG_SERVER" --query '[?isConfigPendingRestart].name' -o tsv)" ]; then
      fa_info "A static parameter changed; restarting the server."
      fa_act az postgres flexible-server restart -g "$FA_RG" --name "$FA_PG_SERVER" -o none
    fi
  fi

  # Firewall rules as recorded, then the temporary one for this machine.
  local rule start end
  while IFS="$(printf '\t')" read -r rule start end; do
    [ -n "$rule" ] || continue
    fa_ensure_fw_rule "$rule" "$start" "$end"
  done <<EOF
$(jq -r '.[] | [.name, .startIpAddress, .endIpAddress] | @tsv' "$KIT_DB/firewall-rules.json")
EOF
  fa_ensure_fw_rule "$FA_TEMP_FW_RULE" "$MY_IP" "$MY_IP"
  fa_step_done "server, database, parameters and firewall ready" \
    "az postgres flexible-server delete -g $FA_RG --name $FA_PG_SERVER --yes (only after a fresh dump)"
}

fa_db_create() { # with the charset/collation recorded in databases.json
  local charset collation
  charset=$(jq -r --arg d "$FA_PG_DB" '[.[] | select(.name == $d)][0].charset // "UTF8"' "$KIT_DB/databases.json")
  collation=$(jq -r --arg d "$FA_PG_DB" '[.[] | select(.name == $d)][0].collation // "en_US.utf8"' "$KIT_DB/databases.json")
  fa_act az postgres flexible-server db create -g "$FA_RG" --server-name "$FA_PG_SERVER" --name "$FA_PG_DB" \
    --charset "$charset" --collation "$collation" -o none
}

fa_ensure_fw_rule() { # name start end
  local have
  have=$(az postgres flexible-server firewall-rule list -g "$FA_RG" --server-name "$FA_PG_SERVER" \
    --query "[?name=='$1'] | [0].[startIpAddress, endIpAddress]" -o tsv | tr '\t' ' ')
  if [ "$have" = "$2 $3" ]; then
    fa_ok "Firewall rule $1 ($2 - $3)"
  else
    fa_act az postgres flexible-server firewall-rule create -g "$FA_RG" --server-name "$FA_PG_SERVER" \
      --name "$1" --start-ip-address "$2" --end-ip-address "$3" -o none
  fi
}

# ---------------------------------------------------------------------------------------------
step_restore() {
  fa_step 4 "Restore the dump and verify it" \
"pg_restore --no-owner --no-privileges --exit-on-error --single-transaction into $FA_PG_DB (a failed
restore leaves the database empty, so a retry starts clean). Then compares every table's row count
and jsonb content checksum with what the live database had when paused. Only if everything matches
is the temporary firewall rule removed. If the database already has tables and they match, nothing
is restored. If it has tables that do NOT match, you are asked before it is dropped." || return 0

  if [ "$SERVER_EXISTS" != 1 ]; then
    fa_info "(dry run: server does not exist yet; would restore $(basename "$DUMP") into $FA_PG_DB and verify it)"
    fa_step_done "would restore"
    return 0
  fi
  export PGHOST="$FA_PG_HOST" PGPORT=5432 PGUSER="$FA_PG_ADMIN_USER" PGDATABASE="$FA_PG_DB" PGSSLMODE=require
  export PGPASSWORD="$FA_PG_PASSWORD"
  if ! fa_wait_for_db; then
    if [ "$FA_MODE" = "dry-run" ]; then
      fa_info "(dry run: cannot connect yet — the temporary firewall rule for $MY_IP is created in step 3)"
      fa_step_done "would restore"; unset PGPASSWORD; return 0
    fi
    fa_die "Cannot connect to $PGHOST as $PGUSER."
  fi

  local tables
  tables=$(fa_table_count)
  if [ "$tables" = 0 ]; then
    fa_info "$FA_PG_DB is empty; restoring $(basename "$DUMP") ($(du -h "$DUMP" | awk '{print $1}'))"
    fa_restore "$DUMP"
    if fa_changes_things; then
      fa_verify_against_kit || fa_die "Restored data does not match the live snapshot. Temporary firewall rule kept for investigation."
    fi
  elif fa_verify_against_kit; then
    fa_ok "$FA_PG_DB already holds the restored data ($tables tables)"
  elif [ "$FA_MODE" = "dry-run" ]; then
    fa_warn "$FA_PG_DB has $tables tables that do not match the snapshot; --apply would ask to drop and re-restore it."
  else
    local app
    for app in $FA_APPS; do
      [ "$(az webapp show -g "$FA_RG" -n "$app" --query state -o tsv)" = "Stopped" ] ||
        fa_die "$FA_PG_DB does not match the snapshot, but $app is running and holds connections. Stop both apps first (az webapp stop -g $FA_RG -n <app>), then re-run --from-step 4."
    done
    fa_confirm "$FA_PG_DB on Azure has $tables tables that do NOT match the snapshot. Drop it and restore again?" ||
      fa_die "Left $FA_PG_DB as it is."
    fa_act az postgres flexible-server db delete -g "$FA_RG" --server-name "$FA_PG_SERVER" --name "$FA_PG_DB" --yes -o none
    fa_db_create
    fa_restore "$DUMP"
    fa_verify_against_kit || fa_die "Restored data still does not match. Temporary firewall rule kept."
  fi
  unset PGPASSWORD

  if az postgres flexible-server firewall-rule list -g "$FA_RG" --server-name "$FA_PG_SERVER" \
       --query "[?name=='$FA_TEMP_FW_RULE'].name" -o tsv | grep -q .; then
    fa_act az postgres flexible-server firewall-rule delete -g "$FA_RG" --server-name "$FA_PG_SERVER" \
      --name "$FA_TEMP_FW_RULE" --yes -o none
  fi
  fa_step_done "restored and verified"
}

fa_wait_for_db() {
  local tries=1 max=1
  fa_changes_things && max=12   # a new firewall rule can take a minute to apply
  while ! fa_psql -c 'SELECT 1' >/dev/null 2>&1; do
    [ "$tries" -ge "$max" ] && return 1
    fa_info "waiting for $PGHOST to accept connections ($tries/$max)..."
    sleep 10; tries=$((tries + 1))
  done
  fa_ok "Connected to $PGHOST/$PGDATABASE"
}

# ---------------------------------------------------------------------------------------------
step_local_restore() {
  fa_step 4 "Restore the dump into local PostgreSQL and verify it" "" || return 0
  fa_local_restore_verify "$DUMP" fa_resume_test
  fa_step_done "local restore verified"
}

# ---------------------------------------------------------------------------------------------
step_apps() {
  fa_step 5 "Start both web apps with Always On" \
"Turns Always On back on and starts $FA_API_APP and $FA_PANEL_APP. Their settings, identity and
role assignments were never deleted, so nothing else is needed. Changes Azure." || return 0
  local sku app state always
  sku=$(az appservice plan show -g "$FA_RG" -n "$FA_PLAN" --query sku.name -o tsv)
  if [ "$sku" = "$FA_PLAN_PAUSED_SKU" ] && fa_changes_things; then
    fa_die "Plan is still $sku, which has no Always On. Run step 2 first."
  fi
  for app in $FA_APPS; do
    always=$(az webapp config show -g "$FA_RG" -n "$app" --query alwaysOn -o tsv)
    state=$(az webapp show -g "$FA_RG" -n "$app" --query state -o tsv)
    if [ "$always" = "true" ]; then fa_ok "$app: Always On"
    else fa_act az webapp config set -g "$FA_RG" -n "$app" --always-on true -o none; fi
    if [ "$state" = "Running" ]; then fa_ok "$app: Running"
    else fa_act az webapp start -g "$FA_RG" -n "$app" -o none; fi
  done
  fa_step_done "both apps running with Always On" "az webapp stop -g $FA_RG -n <app>; az webapp config set ... --always-on false"
}

# ---------------------------------------------------------------------------------------------
step_workflows() {
  fa_step 6 "Re-enable the deploy workflows" \
"Enables '$FA_BACKEND_DEPLOY_WF' in $FA_BACKEND_REPO and '$FA_PANEL_DEPLOY_WF' in $FA_PANEL_REPO so a push
to development deploys again. Enabling does not start a run. Changes GitHub, not Azure." || return 0
  local pair repo wf state
  for pair in "$FA_BACKEND_REPO $FA_BACKEND_DEPLOY_WF" "$FA_PANEL_REPO $FA_PANEL_DEPLOY_WF"; do
    repo="${pair% *}"; wf="${pair#* }"
    state=$(gh api "repos/$repo/actions/workflows/$wf" --jq .state)
    if [ "$state" = "active" ]; then fa_ok "$repo workflow $wf is active"
    else fa_info "$repo workflow $wf is $state"; fa_act gh workflow enable "$wf" -R "$repo"; fi
  done
  fa_info "The apps run the build they had when paused. To deploy the latest: gh workflow run $FA_BACKEND_DEPLOY_WF -R $FA_BACKEND_REPO --ref development"
  fa_step_done "deploy workflows active" "gh workflow disable <id> -R <repo>"
}

# ---------------------------------------------------------------------------------------------
step_checks() {
  fa_step 7 "Health and expiry checks" \
"Waits for /health/ready (database + Redis) to answer 200, reads /admin/health when FA_ADMIN_TOKEN
holds an admin bearer token, warns about secrets expiring within $FA_EXPIRY_WARN_DAYS days, and checks
your IP can reach the panel. Read-only, except it offers to add your IP to the panel." || return 0
  fa_check_health
  fa_check_expiries
  fa_check_panel_ip
  fa_step_done "checks done ($FA_WARNINGS warning(s) so far)"
}

fa_check_health() {
  local host code tries=1 max=1 body
  host=$(az webapp show -g "$FA_RG" -n "$FA_API_APP" --query defaultHostName -o tsv)
  fa_changes_things && max=20
  while :; do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "https://$host/health/ready" || true)
    [ "$code" = 200 ] && break
    if [ "$tries" -ge "$max" ]; then
      if [ "$FA_MODE" = "dry-run" ]; then fa_info "/health/ready answers $code (expected while paused)"
      else fa_warn "/health/ready still answers $code after $max tries: az webapp log tail -g $FA_RG -n $FA_API_APP"; fi
      return 0
    fi
    fa_info "/health/ready: $code, waiting for the API to warm up ($tries/$max)..."
    sleep 15; tries=$((tries + 1))
  done
  fa_ok "https://$host/health/ready: 200"
  if [ -z "${FA_ADMIN_TOKEN:-}" ]; then
    fa_info "/admin/health needs an admin token: check the panel's Health page, or re-run step 7 with FA_ADMIN_TOKEN set."
    return 0
  fi
  fa_add_secret "$FA_ADMIN_TOKEN"
  # The header goes in on stdin so the token never appears in curl's argv.
  body=$(printf 'Authorization: Bearer %s\n' "$FA_ADMIN_TOKEN" | curl -fsS --max-time 60 -H @- "https://$host/admin/health") ||
    { fa_warn "/admin/health request failed (token expired?)"; return 0; }
  if [ "$(printf '%s' "$body" | jq -r .status)" = "Healthy" ]; then
    fa_ok "/admin/health: Healthy"
  else
    fa_warn "/admin/health: $(printf '%s' "$body" | jq -r '.status + " — " + ([.checks[] | select(.status != "Healthy") | .name + "=" + .status] | join(", "))')"
  fi
  [ "$(printf '%s' "$body" | jq '.migrations.pending // [] | length')" = 0 ] ||
    fa_warn "/admin/health reports pending migrations: $(printf '%s' "$body" | jq -c .migrations.pending)"
}

fa_warn_expiry() { # label iso-date
  local days
  case "$2" in ''|None|null) fa_info "$1: no expiry set"; return 0 ;; esac
  days=$(fa_days_until "$2")
  if [ "$days" -lt 0 ]; then fa_warn "$1 EXPIRED $((-days)) day(s) ago ($2)"
  elif [ "$days" -le "$FA_EXPIRY_WARN_DAYS" ]; then fa_warn "$1 expires in $days day(s) ($2)"
  else fa_ok "$1 expires in $days days"; fi
}

fa_check_expiries() {
  local s expires client_id creds line token header
  # Key Vault secret attributes (list returns attributes only, never values).
  for s in "$FA_KV_PANEL_SECRET" "$FA_KV_GITHUB_SECRET"; do
    expires=$(az keyvault secret list --vault-name "$FA_KV" --query "[?name=='$s'] | [0].attributes.expires" -o tsv 2>/dev/null || true)
    fa_warn_expiry "Key Vault secret $s" "$expires"
  done
  # The panel's Easy Auth app registration: its client secret is what actually expires.
  client_id=$(az webapp auth show -g "$FA_RG" -n "$FA_PANEL_APP" \
    --query properties.identityProviders.azureActiveDirectory.registration.clientId -o tsv 2>/dev/null || true)
  if [ -z "$client_id" ]; then   # fall back to the authsettingsV2 exported when pausing
    client_id=$(find "$FA_KIT_DIR/01-azure-export" -iname '*panel*auth*' -name '*.json' -exec \
      jq -r '[.. | .registration?.clientId? // empty][0] // empty' {} + 2>/dev/null | head -1)
  fi
  if [ -z "$client_id" ]; then
    fa_warn "Could not read the panel's Easy Auth client id; check its client secret expiry in the portal."
  elif creds=$(az ad app credential list --id "$client_id" --query "[].[displayName, endDateTime]" -o tsv 2>/dev/null); then
    [ -n "$creds" ] || fa_warn "Panel app registration $client_id has no client secrets."
    while IFS="$(printf '\t')" read -r line expires; do
      [ -n "$expires" ] && fa_warn_expiry "Panel Easy Auth secret '${line:-unnamed}' ($client_id)" "$expires"
    done <<EOF
$creds
EOF
  else
    fa_warn "Cannot read app registration $client_id from this tenant (it may live in the External ID tenant); check its secret expiry in the portal."
  fi
  # The GitHub PAT: GitHub reports its expiry in a response header; the value is never printed.
  token=$(az keyvault secret show --vault-name "$FA_KV" --name "$FA_KV_GITHUB_SECRET" --query value -o tsv 2>/dev/null || true)
  if [ -z "$token" ]; then fa_warn "Could not read $FA_KV_GITHUB_SECRET from Key Vault."; return 0; fi
  fa_add_secret "$token"
  header=$(printf 'Authorization: Bearer %s\n' "$token" | curl -sS -o /dev/null -D - --max-time 15 -H @- https://api.github.com/rate_limit |
    tr -d '\r' | grep -i '^github-authentication-token-expiration:' | cut -d' ' -f2- || true)
  token=""
  if [ -n "$header" ]; then fa_warn_expiry "GitHub token ($FA_KV_GITHUB_SECRET)" "$header"
  else fa_info "GitHub token ($FA_KV_GITHUB_SECRET): GitHub reports no expiry (or rejected it — check /admin/ci)"; fi
}

fa_check_panel_ip() {
  local rules rule allowed=0 priority
  rules=$(az webapp config access-restriction show -g "$FA_RG" -n "$FA_PANEL_APP" \
    --query "ipSecurityRestrictions[?action=='Allow'].ipAddress" -o tsv)
  for rule in $rules; do
    if fa_ip_in_cidr "$MY_IP" "$rule"; then allowed=1; break; fi
  done
  if [ "$allowed" = 1 ]; then fa_ok "Your IP $MY_IP may reach $FA_PANEL_APP ($rule)"; return 0; fi
  fa_warn "Your IP $MY_IP is not in $FA_PANEL_APP's access restrictions."
  priority=$(az webapp config access-restriction show -g "$FA_RG" -n "$FA_PANEL_APP" \
    --query "max(ipSecurityRestrictions[?action=='Allow' && priority < \`2147483647\`].priority)" -o tsv)
  priority=$(( ${priority:-99} + 1 ))
  set -- az webapp config access-restriction add -g "$FA_RG" -n "$FA_PANEL_APP" \
    --rule-name "operator-$(date -u +%Y%m%d)" --action Allow --ip-address "$MY_IP/32" --priority "$priority" -o none
  fa_info "To add it:"; fa_show_cmd "$@"
  if [ "$FA_MODE" != "dry-run" ] && fa_confirm "Add $MY_IP/32 to the panel's access restrictions now?"; then
    "$@" && fa_ok "Added"
  fi
}

# ---------------------------------------------------------------------------------------------
if [ "$FA_MODE" = "local-test" ]; then
  printf '%s\n' "${FA_C_B}Local test: steps 1 and 4 against PostgreSQL on $FA_LOCAL_PGHOST:$FA_LOCAL_PGPORT. Azure is not touched.${FA_C_0}"
  step_preflight
  step_local_restore
  fa_finish
  exit 0
fi

printf '%s\n' "${FA_C_B}resume-azure.sh — mode: $FA_MODE${FA_C_0}"
step_preflight
if [ "$FA_MODE" = "apply" ]; then
  fa_confirm "This recreates PostgreSQL, scales the plan to $FA_PLAN_RUNNING_SKU and starts both apps (billing resumes). Continue?" ||
    fa_die "Nothing changed."
fi
step_plan
step_postgres
step_restore
step_apps
step_workflows
step_checks
fa_finish
