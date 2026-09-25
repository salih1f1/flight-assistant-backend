# Handoff

A short bookmark for picking this project back up. The design decisions live in [CLAUDE.md](CLAUDE.md); read that before making changes.

## Where things stand

- `development` is the live branch. Every push deploys via `.github/workflows/ci.yml`.
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

## Pausing Azure to $0/month

The dev subscription costs ~$30/mo when idle. To pause to **$0** while keeping the ability to come back in an afternoon.

### Inventory (resource group `flight-assistant-dev`, West Europe)

| Resource | SKU | Idle cost | Action |
|---|---|---|---|
| App Service Plan `ASP-flightassistantdev-88a9` | B1 Basic | ~$13/mo | **Delete** (bills whether apps run or not) |
| `flight-assistant-dev-api` (Web/sites) | on plan | — | Delete |
| `flight-assistant-dev-panel` (Web/sites) | on plan | — | Delete |
| Postgres Flexible `flightassistantdev-db` | Standard_B1ms, 32 GB, PG 17, admin `pgadmin` | ~$16/mo | **Dump then delete** — a `stop` still bills storage and auto-restarts after 7 days |
| Key Vault `flightassistantdev-kv` | Standard | pennies | **Keep** — soft-delete on, secrets stay recoverable |
| App Insights `flight-assistant-dev-api` | pay-per-GB | $0 when idle | Keep |
| Managed identity `flight-assistant-id-9909` | — | free | Keep |
| Action groups (2) | — | free | Keep |

No Redis in dev, no storage account of ours — nothing else to delete.

### PII note

Only one row in `travelers` (an admin email, no password). The rest of the DB is public reference data — airports, airlines, countries, alliances, loyalty programs, lounges — plus verification history. Not sensitive, but expensive to rebuild: rerunning imports and the verification queue costs metered AeroAPI calls. **Take the dump.**

### Pause procedure

Both scripts default to `--dry-run` and refuse to touch anything without `--apply`.

1. **Take the dump.** `scripts/pause-azure.sh --dump-only` runs `pg_dump` to `./flightassistant-YYYY-MM-DD.dump`. It also creates a temporary firewall rule for your public IP and removes it after. Move the file **out of this repo** — iCloud Drive is a good home given there's no PII:

   ```bash
   mv flightassistant-*.dump ~/Library/Mobile\ Documents/com~apple~CloudDocs/flight-assistant-backups/
   ```

   Verify the dump is readable with `pg_restore --list ~/Library/Mobile\ Documents/com~apple~CloudDocs/flight-assistant-backups/flightassistant-*.dump | head` before deleting anything in Azure.
2. **Delete everything else.** `scripts/pause-azure.sh --apply` deletes both Web Apps, the App Service Plan, and the Postgres server. It leaves the Key Vault, App Insights, managed identity, and action groups intact — so config wiring and secret names survive.
3. **Verify $0.** After the next Azure bill cycle, `/admin/health` shows the panel side, but the API itself is gone; use Azure Cost Management on the subscription to confirm daily spend is $0.

### Resume procedure

`scripts/resume-azure.sh --apply` (against the dump path you saved) does, in order:

1. Recreate the Postgres server with the same name, region, SKU, storage. About 10 min.
2. `pg_restore` your dump into it.
3. Recreate the App Service Plan (B1) and both Web Apps under the same names.
4. Reattach the surviving managed identity to each site and re-grant Key Vault access if RBAC needs it.
5. Print the next-step reminder: push to `development` to trigger `.github/workflows/development_flight-assistant-dev-api.yml`, which redeploys the API. The panel's own workflow in the panel repo redeploys the panel.

### Warned-subscription reminder

The subscription is in ARM state `Warned` (see `project_dev_subscription_billing_state` in the Claude Code memory). Deleting resources does not clear that — check `az account show --query state` if resume fails with 403s.

## Deep context

- Design decisions, invariants, cost/rate-limit rules: [CLAUDE.md](CLAUDE.md). It is the source of truth; this doc deliberately doesn't duplicate it.
- Cross-session memory for a returning Claude Code session: `~/.claude/projects/-Users-salihalishov-Repositories-flight-assistant-backend/memory/MEMORY.md`.
