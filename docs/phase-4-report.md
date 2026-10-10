# Phase 4 Report: IDM-Style Interface, Categories, Queues, Scheduler

**Date:** 2026-10-10
**Branch:** `phase-4` (from `phase-3-stable`)
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17, display at 100 % scaling

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| Flow B (overnight queue) end to end with a simulated clock | **Pass** | `QueueAndScheduleTests.AC10_and_flow_B_overnight_queue_starts_stops_and_resumes_next_day`: "Night" queue, 01:00–07:00 Mon–Fri. At 00:59 it waits; at 01:00 both items start; at 07:00 the queue stops and items return to `queued` with progress saved; Tuesday 01:00 they resume with non-zero Range starts and finish with matching SHA-256. |
| AC-10: scheduled queue starts and stops at configured times (clock abstraction) | **Pass** | The Flow B test above, plus `Saturday_is_skipped_by_a_weekday_schedule`, `Opening_the_app_inside_a_window_starts_the_queue`, and `ScheduleEvaluatorTests` (same-day, overnight, weekend, start-only, disabled). |
| Flow C (network drop and recovery) manually with a network toggle | **Pending manual** | Not toggled on this machine (it would cut the owner's connection). The automated equivalents pass: Phase 3 drop/retry/stall tests, `Exhausted_retries_fail_keep_progress_and_can_be_retried` (Failed + Retry), and the UI shows `retrying` as "Waiting for network" with a Retry button in the details panel. Checklist step below. |
| UI meets §9 layout and keyboard standards at 100 %, 125 %, 150 % DPI | **Partly verified** | Layout and shortcuts verified at 100 % (screenshots below; UI Automation drove row selection). The app manifest declares PerMonitorV2 and all sizes are in device-independent units; 125 % and 150 % need the owner to switch display scaling (checklist). |
| AC-12: idle memory under 80 MB with 10 active downloads, over 10 minutes | **Pass** | 10 downloads × 2 connections from the throttled test server (50 KB/s per connection), all 10 active for the whole run (log: `active downloads 10`; ~46 MB fetched per download), app tray-only, sampled every 15 s for 10 minutes by process id. **Single-file uncompressed exe** (the shipped form, decision D-56): private working set (Task Manager "Memory") avg 35.6 MB, max 37.4 MB; private bytes avg 62.2 MB, max 64.3 MB; 19–31 threads, ~710–745 handles, flat. Framework-dependent build in parallel: 34.4 / 38.8 MB working set, 60.1 / 65.3 MB private bytes. The app's own `Memory:` log line agreed (managed heap 7–12 MB, GC committed 17 MB). An earlier build with `EnableCompressionInSingleFile` measured ~98 MB working set, because compressed assemblies are unpacked into private memory, so compression is now off. A one-off 64 → 111 MB native step seen during an earlier diagnostic run did not recur in these two 10-minute runs; the 5-minute `Memory:` log line stays in place to catch it in the field. |
| Manual checklist from §14.1 (UI row) signed off in this report | **Pending owner sign-off** | Checklist below. Items I could check are ticked with how; the rest need the owner. |

## Test run

```
Passed!  - Failed: 0, Passed: 135, Skipped: 0, Total: 135 - OB2.Core.Tests.dll
Passed!  - Failed: 0, Passed:  36, Skipped: 0, Total:  36 - OB2.Storage.Tests.dll
Passed!  - Failed: 0, Passed:  55, Skipped: 2, Total:  57 - OB2.Integration.Tests.dll (33 s)
```

New in Phase 4: schedule/category unit tests, organization repository tests, queue and scheduler integration tests (concurrency limit, ordering, completion action, empty-queue safety, Flow B, AC-10, Stop/Resume All), and the instance-lock test.

## What was checked by running the real app

- **Main window**: launched with a seeded data folder. Two auto-resumed downloads showed live progress, speed, and time left. Two completed rows. Tree counts (All 4, Unfinished 2, Finished 2), queues and categories. Status bar: total speed, active count, queue state, tray state.
- **Details panel**: selecting a row (via UI Automation) showed address, destination, and one bar per connection (8 for the ISO).
- **Add dialog**: `ob2.exe <url>` opened it pre-filled, and **Download Now** added and started the download. The owner happened to click it during the check, so this was a real user action.
- **Download Complete dialog**: appeared when a download finished; "Open File" on an `.iso` exposed a crash path (Windows cancelled the shell launch), now fixed to fall back to Open Folder.
- **Bugs found and fixed during the check**: (1) a status could stay "Connecting…" when auto-resume started before the window subscribed; (2) segment bars of a just-finished download showed the last live numbers; (3) Open File threw when Windows had no handler or the user cancelled; (4) the 4 Hz refresh queried the database for queue names.

## §14.1 UI manual checklist

| # | Check | Status |
|---|-------|--------|
| 1 | Toolbar: Add URL, Resume, Stop, Stop All, Resume All, Delete, Start/Stop Queue, Scheduler, Options visible with icons and labels | ✔ seen in screenshot |
| 2 | Left tree: All / Unfinished / Finished / Queues / Categories with counts | ✔ seen in screenshot |
| 3 | List columns: File name, Size, Status, Progress, Speed, Time left, Date added, Save folder, Category, Queue; click to sort, drag to reorder | ✔ columns seen; sort/reorder are built-in DataGrid features (owner to try) |
| 4 | Lower panel: per-connection bars, address, destination, error + Retry | ✔ bars, address, destination seen; error/Retry owner to see on a failed item |
| 5 | Ctrl+N add, Del delete, Space pause/resume, Ctrl+Shift+P pause all, Ctrl+F search | Owner |
| 6 | Delete asks first; "Also delete the downloaded files" is off by default | Owner |
| 7 | Add dialog: probe shows size and resume support; category and folder follow the file type; Download Later / Now / Cancel | ✔ Download Now used; owner to try Later and the checksum field |
| 8 | Download Complete dialog: Open File (warns for .exe), Open Folder, Close, "Do not show again for this category" | ✔ appeared; owner to try the checkbox |
| 9 | Tray: left-click toggles window; menu Show / Pause All / Resume All / Options / Exit; tooltip shows active count and speed; closing the window hides to tray | Owner |
| 10 | Windows notification on completion and failure | Owner |
| 11 | Options: default folder, connections, retries, timeouts, user agent, speed limit, categories editor | Owner |
| 12 | Scheduler: create "Night" queue, 01:00–07:00 Mon–Fri, completion action with countdown, reorder items | Owner (logic covered by AC-10 tests) |
| 13 | 125 % and 150 % display scaling: no clipped text, crisp icons | Owner |
| 14 | Windows high-contrast theme: all text readable | Owner |
| 15 | Flow C: start a large download, turn Wi-Fi off for ~30 s, see "Waiting for network", turn it back on, download continues | Owner |

## Delivered

| Requirement | Where |
|-------------|-------|
| §9.1 main window | `MainWindow.xaml`, `MainViewModel`, `DownloadRowViewModel` |
| §9.2 Add Download dialog (probe, category/folder auto-fill, queue, connections, checksum, per-download speed limit) | `Views/AddDownloadWindow`, `AddDownloadViewModel` |
| §9.3 Download Complete dialog with per-category opt-out | `Views/CompleteWindow` |
| FR-18 / FR-29 categories with extension rules and folders | `Core/Organization/Category.cs`, migration `002`, Options → Categories |
| FR-13 / FR-14 / FR-15 queues, concurrency, start/stop, reordering | `Core/Organization/QueueManager.cs`, Scheduler window, context menu |
| FR-16 weekday schedules, FR-17 completion actions | `QueueScheduler`, `ScheduleEvaluator`, `CompletionActionService` |
| FR-19 / FR-25 history view with search and filters | Tree filters + search box over all downloads; per-download history events |
| §9.4 tray, FR-27 notifications | `Services/TrayService.cs` |
| §9.6 keyboard shortcuts, delete confirmation | `MainWindow.xaml` input bindings, `ConfirmWindow` |
| FR-30 settings screen | `Views/SettingsWindow`, `SettingsViewModel` |
| FR-1 drag-and-drop, clipboard pre-fill, `ob2.exe <url>` | `MainWindow.xaml.cs`, `StartupArguments` |
| App icon (original artwork) | `Resources/ob2.ico`, `tools/make-icon.ps1` |
| Dev test server for manual checks | `tools/OB2.TestServer` |
