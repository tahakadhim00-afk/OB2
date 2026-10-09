# Product Requirements Document (PRD): Personal Download Manager ("OB2")

**Version:** 1.0 (implementation draft)
**Date:** 2026-10-09
**Owner:** Taha
**Target platform:** Windows 10 (1809+) / Windows 11, x64

---

## 0. How to Use This Document (for the AI coding agent)

- Build in the six phases defined in §15, one phase at a time. Never ask the agent to build the whole project at once. Start a phase only after the previous phase's exit criteria pass and the build is tagged stable.
- Each phase has an explicit "Out of scope" list. If work touches an out-of-scope item, stop and record it in `docs/backlog.md` instead of building it.
- Requirement IDs (FR-x, NFR, AC-x) are stable. Reference them in commits, tests, and TODOs.
- Where this PRD specifies a value (e.g., 8 default connections, 5 retries), use it. Where it says *(decide)*, pick a sensible default, record the decision in `docs/decisions.md`, and continue.
- The protocol in §8.3 is a contract. Do not change message names or fields without bumping `v`.
- Keep the Core Engine free of UI references. Tests for §14 must run without launching the WPF app.

---

## Source Confidence Note

This PRD was written from general knowledge of IDM and public Chrome/.NET documentation. It has **not** been re-verified against live sources in this revision. Treat IDM statements as reference points, and check the items marked *(verify)* before finalizing the feature matrix.

- **Known at a general level:** IDM is a Windows-only commercial download manager from Tonec Inc. It accelerates downloads with multiple connections per file (up to 32), supports resume, scheduling, queues, categories, speed limits, site/media grabbing, and browser integration through extensions for Chromium browsers and Firefox.
- **Not publicly confirmed:** IDM's internal implementation language, its exact browser-interception protocol, its database format, and its UI framework. This PRD does not assume any of them. The recommended stack is chosen on its own merits.
- **Reference only:** IDM is a UX benchmark. No IDM code, icons, sounds, or assets may be reused.

---

## 1. Product Overview and Goals

### 1.1 Overview
A personal Windows download manager that accelerates and organizes file downloads. It runs as a standalone desktop app and optionally integrates with Chrome through an extension. The app ships as a portable package that runs from a folder or a single executable without an installer.

### 1.2 Goals
1. Download files faster than the browser's built-in downloader using segmented, multi-connection transfers.
2. Give the user full control: pause, resume, queue, schedule, limit speed, and recover from failures.
3. Capture downloads from Chrome automatically, with cookies and referrer preserved so authenticated links work.
4. Run fully without the browser extension.
5. Be copyable to another Windows PC and run with minimal setup.
6. Be simple to maintain and extend by a single developer working with AI coding agents.

### 1.3 Non-Goals (v1)
- Cross-platform support (macOS, Linux, Android).
- Cloud sync, accounts, or telemetry.
- BitTorrent or peer-to-peer transfers.
- DRM-protected media capture.
- Commercial distribution or auto-updates from a public server.

---

## 2. Target Users and Use Cases

### 2.1 Primary User
A technically capable individual who runs Windows, downloads large files regularly (software, datasets, media, documents), and wants a familiar IDM-style workflow without bloated software. The user may move between several PCs.

### 2.2 Use Cases
| ID | Use case | Expected outcome |
|----|----------|------------------|
| UC-1 | Download a large file from a fast server | Transfer uses 4–16 connections and saturates available bandwidth |
| UC-2 | Click a download link in Chrome | Download is captured, an "Add download" dialog appears, and the file downloads through the app |
| UC-3 | Download a login-protected file | Cookies and referrer from Chrome are sent, so the download succeeds |
| UC-4 | Pause all downloads before a video call | All active transfers pause; they resume on demand |
| UC-5 | Schedule large downloads overnight | Queue starts at 01:00 and stops at 07:00 |
| UC-6 | Recover after a network drop | Partial downloads resume automatically with retry backoff |
| UC-7 | Copy the app to a new PC and run it | App runs from a folder; Chrome registration is one button press |
| UC-8 | Find a file downloaded last month | Searchable history shows source URL, date, size, and location |

---

## 3. Functional Requirements

Priority: **P0** = MVP, **P1** = v1.0, **P2** = later.

### 3.1 Download Engine
| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1 | Add a download by URL (paste, drag text, or dialog) | P0 |
| FR-2 | Probe the server (HEAD, or ranged GET if HEAD is refused) to read size, filename, and `Accept-Ranges` | P0 |
| FR-3 | Split files into N segments (default 8, configurable 1–32) and download in parallel when the server supports range requests | P0 |
| FR-4 | Fall back to a single connection when ranges are unsupported or size is unknown | P0 |
| FR-5 | Pause and resume a download; persist segment progress so resume survives app restart | P0 |
| FR-6 | Retry failed segments with exponential backoff (default 5 attempts) | P0 |
| FR-7 | Handle HTTP redirects, `Content-Disposition` filenames, and auto-rename on collision | P0 |
| FR-8 | Support HTTP and HTTPS; pass custom headers, cookies, referrer, and user agent | P0 |
| FR-9 | Global and per-download speed limits (token bucket) | P1 |
| FR-10 | Proxy support: system proxy, manual HTTP/SOCKS5 | P1 |
| FR-11 | Verify integrity when a checksum (MD5/SHA-256) is supplied | P1 |
| FR-12 | Set file timestamp from `Last-Modified` | P2 |

### 3.2 Queues, Scheduling, and Management
| ID | Requirement | Priority |
|----|-------------|----------|
| FR-13 | Multiple named queues, each with a concurrency limit (default 3) | P0 |
| FR-14 | Start, stop, pause, resume, and delete downloads individually or in bulk | P0 |
| FR-15 | Reorder items within a queue | P1 |
| FR-16 | Schedule a queue to start and stop at set times, by weekday | P1 |
| FR-17 | Actions after completion: none, close app, sleep, shutdown, or run a command | P1 |
| FR-18 | Categories (Documents, Music, Video, Programs, Compressed, Custom) with auto-assignment by file extension and per-category save folder | P0 |
| FR-19 | Filters and search across active, completed, and failed downloads | P1 |

### 3.3 Browser Capture and Media
| ID | Requirement | Priority |
|----|-------------|----------|
| FR-20 | Chrome extension captures browser downloads that match rules (extension list, min size) | P1 |
| FR-21 | Context-menu item "Download with [App]" on any link | P1 |
| FR-22 | Confirmation dialog in the desktop app before a captured download starts (with option to skip) | P1 |
| FR-23 | Site grabber: crawl a page and list links matching a pattern (depth 1–3, same domain by default) | P2 |
| FR-24 | Media detection: list video/audio resources loaded by a page (HLS/DASH manifests, direct media files) | P2 |

### 3.4 History and Notifications
| ID | Requirement | Priority |
|----|-------------|----------|
| FR-25 | Download history with source URL, final path, size, start/end time, and status | P0 |
| FR-26 | Export history to CSV | P2 |
| FR-27 | Windows toast notification on completion or failure | P1 |
| FR-28 | Clipboard monitor: offer to download URLs copied to clipboard (off by default) | P2 |

### 3.5 Settings
| ID | Requirement | Priority |
|----|-------------|----------|
| FR-29 | Default save folder and per-category folders | P0 |
| FR-30 | Default connection count, retry count, timeout, user agent | P0 |
| FR-31 | Start with Windows (optional; off by default for portable use) | P1 |
| FR-32 | Import and export settings as a JSON file | P2 |

---

## 4. Non-Functional Requirements

| Category | Requirement |
|----------|-------------|
| Performance | Idle memory under 80 MB with 10 active downloads. Progress UI updates at 4 Hz. Segment writes use buffers of at least 64 KB. |
| Throughput | Saturates a 100 Mbps link on a server that permits multiple connections (benchmark on a known test server). |
| Reliability | Downloads survive app crash and power loss; progress is checkpointed at least every 5 seconds or 10 MB. A corrupted state record falls back to restarting that download, not the whole queue. |
| Storage | Metadata in SQLite; file data written directly to the destination with pre-allocation. |
| Usability | First download possible within 30 seconds of first launch. Keyboard shortcuts for add, pause, and delete. |
| Accessibility | Keyboard navigable; respects Windows high contrast and 125%/150% DPI scaling. |
| Maintainability | Core engine has no UI dependency and is covered by unit tests. |
| Localization | English in v1. Strings externalized for later translation. Right-to-left layout is P2. |
| Logging | Rolling local log files (7 days, 10 MB max each). No network logging of user data. |

---

## 5. IDM Feature Comparison Matrix

| Feature | IDM (general knowledge; *verify*) | This app v1.0 | Improvement |
|---------|-----------------------------------|---------------|-------------|
| Multi-connection segmented download | Yes (up to 32) | Yes (1–32) | Same |
| Pause / resume | Yes | Yes, survives restart | Persistent state in SQLite |
| Queues and scheduling | Yes | Yes, with weekday schedules | Clearer schedule UI |
| Categories | Yes | Yes | Per-category folders and rules |
| Speed limiter | Yes | Yes, global and per-download | Per-download limits |
| Browser capture (Chromium) | Yes (extension) | Yes (MV3 extension via Native Messaging) | Explicit cookie and referrer handoff |
| Site grabber | Yes | P2 | Deferred |
| Media / stream detection | Yes | P2 | Deferred; HLS/DASH scope limited |
| Integrity checksum | Partial *(verify)* | Yes (MD5, SHA-256) | Built in |
| Portable run without installer | No (installer-based) *(verify)* | Yes | Main differentiator |
| Open source | No | Personal use; source owned by user | Full control |
| Antivirus post-scan | Yes *(verify)* | Not in v1 | Consider later via Windows Defender CLI |

---

## 6. Recommended Technology Stack

### 6.1 Options Considered
| Option | Pros | Cons | Verdict |
|--------|------|------|---------|
| **C# / .NET 8 + WPF** | Mature HTTP and file I/O; native Windows look; single-file self-contained publish; Native Messaging well documented; good async model for segmented downloads | XAML-heavy; WPF looks dated unless styled | **Recommended** |
| Flutter (Windows desktop) | Single codebase skill set if already used for mobile; good UI tooling | Tray, Native Messaging host, and registry need custom platform code; larger binary; less mature for system-level apps | Viable alternative |
| Tauri (Rust core + web UI) | Small binary; modern UI with web tech | Rust learning curve for the segment engine; Native Messaging host adds a second binary | Viable if Rust is preferred |
| Electron | Fast UI development | Large memory footprint; contrary to portable/lightweight goal | Rejected |
| Python + PyQt | Fast prototyping | Clean single-exe packaging is awkward; antivirus false positives are common | Rejected |

### 6.2 Recommended Stack
- **Language/runtime:** C# 12, .NET 8 LTS
- **UI:** WPF with MVVM (CommunityToolkit.Mvvm). Custom styles that emulate the classic IDM layout (toolbar, left category tree, list view, status bar) without copying assets.
- **HTTP:** `HttpClient` with `SocketsHttpHandler` (connection pooling, HTTP/2 where available), ranged `GET` requests.
- **Storage:** SQLite via Microsoft.Data.Sqlite (metadata, segments, history). Downloaded bytes are written directly to the destination file with `FileStream` and pre-allocation.
- **Logging:** Serilog with rolling file sink.
- **Tray and notifications:** H.NotifyIcon (or WinForms `NotifyIcon` interop); Windows toast via `Microsoft.Toolkit.Uwp.Notifications`.
- **Browser bridge:** Chrome Native Messaging host (a second small .NET console executable, `host.exe`) that talks to the desktop app over a named pipe.
- **Extension:** Chrome Manifest V3, plain TypeScript compiled with esbuild. No frameworks.
- **Testing:** xUnit, FluentAssertions, a local test HTTP server (Kestrel test host) that simulates ranges, redirects, and failures.
- **Build/publish:** `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

### 6.3 Justification
C# and .NET give the best balance of HTTP performance, file I/O control, and Windows integration with the least custom platform code. Single-file self-contained publishing satisfies the portability requirement without an installer. The Native Messaging host is a documented Chrome mechanism that avoids opening a localhost port to the web.

---

## 7. System Architecture

### 7.1 Components
```
+-------------------+        Native Messaging        +------------------+
|  Chrome Extension | <-----------(stdio)----------> |  Native Host     |
|  (MV3 service     |   JSON messages, 1 MB limit    |  host.exe        |
|   worker + popup) |                                |                  |
+-------------------+                                +--------+---------+
                                                              | Named pipe
                                                              | (current user only)
                                                     +--------v---------+
                                                     |  Desktop App     |
                                                     |  (WPF, tray)     |
                                                     |                  |
                                                     |  UI (ViewModels) |
                                                     |  ----------------|
                                                     |  Core Engine     |
                                                     |  - Scheduler     |
                                                     |  - Queue manager |
                                                     |  - Segment engine|
                                                     |  - Rate limiter  |
                                                     |  - Retry policy  |
                                                     |  - Writer        |
                                                     |  ----------------|
                                                     |  Storage (SQLite)|
                                                     |  Settings (JSON) |
                                                     +------------------+
```

### 7.2 Component Responsibilities
| Component | Responsibilities | Does not do |
|-----------|------------------|-------------|
| **Desktop App (UI)** | Renders lists, dialogs, settings; binds to ViewModels; shows tray icon | Network I/O, file writes |
| **Core Engine** | Owns download lifecycle, segmentation, retries, rate limiting, file writes | Any UI reference |
| **Scheduler** | Starts and stops queues on time rules; triggers completion actions | Downloading |
| **Queue Manager** | Holds ordered items per queue; enforces concurrency | Segment logic |
| **Segment Engine** | Splits ranges, runs parallel workers, records per-segment progress | Persistence format |
| **Writer** | Writes bytes at offsets; flushes and checkpoints | Scheduling |
| **Rate Limiter** | Token bucket per download and global | Retry decisions |
| **Storage** | SQLite repositories; settings file; atomic state writes | Business rules |
| **Native Host (host.exe)** | Reads/writes Chrome Native Messaging frames on stdio; forwards to desktop app via named pipe; launches the app if not running | Download logic |
| **Chrome Extension** | Detects downloads (`chrome.downloads`), reads cookies and referrer, cancels browser download once captured, sends requests to the host, shows status in popup | Transfers data |

### 7.3 Project Structure
```
OB2/
  src/
    OB2.Core/          # engine, models, interfaces (no UI refs)
    OB2.Storage/       # SQLite repos, settings
    OB2.App/           # WPF UI, tray, DI composition root
    OB2.Host/          # Native Messaging host console app
    OB2.Shared/        # IPC contracts (message DTOs)
  extension/
    manifest.json
    src/background.ts
    src/popup/
  tests/
    OB2.Core.Tests/
    OB2.Storage.Tests/
    OB2.Integration.Tests/
  docs/
    PRD.md
    protocol.md
    decisions.md
```

### 7.4 Data Flow (typical download)
1. User clicks a link in Chrome. The extension's `chrome.downloads.onCreated` handler fires.
2. The extension collects URL, referrer, final URL, filename hint, MIME type, cookies for the origin, and user agent.
3. The extension cancels the browser download and sends `add_download` to the native host.
4. The host forwards the message over the named pipe to the desktop app.
5. The app shows the "Add download" dialog (or auto-adds if the user disabled confirmation).
6. The Core Engine probes the URL, creates a download record, splits it into segments, and enqueues it.
7. Segment workers stream bytes into the `.pgpart` file at their offsets. Progress checkpoints go to SQLite.
8. On completion, the engine verifies size (and checksum if given), renames the file, updates history, and raises a notification.
9. Status updates flow back through the pipe to the host and the extension popup.

---

## 8. Chrome Extension and Desktop Integration

### 8.1 Extension Permissions (MV3)
| Permission | Reason |
|------------|--------|
| `downloads` | Observe and cancel browser downloads |
| `cookies` | Read cookies for the download origin (authenticated links) |
| `contextMenus` | "Download with [App]" menu item |
| `nativeMessaging` | Communicate with `host.exe` |
| `storage` | Store capture rule settings locally |
| `<all_urls>` host permission | Required for cookie reads on arbitrary download domains; documented in README for personal use |

### 8.2 Native Messaging Registration
- The host manifest JSON is written to the app folder (`host-manifest.json`) and points to `host.exe` using a path resolved at runtime.
- A per-user registry key, no admin rights required:
  `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ob2.host` → default value = full path to `host-manifest.json`.
- Settings → **Register Browser Integration** writes the key and manifest. **Unregister** removes them.
- The manifest's `allowed_origins` lists the extension ID. Use a `key` field in the extension manifest during development so the extension ID is identical on all PCs.

### 8.3 Message Protocol (v1)
All messages are UTF-8 JSON. Each message has `v` (protocol version), `id` (GUID), `type`, and `payload`.

**Extension → Host → App**
```json
{
  "v": 1,
  "id": "9f6c1e2a-4b1d-4a8e-9a7b-2f3c4d5e6f70",
  "type": "add_download",
  "payload": {
    "url": "https://example.com/files/setup.exe",
    "finalUrl": "https://cdn.example.com/abc/setup.exe",
    "referrer": "https://example.com/downloads",
    "filenameHint": "setup.exe",
    "mimeType": "application/octet-stream",
    "fileSizeHint": 524288000,
    "userAgent": "Mozilla/5.0 ...",
    "cookies": "session=abc123; other=xyz",
    "headers": { "Accept-Language": "en-US" },
    "source": "chrome-downloads",
    "autoStart": false
  }
}
```

**App → Host → Extension (ack)**
```json
{ "v": 1, "id": "9f6c1e2a-...", "type": "ack", "payload": { "downloadId": 42, "status": "queued" } }
```

**Other message types**
| Type | Direction | Purpose |
|------|-----------|---------|
| `ping` / `pong` | Ext → Host | Health check; host reports whether app is running |
| `get_status` | Ext → App | Return active and recent download summaries |
| `pause_all` / `resume_all` | Ext → App | Popup controls |
| `capture_rules_updated` | App → Ext | Sync which extensions and sizes to capture |
| `error` | Any | `{ code, message }` with codes listed below |

**Error codes:** `APP_NOT_RUNNING`, `INVALID_URL`, `UNSUPPORTED_SCHEME`, `PIPE_TIMEOUT`, `PROTOCOL_VERSION_MISMATCH`, `INTERNAL_ERROR`.

**Protocol rules**
- Only `http` and `https` URLs are accepted. Other schemes are rejected.
- Cookies travel over the pipe only; they are never written to logs.
- Messages above 1 MB are rejected.
- The host acknowledges every request within 2 seconds, even if the download has not started.
- If the app is not running, the host starts it with a `--from-host` flag and holds the message for up to 15 seconds while waiting for the pipe.

### 8.4 Capture Behavior
- The extension cancels a captured browser download only after the app acknowledges receipt. If the app is unreachable, the browser download continues normally (fail-open).
- Capture rules: minimum size (default 1 MB), file extensions, and excluded domains.
- Holding Shift during a click bypasses capture for that one download.

### 8.5 Desktop App Without Extension
The app works fully without the extension. Downloads can be added by pasting URLs, dragging text or links onto the window, the clipboard monitor (P2), or `ob2.exe "https://..."` from a command line. The app does not depend on the extension or Chrome being installed.

---

## 9. UI/UX Requirements and Primary User Flows

### 9.1 Main Window Layout
Mirror the familiar three-zone layout without copying IDM's assets:
- **Toolbar (top):** Add URL, Resume, Stop, Stop All, Delete, Options, Scheduler.
- **Left pane:** Tree with **All Downloads**, **Unfinished**, **Finished**, **Queues**, and **Categories**. Counts shown next to each item.
- **Main list:** Columns: File name, Size, Status, Progress (bar with percentage), Speed, Time left, Date added, Save folder, Category. Sortable and reorderable.
- **Lower panel (selected item):** Segment visualization (one bar per connection with per-segment progress), source URL, destination, and error text.
- **Status bar:** Total speed, active count, queue status, and tray state.

### 9.2 Add Download Dialog
Fields: URL (pre-filled), Save as (from filename hint), Category (from extension), Save folder, Connections (default from settings), Queue, Start now or Add to queue, Checksum (optional). Buttons: **Download Later**, **Download Now**, **Cancel**. Shows probed file size and whether resume is supported.

### 9.3 Download Complete Dialog
Shows file name, size, and time taken. Buttons: **Open File**, **Open Folder**, **Close**. Checkbox: "Do not show again for this category."

### 9.4 Tray Behavior
- Left-click toggles the main window.
- Right-click menu: Show, Pause All, Resume All, Options, Exit.
- Closing the window minimizes to tray if the setting is on (default on).
- Tray tooltip shows active count and total speed.

### 9.5 Primary Flows
**Flow A: Capture from Chrome**
1. User clicks a file link in Chrome.
2. Add Download dialog appears (or auto-add per settings).
3. User clicks Download Now.
4. Row appears in the main list; progress updates; the browser download did not start.

**Flow B: Schedule overnight queue**
1. User selects queue "Night", opens Scheduler.
2. Sets Start 01:00, Stop 07:00, Days Mon–Fri.
3. Adds items to the queue. Queue waits until 01:00.
4. Queue stops at 07:00; items resume next day from saved progress.

**Flow C: Recover from failure**
1. Network drops mid-download. Segments enter Retrying.
2. Backoff applies; status bar shows "Waiting for network".
3. Network returns; segments resume from checkpoint.
4. If retries are exhausted, the item shows Failed with a Retry button and error detail.

**Flow D: Move to a new PC**
1. Copy the portable folder (or the single exe plus `data/`).
2. Run `ob2.exe`. First-run wizard sets the save folder.
3. Settings → Browser Integration → Register.
4. Load the unpacked extension in Chrome (see §12.5).

### 9.6 Interaction Standards
- Keyboard: Ctrl+N (add), Del (delete), Space (pause/resume selected), Ctrl+Shift+P (pause all), Ctrl+F (search).
- Deleting a file from disk requires confirmation (checkbox: "Also delete file").
- Destructive actions use plain language.

---

## 10. Data Models and Storage

### 10.1 SQLite Schema (v1)
```sql
CREATE TABLE categories (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  name            TEXT NOT NULL UNIQUE,
  save_folder     TEXT,
  extensions      TEXT                 -- comma-separated
);

CREATE TABLE queues (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  name            TEXT NOT NULL UNIQUE,
  concurrency     INTEGER NOT NULL DEFAULT 3,
  schedule_json   TEXT,                -- weekdays, start/stop times
  sort_order      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE downloads (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  url              TEXT NOT NULL,
  final_url        TEXT,
  filename         TEXT NOT NULL,
  dest_path        TEXT NOT NULL,
  category_id      INTEGER REFERENCES categories(id),
  queue_id         INTEGER REFERENCES queues(id),
  status           TEXT NOT NULL,      -- queued|probing|downloading|paused|retrying|completed|failed|cancelled
  total_bytes      INTEGER,            -- NULL if unknown
  downloaded_bytes INTEGER NOT NULL DEFAULT 0,
  connections      INTEGER NOT NULL DEFAULT 1,
  supports_ranges  INTEGER NOT NULL DEFAULT 0,
  etag             TEXT,
  last_modified    TEXT,
  checksum_algo    TEXT,               -- md5|sha256|NULL
  checksum_value   TEXT,
  referrer         TEXT,
  user_agent       TEXT,
  headers_json     TEXT,               -- non-sensitive headers only
  speed_limit_bps  INTEGER,
  error_code       TEXT,
  error_message    TEXT,
  created_at       TEXT NOT NULL,      -- ISO 8601 UTC
  started_at       TEXT,
  completed_at     TEXT,
  source           TEXT NOT NULL       -- manual|chrome|clipboard|cli
);

CREATE TABLE segments (
  download_id     INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
  segment_index   INTEGER NOT NULL,
  range_start     INTEGER NOT NULL,
  range_end       INTEGER NOT NULL,    -- inclusive
  downloaded      INTEGER NOT NULL DEFAULT 0,
  status          TEXT NOT NULL,       -- pending|active|done|failed
  retry_count     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (download_id, segment_index)
);

CREATE TABLE history_events (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  download_id     INTEGER REFERENCES downloads(id) ON DELETE SET NULL,
  event_type      TEXT NOT NULL,
  detail          TEXT,
  occurred_at     TEXT NOT NULL
);

CREATE INDEX idx_downloads_status ON downloads(status);
CREATE INDEX idx_downloads_created ON downloads(created_at);
```

Schema changes go through numbered migration scripts (`001_init.sql`, `002_...`). The app records the applied version in a `schema_version` table.

### 10.2 Settings
Stored in `settings.json`. If a file named `portable.flag` sits next to the executable, the settings and `data/` folder live beside the executable. Otherwise they live in `%APPDATA%\OB2\`. Settings include default connections, save folders, speed limits, capture rules, tray behavior, and schedule defaults.

### 10.3 Partial File Handling
- Partial files use a `.pgpart` suffix until complete, then rename atomically to the final name.
- Pre-allocation uses `SetFileValidData` where possible (requires privilege; fall back to `FileStream.SetLength`).
- If a `.pgpart` exists but its database record is missing, the file is left alone and listed under "Recovered orphan files", never deleted automatically.

### 10.4 Data Retention
- History is kept indefinitely by default. The user can set a retention limit (e.g., 90 days).
- Logs roll over at 7 days or 10 MB.

---

## 11. Security, Privacy, and Reliability

### 11.1 Security
| Concern | Requirement |
|---------|-------------|
| Local IPC | Named pipe ACL restricted to the current user. Pipe name includes a per-install random suffix. |
| Native host | Accepts connections only from the registered extension ID (`allowed_origins`). |
| Input validation | Accept only `http`/`https`. Reject URLs with embedded credentials unless the user confirms. Limit URL length to 2048 characters. |
| Path safety | Sanitize filenames (remove path separators and `..`, reject reserved names such as `CON` and `NUL`, limit to 200 characters). Resolve the final path and confirm it is inside the chosen save folder. |
| Cookies | Held in memory for the download's duration. Persisted only if the user enables "remember auth" (off by default), encrypted with DPAPI. |
| Executables | Warn before opening `.exe`, `.msi`, `.bat`, `.ps1`, `.scr`. Preserve Mark-of-the-Web by writing the `Zone.Identifier` alternate stream. |
| Dependencies | Pin NuGet versions; review on update. |
| Malware scanning | Not in v1. Optional Windows Defender CLI hook in v1.1. |
| Secrets | No API keys in code or the extension. |

### 11.2 Privacy
- No telemetry, analytics, or crash reporting to external services.
- Logs redact query strings and cookie values.
- The extension stores only settings, not browsing history.
- The extension loads no remote code and uses a strict Content Security Policy.

### 11.3 Reliability
- Atomic writes for settings and checkpoints (write temp file, then replace).
- Checkpoint progress every 5 seconds or 10 MB.
- On startup, downloads marked `downloading` are set to `paused` and listed for resume (not auto-resumed unless the user enables it).
- Crash recovery reconciles segment byte counts against actual file size.
- Each segment worker has per-request timeouts (30 seconds to connect, 60 seconds idle read).
- A global error boundary in the UI shows a recoverable error dialog instead of crashing.

---

## 12. Portable Windows Distribution Strategy

### 12.1 Options Compared
| Method | Description | Pros | Cons | Fit |
|--------|-------------|------|------|-----|
| **Portable folder (zip)** | Self-contained publish output in a folder; unzip and run | Simple; no admin; easy to copy | Multiple files | Good |
| **Single-file self-contained exe** | `PublishSingleFile` with the runtime bundled | One file to copy; no runtime install | ~70–150 MB; may extract to temp on first run | **Best for copying** |
| **Portable app package (PortableApps.com format)** | Adds launcher, metadata, packaging conventions | Standardized | Extra tooling; overkill for personal use | Optional |
| **Standard installer (MSIX, Inno Setup, WiX)** | Installs to Program Files; registers uninstaller | Familiar; clean Start menu and registration | Needs admin or per-user install; not portable | Only if distributing widely |

### 12.2 Recommendation
Ship **two artifacts** from the same build:
1. **`OB2-portable.zip`**: contains `OB2.exe` (single-file self-contained), `host.exe` (single-file), `host-manifest.json`, `portable.flag`, and a README. Unzip anywhere and run.
2. **Optional `OB2-Setup.exe`** (Inno Setup) for a standard install, if needed later.

For copying to another PC, the portable zip is the primary method.

### 12.3 Runtime Requirements
- Windows 10 1809+ or Windows 11, x64.
- No .NET install required (self-contained).
- WebView2 not required (WPF, not WebView).
- Writable folder for settings and data; falls back to `%APPDATA%` if the executable's folder is read-only.
- Chrome (optional) for browser capture.

### 12.4 Build and Packaging Steps
```powershell
dotnet publish src/OB2.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/app
dotnet publish src/OB2.Host -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish/host
# Assemble zip: app\OB2.exe, host\host.exe, host-manifest.json, portable.flag, README.txt
```
If SmartScreen warnings become a problem on other PCs, sign the executables with a personal code-signing certificate.

### 12.5 Extension Distribution (Personal Use)
- **Simplest:** Load unpacked from `extension/dist/` via `chrome://extensions` (Developer mode) on each PC.
- **Alternative:** Pack as `.crx` and use enterprise policy on managed machines (not recommended for home PCs).
- Keep the `key` in the extension manifest so the extension ID is identical on all PCs.

### 12.6 Portable Caveats
- The Native Messaging registry key is per user and per PC. Run **Register** once on each machine.
- Moving the folder after registration breaks the manifest path. The app re-registers on launch if it detects a path mismatch.
- Antivirus products may flag unsigned single-file exes; the README should explain this.

---

## 13. Architecture Decisions (Summary)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# 12 / .NET 8 | Mature HTTP and I/O; good Windows integration |
| UI | WPF + MVVM | Native, lightweight, no web runtime |
| Persistence | SQLite | Atomic, queryable, single file |
| Browser bridge | Native Messaging + named pipe | No open network port; Chrome-supported |
| Extension | MV3, TypeScript | Current Chrome standard |
| Distribution | Single-file self-contained exe in zip | Copy-and-run, no admin |
| Engine separation | Core library with no UI reference | Testable; AI agents can work on it in isolation |

Record any deviation from these decisions in `docs/decisions.md` with the reason.

---

## 14. Testing and Acceptance Criteria

### 14.1 Test Strategy
| Level | Scope | Tools |
|-------|-------|-------|
| Unit | Segment splitting, retry policy, rate limiter, filename sanitizer, schedule evaluator, protocol parsing | xUnit |
| Integration | Local HTTP server simulating ranges, no-range servers, redirects, 503 retries, slow servers, mid-download drops | Kestrel test host |
| Persistence | Crash simulation (kill process mid-download), resume correctness, schema migration | xUnit + temp DB |
| IPC | Host ↔ app pipe messages, malformed frames, oversize messages, app not running | xUnit |
| Extension | Message handling, fail-open behavior, cookie collection | Jest with mocked `chrome.*` APIs; manual smoke test |
| UI | Main flows, DPI scaling, tray behavior | Manual checklist; FlaUI for basic automation (P1) |
| Portable | Copy to clean Windows VM, run, register, download a file | Manual checklist on a VM |

### 14.2 Acceptance Criteria (v1.0)
| ID | Criterion |
|----|-----------|
| AC-1 | A 1 GB test file from a range-capable local server downloads with 8 connections and its checksum matches the source. |
| AC-2 | Pausing and closing the app mid-download, then reopening, resumes from checkpoint with no corrupted bytes. |
| AC-3 | Forcing a server to drop connections 5 times in one segment retries and completes. |
| AC-4 | A download with no range support completes with one connection. |
| AC-5 | A Chrome download link captured by the extension appears in the app within 2 seconds, with cookies passed. |
| AC-6 | With the app closed, a captured link starts the app and queues the item within 5 seconds. |
| AC-7 | With the extension disabled, the app downloads a manually pasted URL. |
| AC-8 | The portable zip runs on a clean Windows 11 VM with no .NET installed. |
| AC-9 | Registration writes the HKCU key; unregistration removes it; no admin prompt appears. |
| AC-10 | A scheduled queue starts and stops at configured times (tested with a clock abstraction). |
| AC-11 | Filenames with path traversal sequences are sanitized and saved only inside the save folder. |
| AC-12 | Idle memory is under 80 MB with 10 active downloads (measured over 10 minutes). |
| AC-13 | No cookie values appear in any log file during a captured download. |

---

## 15. Development Phases

Six phases, built strictly in sequence. Each phase is a separate agent session with its own brief (the "In scope" and "Out of scope" lists below are the brief). Do not combine phases.

**Rules for every phase**
1. Start from the tagged build of the previous phase. Create a branch `phase-N`.
2. Implement only the "In scope" items. Log anything else in `docs/backlog.md`.
3. Write or update tests for every requirement the phase touches.
4. Run the phase's exit criteria and record results in `docs/phase-N-report.md`.
5. Tag the build `phase-N-stable` only when all exit criteria pass. Do not start the next phase on a failing build.

### Phase 1: Project Setup, Database, and Configuration
**Maps to:** M0
**In scope**
- Solution structure per §7.3 (Core, Storage, App, Host, Shared, tests).
- Dependency injection composition root, Serilog logging with rolling files (NFR Logging).
- SQLite schema v1 (§10.1) delivered as numbered migrations with a `schema_version` table.
- Settings load/save as `settings.json` with portable vs. `%APPDATA%` resolution (§10.2).
- Atomic write helper for settings and checkpoints (§11.3).
- CI build for the solution and the test projects.

**Out of scope:** Any network I/O, download logic, UI beyond an empty shell window, extension, Native Messaging.

**Exit criteria**
- Solution builds in Release with zero build errors.
- Migrations apply to an empty database and are idempotent on re-run.
- Settings round-trip correctly, including the portable-flag path.
- A log file is created and rolls at the configured size.
- Unit tests for the migration runner and atomic writer pass.

### Phase 2: Download Engine with Pause/Resume and Integrity Verification
**Maps to:** M1 (plus checksum from M2)
**In scope**
- Probe the URL: HEAD, falling back to ranged GET (FR-2).
- Single-connection download with streaming writes to a `.pgpart` file (FR-4, §10.3).
- Pause, resume, and persistence of progress so it survives restart (FR-5). Checkpoint every 5 seconds or 10 MB (§11.3).
- Redirect handling, `Content-Disposition` filenames, collision rename (FR-7).
- HTTP/HTTPS with custom headers, cookies, referrer, and user agent passed in code (FR-8).
- Integrity verification: MD5 and SHA-256 against a supplied checksum (FR-11 moved up from P1).
- Filename sanitizer and path-safety checks (§11.1).
- Atomic rename from `.pgpart` to final name on completion.
- A CLI entry point for testing: `ob2.exe <url> [--sha256 <hex>] [--out <dir>]`.

**Out of scope:** Multiple connections or segments, retry policy beyond a single resume, UI, queues, scheduling, rate limiting, extension.

**Exit criteria**
- AC-2 passes for the single-connection case (pause, close app, reopen, resume, no corrupted bytes).
- AC-11 passes (path traversal sanitized).
- A download with a supplied SHA-256 that does not match is rejected and the `.pgpart` file is kept for inspection, not renamed.
- Crash simulation (kill process mid-download) resumes correctly.
- Engine unit tests run without any UI reference.

### Phase 3: Multi-Connection Downloading and Retries
**Maps to:** M2
**In scope**
- Segment engine: split ranges into N segments (default 8, range 1–32) and download in parallel (FR-3).
- Segment-level progress persisted in the `segments` table.
- Retry policy with exponential backoff, default 5 attempts per segment (FR-6).
- Per-request timeouts: 30 s connect, 60 s idle read (§11.3).
- Fallback to one connection when ranges are unsupported (FR-4, AC-4).
- Per-host connection cap with automatic fallback after repeated errors (§16.1 mitigation).
- Global rate limiter using a token bucket (FR-9, global only).
- Crash recovery reconciles segment byte counts against file size (§11.3).

**Out of scope:** Per-download speed limits, UI, queues, scheduling, extension, proxy support.

**Exit criteria**
- AC-1 passes: 1 GB file, 8 connections, checksum matches.
- AC-3 passes: forced connection drops retry and complete.
- AC-4 passes: no-range server completes with one connection.
- AC-2 passes for the multi-connection case.
- Throughput benchmark recorded against the local test server; results stored in the phase report.

### Phase 4: IDM-Style Interface with Menus, Categories, and Queues
**Maps to:** M3 and parts of M6
**In scope**
- Main window per §9.1: toolbar, left tree (All, Unfinished, Finished, Queues, Categories), main list with columns, lower segment panel, status bar.
- Add Download dialog per §9.2 (manual add only; no browser-capture path yet).
- Download Complete dialog per §9.3.
- Categories with extension-based auto-assignment and per-category folders (FR-18, FR-29).
- Queues with concurrency limits, reordering, and per-queue start/stop (FR-13, FR-14, FR-15).
- Scheduler with weekday start/stop times (FR-16) and completion actions (FR-17). Passes AC-10 using the clock abstraction.
- Download history view with search and filters (FR-19, FR-25).
- Tray icon with show/hide, pause all, resume all, exit (§9.4).
- Keyboard shortcuts from §9.6.
- Windows toast on completion or failure (FR-27).
- Settings screen for defaults: connections, retries, timeout, user agent, save folders (FR-30).

**Out of scope:** Browser capture, Native Messaging, extension, site grabber, media detection, clipboard monitor, CSV export, startup with Windows.

**Exit criteria**
- Flow B (overnight queue) works end to end, verified with a simulated clock.
- Flow C (network drop and recovery) works manually with a network toggle.
- AC-10 passes.
- UI meets §9 layout and keyboard standards on 100%, 125%, and 150% DPI.
- Idle memory measured against AC-12 (under 80 MB with 10 active downloads).
- A manual checklist from §14.1 (UI row) is signed off in the phase report.

### Phase 5: Portable EXE and Clean-Machine Test
**Maps to:** M4
**In scope**
- Single-file self-contained publish of `OB2.exe` per §12.4.
- Zip assembly: `OB2-portable.zip` with exe, README, and `portable.flag`.
- First-run wizard that sets the save folder (Flow D, steps 1–2).
- Fallback to `%APPDATA%` when the exe folder is read-only.
- Startup time measured and recorded (§16.1 mitigation).

**Out of scope:** Installer (Inno Setup, MSIX), code signing unless SmartScreen blocks testing, Chrome registration, extension.

**Exit criteria**
- AC-8 passes: zip runs on a clean Windows 11 VM with no .NET installed.
- Phase 4 smoke tests (add, download, pause, resume, queue) pass on the clean VM.
- A second clean Windows 10 1809+ check is recorded if a VM is available.
- VirusTotal scan result recorded before the zip is shared.

### Phase 6: Chrome Extension Integration
**Maps to:** M5
**Precondition:** Phases 1–5 tagged stable. The standalone app is not changed by this phase except for the IPC server and capture settings.
**In scope**
- Native Messaging host `host.exe` and the named-pipe bridge (§7.2, §8.3).
- Host registration and unregistration under HKCU (§8.2, FR-20 setup).
- Protocol v1 messages: `add_download`, `ack`, `ping`/`pong`, `get_status`, `error` (§8.3).
- Chrome MV3 extension: `chrome.downloads` capture, cookie and referrer handoff, fail-open behavior, context menu (FR-21).
- Capture confirmation dialog in the app (FR-22) and capture rules (§8.4).
- Extension popup showing status and pause/resume all.
- Re-registration on folder move (§12.6).

**Out of scope:** Firefox or Edge support, site grabber, media detection, Chrome Web Store listing.

**Exit criteria**
- AC-5 passes: captured link appears in the app within 2 seconds, cookies passed (test with an authenticated test server).
- AC-6 passes: app closed, captured link starts the app and queues the item within 5 seconds.
- AC-7 passes: with the extension disabled, manual add still works.
- AC-9 passes: HKCU key written and removed, no admin prompt.
- AC-13 passes: no cookie values in any log file during capture.
- Protocol tests for malformed frames, oversize messages, and app-not-running pass.

### Release and Later
| Milestone | Scope | Exit criteria |
|-----------|-------|---------------|
| **v1.0 (personal)** | Bug fixes, README, `docs/` complete | All P0 and P1 items in §3 done; AC-1 to AC-13 pass |
| **v1.1 (P2)** | Site grabber, media detection, clipboard monitor, CSV export, Defender scan hook | Per feature, each with its own brief following the same rules |

**Priority rule:** Engine correctness (Phases 2–3) before UI (Phase 4). The browser integration (Phase 6) comes last, so the core product never depends on it.

---

## 16. Risks, Trade-offs, and Open Questions

### 16.1 Technical Risks
| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Servers reject or throttle multiple connections | Medium | Medium | Detect and cap connections per host; expose setting; fall back to one connection after repeated errors |
| Sites use JavaScript-generated or signed URLs that expire | High | Medium | Capture final URL via `chrome.downloads`; allow manual URL refresh; document limitation |
| Chrome MV3 service worker suspends during long transfers | Medium | Low | Transfers run in the desktop app, not the extension; extension only hands off and receives status |
| Native Messaging registration breaks when the folder moves | Medium | Low | Auto-detect and re-register on launch; clear message in UI |
| Antivirus false positives on single-file exe | Medium | Medium | Code signing; scan before sharing; reproducible build |
| Large single-file exe is slow to start (runtime extraction) | Medium | Low | Configure extraction location or use `IncludeNativeLibrariesForSelfExtract`; measure startup |
| Many small segments cause slow I/O on HDD | Low | Medium | Pre-allocate; larger buffers; limit segments on HDD (detect drive type) |
| Cookies leak into logs or storage | Low | High | Redaction tests (AC-13); memory-only by default |
| Some sites forbid automated downloading (ToS) | Medium | Low | Respect `robots.txt` for site grabber (P2); document personal-use scope |

### 16.2 Trade-offs
- **Single-file exe vs. folder:** Single file is easier to copy but larger and slower to start. Chosen for copy-and-run priority.
- **WPF vs. Flutter:** WPF has less UI flexibility but more reliable system integration with less custom native code. Flutter remains reasonable if UI learning is the main goal, at the cost of extra platform plumbing for tray and Native Messaging.
- **Native Messaging vs. localhost HTTP:** Native Messaging avoids an open port but needs per-PC registry setup. Localhost HTTP is simpler but exposes a surface to other local processes and web pages unless tokens are enforced. Native Messaging is chosen.
- **Capture by default vs. opt-in:** Auto-capture is convenient but can surprise. Default is confirmation dialog on; auto-add is an option.
- **Fail-open capture:** If the app is down, Chrome downloads normally. This prevents lost downloads at the cost of occasional duplicate handling.

### 16.3 Open Questions
1. Should v1 include a checksum-verification UI, or only the API?
2. Is a dark theme required for v1, or is it P2?
3. Should interrupted downloads auto-resume on launch? (This PRD defaults to off.)
4. Is file-type categorization by extension enough, or is MIME sniffing needed?
5. Is a Chrome Web Store listing wanted, or is unpacked loading sufficient for personal use?
6. Should capture also support Edge in v1.1? (Edge is Chromium-compatible with the same extension.)
7. What are the expected maximum file size and number of concurrent downloads? (Affects segment and disk design.)
8. Is Flutter preferred over WPF as the UI framework, for personal reasons?

---

## Appendix A: Glossary
- **Segment:** A byte range of a file downloaded on its own connection.
- **Native Messaging:** A Chrome mechanism for an extension to talk to a local executable over stdin/stdout.
- **MV3:** Manifest Version 3, Chrome's current extension platform.
- **Mark-of-the-Web:** Windows metadata (`Zone.Identifier`) that marks files from the internet.
- **Fail-open:** If the app cannot be reached, browser downloads continue normally.

## Appendix B: Sources to Verify During Development
- Chrome Native Messaging documentation (developer.chrome.com)
- Chrome `downloads` and `cookies` extension API references
- .NET single-file and self-contained publishing documentation (learn.microsoft.com)
- Public IDM product pages and release notes (Tonec Inc.) for the feature matrix
