# Backlog

Items noticed during a phase but outside its scope (PRD §0). Each one names the phase or requirement that should pick it up.

| Item | Raised in | Target |
|------|-----------|--------|
| **.NET 8 end of support is 2026-11-10.** Decide whether to move to .NET 10 LTS. Needs the .NET 10 SDK on the dev machine and CI. Owner decision, since §13 names .NET 8. | Phase 1 | Owner decision |
| Log retention by age (7 days) relies on Serilog's `retainedFileTimeLimit`; only rolling by size is covered by a test. | Phase 1 | Any |
| Single-instance guard: a second `ob2.exe <url>` should hand the URL to the running app instead of opening a second window (§8.5). | Phase 1 | Phase 6 (named pipe) |
| "Remember auth": persist cookies encrypted with DPAPI when the user opts in (§11.1). Today cookies are memory-only, so an authenticated download resumed after an app restart may get 403. | Phase 2 | v1.0 |
| Manual HTTP/SOCKS5 proxy (FR-10, P1). The engine uses the system proxy. No phase lists it in scope. | Phase 2 | v1.0 |
| Retry with the original URL when an expired final (signed) URL returns 403/410 mid-download (§16.1). | Phase 2 | v1.0 |
| Set the file timestamp from `Last-Modified` (FR-12, P2). The value is already stored. | Phase 2 | v1.1 |
| Dynamic segment splitting: when a connection finishes early, split the largest remaining segment (IDM-style). | Phase 3 | v1.1 |
| Detect HDD vs SSD and limit segments on rotating disks (§16.1). | Phase 3 | v1.1 |
| Start with Windows (FR-31, P1). The setting exists (`general.startWithWindows`, off) but nothing writes the HKCU Run key. Out of scope for Phase 4. | Phase 4 | v1.0 |
| Enforce `history.retentionDays` (§10.4). The setting exists; nothing prunes old rows yet. | Phase 4 | v1.0 |
| List orphaned `.pgpart` files under "Recovered orphan files" (§10.3). They are never deleted, but they are not shown either. | Phase 4 | v1.0 |
| FlaUI automation for the main flows (§14.1 UI row, P1). Phase 4 UI checks were manual plus UI Automation scripts. | Phase 4 | v1.0 |
| Remember column order/width and window size between runs. | Phase 4 | v1.1 |
| History CSV export (FR-26, P2) and clipboard monitor (FR-28, P2). | Phase 4 | v1.1 |
| A dark theme (open question 2). The UI uses system colours, so Windows high contrast works, but there is no dark palette. | Phase 4 | Owner decision |
