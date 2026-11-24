# Backlog

Items noticed during a phase but outside its scope (PRD §0). Each one names the phase or requirement that should pick it up.

| Item | Raised in | Target |
|------|-----------|--------|
| Log retention by age (7 days) relies on Serilog's `retainedFileTimeLimit`; only rolling by size is covered by a test. | Phase 1 | Any |
| Manual HTTP/SOCKS5 proxy (FR-10, P1). The engine uses the system proxy. No phase lists it in scope. | Phase 2 | v1.0 |
| Set the file timestamp from `Last-Modified` (FR-12, P2). The value is already stored. | Phase 2 | v1.3 |
| Detect HDD vs SSD and limit segments on rotating disks (§16.1). | Phase 3 | v1.3 |
| FlaUI automation for the main flows (§14.1 UI row, P1). Phase 4 UI checks were manual plus UI Automation scripts. | Phase 4 | v1.0 |
| Remember column order/width and window size between runs. | Phase 4 | v1.3 |
| History CSV export (FR-26, P2) and clipboard monitor (FR-28, P2). | Phase 4 | v1.3 |
| Firefox support (a different extension format and registry key). Edge is done (D-65). | Phase 6 (out of scope) | v1.3 |
| Shift-click bypass state lives in the extension's service worker. If Chrome stops the worker between the click and the download (rare, within 15 s), the download is captured anyway. | Phase 6 | v1.3 |
| When the user cancels a confirmation dialog for a captured download, the browser download is already gone (D-53). Offer "Let Chrome download it" in the dialog, which would need a reply channel to the extension. | Phase 6 | v1.3 |
| Start the pipe server before the window is created (with requests waiting for start-up to finish) to shave a few hundred ms off captures that start the app. | Phase 6 | v1.3 |
| Unit tests for `BrowserBridge` (credentials → confirmation, source mapping) without starting WPF. Today it is covered by the end-to-end test with confirmation off. | Phase 6 | v1.0 |
| Video pages that need a sign-in (age-restricted or members-only YouTube videos, private posts): pass the browser's cookies to yt-dlp without writing them to disk in the clear. Today such pages fail with yt-dlp's "sign in" message (D-71). | v1.0 | v1.3 |
| Playlists and channels: the Download Video window refuses them; offer "download all" with one quality choice for the whole list (D-71). | v1.0 | v1.3 |
| Options > Video sites: show the yt-dlp/Deno/ffmpeg versions, update yt-dlp, and remove the tools. Today "Update yt-dlp" lives in the Download Video window (D-71). | v1.0 | v1.3 |
| Download YouTube streams with OB2's own segmented engine (yt-dlp only for the addresses, ffmpeg for the merge), for more speed on lines that limit each connection (D-71). | v1.0 | v1.3 |
| Media button: videos inside closed shadow roots of custom players are not found, and only the frame that loaded a file offers it (a `blob:` player in the top page whose file was fetched by an iframe gets no button). | v1.0 | v1.3 |

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

## Done in v1.2

| Item | Shipped as |
|------|-----------|
| Dynamic segment splitting: when a connection finishes early, split the largest remaining segment so no connection sits idle (Phase 3). | Every connection busy to the end, and connections added while they help (D-90) |

## Raised in v1.2 (speed, D-90)

| Item | Raised in | Target |
|------|-----------|--------|
| Servers that limit how often one visitor may ask (proof.ovh.net answered 429 with `Retry-After: 10` to a second request 2 s after the first): OB2 waits as asked and cuts to one connection, but fails the download once one part has been refused 5 times in a row, after about 2 minutes. When down to one connection, ask for all the remaining parts in one request, and let a `Retry-After` wait not use up a retry. | D-90 | v1.3 |
| Hetzner's speed-test servers (fsn1/nbg1-speed.hetzner.com) close the connection on any .NET `HttpClient` request, also over plain HTTP and with a browser's user agent, while curl downloads the same file; GitHub and the others tried work. Find what they reject (header order, HTTP/1.1 details) in case other servers do the same. | D-90 | v1.3 |

## Raised in v1.2 (updates)

| Item | Raised in | Target |
|------|-----------|--------|
| Authenticode-sign OB2.exe and host.exe, so SmartScreen and antivirus stop warning on a fresh download. The release signature (D-84) covers only what the in-app updater installs. | D-84 | Any |
| Publish the extension to the Chrome Web Store (and Edge Add-ons), so it updates itself instead of asking for a reload in chrome://extensions after an update (D-85). | D-85 | v1.3 |
| Going back after a failed start (D-85) cannot help once the new version has migrated the database: the previous version refuses a schema it does not know (D-8). Keep a copy of `ob2.db` before migrating, and restore it when going back. | D-85 | v1.3 |
| Grab links from more places: text or links dropped on the main window (today only the first link is kept), "Send all to OB2" in the popup's Find downloads list and a "Grab links with OB2" page menu in Chrome (with the browser's cookies), and watching the clipboard (FR-28, off by default). | D-87 | v1.3 |
| Grab links rules, like JDownloader's Packagizer: by type, site or name, set the folder, the queue, or leave a link out. | D-87 | v1.3 |
| Grab links: a subfolder per pasted page (named after its title), and archive parts (`.part1.rar`…) grouped as one package. | D-87 | v1.3 |
| Grab links reads a page's HTML as the server sends it: links a page's scripts add after loading are not found (the browser's button finds those). Pages behind a sign-in are read without cookies. | D-87 | v1.3 |
