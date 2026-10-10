# Phase 6 Report: Chrome Extension Integration

**Date:** 2026-10-10
**Branch:** `phase-6` (from `phase-5-rc`) · **Tag:** `phase-6-rc`
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17, Node.js 22.14.0. No Chrome session was driven: the owner uses this PC, and loading an extension into their browser is theirs to do.

**Precondition note:** §15 wants Phases 1–5 tagged stable first. Phases 4 and 5 are `-rc`, because their remaining criteria need the owner (UI checklist, DPI, a clean VM, VirusTotal). Phase 6 was built on top at the owner's request to finish all phases; nothing in it changes the standalone app beyond the IPC server, capture settings, and the Browser tab.

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| AC-5: a captured link appears in the app within 2 s, cookies passed (authenticated test server) | **Pass (host + app); Chrome click pending manual** | `BrowserBridgeEndToEndTests.Captured_link_starts_the_app_downloads_with_cookies_and_never_logs_them` drives the real `host.exe` exactly as Chrome does: framed `add_download` on stdin, reply on stdout. The test server serves the file only when the request carries the browser's cookie; the file arrives with a matching SHA-256 and the right `Referer`. The second capture, with the app running, is acked in < 2 s (asserted). Published single-file exes: **170–250 ms** per round trip. The extension side (pause → send → cancel after ack, cookie collection for the final URL) is covered by Jest with mocked `chrome.*`. |
| AC-6: app closed, a captured link starts the app and queues the item within 5 s | **Pass (host + app); Chrome click pending manual** | Same test, first step: no OB2 running; `host.exe` starts `OB2.exe --from-host` and the ack (with the new download id) arrives in < 5 s (asserted). Published exes: **1.8–2.0 s**, and **3.9 s** on the very first run of a freshly unpacked copy (Windows scans the new 158 MB exe). An earlier host version took 4.6 s; see D-60 for the fix. |
| AC-7: with the extension disabled, manual add still works | **Pass** | Nothing in the add path depends on the extension, the host, or the registration: the Add dialog, drag-and-drop, and `OB2.exe <url>` are unchanged from Phase 4. All engine and CLI tests (`CliTests`) download pasted URLs with no extension or host present. |
| AC-9: HKCU key written and removed, no admin prompt | **Pass** | `NativeHostRegistrationTests` run the real `NativeHostRegistration` against a scratch HKCU key (`HKCU\Software\OB2.Tests\<guid>`, removed afterwards): Register writes the default value and a manifest with `name`, `path`, `type: stdio`, and `allowed_origins` for the pinned ID; Unregister removes both; a moved folder is detected and repaired; another working copy keeps its registration. The test process is not elevated and HKCU needs no elevation; the app manifest is `asInvoker`. The real Chrome key was not written on this PC. |
| AC-13: no cookie values in any log file during a captured download | **Pass** | The end-to-end test runs the app at **Debug** log level, captures two downloads carrying `ob2session=TOPSECRET-c00k1e-value`, then scans every log file: no `TOPSECRET`, no `c00k1e`. The capture is logged as `(cookies: true)`, and URLs appear with their query replaced by `?…`. host.exe logs nothing. |
| Protocol tests for malformed frames, oversize messages, and app-not-running | **Pass** | `MessageFramingTests` (round trip, wire format, PRD example, truncated prefix and body, oversize rejected before the body is read, 4 GB length prefix, writes above 1 MB refused, 5 malformed JSON shapes, version mismatch, schemes, URL length). `PipeTests` (round trip, 20 concurrent clients, invalid URL answered without reaching the handler, malformed and oversize frames → `INVALID_MESSAGE`, failing handler → `INTERNAL_ERROR` without details and the server keeps serving, client gone mid-frame, app not running → timeout in < 2 s, slow handler times out, disposed server stops). End-to-end: the host answers a malformed frame and exits 1, refuses other extension IDs (exit 2), exits 0 when Chrome closes the port, answers `ping`/`get_status` without starting the app, and rejects `file:` URLs without starting it. |

Chrome itself was not driven, so the build is tagged `phase-6-rc`.

## Test run

```
.NET
Passed!  - Failed: 0, Passed: 135, Skipped: 0, Total: 135 - OB2.Core.Tests.dll
Passed!  - Failed: 0, Passed:  38, Skipped: 0, Total:  38 - OB2.Storage.Tests.dll
Passed!  - Failed: 0, Passed: 114, Skipped: 2, Total: 116 - OB2.Integration.Tests.dll (53 s)

Extension (npm test)
Test Suites: 4 passed, 4 total
Tests:       68 passed, 68 total
```

New in Phase 6: 59 .NET tests (framing, pipe, registration, host lookup, host + app end to end) and 68 Jest tests:

- `rules.test.ts`: every skip reason, excluded-domain matching, file type from name, URL, or MIME, stored settings.
- `capture.test.ts`: the ack path and its order; 6 fail-open paths plus the timeout; pause failure; Shift-click; background rule refresh; context menu and its fallback; status and rule caching.
- `dom.test.ts`: content-script Shift-click detection; popup rendering, including that file names are never parsed as HTML.
- `manifest.test.ts`: the key yields the pinned ID, permissions match §8.1 exactly, MV3 CSP, no remote URLs.

The two skipped tests are the opt-in 1 GiB AC-1 and benchmark runs (D-32).

## Host round-trip timing

Published single-file `host.exe` (64.5 MB) and `OB2.exe` (158.3 MB) from `build/publish.ps1 -IncludeHost`, one host process per message as Chrome does it:

| Request | App state | Time |
|---------|-----------|-----:|
| `ping` | closed | 1384 ms (first start of a freshly copied host.exe, plus the 300 ms connect timeout) |
| `get_status` | closed | 669 ms → `APP_NOT_RUNNING`, app not started |
| `add_download` | closed (first run of the copy) | 3859 ms → `ack`, id 1 |
| `add_download` | closed (later runs) | 2014 ms, 1841 ms |
| `ping` | running | 168–252 ms |
| `add_download` | running | 167–222 ms |
| `get_status` | running | 168 ms |

The app reached tray-ready 1.0–1.2 s after the host started it (its own `Startup:` log line).

## Delivered

| Requirement | Where |
|-------------|-------|
| Native Messaging host and named-pipe bridge (§7.2, §8.3) | `src/OB2.Host/Program.cs`, `OB2.Shared/Ipc/PipeServer.cs` (`PipeServer`, `PipeClient`), `OB2.Shared/Protocol/*` |
| Host registration and unregistration under HKCU (§8.2, FR-20 setup) | `OB2.Shared/Ipc/NativeHostRegistration.cs`, Options → Browser tab |
| Protocol v1: `add_download`, `ack`, `ping`/`pong`, `get_status`, `error`, plus `pause_all`/`resume_all` | [docs/protocol.md](protocol.md) |
| MV3 extension: `chrome.downloads` capture, cookie and referrer handoff, fail-open, context menu (FR-21) | `extension/src/capture.ts`, `background.ts`, `content.ts`, `manifest.json` |
| Capture confirmation dialog (FR-22) and capture rules (§8.4) | `BrowserBridge` (confirmation, credentials rule D-59); rules in Options → Browser, enforced by the extension (`rules.ts`) |
| Extension popup with status and Pause all / Resume all | `extension/src/popup/*` |
| Re-registration on folder move (§12.6) | `App.StartBrowserBridge` → `NeedsRepair` / `Register` (D-55) |
| Single instance: a second `OB2.exe` hands its URL to the running one (§8.5) | `App.ForwardToRunningInstanceAsync` (D-54) |
| Packaging | `build/publish.ps1 -IncludeHost` builds the extension and adds `host.exe` and `chrome-extension/` to the zip; the icons come from `tools/make-icon.ps1` |

## Found and fixed during this phase

1. **Two copies kept taking the Chrome registration from each other.** The repair-on-start check treated "registered for a different folder" as broken, so any second copy (or a test's temporary copy) re-pointed Chrome at itself on start. It now repairs only when the registered manifest or host.exe is gone (D-55).
2. **AC-6 was within 0.4 s of failing.** The host waited 700 ms before deciding OB2 wasn't running, then polled for the pipe. It now waits 300 ms and connects the instant the pipe exists (D-60): 4.6 s became 1.8–2.0 s.
3. **§11.1 credentials rule was missing**: `https://user:pass@host/…` from the browser is now always confirmed by the user (D-59).

## Manual checklist for the owner (Chrome)

| # | Step | Expected |
|---|------|----------|
| 1 | `powershell -ExecutionPolicy Bypass -File build/publish.ps1 -IncludeHost`, unzip `OB2-portable.zip` somewhere writable, run `OB2.exe` | First-run wizard, then the main window |
| 2 | Options → Browser → Register | "Registered for this copy of OB2 (extension mgmmgplfngnhcencbhpfagnjcnenflpm)" |
| 3 | `chrome://extensions` → Developer mode → Load unpacked → the `chrome-extension` folder | ID shown is `mgmmgplfngnhcencbhpfagnjcnenflpm`; the OB2 popup says "Connected" |
| 4 | Click a download link for a file over 1 MB (e.g. a `.zip`) | Chrome's download vanishes; OB2's Add window appears within 2 s (**AC-5**) |
| 5 | Same on a site that needs a login (or a test server with `?cookie=`) | The download succeeds in OB2 (cookies passed) |
| 6 | Exit OB2 from the tray, click another link | OB2 starts in the tray and the Add window appears within 5 s (**AC-6**) |
| 7 | Shift-click a download link | Chrome downloads it; OB2 does nothing |
| 8 | Right-click a link → "Download with OB2" | OB2's Add window, pre-filled |
| 9 | Click a link to a file under 1 MB, or a `.html` page | Chrome keeps it |
| 10 | Popup: Pause all, Resume all; turn "Capture downloads" off and click a link | Downloads pause and resume; with capture off, Chrome keeps the download |
| 11 | Disable the extension; paste a URL into OB2 (Ctrl+N) | Downloads normally (**AC-7**) |
| 12 | Options → Browser → Unregister; click a link in Chrome | The popup says "Not set up"; Chrome downloads normally (fail-open) |
| 13 | Search `data\logs\*.log` for a cookie value from step 5 | Not found (**AC-13**) |
