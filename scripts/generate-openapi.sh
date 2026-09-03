#!/usr/bin/env bash
# Regenerates the committed OpenAPI spec (spec/openapi.json).
#
# Usage:
#   scripts/generate-openapi.sh [output-path] [assembly-dir]
#
#   output-path   Where to write the spec. Defaults to spec/openapi.json.
#   assembly-dir  A directory already containing FlightAssistant.API.dll (e.g. from a
#                 prior `dotnet publish`). If omitted, this script publishes the API
#                 itself into a temporary directory. CI passes this so the drift check
#                 reuses the build the job already did, instead of a third full build.
#
# This is the single copy of the dummy configuration block: the host boots to produce
# the spec (AeroApiOptions.ApiKey is [Required] and validated on start, so it won't
# start without *something*), but the export makes no outbound call and the spec
# itself carries no configuration values, so dummy values are enough — and only
# dummy values belong here. Never point this at real credentials.
#
# ci.yml's drift check and development_flight-assistant-dev-api.yml's export step
# both call this script rather than each keeping their own copy of the block above —
# two divergent copies of it is exactly the kind of drift this script exists to avoid.
set -euo pipefail

# The dotnet tool manifest (.config/dotnet-tools.json) is repo-local, so `dotnet
# swagger` only resolves from the repository root. Run elsewhere and the dotnet CLI
# prints "You misspelled a built-in dotnet command", which points nowhere near the
# real cause — so check for it ourselves and say so plainly.
if [[ ! -f .config/dotnet-tools.json ]]; then
  echo "error: run this script from the repository root (no .config/dotnet-tools.json in $(pwd))." >&2
  exit 1
fi

OUTPUT_PATH="${1:-spec/openapi.json}"
ASSEMBLY_DIR="${2:-}"

CLEANUP_DIR=""
cleanup() {
  [[ -n "$CLEANUP_DIR" ]] && rm -rf "$CLEANUP_DIR"
}
trap cleanup EXIT

if [[ -z "$ASSEMBLY_DIR" ]]; then
  CLEANUP_DIR="$(mktemp -d)"
  ASSEMBLY_DIR="$CLEANUP_DIR"
  echo "Publishing FlightAssistant.API (Release)..."
  dotnet publish FlightAssistant.API --configuration Release -o "$ASSEMBLY_DIR"
elif [[ ! -f "$ASSEMBLY_DIR/FlightAssistant.API.dll" ]]; then
  echo "error: $ASSEMBLY_DIR does not contain FlightAssistant.API.dll — pass a directory from 'dotnet publish FlightAssistant.API', or omit the argument to let this script publish it." >&2
  exit 1
fi

echo "Restoring dotnet tools..."
dotnet tool restore

# Dummy values only — see the file header. The host is booted only to walk its own
# route/DTO metadata; it never makes an outbound call while doing so.
export ASPNETCORE_ENVIRONMENT=Development
export PostgresConnectionString="Host=localhost;Database=dummy;Username=dummy;Password=dummy"
export Azure__TenantId="00000000-0000-0000-0000-000000000000"
export Azure__ClientId="00000000-0000-0000-0000-000000000000"
export Azure__Instance="https://login.microsoftonline.com/"
export Azure__Domain="dummy.onmicrosoft.com"
export Azure__SignUpSignInPolicyId="B2C_1_signupsignin"
export Azure__Issuer="https://dummy.b2clogin.com/{0}/v2.0/"
export Azure__Authority="https://dummy.b2clogin.com/{0}/B2C_1_signupsignin/v2.0"
export AeroApi__ApiKey="dummy"

mkdir -p "$(dirname "$OUTPUT_PATH")"

echo "Generating $OUTPUT_PATH..."
dotnet swagger tofile --output "$OUTPUT_PATH" "$ASSEMBLY_DIR/FlightAssistant.API.dll" v1

echo "Wrote $OUTPUT_PATH."
