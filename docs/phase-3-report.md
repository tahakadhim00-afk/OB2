# Phase 3 Report: Multi-Connection Downloading and Retries

**Date:** 2026-10-10
**Branch:** `phase-3` (from `phase-2-stable`) · **Tag:** `phase-3-stable`
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17, local NVMe disk, Kestrel test server on loopback

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| AC-1: 1 GB file, 8 connections, checksum matches | **Pass** | `AC1_one_gigabyte_with_eight_connections_matches_checksum` (run with `OB2_RUN_LARGE=1`): 1 GiB, 8 segments, SHA-256 verified by the engine (`verified` history event). **5.3 s total, 194 MiB/s including the hash.** |
| AC-3: 5 forced drops in one segment retry and complete | **Pass** | `AC3_five_dropped_connections_in_one_segment_are_retried` (8 connections, drops only in the last segment, after 100 KB each) and `AC3_five_drops_without_progress_still_complete_on_the_sixth_attempt` (exactly 6 GETs). The counterpart: `Exhausted_retries_fail_keep_progress_and_can_be_retried` (6 drops → Failed with `NETWORK_ERROR`, `.pgpart` kept, Retry completes). |
| AC-4: no-range server completes with one connection | **Pass** | `AC4_server_without_ranges_uses_one_connection_even_when_eight_are_requested`: 1 segment, the server never saw 2 concurrent responses, SHA-256 matches. |
| AC-2 for the multi-connection case | **Pass** | `AC2_multi_connection_pause_reopen_resume_has_no_corrupted_bytes`: 12 MiB over 8 throttled connections, paused after ≥2 MiB, engine recreated, each partially done segment resumes at exactly `start + downloaded`, SHA-256 matches. The CLI crash test now also kills a 4-connection download. |
| Throughput benchmark recorded | **Pass** | Results below. |

## Throughput benchmark

`Benchmark_throughput`, local Kestrel server on loopback, default engine settings.

**Server that caps each connection at 4 MB/s** (typical CDN/mirror behaviour, 64 MiB file):

| Connections | Throughput |
|------------:|-----------:|
| 1 | 3.8 MiB/s |
| 4 | 15.2 MiB/s |
| 8 | 30.2 MiB/s |
| 16 | 59.6 MiB/s |

Throughput scales almost linearly with connections, which is the acceleration FR-3 / UC-1 asks for.

**Unthrottled loopback** (512 MiB; measures the engine's own CPU and disk path):

| Connections | Throughput |
|------------:|-----------:|
| 1 | 294 MiB/s |
| 8 | 432 MiB/s |

Both are far above the NFR's 100 Mbps (≈12 MiB/s) saturation target. A real 100 Mbps link test still needs an external server; that is part of the Phase 4/5 manual checks.

## Test run

```
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112 - OB2.Core.Tests.dll
Passed!  - Failed: 0, Passed:  32, Skipped: 0, Total:  32 - OB2.Storage.Tests.dll
Passed!  - Failed: 0, Passed:  45, Skipped: 2, Total:  47 - OB2.Integration.Tests.dll (32 s)
```

The 2 skipped tests are the large ones (AC-1 and the benchmark), which were run separately as above. The integration suite passed in 3 consecutive runs.

## Delivered

| Requirement | Where |
|-------------|-------|
| FR-3 segmentation (default 8, 1–32), parallel workers | `SegmentPlanner`, `DownloadJob.RunPoolAsync` / `WorkerLoopAsync` |
| Segment progress in the `segments` table | `IDownloadRepository.Checkpoint` (one transaction per checkpoint) |
| FR-6 exponential backoff, 5 retries per segment | `RetryPolicy`, `DownloadJob.RunSegmentWithRetriesAsync` |
| §11.3 timeouts: 30 s connect, 60 s idle read | `SocketsHttpHandler.ConnectTimeout`; per-read idle timer in `SegmentWorker` (test: stalled connection) |
| FR-4 / AC-4 single-connection fallback, including servers that lie about ranges | `SegmentPlanner`, `DownloadJob.FallBackToSingleConnection` |
| §16.1 per-host cap and adaptive back-off | `HostConnectionLimiter`, `DownloadJob.NoteThrottle` |
| FR-9 global token-bucket rate limiter | `TokenBucket`, `DownloadEngine.GlobalLimiter` (CLI applies `speedLimits.globalBytesPerSecond`) |
| §11.3 crash reconciliation against file size | `DownloadJob.Reconcile` (test: truncated `.pgpart`) |
| Sparse partial files | `PartFile.Create` / `TryClearSparse` |

## Decisions and backlog

D-27 to D-32 were added to [decisions.md](decisions.md). New backlog items: per-download speed limit (moved to Phase 4 with the UI field), `MaxConnectionsPerHost` setting, dynamic segment splitting, HDD detection.
