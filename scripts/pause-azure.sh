#!/usr/bin/env bash
# Pauses the FlightAssistant dev environment down to near-zero cost, keeping everything needed to
# bring it back with scripts/resume-azure.sh.
#
#   scripts/pause-azure.sh                 dry run: checks the current state, shows the plan, changes nothing
#   scripts/pause-azure.sh --apply         does it (asks once up front; deleting Postgres always asks again)
#   scripts/pause-azure.sh --guided        explains each step and asks y/n/q before running it
#   scripts/pause-azure.sh --from-step 5   continue after a failure (a letter works too: --from-step e)
#
# Never deletes the App Service plan or the web apps: the plan drops to F1 and the apps are stopped,
# so settings, managed identity, role assignments and publish profiles all survive. Postgres is the
# only thing deleted, and only after its dump has been restored locally and verified.
# Needs: az, gh, jq, curl, PostgreSQL 17 client tools, local PostgreSQL 17 on port 5433 (for the
# verification restore), and the encrypted backup kit mounted at /Volumes/FA-Pause.
#
# Steps:
#   1 (a) preflight         tools, subscription, kit mounted and writable, Key Vault password
#   2 (b) export            plan, apps, identity, roles, Key Vault, budget, alerts -> 01-azure-export, 03-roles-budget
#   3 (c) workflows         disable both deploy workflows (so a push cannot restart a stopped app)
#   4 (d) apps              Always On off, stop both web apps (nothing writes to the database after this)
#   5 (e) dump              pg_dump + sha256 + server/firewall/parameter/database records + live row counts and checksums
#   6 (f) verify            restore the dump into local PostgreSQL and compare every table; writes 02-db-dump/VERIFIED
#   7 (g) plan              App Service plan -> F1
#   8 (h) delete postgres   only with a VERIFIED marker for this exact dump, and you type the server name
set -Eeo pipefail
set -u

# shellcheck source-path=SCRIPTDIR source=lib/azure-dev.sh
. "$(cd "$(dirname "$0")" && pwd)/lib/azure-dev.sh"

fa_usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; }

fa_parse_args "$@"
[ "$FA_MODE" = "local-test" ] && fa_die "pause-azure.sh has no --local-test; step 6 already verifies locally."
trap 'fa_on_error $LINENO' ERR

EXP="" ROLES="" KIT_DB="" MY_IP="" SERVER_EXISTS=0

# ---------------------------------------------------------------------------------------------
step_preflight() {
  fa_step 1 "Preflight" "" || return 0
  fa_need jq "brew install jq"
  fa_need curl ""
  fa_find_pg_bin
  fa_ok "PostgreSQL client $(fa_pg_major "$PG_DUMP") at $PG_BIN"
  if [ ! -f "$FA_KIT_DIR/README.md" ] && [ ! -f "$FA_KIT_IMAGE" ]; then
    fa_info "No backup kit yet. Create an encrypted image first (asks for a password — keep it in your password manager):"
    fa_show_cmd hdiutil create -size 2g -type SPARSE -fs APFS -encryption AES-256 -volname FA-Pause "$FA_KIT_IMAGE"
    fa_info "then: hdiutil attach \"$FA_KIT_IMAGE\" && touch /Volumes/FA-Pause/README.md"
    fa_die "Backup kit missing."
  fi
  fa_mount_kit
  [ -w "$FA_KIT_DIR" ] || fa_die "$FA_KIT_DIR is not writable."
  EXP="$FA_KIT_DIR/01-azure-export"; KIT_DB="$FA_KIT_DIR/02-db-dump"; ROLES="$FA_KIT_DIR/03-roles-budget"
  if fa_changes_things; then
    mkdir -p "$EXP" "$KIT_DB" "$ROLES"
    [ -f "$FA_KIT_DIR/LOG.md" ] || printf '# Pause log\n\n| # | When | Step | Result | Undo |\n|---|---|---|---|---|\n' > "$FA_KIT_DIR/LOG.md"
  fi
  fa_check_az
  fa_check_gh
  if fa_az_exists az postgres flexible-server show -g "$FA_RG" --name "$FA_PG_SERVER"; then
    SERVER_EXISTS=1
    fa_load_pg_secret
  else
    fa_ok "Postgres server $FA_PG_SERVER is already gone"
  fi
  MY_IP=$(fa_my_ip)
  fa_ok "Your public IP: $MY_IP"
  fa_step_done "ok"
}

# ---------------------------------------------------------------------------------------------
# Writes az output to a kit file once. An existing non-empty file is kept: the first capture is the
# pre-pause state, and a re-run after the apps are stopped must not overwrite it.
fa_capture() { # file az args...
  local file="$1"; shift
  if [ -s "$file" ]; then fa_ok "kept ${file#"$FA_KIT_DIR"/}"; return 0; fi
  if [ "$FA_MODE" = "dry-run" ]; then fa_info "would write ${file#"$FA_KIT_DIR"/}"; FA_STEP_PLANNED=$((FA_STEP_PLANNED + 1)); return 0; fi
  if "$@" -o json > "$file.tmp" 2>/dev/null; then mv "$file.tmp" "$file"; fa_ok "wrote ${file#"$FA_KIT_DIR"/}"
  else rm -f "$file.tmp"; fa_warn "could not export ${file#"$FA_KIT_DIR"/} ($*)"; fi
}

step_export() {
  fa_step 2 "Export Azure configuration into the kit" \
"Records the plan, both apps (show, config, app settings, connection strings, access restrictions,
identity, authsettingsV2), the managed identity and every role assignment, Key Vault and its secret
NAMES (never values), budgets, App Insights, action groups and metric alerts. Read-only on Azure;
writes only into the encrypted kit. Files that already exist are kept." || return 0
  local app sub="/subscriptions/$FA_SUBSCRIPTION_ID"
  fa_capture "$EXP/plan.json" az appservice plan show -g "$FA_RG" -n "$FA_PLAN"
  for app in $FA_APPS; do
    fa_capture "$EXP/$app-show.json" az webapp show -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-config.json" az webapp config show -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-appsettings.json" az webapp config appsettings list -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-connstrings.json" az webapp config connection-string list -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-access-restrictions.json" az webapp config access-restriction show -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-identity.json" az webapp identity show -g "$FA_RG" -n "$app"
    fa_capture "$EXP/$app-authsettingsV2.json" az rest --method get \
      --url "https://management.azure.com$sub/resourceGroups/$FA_RG/providers/Microsoft.Web/sites/$app/config/authsettingsV2/list?api-version=2023-12-01"
  done
  fa_capture "$ROLES/managed-identities.json" az identity list -g "$FA_RG"
  fa_capture "$ROLES/role-assignments.json" az role assignment list --all --include-inherited
  fa_capture "$ROLES/keyvault.json" az keyvault show -n "$FA_KV"
  fa_capture "$ROLES/keyvault-secret-names.json" az keyvault secret list --vault-name "$FA_KV" --query "[].{name:name, enabled:attributes.enabled, expires:attributes.expires}"
  fa_capture "$ROLES/budgets.json" az consumption budget list
  fa_capture "$ROLES/app-insights.json" az resource list -g "$FA_RG" --resource-type microsoft.insights/components
  fa_capture "$ROLES/action-groups.json" az monitor action-group list -g "$FA_RG"
  fa_capture "$ROLES/metric-alerts.json" az monitor metrics alert list -g "$FA_RG"
  fa_step_done "configuration exported"
}

# ---------------------------------------------------------------------------------------------
step_workflows() {
  fa_step 3 "Disable the deploy workflows" \
"Disables '$FA_BACKEND_DEPLOY_WF' in $FA_BACKEND_REPO and '$FA_PANEL_DEPLOY_WF' in $FA_PANEL_REPO, so a push to
development cannot deploy to (and restart) a stopped app. CI workflows stay on. Changes GitHub." || return 0
  local pair repo wf state
  for pair in "$FA_BACKEND_REPO $FA_BACKEND_DEPLOY_WF" "$FA_PANEL_REPO $FA_PANEL_DEPLOY_WF"; do
    repo="${pair% *}"; wf="${pair#* }"
    state=$(gh api "repos/$repo/actions/workflows/$wf" --jq .state)
    if [ "$state" = "active" ]; then fa_act gh workflow disable "$wf" -R "$repo"
    else fa_ok "$repo workflow $wf is $state"; fi
  done
  fa_step_done "deploy workflows disabled" "gh workflow enable <id> -R <repo>"
}

# ---------------------------------------------------------------------------------------------
step_apps() {
  fa_step 4 "Stop both web apps (Always On off)" \
"Turns Always On off (F1 does not allow it) and stops $FA_API_APP and $FA_PANEL_APP. After this
nothing writes to the database, so the dump in step 5 and its live checksums describe the same data.
The apps themselves are kept. Changes Azure." || return 0
  local app always state
  for app in $FA_APPS; do
    always=$(az webapp config show -g "$FA_RG" -n "$app" --query alwaysOn -o tsv)
    state=$(az webapp show -g "$FA_RG" -n "$app" --query state -o tsv)
    if [ "$always" = "true" ]; then fa_act az webapp config set -g "$FA_RG" -n "$app" --always-on false -o none
    else fa_ok "$app: Always On off"; fi
    if [ "$state" = "Stopped" ]; then fa_ok "$app: Stopped"
    else fa_act az webapp stop -g "$FA_RG" -n "$app" -o none; fi
  done
  fa_step_done "both apps stopped, Always On off" "az webapp config set ... --always-on true; az webapp start -g $FA_RG -n <app>"
}

# ---------------------------------------------------------------------------------------------
fa_live_snapshot() { # $1 suffix — runs the kit's verification SQL against Azure
  local kind
  for kind in rowcounts checksums; do fa_psql -f "$KIT_DB/$kind.sql" > "$KIT_DB/live-$kind.$1"; done
}

step_dump() {
  fa_step 5 "Dump the database into the kit" \
"Adds a temporary firewall rule for your IP, then pg_dump -Fc into 02-db-dump with a sha256, plus
server.json, the changed server parameters, firewall rules, databases and extensions, and every
table's row count and jsonb checksum. Checksums are taken before and after the dump and must agree,
proving nothing wrote in between. Changes Azure only by the temporary firewall rule." || return 0
  if [ "$SERVER_EXISTS" != 1 ]; then fa_ok "Server already deleted; nothing to dump"; fa_step_done "skipped (no server)"; return 0; fi
  local app existing dump
  for app in $FA_APPS; do
    if [ "$(az webapp show -g "$FA_RG" -n "$app" --query state -o tsv)" != "Stopped" ] && fa_changes_things; then
      fa_die "$app is still running; run step 4 first so nothing writes during the dump."
    fi
  done
  existing=$(find "$KIT_DB" -maxdepth 1 -name '*.dump' -type f)

  fa_act az postgres flexible-server firewall-rule create -g "$FA_RG" --server-name "$FA_PG_SERVER" \
    --name "$FA_TEMP_FW_RULE" --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" -o none
  if [ "$FA_MODE" = "dry-run" ]; then
    fa_info "would write: $(basename "${existing:-flightassistant-$(date -u +%F).dump}"), .sha256, server.json, server-params-changed.json, firewall-rules.json, databases.json, live-*.txt"
    fa_step_done "would dump"; return 0
  fi
  export PGHOST="$FA_PG_HOST" PGPORT=5432 PGUSER="$FA_PG_ADMIN_USER" PGDATABASE="$FA_PG_DB" PGSSLMODE=require
  export PGPASSWORD="$FA_PG_PASSWORD"
  local tries=1
  until fa_psql -c 'SELECT 1' >/dev/null 2>&1; do
    [ "$tries" -ge 12 ] && fa_die "Cannot connect to $PGHOST."
    fa_info "waiting for the firewall rule ($tries/12)..."; sleep 10; tries=$((tries + 1))
  done

  [ -f "$KIT_DB/checksums.sql" ] || fa_write_verify_sql "$KIT_DB"
  if [ -n "$existing" ]; then
    # Idempotent re-run: the dump already here is current only if the live data still matches it.
    if fa_verify_against_kit; then
      fa_ok "$(basename "$existing") already matches the live database; not dumping again"
      unset PGPASSWORD; fa_step_done "dump already current"; return 0
    fi
    fa_die "$KIT_DB already holds $(basename "$existing"), which no longer matches the live database. Use a new kit (FA_KIT_DIR / FA_KIT_IMAGE) or move it aside."
  fi

  dump="$KIT_DB/flightassistant-$(date -u +%F).dump"
  fa_capture "$KIT_DB/server.json" az postgres flexible-server show -g "$FA_RG" --name "$FA_PG_SERVER"
  fa_capture "$KIT_DB/server-params-changed.json" az postgres flexible-server parameter list -g "$FA_RG" --server-name "$FA_PG_SERVER" \
    --query "[?source=='user-override'].{name:name, value:value, source:source}"
  fa_capture "$KIT_DB/firewall-rules.json" az postgres flexible-server firewall-rule list -g "$FA_RG" --server-name "$FA_PG_SERVER" \
    --query "[?name!='$FA_TEMP_FW_RULE'].{name:name, startIpAddress:startIpAddress, endIpAddress:endIpAddress}"
  fa_capture "$KIT_DB/databases.json" az postgres flexible-server db list -g "$FA_RG" --server-name "$FA_PG_SERVER"
  fa_psql -c "SELECT extname FROM pg_extension ORDER BY 1" > "$KIT_DB/live-extensions.txt"

  fa_live_snapshot before
  fa_show_cmd "$PG_DUMP" --format custom --no-owner --no-privileges --file "$dump.tmp" "$PGDATABASE"
  "$PG_DUMP" --format custom --no-owner --no-privileges --file "$dump.tmp" "$PGDATABASE"
  fa_live_snapshot txt
  if ! cmp -s "$KIT_DB/live-checksums.before" "$KIT_DB/live-checksums.txt"; then
    rm -f "$dump.tmp"
    fa_die "The data changed while dumping (something is still writing). Nothing kept; re-run step 5."
  fi
  rm -f "$KIT_DB/live-rowcounts.before" "$KIT_DB/live-checksums.before"
  mv "$dump.tmp" "$dump"
  (cd "$KIT_DB" && printf '%s  %s\n' "$(fa_sha256 "$(basename "$dump")")" "$(basename "$dump")" > "$(basename "$dump").sha256")
  unset PGPASSWORD
  fa_ok "$(basename "$dump"): $(du -h "$dump" | awk '{print $1}'), $(grep -c . "$KIT_DB/live-rowcounts.txt") tables"
  fa_step_done "dumped $(basename "$dump")"
}

# ---------------------------------------------------------------------------------------------
step_verify() {
  fa_step 6 "Verify the dump by restoring it locally" \
"Restores the dump into a scratch database (fa_pause_verify) on your local PostgreSQL 17 (port
$FA_LOCAL_PGPORT) and compares every table's row count and jsonb checksum with the live values. On
success writes 02-db-dump/VERIFIED with the dump's sha256 — step 8 refuses to delete without it.
Local only; Azure is not touched." || return 0
  local dump sha
  if [ "$FA_MODE" = "dry-run" ] && ! find "$KIT_DB" -maxdepth 1 -name '*.dump' | grep -q .; then
    fa_info "would restore the new dump into local fa_pause_verify and write VERIFIED"; FA_STEP_PLANNED=1
    fa_step_done "would verify"; return 0
  fi
  dump=$(fa_kit_dump)
  fa_verify_dump_sha "$dump"
  sha=$(fa_sha256 "$dump")
  if [ "$(awk '{print $1; exit}' "$KIT_DB/VERIFIED" 2>/dev/null)" = "$sha" ]; then
    fa_ok "Already verified: $(cat "$KIT_DB/VERIFIED")"; fa_step_done "already verified"; return 0
  fi
  fa_local_restore_verify "$dump" fa_pause_verify
  if fa_changes_things; then
    printf '%s  %s  verified %s on local PostgreSQL %s\n' "$sha" "$(basename "$dump")" "$(date -u '+%F %H:%M UTC')" \
      "$(fa_pg_major "$PG_RESTORE")" > "$KIT_DB/VERIFIED"
    fa_ok "Wrote 02-db-dump/VERIFIED"
  fi
  fa_step_done "dump verified locally"
}

# ---------------------------------------------------------------------------------------------
step_plan() {
  fa_step 7 "App Service plan -> $FA_PLAN_PAUSED_SKU" \
"Moves the shared plan to the free F1 tier. The plan and both apps are kept (never deleted), so
resuming is a scale-up, not a rebuild. Changes Azure." || return 0
  local sku app
  sku=$(az appservice plan show -g "$FA_RG" -n "$FA_PLAN" --query sku.name -o tsv)
  if [ "$sku" = "$FA_PLAN_PAUSED_SKU" ]; then fa_ok "Plan is already $sku"; fa_step_done "already $sku"; return 0; fi
  for app in $FA_APPS; do
    if [ "$(az webapp config show -g "$FA_RG" -n "$app" --query alwaysOn -o tsv)" = "true" ] && fa_changes_things; then
      fa_die "$app still has Always On, which F1 rejects. Run step 4 first."
    fi
  done
  fa_act az appservice plan update -g "$FA_RG" -n "$FA_PLAN" --sku "$FA_PLAN_PAUSED_SKU" -o none
  fa_step_done "$sku -> $FA_PLAN_PAUSED_SKU" "az appservice plan update -g $FA_RG -n $FA_PLAN --sku $sku"
}

# ---------------------------------------------------------------------------------------------
step_delete_postgres() {
  fa_step 8 "Delete the PostgreSQL server" \
"Deletes $FA_PG_SERVER — the only resource removed. Refuses unless 02-db-dump/VERIFIED matches the
dump's sha256 exactly, and asks you to type the server name. Key Vault keeps PostgresConnectionString
(old password), which resume-azure.sh reuses to recreate the server. Changes Azure; cannot be undone
except by restoring the dump." || return 0
  local dump sha typed
  if [ "$SERVER_EXISTS" != 1 ]; then fa_ok "Already deleted"; fa_step_done "already deleted"; return 0; fi
  if [ "$FA_MODE" = "dry-run" ]; then
    fa_info "would delete $FA_PG_SERVER once VERIFIED matches the dump, after you type its name"; FA_STEP_PLANNED=1
    fa_step_done "would delete"; return 0
  fi
  dump=$(fa_kit_dump)
  sha=$(fa_sha256 "$dump")
  [ "$(awk '{print $1; exit}' "$KIT_DB/VERIFIED" 2>/dev/null)" = "$sha" ] ||
    fa_die "No VERIFIED marker for $(basename "$dump"). Run step 6 first."
  fa_ok "VERIFIED matches $(basename "$dump")"
  [ -t 0 ] || fa_die "Deleting needs you at a terminal."
  printf '%s' "  ${FA_C_B}Type the server name ($FA_PG_SERVER) to delete it:${FA_C_0} "
  read -r typed
  [ "$typed" = "$FA_PG_SERVER" ] || fa_die "Name did not match; nothing deleted."
  fa_act az postgres flexible-server delete -g "$FA_RG" --name "$FA_PG_SERVER" --yes -o none
  fa_step_done "deleted $FA_PG_SERVER" "scripts/resume-azure.sh --apply --from-step 3"
}

# ---------------------------------------------------------------------------------------------
printf '%s\n' "${FA_C_B}pause-azure.sh — mode: $FA_MODE${FA_C_0}"
step_preflight
if [ "$FA_MODE" = "apply" ]; then
  fa_confirm "This stops both apps, dumps and verifies the database, moves the plan to $FA_PLAN_PAUSED_SKU and then deletes Postgres. Continue?" ||
    fa_die "Nothing changed."
fi
step_export
step_workflows
step_apps
step_dump
step_verify
step_plan
step_delete_postgres
fa_finish
if [ -d "$FA_KIT_DIR" ] && fa_changes_things; then fa_info "Eject the kit when done: hdiutil detach $FA_KIT_DIR"; fi
