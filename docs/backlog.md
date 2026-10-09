# Backlog

Items noticed during a phase but outside its scope (PRD §0). Each one names the phase or requirement that should pick it up.

| Item | Raised in | Target |
|------|-----------|--------|
| **.NET 8 end of support is 2026-11-10.** Decide whether to move to .NET 10 LTS before the Phase 5 portable release. Needs the .NET 10 SDK on the dev machine and CI. Owner decision, since §13 names .NET 8. | Phase 1 | Before Phase 5 |
| Seed default categories (Documents, Music, Video, Programs, Compressed) and a default queue through migration `002_*.sql`. | Phase 1 | Phase 4 (FR-13, FR-18) |
| Fall back to `%APPDATA%\OB2` when `portable.flag` exists but the exe folder is read-only (§12.3). | Phase 1 | Phase 5 |
| Log retention by age (7 days) relies on Serilog's `retainedFileTimeLimit`; only rolling by size is covered by a test. | Phase 1 | Any |
| Single-instance guard (a second `ob2.exe` launch forwards its URL to the running app). Needed for `ob2.exe "https://..."` (§8.5) and `--from-host` (§8.3). | Phase 1 | Phase 6 |
| The empty shell window's working set is ~110 MB. AC-12 targets < 80 MB with 10 active downloads; measure private bytes too. | Phase 1 | Phase 4 |
| "Remember auth": persist cookies encrypted with DPAPI when the user opts in (§11.1). Today cookies are memory-only, so an authenticated download resumed after an app restart may get 403. | Phase 2 | v1.0 |
| Manual HTTP/SOCKS5 proxy (FR-10, P1). The engine uses the system proxy. No phase lists it in scope. | Phase 2 | v1.0 |
| Retry with the original URL when an expired final (signed) URL returns 403/410 mid-download (§16.1). | Phase 2 | v1.0 |
| Set the file timestamp from `Last-Modified` (FR-12, P2). The value is already stored. | Phase 2 | v1.1 |
