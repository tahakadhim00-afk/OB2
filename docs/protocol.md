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
| `cookies` | string | A `Cookie` header value for `finalUrl` (or `url`). **Travels over stdio and the pipe only. Never written to logs or the database.** |
| `headers` | object | Extra request headers (the extension sends `Accept-Language`). |
| `source` | string | `chrome-downloads`, `chrome-context-menu`, or `cli`. |
| `autoStart` | bool | `true` skips the confirmation dialog for this request. |

Validation, done by both the host and the app (`ProtocolValidator`): the URL must be present, at most 2048 characters, absolute, and `http`/`https` (otherwise `INVALID_URL` or `UNSUPPORTED_SCHEME`).

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

### Errors

```json
{ "v": 1, "id": "…", "type": "error", "payload": { "code": "APP_NOT_RUNNING", "message": "OB2 is not running." } }
```

| Code | When |
|------|------|
| `APP_NOT_RUNNING` | `get_status` / `pause_all` / `resume_all` while OB2 is closed; `OB2.exe` not found next to `host.exe`, or it would not start. |
| `PIPE_TIMEOUT` | The host started OB2, but it did not answer within 15 seconds. |
| `INVALID_URL` | Missing, over 2048 characters, or not an absolute URL. |
| `UNSUPPORTED_SCHEME` | Not `http`/`https` (for example `ftp:`, `file:`, `javascript:`). |
| `PROTOCOL_VERSION_MISMATCH` | `v` is not 1. |
| `INVALID_MESSAGE` | Malformed or oversize frame, missing `type`, or a type the app does not know (D-50). |
| `INTERNAL_ERROR` | The app failed while handling the request (details go to the app log only), or the pipe connection broke. |

## host.exe

- Chrome starts it with the caller's origin as an argument. Anything other than `chrome-extension://mgmmgplfngnhcencbhpfagnjcnenflpm/` exits with code 2. Chrome already enforces `allowed_origins`; this is a second check.
- Finds the pipe name through `HostLocator` (D-52): `settings.json` beside `host.exe` when `portable.flag` is there, then `%APPDATA%\OB2\settings.json`.
- Timeouts (D-60): **300 ms** to connect to a running app (a running app always has a pipe instance waiting, so this only sets how quickly "not running" is detected); **10 s** for the app's reply; **15 s** to wait for an app it started (§8.3).
- If OB2 is not running and the request is `add_download`, it starts `OB2.exe --from-host` from its own folder. Then it connects as soon as the app creates its pipe; the connect call waits for the pipe instead of polling.
- Logs nothing, so no cookie or URL can leak from it.

Measured with the published single-file exes (Phase 6 report): **170–250 ms** per round trip with OB2 running, including starting `host.exe`; **1.8–2.0 s** for a capture that has to start OB2; **3.9 s** on the very first run of a freshly unpacked copy.

## Named pipe

- Name `OB2-<16 hex digits>`, generated on the app's first start and saved as `ipc.pipeName` in `settings.json` (§11.1 per-install random suffix).
- Created with `PipeOptions.CurrentUserOnly`, so other Windows users cannot connect.
- The server accepts connections in a loop and serves each on its own task, with a 30-second limit per connection.

## Registration (§8.2, AC-9)

**Options > Browser > Register** writes:

- `host-manifest.json` next to `host.exe` (or in the data folder if the program folder is read-only):
  ```json
  { "name": "com.ob2.host", "description": "OB2 download manager bridge", "path": "C:\\…\\host.exe",
    "type": "stdio", "allowed_origins": [ "chrome-extension://mgmmgplfngnhcencbhpfagnjcnenflpm/" ] }
  ```
- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ob2.host`, default value = the manifest's full path. Per-user, so no admin prompt.

**Unregister** removes both. On every start the app repairs the registration if the manifest or the `host.exe` it names no longer exists, for example after the folder was moved (§12.6). It leaves alone a registration that points at another copy that still works (D-55).

The extension ID is fixed by the `key` in `extension/manifest.json` (D-51). A Jest test recomputes the ID from the key.

## Extension behaviour

- **Capture** (D-58): on `downloads.onCreated`, if the rules say capture, the extension **pauses** the browser download, sends `add_download` (20-second timeout), and only after an `ack` **cancels and erases** it. Any other outcome resumes the browser download: no host, host error, timeout, error reply, unexpected reply. That is §8.4's fail-open.
- **Not captured** (D-61): downloads started by an extension (including the context-menu fallback below), incognito downloads, non-HTTP URLs (`blob:`, `data:`), downloads already finished, Shift-clicked links, excluded domains (download host or the page it came from), files known to be smaller than the minimum, and file types not in the list. The type comes from Chrome's file name, else the URL path, else the MIME type. A listed type of unknown size is captured.
- **Shift-click**: a content script reports links clicked with Shift held. The next download from that URL within 15 seconds is left to Chrome.
- **Context menu** "Download with OB2": sends the link with the page as referrer, regardless of the rules. If OB2 can't be reached, Chrome downloads the link instead.
- **Rules** (§8.4): cached in `chrome.storage.local`, refreshed from `status` on install, on browser start, when the popup is open, and in the background when older than 10 minutes. Until OB2 has been reached once, the app's defaults apply. The popup's switch turns capture off without touching the app.
- **Popup**: polls `get_status` every 2 seconds while open, with no overlapping requests. Pause all / Resume all.
- **Stored data**: the switch, the rules, and when they were fetched. No browsing history (§11.2).
