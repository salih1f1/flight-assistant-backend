# Handoff

A short bookmark for picking this project back up. The design decisions live in [CLAUDE.md](CLAUDE.md); read that before making changes.

## Where things stand

- **The Azure dev environment is paused** (since 2026-09-26) to stop the bill. See [Pause / resume Azure dev](#pause--resume-azure-dev) — bringing it back is one command.
- `development` is the live branch. CI (`.github/workflows/ci.yml`) runs on every push; the deploy workflow (`development_flight-assistant-dev-api.yml`) is **disabled** while paused, and resume re-enables it.
- No open pull requests.
- The admin panel is a **separate repository** (see [project_admin_panel](https://github.com/salih1f1) in Notion / memory).

## Resume locally

From [CLAUDE.md](CLAUDE.md#quick-start):

```bash
docker compose up -d    # PostgreSQL 16 on :5432, Redis 7 on :6379
dotnet run --project FlightAssistant.API
```

Swagger: <http://localhost:5100/swagger>. To apply migrations locally EF reads `CONNECTION_STRING`, not `appsettings` — see the Quick Start section.

**Keep `FlightSync__Enabled` and `Verification__Enabled` `false` in `launchSettings.json`** for local dev, or your machine polls AeroAPI (billed) and claims from the shared verification queue alongside the deployed app.

## Pause / resume Azure dev

**Bring it back — one command** (from the repo root, on the Mac):

```bash
scripts/resume-azure.sh            # dry run: checks everything, shows the plan, changes nothing
scripts/resume-azure.sh --apply    # does it; asks once up front and again before anything destructive
```

**Guided fallback:** `scripts/resume-azure.sh --guided` explains each step and asks y/n/q before running it. After a failure, re-run with `--from-step N` (or a letter, `--from-step d`); every step checks the current state and skips what is already done, so re-running is always safe. `scripts/resume-azure.sh --local-test` proves the dump restores and verifies against local PostgreSQL 17 on port 5433, without touching Azure.

What resume does, in order: (a) preflight — `az` on subscription `flight-assistant`, `gh`, `jq`, PostgreSQL 17 client tools, mounts the backup image, checks the dump's sha256; (b) App Service plan F1 → B1; (c) recreates Postgres `flightassistantdev-db` from the recorded `server.json` with the password already in Key Vault (so `PostgresConnectionString` needs no change), its changed parameters and firewall rules, plus a temporary rule for your IP; (d) restores the dump and verifies every table's row count and jsonb checksum, then removes the temporary rule; (e) Always On + start for both web apps; (f) re-enables the backend and panel deploy workflows; (g) `/health/ready`, `/admin/health` (set `FA_ADMIN_TOKEN` to an admin bearer token), secrets expiring within 30 days, and your IP in the panel's access restrictions. Each step appends a row to the kit's `LOG.md`.

**The backup kit** is an encrypted sparse image in iCloud: `~/Library/Mobile Documents/com~apple~CloudDocs/Projects/Development/FlightAssistant/Azure-Pause-2026-09/fa-pause.sparseimage`. The scripts mount it themselves (`hdiutil attach` asks for its password); it mounts at `/Volumes/FA-Pause`, and `hdiutil detach /Volumes/FA-Pause` ejects it. It holds the Azure config export, the database dump with its verification data, roles/budget/alerts, the step log, and a copy of these scripts.

**Pausing again:** `scripts/pause-azure.sh` (same dry-run / `--apply` / `--guided` / `--from-step` pattern) disables the deploy workflows, stops both apps with Always On off, dumps the database with live row counts and checksums, restores the dump locally to verify it, moves the plan to F1, and only then deletes Postgres — refusing unless the dump was verified, and only after you type the server name. **It never deletes the App Service plan or the web apps**: their settings, managed identity, role assignments and publish profiles survive the pause.

Both scripts share `scripts/lib/azure-dev.sh`, which holds every resource name. They are written for macOS's stock bash 3.2 and are shellcheck-clean.

## Open issues

- [#541](https://github.com/salih1f1/flight-assistant-backend/issues/541) — `AdminAuditLog.Create` has no length guard on its bounded columns, so a long actor name is a 500 on every admin write.
- [#540](https://github.com/salih1f1/flight-assistant-backend/issues/540) — `ToggleJobCommandHandler` audits in a second transaction, so a job toggle can commit unaudited.
- [#510](https://github.com/salih1f1/flight-assistant-backend/issues/510) — Two hardening items the #465 review triaged as leave, recorded before they are lost.
- [#442](https://github.com/salih1f1/flight-assistant-backend/issues/442) — Never re-enable the App Service codeless App Insights agent: with the SDK active it broke dev with `BadImageFormatException`.
- [#364](https://github.com/salih1f1/flight-assistant-backend/issues/364) — Parent plan to reduce flight-data API cost. Phase 1 shipped, Phase 2 closed (no budget), Phase 4 needs a dev data window before it can start.

## Things not to touch without reading the reason

- **Do not re-enable the codeless App Insights agent** on dev App Service ([#442](https://github.com/salih1f1/flight-assistant-backend/issues/442)). The SDK is already wired via `ApplicationInsightsConnection.Resolve`.
- **Do not hand-edit `spec/openapi.json`.** It's generated; regenerate with `scripts/generate-openapi.sh` from the repo root. CI fails the build on drift.
- **Do not flip `FlightSync__Enabled` / `Verification__Enabled` to `true` in `launchSettings.json` and commit it.** See above.
- **Do not delete the App Service plan, the web apps, Key Vault `flightassistantdev-kv`, App Insights `flight-assistant-dev-api` or managed identity `flight-assistant-id-9909` to save money.** The pause keeps them on purpose; resume depends on all of them. Postgres is the only thing a pause deletes.

## Deep context

- Design decisions, invariants, cost/rate-limit rules: [CLAUDE.md](CLAUDE.md). It is the source of truth; this doc deliberately doesn't duplicate it.
- Cross-session memory for a returning Claude Code session: `~/.claude/projects/-Users-salihalishov-Repositories-flight-assistant-backend/memory/MEMORY.md`.
