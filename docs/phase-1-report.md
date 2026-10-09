# Phase 1 Report: Project Setup, Database, and Configuration

**Date:** 2026-10-10
**Branch:** `phase-1` · **Tag:** `phase-1-stable`
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| Solution builds in Release with zero build errors | **Pass** | `dotnet build OB2.sln -c Release` gave 0 errors and 0 warnings, with warnings treated as errors. |
| Migrations apply to an empty database and are idempotent on re-run | **Pass** | `MigrationRunnerTests.Applies_embedded_v1_schema_to_empty_database`, `Rerun_is_idempotent` (three runs, identical schema, one `schema_version` row), `StorageBootstrapperTests.Second_start_is_idempotent_and_keeps_saved_settings` |
| Settings round-trip correctly, including the portable-flag path | **Pass** | `JsonSettingsStoreTests.Settings_round_trip`, `AppPathsTests.Portable_flag_puts_everything_beside_the_executable`, `StorageBootstrapperTests.Portable_first_run_*` and `Second_start_*` |
| A log file is created and rolls at the configured size | **Pass** | `LogConfiguratorTests.Creates_a_log_file`, `Rolls_to_a_new_file_at_the_configured_size` (4 KB limit, multiple files, no events lost) |
| Unit tests for the migration runner and atomic writer pass | **Pass** | 12 `MigrationRunnerTests`, 9 `AtomicFileTests` |

## Test run

```
Passed!  - Failed: 0, Passed: 18, Skipped: 0, Total: 18 - OB2.Core.Tests.dll (net8.0)
Passed!  - Failed: 0, Passed: 26, Skipped: 0, Total: 26 - OB2.Storage.Tests.dll (net8.0)
Passed!  - Failed: 0, Passed:  4, Skipped: 0, Total:  4 - OB2.Integration.Tests.dll (net8.0)
```

None of the test projects references `OB2.App`.

## Manual smoke test (WPF shell)

I copied the Release output to a scratch folder, added `portable.flag`, and ran `OB2.exe`:

- The window opened with the title "OB2" and a status bar showing the data folder, "Portable", and "Database schema v1".
- `settings.json`, `data\ob2.db` (WAL), and `data\logs\ob2-20261010.log` were created beside the exe. Nothing was written to `%APPDATA%`.
- The log shows startup, `Applying migration 001_init`, `Database migrated from version 0 to 1`, and `OB2 exiting with code 0` after the window was closed.
- Working set of the empty shell: about 110 MB. See the backlog note on AC-12.

## Delivered

- Solution structure per §7.3: Core, Storage, App, Host, Shared, and three test projects.
- Central NuGet version pinning, strict analyzers, `.editorconfig`, `.gitattributes`.
- DI composition root (`OB2.App/CompositionRoot.cs`) and a global UI error boundary (§11.3).
- Serilog rolling files: daily, 10 MB per file, 7-day retention (NFR Logging).
- SQLite schema v1 as `001_init.sql`, embedded, applied by `MigrationRunner` with a `schema_version` table.
- `settings.json` load/save with portable vs. `%APPDATA%` resolution (§10.2).
- `AtomicFile` helper for settings and checkpoints (§11.3).
- CI workflow `.github/workflows/ci.yml` (restore, Release build, test, upload results on `windows-latest`).

## Not verified

- **The CI workflow has not run on GitHub.** The repo has no remote yet. I ran the same commands locally.
- Log retention by age is configured but not covered by a test (see backlog).

## Carried forward

See [backlog.md](backlog.md). Most important: **.NET 8 support ends 2026-11-10**, and the owner needs to decide on .NET 10 before Phase 5.
