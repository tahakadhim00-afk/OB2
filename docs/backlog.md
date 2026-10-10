# Backlog

Items noticed during a phase but outside its scope (PRD §0). Each one names the phase or requirement that should pick it up.

| Item | Raised in | Target |
|------|-----------|--------|
| Log retention by age (7 days) relies on Serilog's `retainedFileTimeLimit`; only rolling by size is covered by a test. | Phase 1 | Any |
| "Remember auth": persist cookies encrypted with DPAPI when the user opts in (§11.1). Today cookies are memory-only, so an authenticated download resumed after an app restart may get 403. | Phase 2 | v1.0 |
| Manual HTTP/SOCKS5 proxy (FR-10, P1). The engine uses the system proxy. No phase lists it in scope. | Phase 2 | v1.0 |
| Retry with the original URL when an expired final (signed) URL returns 403/410 mid-download (§16.1). | Phase 2 | v1.0 |
| Set the file timestamp from `Last-Modified` (FR-12, P2). The value is already stored. | Phase 2 | v1.1 |
| Dynamic segment splitting: when a connection finishes early, split the largest remaining segment (IDM-style). | Phase 3 | v1.1 |
| Detect HDD vs SSD and limit segments on rotating disks (§16.1). | Phase 3 | v1.1 |
| Enforce `history.retentionDays` (§10.4). The setting exists; nothing prunes old rows yet. | Phase 4 | v1.0 |
| List orphaned `.pgpart` files under "Recovered orphan files" (§10.3). They are never deleted, but they are not shown either. | Phase 4 | v1.0 |
| FlaUI automation for the main flows (§14.1 UI row, P1). Phase 4 UI checks were manual plus UI Automation scripts. | Phase 4 | v1.0 |
| Remember column order/width and window size between runs. | Phase 4 | v1.1 |
| History CSV export (FR-26, P2) and clipboard monitor (FR-28, P2). | Phase 4 | v1.1 |
| A dark theme (open question 2). The UI uses system colours, so Windows high contrast works, but there is no dark palette. | Phase 4 | Owner decision |
| Firefox support (a different extension format and registry key). Edge is done (D-65). | Phase 6 (out of scope) | v1.1 |
| host.exe is 64.5 MB because it is self-contained. Trimming (with System.Text.Json source generation for the protocol types) should shrink it a lot and may start it faster; not measured yet. | Phase 6 | v1.0 |
| Shift-click bypass state lives in the extension's service worker. If Chrome stops the worker between the click and the download (rare, within 15 s), the download is captured anyway. | Phase 6 | v1.1 |
| When the user cancels a confirmation dialog for a captured download, the browser download is already gone (D-53). Offer "Let Chrome download it" in the dialog, which would need a reply channel to the extension. | Phase 6 | v1.1 |
| Start the pipe server before the window is created (with requests waiting for start-up to finish) to shave a few hundred ms off captures that start the app. | Phase 6 | v1.1 |
| Unit tests for `BrowserBridge` (credentials → confirmation, source mapping) without starting WPF. Today it is covered by the end-to-end test with confirmation off. | Phase 6 | v1.0 |
