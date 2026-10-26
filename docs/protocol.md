# Browser Bridge Protocol (v1)

How the Chrome extension, `host.exe`, and `OB2.exe` talk to each other. The contract is PRD §8.3; this page records what the code actually does, including the decisions that filled gaps (D-50 to D-60 in [decisions.md](decisions.md)).

```
Chrome extension ──stdio (Native Messaging)──▶ host.exe ──named pipe──▶ OB2.exe
   one sendNativeMessage call                   one process             one connection
   = one request, one reply                     per call                per request
```

Code: `src/OB2.Shared/Protocol` (messages, framing, validation), `src/OB2.Shared/Ipc` (pipe, host lookup, registration), `src/OB2.Host/Program.cs`, `src/OB2.App/Services/BrowserBridge.cs`, `extension/src`.

## Framing

Both links use Chrome's Native Messaging framing: a 32-bit **little-endian** length, then that many bytes of UTF-8 JSON, with no byte-order mark.

- A frame above **1 MB** (1,048,576 bytes) is rejected before its body is read. Neither side sends one.
- A truncated length prefix, a truncated body, or invalid JSON is a protocol error. The receiver replies `error` / `INVALID_MESSAGE` with an empty `id` (it could not read one).
- On the pipe, each connection carries exactly one request and one reply.
- On stdio, the host reads requests until Chrome closes stdin (exit code 0). After a framing error it replies once and exits with code 1, because the stream is out of sync.

## Envelope

```json
{ "v": 1, "id": "9f6c1e2a-4b1d-4a8e-9a7b-2f3c4d5e6f70", "type": "add_download", "payload": { } }
```

| Field | Rule |
|-------|------|
| `v` | Always `1`. Any other value gets `PROTOCOL_VERSION_MISMATCH`. Bump it only when a message name or field changes. |
| `id` | A GUID chosen by the sender. The reply carries the same `id`. |
| `type` | Required; see below. |
| `payload` | Object or absent. Names are camelCase. Null fields are left out. Unknown fields are ignored. |

## Messages

| Request | Sent by | Reply | Notes |
|---------|---------|-------|-------|
| `add_download` | extension, second `OB2.exe` | `ack` or `error` | Payload below. The **host** starts OB2 if it isn't running. |
| `get_media_formats` | extension (a click on the video button, D-72) | `media_formats` or `error` | `{ url, streams?, title?, userAgent? }`: a video page, and (D-79) up to 5 HLS/DASH manifests its player loaded, oldest first, each as the browser requested it (D-80), with the tab's title. The app asks yt-dlp and reads streams itself; up to 90 s. The host starts OB2 if it isn't running. |
| `ping` | extension | `pong` `{ appRunning, hostVersion }` | Answered by the host itself; never starts OB2. |
| `get_status` | extension (popup, rule refresh) | `status` or `error` | Never starts OB2: `APP_NOT_RUNNING` instead. |
| `pause_all` | extension popup | `ack` `{ status: "paused" }` | Same as Stop All in the window. |
| `resume_all` | extension popup | `ack` `{ status: "resumed" }` | Same as Resume All. |
| `activate` | second `OB2.exe` (app-internal, D-50) | `ack` `{ status: "activated" }` | Shows the running window. |
| `capture_rules_updated` | — | — | **Not sent** (D-57). Native Messaging calls are one-shot, so the app can't push. Rules ride on every `status` reply instead. |

### `add_download` payload

Exactly as in PRD §8.3:

| Field | Type | Meaning |
|-------|------|---------|
| `url` | string, required | The link Chrome started with. |
| `finalUrl` | string | After redirects. If present, **this** is what OB2 downloads and what is validated. |
| `referrer` | string | Sent as `Referer`. |
| `filenameHint` | string | Chrome's file name, if it had one. OB2 still sanitizes it. |
| `mimeType` | string | |
| `fileSizeHint` | number | Bytes, if known. |
| `userAgent` | string | Chrome's UA; the download uses it. |
| `cookies` | string | A `Cookie` header value for `finalUrl` (or `url`). **Travels over stdio and the pipe only. Never written to logs.** Kept in memory; written to the database only if the user turns on "Remember website sign-ins", and then only DPAPI-encrypted until the download finishes (D-67). |
| `headers` | object | Extra request headers (the extension sends `Accept-Language`). |
| `source` | string | `chrome-downloads`, `chrome-context-menu`, `chrome-media` (the button over a video or the video/audio context menu, D-69), `chrome-page` (a link picked from the popup's "Find downloads" list, D-70), or `cli`. |
| `autoStart` | bool | `true` skips the confirmation dialog for this request. |
| `mediaPage` | bool | `true`: `url` is a video page (YouTube or another site yt-dlp supports), not a file. The app opens the Download Video window to choose quality and audio, and replies `"confirming"`. YouTube watch/Shorts/embed addresses are treated this way even without the flag (D-71). |
| `mediaFormat` | string | Only with `mediaPage`: the quality picked on the video button, a `format` from `media_formats` (`137+140`). With confirmation off the app adds the download at once and replies with its id; with confirmation on, the Download Video window opens with this quality selected (D-72). One or two plain format ids, else `INVALID_MESSAGE`. Or a subtitle track's `format` (`subs:en`, `auto-subs:en-orig`, D-74): `subs:` or `auto-subs:` and a language key of letters, digits, `_` and `-`. Or (D-80) tracks of a stream OB2 reads itself: `stream:v1+a0` (picture and sound), `stream:v2` (a picture with its own sound) or `stream:a0` (sound alone); OB2 downloads these without yt-dlp. |
| `mediaContainer` | string | Required with `mediaFormat`: the finished file's extension (`mp4`), from the same option; `srt`, `ass` or `vtt` for subtitles. `filenameHint` then carries the video's title with that extension; the app adds the language to a subtitle's name (`Title.en.srt`). |

Validation, done by both the host and the app (`ProtocolValidator`): the URL must be present, at most 2048 characters, absolute, and `http`/`https` (otherwise `INVALID_URL` or `UNSUPPORTED_SCHEME`). D-80: `headers` holds at most 50 entries, each name an RFC 9110 token and each value (like `referrer` and `cookies`) at most 8192 characters with no line break or other control character but tab (otherwise `INVALID_MESSAGE`), because headers go into requests and onto yt-dlp's command line.

`url` may also be a stream from a `media_formats` reply's `url` (D-79), with `mediaPage`. D-80: the extension then sends the request the browser made for it: `referrer` and `userAgent` as Chrome sent them, the other headers it sent (`Origin`, the player's own tokens) in `headers`, and the browser's cookies for the stream in `cookies`. Every piece of the stream is fetched with them.

### `get_media_formats` payload (D-72, D-79, D-80)

| Field | Type | Meaning |
|-------|------|---------|
| `url` | string, required | The video page. Validated like `add_download`'s. |
| `streams` | object[] | At most 5 HLS (`.m3u8`) or DASH (`.mpd`) manifests the page's player loaded, oldest first (otherwise `INVALID_MESSAGE`). Sent only after the click. Never on YouTube. Each is `{ url, referrer?, headers?, cookies? }` (D-80): an `http`/`https` address of at most 2048 characters, the `Referer` Chrome sent for it, the other request headers Chrome sent (never Cookie, Authorization, Range or the connection's own; checked like `add_download`'s), and the Cookie header for its address, which is never logged. |
| `title` | string | The tab's title, at most 300 characters; sent only with `streams`. Names a video read from a stream, whose own name is `master` or `index`. |
| `userAgent` | string | D-80: the browser's user agent, sent with `streams`. |

The app asks yt-dlp for the page first. When yt-dlp fails, or only guessed from the page's HTML (its generic extractor), it tries the streams in order, and the first with formats wins. D-80: OB2 reads each stream itself first, with the browser's request; its options are then `stream:` formats, which OB2 downloads without yt-dlp. When OB2 cannot read one, yt-dlp tries it with the same headers (not the cookies). A copy-protected or live stream ends the search at once. If none works, the page's own guess (if any) is the answer; otherwise the error is the page's, or, for a site yt-dlp does not support, the stream's.

### `ack` for `add_download`

```json
{ "v": 1, "id": "…", "type": "ack", "payload": { "downloadId": 42, "status": "queued" } }
```

- **Confirmation on** (default, FR-22), **`source: "cli"`**, or a URL with **embedded credentials** (`user:pass@host`, §11.1, D-59): the app shows the Add Download window and replies at once with `{ "downloadId": null, "status": "confirming" }` (D-53). If the user cancels, nothing is downloaded, and the browser download is already gone. That is the trade-off for the 2-second budget.
- **Confirmation off**: the download is added from the browser's hints and started; the reply carries its id and status. The job probes the server when it starts (D-21), so the hints only need to be close.

### `status` payload

```json
{
  "active": [ { "id": 3, "fileName": "ubuntu.iso", "status": "downloading", "progress": 42.5, "bytesPerSecond": 2097152, "totalBytes": 5368709120 } ],
  "recent": [ { "id": 2, "fileName": "notes.pdf", "status": "completed", "progress": 100, "bytesPerSecond": 0, "totalBytes": 2048 } ],
  "captureRules": { "enabled": true, "minSizeBytes": 1048576, "extensions": ["zip", "iso"], "excludedDomains": [] }
}
```

`recent` holds up to 8 downloads that are not running, newest first. `progress` is a percentage, or absent when the size is unknown.

### `media_formats` payload (D-72)

```json
{
  "title": "Launch day / highlights",
  "durationSeconds": 212,
  "options": [
    { "format": "137+140", "container": "mp4", "audioOnly": false, "quality": "1080p", "codec": "H.264", "size": 108400000 },
    { "format": "140", "container": "m4a", "audioOnly": true, "quality": "130 kbit/s", "codec": "AAC", "track": "English original (default)", "size": 3400000 }
  ],
  "subtitles": [
    { "format": "subs:en", "language": "en", "name": "English", "automatic": false, "containers": ["srt", "ass", "vtt"] },
    { "format": "auto-subs:en-orig", "language": "en-orig", "name": "English (Original)", "automatic": true, "containers": ["srt", "ass", "vtt"] }
  ]
}
```

Every picture quality, best first, each with the site's default sound track, then sound alone, one per language. `track` is present only when the video has sound in several languages; `size` is yt-dlp's estimate. The app keeps the answer for 10 minutes, so the `add_download` that follows does not run yt-dlp again.

`url` (D-79) is the address the options belong to: the page, or one of the request's `streams` exactly as it was sent. The extension sends that address back in `add_download`, and uses only a stream it sent. Absent from an app older than D-79.

`subtitles` (D-74) lists the tracks OB2 can save: the site's own, then the auto-generated ones (`automatic: true`) in languages the site has none in. YouTube's machine translations are left out. It is an empty list when the video has none, and absent from an app older than D-74. Each track goes back in `add_download` as `mediaFormat` = `format` and `mediaContainer` = one of `containers`.

### Errors

```json
{ "v": 1, "id": "…", "type": "error", "payload": { "code": "APP_NOT_RUNNING", "message": "OB2 is not running." } }
```

| Code | When |
|------|------|
| `APP_NOT_RUNNING` | `get_status` / `pause_all` / `resume_all` while OB2 is closed; `OB2.exe` not found next to `host.exe`, or it would not start. |
| `PIPE_TIMEOUT` | The host started OB2, but it did not answer within 15 seconds; or OB2 is running but did not reply in time (10 s, or 90 s for `get_media_formats`). |
| `MEDIA_TOOLS_MISSING` | `get_media_formats` before yt-dlp, ffmpeg and Deno are installed. The Download Video window installs them (D-72). |
| `MEDIA_FAILED` | yt-dlp could not list the page; `message` is its reason ("Video unavailable"), meant for the user. |
| `INVALID_URL` | Missing, over 2048 characters, or not an absolute URL. |
| `UNSUPPORTED_SCHEME` | Not `http`/`https` (for example `ftp:`, `file:`, `javascript:`). |
| `PROTOCOL_VERSION_MISMATCH` | `v` is not 1. |
| `INVALID_MESSAGE` | Malformed or oversize frame, missing `type`, or a type the app does not know (D-50). |
| `INTERNAL_ERROR` | The app failed while handling the request (details go to the app log only), or the pipe connection broke. |

## host.exe

- Chrome starts it with the caller's origin as an argument. Anything other than `chrome-extension://mgmmgplfngnhcencbhpfagnjcnenflpm/` exits with code 2. Chrome already enforces `allowed_origins`; this is a second check.
- Finds the pipe name through `HostLocator` (D-52): `settings.json` beside `host.exe` when `portable.flag` is there, then `%APPDATA%\OB2\settings.json`.
- Timeouts (D-60): **300 ms** to connect to a running app (a running app always has a pipe instance waiting, so this only sets how quickly "not running" is detected); **10 s** for the app's reply (**90 s** for `get_media_formats`, D-72); **15 s** to wait for an app it started (§8.3). A running app that replies late gets `PIPE_TIMEOUT`; it is not started again.
- If OB2 is not running and the request is `add_download` or `get_media_formats`, it starts `OB2.exe --from-host` from its own folder. Then it connects as soon as the app creates its pipe; the connect call waits for the pipe instead of polling.
- Logs nothing, so no cookie or URL can leak from it.

Measured with the published exes in v1.0 (trimmed, ReadyToRun 16.8 MB `host.exe`, D-68): **66–83 ms** per round trip with OB2 running, including starting `host.exe`; **1.2–1.4 s** for a capture that has to start OB2. (Phase 6, with a 64.5 MB untrimmed host on .NET 8: 170–250 ms and 1.8–2.0 s.)

## Named pipe

- Name `OB2-<16 hex digits>`, generated on the app's first start and saved as `ipc.pipeName` in `settings.json` (§11.1 per-install random suffix).
- Created with `PipeOptions.CurrentUserOnly`, so other Windows users cannot connect.
- The server accepts connections in a loop and serves each on its own task, with a 30-second limit per connection (90 s once the request is known to be `get_media_formats`).

## Registration (§8.2, AC-9)

**Options > Browser > Register** writes:

- `host-manifest.json` next to `host.exe` (or in the data folder if the program folder is read-only):
  ```json
  { "name": "com.ob2.host", "description": "OB2 download manager bridge", "path": "C:\\…\\host.exe",
    "type": "stdio", "allowed_origins": [ "chrome-extension://mgmmgplfngnhcencbhpfagnjcnenflpm/" ] }
  ```
- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ob2.host` and `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.ob2.host` (D-65), each with default value = the manifest's full path. Per-user, so no admin prompt. Edge runs the same extension and uses the same `chrome-extension://` origin, so one manifest serves both.

**Unregister** removes all of these. On every start the app repairs the registration if a manifest or the `host.exe` it names no longer exists, for example after the folder was moved (§12.6), and completes a registration of this copy that is missing a browser (one made before Edge support). It leaves alone any key that points at another copy that still works (D-55).

The extension ID is fixed by the `key` in `extension/manifest.json` (D-51). A Jest test recomputes the ID from the key.

## Extension behaviour

- **Capture** (D-58): on `downloads.onCreated`, if the rules say capture, the extension **pauses** the browser download, sends `add_download` (20-second timeout), and only after an `ack` **cancels and erases** it. Any other outcome resumes the browser download: no host, host error, timeout, error reply, unexpected reply. That is §8.4's fail-open.
- **Not captured** (D-61): downloads started by an extension (including the context-menu fallback below), incognito downloads, non-HTTP URLs (`blob:`, `data:`), downloads already finished, Shift-clicked links, excluded domains (download host or the page it came from), files known to be smaller than the minimum, and file types not in the list. The type comes from Chrome's file name, else the URL path, else the MIME type. A listed type of unknown size is captured.
- **Shift-click**: a content script reports links clicked with Shift held. The next download from that URL within 15 seconds is left to Chrome.
- **Context menu** "Download with OB2": sends the link with the page as referrer, regardless of the rules. If OB2 can't be reached, Chrome downloads the link instead.
- **Rules** (§8.4): cached in `chrome.storage.local`, refreshed from `status` on install, on browser start, when the popup is open, and in the background when older than 10 minutes. Until OB2 has been reached once, the app's defaults apply. The popup's switch turns capture off without touching the app.
- **Popup**: polls `get_status` every 2 seconds while open, with no overlapping requests. Pause all / Resume all.
- **Stored data**: the switch, the rules, and when they were fetched. No browsing history (§11.2).
