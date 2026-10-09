# Phase 2 Report: Download Engine with Pause/Resume and Integrity Verification

**Date:** 2026-10-10
**Branch:** `phase-2` (from `phase-1-stable`) · **Tag:** `phase-2-stable`
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| AC-2 (single connection): pause, close the app, reopen, resume, no corrupted bytes | **Pass** | `SingleConnectionTests.Pause_then_reopen_resumes_from_checkpoint_without_corruption`: 4 MB file at 2 MB/s, paused after ≥1 MB was checkpointed, engine disposed and recreated over the same database, resumed with `Range: bytes=<checkpoint>-`, SHA-256 matches. |
| AC-11: path traversal sanitized | **Pass** | `Path_traversal_in_server_name_stays_inside_save_folder` (4 hostile Content-Disposition names; nothing written outside the save folder), plus `FileNameSanitizerTests`, `SafePathTests`, `FileNameResolverTests.Encoded_traversal_in_url_is_sanitized`. |
| A wrong SHA-256 is rejected and the `.pgpart` file is kept, not renamed | **Pass** | `Sha256_mismatch_fails_and_keeps_the_partial_file`; CLI `Get_with_wrong_sha256_exits_2_and_keeps_partial_file`. |
| Crash simulation (kill the process mid-download) resumes correctly | **Pass** | `CliTests.Killed_process_resumes_from_checkpoint_without_corruption`: the real `OB2.exe get` downloads 8 MB at 1 MB/s, gets `Kill()`ed after its first 5-second checkpoint, is run again, prints "Resuming", continues with a non-zero Range start, and the SHA-256 matches. |
| Engine unit tests run without any UI reference | **Pass** | `OB2.Core.Tests` references only `OB2.Core`. `OB2.Integration.Tests` builds `OB2.App` without referencing its assembly, and runs it only as a CLI child process. |

## Test run

```
Passed!  - Failed: 0, Passed: 88, Skipped: 0, Total: 88 - OB2.Core.Tests.dll
Passed!  - Failed: 0, Passed: 32, Skipped: 0, Total: 32 - OB2.Storage.Tests.dll
Passed!  - Failed: 0, Passed: 30, Skipped: 0, Total: 30 - OB2.Integration.Tests.dll (13 s)
```

The integration suite was run three times in a row with no failures, to check for flaky timing.

## Delivered

| Requirement | Where |
|-------------|-------|
| FR-2 probe (HEAD, ranged-GET fallback) | `Core/Http/UrlProber.cs` |
| FR-4 single connection, streaming `.pgpart` writes | `Core/Engine/DownloadJob.cs`, `SegmentWorker.cs` |
| FR-5 pause/resume across restarts; checkpoint every 5 s or 10 MB | `DownloadJob.Checkpoint`, `IDownloadRepository.Checkpoint` (one transaction) |
| FR-7 redirects, Content-Disposition (`filename*` first), collision rename | `UrlProber`, `Files/FileNameResolver.cs`, `Files/UniquePath.cs` |
| FR-8 custom headers, cookies, referrer, user agent | `Http/DownloadHttpClient.cs` (credentials never persisted) |
| FR-11 MD5 / SHA-256 verification | `Integrity/Checksum.cs` |
| §11.1 filename sanitizer, path safety, URL validation, Mark-of-the-Web | `Files/FileNameSanitizer.cs`, `SafePath.cs`, `Http/UrlValidator.cs`, `Files/MarkOfTheWeb.cs` |
| §11.2 log redaction | `Logging/LogRedactor.cs` (engine logs use it; cookies are never logged) |
| §11.3 startup recovery, crash reconciliation | `MarkInterruptedAsPaused`, `DownloadJob.Reconcile` |
| Atomic rename `.pgpart` → final name | `DownloadJob.CompleteAsync` |
| CLI `ob2.exe get <url> [--sha256 <hex>] [--out <dir>]` | `App/Cli/CliCommand.cs`, `App/Program.cs` |
| Persistence | `Storage/Repositories/SqliteDownloadRepository.cs` |
| Test server (ranges, no-range, unknown length, redirects, throttle, drops, stalls, cookies) | `tests/OB2.Integration.Tests/Infrastructure/TestHttpServer.cs` |

The engine is already built around segments. In Phase 2 the planner is fixed to one connection (`DownloadJob.PlannedConnections`); Phase 3 turns segmentation on.

## Decisions and backlog

D-19 to D-26 were added to [decisions.md](decisions.md). New backlog items: DPAPI "remember auth", manual proxy (FR-10), expired signed URL refresh, `Last-Modified` timestamps (FR-12).
