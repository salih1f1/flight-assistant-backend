# shellcheck shell=bash disable=SC2034 # the variables here are read by the scripts that source it
# Shared code for scripts/pause-azure.sh and scripts/resume-azure.sh.
#
# Sourced, never run. Written for macOS's stock bash 3.2 (no associative arrays, no ${x,,},
# no mapfile, empty arrays guarded under `set -u`), because `#!/usr/bin/env bash` on a Mac
# without Homebrew bash resolves to exactly that.
#
# Nothing in here changes Azure on its own: every Azure write goes through fa_act, which only
# prints in dry-run mode.

# ---------------------------------------------------------------------------------------------
# The dev environment. Change these here, never in the two scripts.
# ---------------------------------------------------------------------------------------------
FA_SUBSCRIPTION_ID="a39b9c33-675b-40ca-84fc-6e548ef6d6c5"
FA_SUBSCRIPTION_NAME="flight-assistant"
FA_TENANT_ID="967285b3-fe6f-48ae-b2b9-d9b136222022"
FA_RG="flight-assistant-dev"
FA_PLAN="ASP-flightassistantdev-88a9"
FA_PLAN_RUNNING_SKU="B1"
FA_PLAN_PAUSED_SKU="F1"
FA_API_APP="flight-assistant-dev-api"
FA_PANEL_APP="flight-assistant-dev-panel"
FA_APPS="$FA_API_APP $FA_PANEL_APP"
FA_PG_SERVER="flightassistantdev-db"
FA_PG_DB="flight_assistant"
FA_KV="flightassistantdev-kv"
FA_KV_PG_SECRET="PostgresConnectionString"
FA_KV_PANEL_SECRET="panel-gate-client-secret"
FA_KV_GITHUB_SECRET="GitHub--Token"
FA_BACKEND_REPO="salih1f1/flight-assistant-backend"
FA_BACKEND_DEPLOY_WF="185321381"
FA_PANEL_REPO="salih1f1/flight-assistant-admin-panel"
FA_PANEL_DEPLOY_WF="362302398"
FA_TEMP_FW_RULE="fa-temp-operator"
FA_EXPIRY_WARN_DAYS=30

# The encrypted backup kit. FA_KIT_DIR can be pointed elsewhere for testing.
FA_KIT_IMAGE="${FA_KIT_IMAGE:-$HOME/Library/Mobile Documents/com~apple~CloudDocs/Projects/Development/FlightAssistant/Azure-Pause-2026-09/fa-pause.sparseimage}"
FA_KIT_DIR="${FA_KIT_DIR:-/Volumes/FA-Pause}"

# Local PostgreSQL used to prove a dump restores (Homebrew postgresql@17 on 5433 by default).
FA_LOCAL_PGHOST="${FA_LOCAL_PGHOST:-localhost}"
FA_LOCAL_PGPORT="${FA_LOCAL_PGPORT:-5433}"
FA_LOCAL_PGUSER="${FA_LOCAL_PGUSER:-$(id -un)}"

# ---------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------
if [ -t 1 ]; then
  FA_C_B=$'\033[1m'; FA_C_G=$'\033[32m'; FA_C_Y=$'\033[33m'; FA_C_R=$'\033[31m'; FA_C_D=$'\033[2m'; FA_C_0=$'\033[0m'
else
  FA_C_B=""; FA_C_G=""; FA_C_Y=""; FA_C_R=""; FA_C_D=""; FA_C_0=""
fi

fa_info() { printf '%s\n' "  $*"; }
fa_ok()   { printf '%s\n' "  ${FA_C_G}✓${FA_C_0} $*"; }
fa_warn() { printf '%s\n' "  ${FA_C_Y}! $*${FA_C_0}" >&2; FA_WARNINGS=$((FA_WARNINGS + 1)); }
fa_die()  { printf '%s\n' "${FA_C_R}✗ $*${FA_C_0}" >&2; exit 1; }
FA_WARNINGS=0

# Values that must never appear on screen or in LOG.md. fa_redact replaces each with REDACTED.
FA_SECRETS=()
fa_add_secret() { [ -n "$1" ] && FA_SECRETS+=("$1"); return 0; }

# Literal replacement in awk (index(), not a pattern), so it behaves identically on macOS's bash 3.2
# — whose ${s//"$x"/y} does not reliably treat a quoted pattern as literal — and for secrets holding
# *, ? or [. Values travel through the environment, never through argv.
fa_redact() {
  local s="$1" secret
  for secret in ${FA_SECRETS[@]+"${FA_SECRETS[@]}"}; do
    s=$(FA_T="$s" FA_X="$secret" awk 'BEGIN {
      t = ENVIRON["FA_T"]; x = ENVIRON["FA_X"]; out = ""
      while ((i = index(t, x)) > 0) { out = out substr(t, 1, i - 1) "REDACTED"; t = substr(t, i + length(x)) }
      printf "%s", out t }')
  done
  printf '%s' "$s"
}

# Redacts each argument BEFORE shell-quoting it: quoting first turns a password with special
# characters into a different string that the redaction would no longer find.
fa_show_cmd() {
  local line="" arg
  for arg in "$@"; do line="$line $(printf '%q' "$(fa_redact "$arg")")"; done
  printf '%s\n' "    ${FA_C_D}\$${FA_C_0}$line"
}

# ---------------------------------------------------------------------------------------------
# Modes and steps
#   FA_MODE: dry-run | apply | guided | local-test   (set by the calling script)
#   FA_FROM_STEP: first step to run; preflight (step 1) always runs.
# ---------------------------------------------------------------------------------------------
FA_MODE="dry-run"
FA_FROM_STEP=1
FA_CURRENT_STEP=""
FA_SUMMARY=()
FA_LOG_SCRIPT="$(basename "$0")"

fa_changes_things() { [ "$FA_MODE" = "apply" ] || [ "$FA_MODE" = "guided" ] || [ "$FA_MODE" = "local-test" ]; }

# Runs a command that changes something. Dry-run prints it; every other mode prints and runs it.
FA_STEP_PLANNED=0
fa_act() {
  if fa_changes_things; then
    fa_show_cmd "$@"
    "$@"
  else
    FA_STEP_PLANNED=$((FA_STEP_PLANNED + 1))
    printf '%s\n' "    ${FA_C_Y}would run:${FA_C_0}"
    fa_show_cmd "$@"
  fi
}

fa_confirm() { # $1 = question. Returns 0 on yes. Refuses without a terminal.
  local answer
  [ -t 0 ] || fa_die "Needs an answer to \"$1\" but stdin is not a terminal."
  printf '%s' "  ${FA_C_B}$1 [y/N]${FA_C_0} "
  read -r answer
  case "$answer" in y|Y|yes|YES) return 0 ;; *) return 1 ;; esac
}

# fa_step N "Title" "Plain-English explanation" — returns 1 when the step should be skipped.
fa_step() {
  local n="$1" title="$2" why="$3" answer
  FA_CURRENT_STEP="$n. $title"
  FA_STEP_PLANNED=0
  printf '\n%s\n' "${FA_C_B}── Step $n: $title${FA_C_0}"
  if [ "$n" -ne 1 ] && [ "$n" -lt "$FA_FROM_STEP" ]; then
    fa_info "skipped (--from-step $FA_FROM_STEP)"
    FA_SUMMARY+=("$n. $title — skipped (--from-step)")
    return 1
  fi
  if [ "$FA_MODE" = "guided" ] && [ "$n" -ne 1 ]; then
    printf '%s\n' "$why" | sed 's/^/  /'
    [ -t 0 ] || fa_die "--guided needs a terminal."
    printf '%s' "  ${FA_C_B}Run step $n? [y = run / n = skip / q = quit]${FA_C_0} "
    read -r answer
    case "$answer" in
      y|Y|yes) ;;
      q|Q) fa_info "Stopped before step $n. Continue later with: $FA_LOG_SCRIPT --guided --from-step $n"; fa_finish; exit 0 ;;
      *) FA_SUMMARY+=("$n. $title — skipped by you"); return 1 ;;
    esac
  fi
  return 0
}

# fa_step_done "result" ["undo"] — records the step for the summary and in the kit's LOG.md.
fa_step_done() {
  local result="$1" undo="${2:-}"
  if [ "$FA_MODE" = "dry-run" ] && [ "$FA_STEP_PLANNED" -gt 0 ]; then
    result="planned — $FA_STEP_PLANNED change(s), see above"
  fi
  FA_SUMMARY+=("$FA_CURRENT_STEP — $result")
  if fa_changes_things && [ "$FA_MODE" != "local-test" ]; then
    fa_log_row "$FA_CURRENT_STEP" "$result" "$undo"
  fi
}

FA_ERROR_REPORTED=0
fa_on_error() { # ERR fires again in every caller as the failure unwinds; report it once, from the top shell
  [ "$BASH_SUBSHELL" -gt 0 ] && return 0
  [ "$FA_ERROR_REPORTED" = 1 ] && return 0
  FA_ERROR_REPORTED=1
  printf '%s\n' "${FA_C_R}✗ Failed in step ${FA_CURRENT_STEP:-?} (line $1).${FA_C_0}" >&2
  printf '%s\n' "  Every step checks its own state first, so it is safe to re-run from here:" >&2
  local flag="--$FA_MODE"; [ "$FA_MODE" = "dry-run" ] && flag=""
  printf '%s\n' "    $FA_LOG_SCRIPT $flag --from-step ${FA_CURRENT_STEP%%.*}" >&2
}

fa_finish() {
  local line
  printf '\n%s\n' "${FA_C_B}── Summary ($FA_MODE)${FA_C_0}"
  for line in ${FA_SUMMARY[@]+"${FA_SUMMARY[@]}"}; do fa_info "$line"; done
  [ "$FA_WARNINGS" -gt 0 ] && fa_info "${FA_C_Y}$FA_WARNINGS warning(s) above.${FA_C_0}"
  [ "$FA_MODE" = "dry-run" ] && fa_info "Nothing was changed. Run with --apply (or --guided) to do it."
  return 0
}

# Appends one row to the kit's LOG.md, matching its table's column count.
fa_log_row() {
  local log="$FA_KIT_DIR/LOG.md" cols last n row i
  [ -w "$log" ] || return 0
  cols=$( (grep -m1 '^|' "$log" || true) | tr -cd '|' | wc -c | tr -d ' ')
  cols=$((cols - 1))
  last=$(grep -E '^\|[[:space:]]*[0-9]+[[:space:]]*\|' "$log" | tail -1 | sed -E 's/^\|[[:space:]]*([0-9]+).*/\1/' || true)
  n=$(( ${last:-0} + 1 ))
  row="| $n | $(date -u '+%Y-%m-%d %H:%M UTC') | $FA_LOG_SCRIPT: $1 | $(fa_redact "$2") | $(fa_redact "$3") |"
  i=5
  while [ "$i" -lt "$cols" ]; do row="$row  |"; i=$((i + 1)); done
  [ -n "$(tail -c1 "$log")" ] && printf '\n' >> "$log"
  printf '%s\n' "$row" >> "$log"
}

fa_parse_args() { # shared flags; the caller passes "$@"
  while [ $# -gt 0 ]; do
    case "$1" in
      --apply) FA_MODE="apply" ;;
      --guided) FA_MODE="guided" ;;
      --local-test) FA_MODE="local-test" ;;
      --dry-run) FA_MODE="dry-run" ;;
      --from-step) shift; FA_FROM_STEP="${1:-}" ;;
      --from-step=*) FA_FROM_STEP="${1#*=}" ;;
      -h|--help) fa_usage; exit 0 ;;
      *) fa_usage >&2; exit 2 ;;
    esac
    shift
  done
  # Accept a letter too (a = 1, b = 2, ...), matching the step letters in HANDOFF.md.
  case "$FA_FROM_STEP" in
    [a-z]) FA_FROM_STEP=$(( $(printf '%d' "'$FA_FROM_STEP") - 96 )) ;;
  esac
  case "$FA_FROM_STEP" in ''|*[!0-9]*) fa_die "--from-step needs a step number (1-9) or letter (a-i)." ;; esac
  return 0
}

# ---------------------------------------------------------------------------------------------
# Tools
# ---------------------------------------------------------------------------------------------
fa_need() { command -v "$1" >/dev/null 2>&1 || fa_die "$1 is not installed. $2"; }

fa_sha256() {
  if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
  else sha256sum "$1" | awk '{print $1}'; fi
}

fa_pg_major() { "$1" --version 2>/dev/null | sed -E 's/^[^0-9]*([0-9]+).*/\1/'; }

# Finds pg_dump/pg_restore/psql of at least FA_MIN_PG_MAJOR (17: the dump is PG 17 custom format).
# Homebrew's postgresql@17 is keg-only, so it is usually NOT on PATH; look there too.
fa_find_pg_bin() {
  local min="${FA_MIN_PG_MAJOR:-17}" dir candidates
  candidates="${FA_PG_BIN:-}"
  if command -v pg_restore >/dev/null 2>&1; then
    candidates="$candidates $(dirname "$(command -v pg_restore)")"
  fi
  candidates="$candidates /opt/homebrew/opt/postgresql@17/bin /usr/local/opt/postgresql@17/bin"
  candidates="$candidates /opt/homebrew/opt/postgresql@18/bin /usr/local/opt/postgresql@18/bin"
  candidates="$candidates /usr/lib/postgresql/18/bin /usr/lib/postgresql/17/bin"
  for dir in $candidates; do
    if [ ! -x "$dir/pg_restore" ] || [ ! -x "$dir/psql" ]; then continue; fi
    if [ "$(fa_pg_major "$dir/pg_restore")" -ge "$min" ] 2>/dev/null; then
      PG_BIN="$dir"; PG_RESTORE="$dir/pg_restore"; PG_DUMP="$dir/pg_dump"; PSQL="$dir/psql"
      return 0
    fi
  done
  fa_die "No pg_restore >= $min found. brew install postgresql@17 (or set FA_PG_BIN to its bin directory)."
}

fa_check_az() {
  local id tenant state
  fa_need az "brew install azure-cli"
  id=$(az account show --query id -o tsv 2>/dev/null) || fa_die "az is not logged in. Run: az login --tenant $FA_TENANT_ID"
  [ "$id" = "$FA_SUBSCRIPTION_ID" ] || fa_die "az is on subscription $id, not $FA_SUBSCRIPTION_NAME. Run: az account set --subscription $FA_SUBSCRIPTION_ID"
  tenant=$(az account show --query tenantId -o tsv)
  [ "$tenant" = "$FA_TENANT_ID" ] || fa_die "az tenant is $tenant, expected $FA_TENANT_ID."
  state=$(az account show --query state -o tsv)
  fa_ok "az logged in: subscription $FA_SUBSCRIPTION_NAME ($id), state $state"
  [ "$state" = "Enabled" ] || fa_info "(az reports '$state' while the portal says Active — fine unless something below gets a 403)"
  # The postgres sub-commands moved to -s/--server-name + -n/--name; the old forms print
  # deprecation warnings and will eventually stop working. Refuse an az too old to know the new form.
  az postgres flexible-server firewall-rule create --help 2>/dev/null | grep -q -- '--server-name' ||
    fa_die "This az is too old for 'flexible-server firewall-rule --server-name'. Run: az upgrade"
  fa_ok "az $(az version --query '"azure-cli"' -o tsv) supports the current postgres flags"
}

fa_check_gh() {
  local repo
  fa_need gh "brew install gh"
  gh auth status >/dev/null 2>&1 || fa_die "gh is not logged in. Run: gh auth login"
  for repo in "$FA_BACKEND_REPO" "$FA_PANEL_REPO"; do
    gh api "repos/$repo" --jq .full_name >/dev/null 2>&1 || fa_die "gh cannot see $repo."
  done
  fa_ok "gh logged in and can see both repositories"
}

# Distinguishes "does not exist" from every other failure (a 403 must not read as "missing").
# Usage: fa_az_exists az <show command...>   → 0 exists, 1 missing, dies otherwise.
fa_az_exists() {
  local err
  if err=$("$@" -o none 2>&1); then return 0; fi
  case "$err" in
    *ResourceNotFound*|*"was not found"*|*"could not be found"*|*"not found"*|*NotFound*) return 1 ;;
  esac
  fa_die "Could not check ($*): $err"
}

fa_my_ip() {
  local ip
  ip=$(curl -fsS --max-time 10 https://api.ipify.org 2>/dev/null || true)
  case "$ip" in
    *[!0-9.]*|'') fa_die "Could not determine your public IPv4 address (https://api.ipify.org)." ;;
  esac
  printf '%s' "$ip"
}

fa_ip_to_int() {
  local a b c d
  IFS=. read -r a b c d <<EOF
$1
EOF
  printf '%s' $(( (a << 24) + (b << 16) + (c << 8) + d ))
}

fa_ip_in_cidr() { # $1 ip, $2 cidr or bare ip. "Any" matches everything.
  local net bits mask
  [ "$2" = "Any" ] && return 0
  net="${2%/*}"; bits="${2#*/}"; [ "$bits" = "$2" ] && bits=32
  case "$net" in *[!0-9.]*|'') return 1 ;; esac
  mask=$(( bits == 0 ? 0 : (0xFFFFFFFF << (32 - bits)) & 0xFFFFFFFF ))
  [ $(( $(fa_ip_to_int "$1") & mask )) -eq $(( $(fa_ip_to_int "$net") & mask )) ]
}

# Days until an ISO-8601 timestamp (negative when past). jq does the date maths so it works the
# same on macOS and Linux.
fa_days_until() {
  jq -nr --arg d "$1" '
    ($d | sub("\\.[0-9]+"; "") | sub("(\\+00:00|\\+0000| UTC)$"; "Z") | sub(" "; "T")
        | if test("Z$") then . else . + "Z" end | fromdateiso8601) as $t
    | (($t - now) / 86400 | floor)'
}

# ---------------------------------------------------------------------------------------------
# The Postgres password, from the Key Vault connection string. Never printed, never in argv of
# anything but `az postgres flexible-server create` (which has no other way to take it).
# ---------------------------------------------------------------------------------------------
fa_connstr_get() { # $1 connection string, $2.. accepted key names (lower case)
  local cs="$1" part key want parts
  shift
  IFS=';' read -r -a parts <<EOF
$cs
EOF
  # A fragment with no '=' means a ';' inside a value: refuse rather than truncate the password.
  for part in ${parts[@]+"${parts[@]}"}; do
    case "$part" in *=*|'') ;; *[![:space:]]*) return 2 ;; esac
  done
  for part in ${parts[@]+"${parts[@]}"}; do
    case "$part" in *=*) ;; *) continue ;; esac
    key=$(printf '%s' "${part%%=*}" | tr '[:upper:]' '[:lower:]' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g')
    for want in "$@"; do
      if [ "$key" = "$want" ]; then printf '%s' "${part#*=}"; return 0; fi
    done
  done
  return 1
}

# Sets FA_PG_ADMIN_USER / FA_PG_PASSWORD from Key Vault and checks the secret points at this server.
fa_load_pg_secret() {
  local cs host db
  cs=$(az keyvault secret show --vault-name "$FA_KV" --name "$FA_KV_PG_SECRET" --query value -o tsv) ||
    fa_die "Cannot read $FA_KV_PG_SECRET from $FA_KV (need 'Key Vault Secrets User' on it)."
  fa_add_secret "$cs"
  FA_PG_PASSWORD=$(fa_connstr_get "$cs" password pwd) ||
    fa_die "$FA_KV_PG_SECRET has no Password=, or a value contains ';' (this script will not guess where it ends)."
  case "$FA_PG_PASSWORD" in \"*|\'*) fa_die "$FA_KV_PG_SECRET's password is quoted; this script cannot parse that safely." ;; esac
  [ -n "$FA_PG_PASSWORD" ] || fa_die "$FA_KV_PG_SECRET has an empty password."
  fa_add_secret "$FA_PG_PASSWORD"
  FA_PG_ADMIN_USER=$(fa_connstr_get "$cs" username "user id" "user name" userid user) || fa_die "$FA_KV_PG_SECRET has no Username=."
  host=$(fa_connstr_get "$cs" host server) || fa_die "$FA_KV_PG_SECRET has no Host=."
  db=$(fa_connstr_get "$cs" database db) || fa_die "$FA_KV_PG_SECRET has no Database=."
  case "$host" in "$FA_PG_SERVER".*) ;; *) fa_die "$FA_KV_PG_SECRET points at host $host, not $FA_PG_SERVER." ;; esac
  [ "$db" = "$FA_PG_DB" ] || fa_die "$FA_KV_PG_SECRET names database $db, not $FA_PG_DB."
  FA_PG_HOST="$host"
  fa_ok "Key Vault secret $FA_KV_PG_SECRET: host $host, database $db, user $FA_PG_ADMIN_USER, password loaded (not shown)"
}

# ---------------------------------------------------------------------------------------------
# The backup kit (encrypted sparse image)
# ---------------------------------------------------------------------------------------------
fa_mount_kit() {
  if [ -f "$FA_KIT_DIR/README.md" ]; then fa_ok "Backup kit mounted at $FA_KIT_DIR"; return 0; fi
  command -v hdiutil >/dev/null 2>&1 || fa_die "Backup kit not found at $FA_KIT_DIR (and no hdiutil to mount it)."
  [ -f "$FA_KIT_IMAGE" ] || fa_die "Backup image not found: $FA_KIT_IMAGE (is iCloud Drive synced?)"
  fa_info "Mounting the backup kit — macOS will ask for the image password."
  hdiutil attach "$FA_KIT_IMAGE" >/dev/null || fa_die "hdiutil attach failed."
  [ -f "$FA_KIT_DIR/README.md" ] || fa_die "Mounted, but $FA_KIT_DIR/README.md is missing."
  fa_ok "Backup kit mounted at $FA_KIT_DIR (eject later: hdiutil detach $FA_KIT_DIR)"
}

# Prints the path of the single *.dump in 02-db-dump.
fa_kit_dump() {
  local dumps count
  dumps=$(find "$FA_KIT_DIR/02-db-dump" -maxdepth 1 -name '*.dump' -type f 2>/dev/null)
  count=$(printf '%s' "$dumps" | grep -c . || true)
  [ "$count" -eq 1 ] || fa_die "Expected exactly one .dump in $FA_KIT_DIR/02-db-dump, found $count."
  printf '%s' "$dumps"
}

fa_verify_dump_sha() { # $1 dump path; expects "$1.sha256" or <name-without-.dump>.sha256
  local dump="$1" shafile expected actual
  shafile="$dump.sha256"; [ -f "$shafile" ] || shafile="${dump%.dump}.sha256"
  [ -f "$shafile" ] || fa_die "No .sha256 next to $dump."
  expected=$(awk '{print $1; exit}' "$shafile")
  actual=$(fa_sha256 "$dump")
  [ "$expected" = "$actual" ] || fa_die "Dump checksum mismatch: expected $expected, got $actual."
  fa_ok "Dump $(basename "$dump") matches its sha256 ($actual)"
}

# ---------------------------------------------------------------------------------------------
# Verification: row counts and content checksums for every table.
#
# The checksum hashes each row as to_jsonb(t)::text, ordered COLLATE "C". Not t::text: row-text
# output quotes non-ASCII bytes (e.g. 0xA0 in airport names) differently on macOS and Linux, so a
# dump verified on a Mac would never match Azure. TimeZone is pinned because to_jsonb renders
# timestamptz in the session zone.
# ---------------------------------------------------------------------------------------------
fa_write_verify_sql() { # $1 dir — writes rowcounts.sql and checksums.sql (used by pause-azure.sh)
  cat > "$1/rowcounts.sql" <<'SQL'
-- One line per table: schema.table|row count. Run: psql -X -At -v ON_ERROR_STOP=1 -f rowcounts.sql
\set QUIET on
SET TimeZone = 'UTC';
SELECT format('SELECT %L || ''|'' || count(*) FROM %I.%I', n.nspname || '.' || c.relname, n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
  AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C"
\gexec
SQL
  cat > "$1/checksums.sql" <<'SQL'
-- One line per table: schema.table|md5 of every row as jsonb text, ordered COLLATE "C".
-- jsonb, not t::text: row text differs between macOS and Linux for non-ASCII bytes.
\set QUIET on
SET TimeZone = 'UTC';
SET extra_float_digits = 1;
SELECT format('SELECT %L || ''|'' || md5(coalesce(string_agg(to_jsonb(t)::text, E''\n'' ORDER BY to_jsonb(t)::text COLLATE "C"), '''')) FROM %I.%I t',
              n.nspname || '.' || c.relname, n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
  AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C"
\gexec
SQL
}

# Normalises psql output so "public.x|1", "x | 1" and aligned output with headers compare equal.
fa_normalize() {
  tr -d '\r' | sed -E \
    -e 's/[[:space:]]*\|[[:space:]]*/|/g' -e 's/^[[:space:]]+//' -e 's/[[:space:]]+$//' \
    -e 's/"//g' -e 's/^public\.//' \
    | grep -vE '^$|^-+(\+-+)*$|^\([0-9]+ rows?\)$|^SET$' | LC_ALL=C sort
}

fa_psql() { PGTZ=UTC "$PSQL" -X -At -v ON_ERROR_STOP=1 "$@"; }

fa_table_count() {
  fa_psql -c "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE c.relkind IN ('r','p') AND NOT c.relispartition
                AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\_%'"
}

# Compares the database the PG* environment points at with the kit's live-*.txt, using the kit's
# own SQL (the SQL that produced those files). Returns 1 on any difference.
fa_verify_against_kit() {
  local dir="$FA_KIT_DIR/02-db-dump" tmp kind expected tables rc=0
  tmp=$(mktemp -d)
  if ! grep -q 'to_jsonb' "$dir/checksums.sql"; then
    fa_warn "checksums.sql hashes row text, not jsonb: tables with non-ASCII names can differ between macOS and Linux."
  fi
  for kind in rowcounts checksums; do
    fa_psql -f "$dir/$kind.sql" | fa_normalize > "$tmp/$kind.actual"
    fa_normalize < "$dir/live-$kind.txt" > "$tmp/$kind.expected"
    expected=$(grep -c . "$tmp/$kind.expected" || true)
    if diff -u "$tmp/$kind.expected" "$tmp/$kind.actual" > "$tmp/$kind.diff"; then
      fa_ok "$kind: all $expected tables match live-$kind.txt"
    else
      fa_warn "$kind differ from live-$kind.txt (- expected, + restored):"
      sed -n '3,40p' "$tmp/$kind.diff" | sed 's/^/      /' >&2
      rc=1
    fi
  done
  tables=$(fa_table_count)
  expected=$(grep -c . "$tmp/rowcounts.expected" || true)
  if [ "$tables" != "$expected" ]; then
    fa_warn "Database has $tables tables; live-rowcounts.txt lists $expected."
    rc=1
  fi
  if [ -f "$dir/live-extensions.txt" ]; then
    fa_psql -c "SELECT extname FROM pg_extension ORDER BY 1" | fa_normalize > "$tmp/ext.actual"
    fa_normalize < "$dir/live-extensions.txt" | sed -E 's/\|.*//' | grep -vE '^extname$' > "$tmp/ext.expected"
    if ! diff -q "$tmp/ext.expected" "$tmp/ext.actual" >/dev/null; then
      fa_warn "Extensions differ: expected $(tr '\n' ' ' < "$tmp/ext.expected"), got $(tr '\n' ' ' < "$tmp/ext.actual")"
    fi
  fi
  rm -rf "$tmp"
  return "$rc"
}

# pg_restore into the database the PG* environment points at. --single-transaction makes a failed
# restore leave the database exactly as it was, so a retry starts clean.
fa_restore() { # $1 dump
  fa_act env PGTZ=UTC "$PG_RESTORE" --no-owner --no-privileges --exit-on-error --single-transaction \
    --dbname "$PGDATABASE" "$1"
}

# Restores $1 into a scratch database $2 on the local server (FA_LOCAL_PG*) and verifies it against
# the kit. Proves a dump is good without touching Azure. The scratch database is kept for a look.
fa_local_restore_verify() {
  local dump="$1" server_major
  export PGHOST="$FA_LOCAL_PGHOST" PGPORT="$FA_LOCAL_PGPORT" PGUSER="$FA_LOCAL_PGUSER" PGDATABASE="$2"
  unset PGPASSWORD PGSSLMODE
  "$PG_BIN/pg_isready" -q ||
    fa_die "No PostgreSQL on $PGHOST:$PGPORT. Start it: $PG_BIN/pg_ctl -D /opt/homebrew/var/postgresql@17 -o \"-p $PGPORT\" -l /tmp/pg17.log start"
  server_major=$(fa_psql -d postgres -c 'SHOW server_version_num' | cut -c1-2)
  [ "$server_major" -ge "${FA_MIN_PG_MAJOR:-17}" ] ||
    fa_die "Local server on $PGPORT is PostgreSQL $server_major; need ${FA_MIN_PG_MAJOR:-17}+."
  fa_ok "Local PostgreSQL $server_major on $PGHOST:$PGPORT"
  fa_info "Recreating the scratch database $PGDATABASE (local only)"
  PGOPTIONS="-c client_min_messages=warning" fa_psql -d postgres -c "DROP DATABASE IF EXISTS $PGDATABASE" >/dev/null
  fa_psql -d postgres -c "CREATE DATABASE $PGDATABASE TEMPLATE template0 ENCODING 'UTF8'" >/dev/null
  # Runs even in dry-run: it only touches the local scratch database.
  fa_show_cmd env PGTZ=UTC "$PG_RESTORE" --no-owner --no-privileges --exit-on-error --single-transaction --dbname "$PGDATABASE" "$dump"
  PGTZ=UTC "$PG_RESTORE" --no-owner --no-privileges --exit-on-error --single-transaction --dbname "$PGDATABASE" "$dump"
  fa_verify_against_kit || fa_die "Local restore does not match the live snapshot (database $PGDATABASE kept for inspection)."
  fa_ok "The dump restores cleanly and matches the live snapshot."
  fa_info "Scratch database kept: $PSQL -p $PGPORT $PGDATABASE   (drop it: $PG_BIN/dropdb -p $PGPORT $PGDATABASE)"
}
