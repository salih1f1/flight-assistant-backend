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

## Deep context

- Design decisions, invariants, cost/rate-limit rules: [CLAUDE.md](CLAUDE.md). It is the source of truth; this doc deliberately doesn't duplicate it.
- Cross-session memory for a returning Claude Code session: `~/.claude/projects/-Users-salihalishov-Repositories-flight-assistant-backend/memory/MEMORY.md`.
