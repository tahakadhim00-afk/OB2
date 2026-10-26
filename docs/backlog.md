# Backlog

Items noticed during a phase but outside its scope (PRD §0). Each one names the phase or requirement that should pick it up.

| Item | Raised in | Target |
|------|-----------|--------|
| Log retention by age (7 days) relies on Serilog's `retainedFileTimeLimit`; only rolling by size is covered by a test. | Phase 1 | Any |
| Manual HTTP/SOCKS5 proxy (FR-10, P1). The engine uses the system proxy. No phase lists it in scope. | Phase 2 | v1.0 |
| Set the file timestamp from `Last-Modified` (FR-12, P2). The value is already stored. | Phase 2 | v1.2 |
| Dynamic segment splitting: when a connection finishes early, split the largest remaining segment so no connection sits idle. | Phase 3 | v1.2 |
| Detect HDD vs SSD and limit segments on rotating disks (§16.1). | Phase 3 | v1.2 |
| FlaUI automation for the main flows (§14.1 UI row, P1). Phase 4 UI checks were manual plus UI Automation scripts. | Phase 4 | v1.0 |
| Remember column order/width and window size between runs. | Phase 4 | v1.2 |
| History CSV export (FR-26, P2) and clipboard monitor (FR-28, P2). | Phase 4 | v1.2 |
| Firefox support (a different extension format and registry key). Edge is done (D-65). | Phase 6 (out of scope) | v1.2 |
| Shift-click bypass state lives in the extension's service worker. If Chrome stops the worker between the click and the download (rare, within 15 s), the download is captured anyway. | Phase 6 | v1.2 |
| When the user cancels a confirmation dialog for a captured download, the browser download is already gone (D-53). Offer "Let Chrome download it" in the dialog, which would need a reply channel to the extension. | Phase 6 | v1.2 |
| Start the pipe server before the window is created (with requests waiting for start-up to finish) to shave a few hundred ms off captures that start the app. | Phase 6 | v1.2 |
| Unit tests for `BrowserBridge` (credentials → confirmation, source mapping) without starting WPF. Today it is covered by the end-to-end test with confirmation off. | Phase 6 | v1.0 |
| Video pages that need a sign-in (age-restricted or members-only YouTube videos, private posts): pass the browser's cookies to yt-dlp without writing them to disk in the clear. Today such pages fail with yt-dlp's "sign in" message (D-71). | v1.0 | v1.2 |
| Playlists and channels: the Download Video window refuses them; offer "download all" with one quality choice for the whole list (D-71). | v1.0 | v1.2 |
| Options > Video sites: show the yt-dlp/Deno/ffmpeg versions, update yt-dlp, and remove the tools. Today "Update yt-dlp" lives in the Download Video window (D-71). | v1.0 | v1.2 |
| Download YouTube streams with OB2's own segmented engine (yt-dlp only for the addresses, ffmpeg for the merge), for more speed on lines that limit each connection (D-71). | v1.0 | v1.2 |
| Media button: videos inside closed shadow roots of custom players are not found, and only the frame that loaded a file offers it (a `blob:` player in the top page whose file was fetched by an iframe gets no button). | v1.0 | v1.2 |

## Done in v1.0 after the first release

| Item | Shipped as |
|------|-----------|
| `history.retentionDays` is applied on start; a paused or running download keeps its whole trail. | Enforce history retention |
| Orphaned `.pgpart` files are listed in their own window with Delete, Show in folder, and Download again. | Orphan files window |
| A daily bandwidth window with its own speed limit, sharing the queue scheduler's time rules. | Bandwidth window |
| A failed download is started again after a growing delay; errors another attempt cannot fix are never retried. | Auto-retry |
| A resume against a server with no ETag or Last-Modified compares the first 64 KB before continuing. | Resume fingerprint |

## Done in v1.1

| Item | Shipped as |
|------|-----------|
| A dark theme (open question 2): Light or Dark in Options > General, every colour from one palette, high contrast still the system's. | Dark theme (D-77) |
