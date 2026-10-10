# OB2

A personal Windows download manager. The requirements are in [OB2_PRD.md](OB2_PRD.md), and the build follows its six phases (§15); see `docs/phase-N-report.md` for what each phase delivered.

## Features

- **Segmented downloads**: 1–32 connections per file (default 8), with pause/resume that survives closing the app or a crash.
- **Retries**: exponential backoff, per-read timeouts, and automatic fallback to one connection for servers that don't support ranges.
- **Verification**: MD5 / SHA-256 checksum checks.
- **Organization**: categories with per-extension rules and folders; named queues with concurrency limits, ordering, weekday schedules, and after-completion actions (close, sleep, shut down, run a command).
- **Window and tray**: the IDM-style window with a details panel, a tray icon, Windows notifications, and a global or per-download speed limit.
- **Portable**: put `portable.flag` next to `OB2.exe` and everything stays in that folder. The release is a single self-contained exe; no .NET install needed.
- **Chrome integration**: an MV3 extension hands browser downloads (with their cookies and referrer) to OB2 through a Native Messaging host. If OB2 can't be reached, Chrome downloads normally. Shift-click bypasses capture; "Download with OB2" is on every link's context menu.

## Layout

```
src/
  OB2.Core/      engine, queues, scheduler, categories, services (no UI references)
  OB2.Storage/   SQLite + migrations, settings.json, paths, logging, startup sequence
  OB2.App/       WPF window, dialogs, tray, DI composition root, CLI (builds OB2.exe)
  OB2.Host/      Chrome Native Messaging host (builds host.exe)
  OB2.Shared/    protocol messages and framing, named pipe, host registration
extension/       Chrome MV3 extension (TypeScript, esbuild, Jest)
tests/
  OB2.Core.Tests/  OB2.Storage.Tests/  OB2.Integration.Tests/ (includes IPC and host/app end-to-end)
build/
  publish.ps1      portable release: single-file exes, zip, SHA256SUMS.txt
  README.txt       the README that ships in the zip
tools/
  OB2.TestServer/  local HTTP server for manual checks (ranges, throttling, failures)
  make-icon.ps1    regenerates the app icon and the extension icons
docs/
  protocol.md  decisions.md  backlog.md  phase-N-report.md
```

## Build and test

You need the .NET 8 runtime and the .NET 8 or 9 SDK.

```powershell
dotnet build OB2.sln -c Release
dotnet test  OB2.sln -c Release
# Large tests (1 GiB AC-1 download, throughput benchmark):
$env:OB2_RUN_LARGE = "1"; dotnet test OB2.sln -c Release --filter "FullyQualifiedName~AC1|FullyQualifiedName~Benchmark"

# Chrome extension (Node.js 22):
cd extension; npm ci; npm run typecheck; npm test; npm run build   # → extension/dist
```

The end-to-end bridge tests start the real `host.exe` and a tray-only `OB2.exe` from a temporary portable copy, so an OB2 tray icon appears for a few seconds while they run.

## Portable release

```powershell
powershell -ExecutionPolicy Bypass -File build/publish.ps1 -Version 0.6.0 -IncludeHost
```

This writes `publish/OB2-portable/` (OB2.exe, host.exe, chrome-extension/, portable.flag, README.txt), `publish/OB2-portable.zip`, and `publish/SHA256SUMS.txt`. `-IncludeHost` needs Node.js for the extension build.

## Chrome integration

1. Run OB2 from the portable folder, open **Options > Browser**, click **Register**. This writes `host-manifest.json` and one per-user registry value (`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.ob2.host`), no admin rights needed.
2. In Chrome, open `chrome://extensions`, turn on Developer mode, click **Load unpacked**, and choose the `chrome-extension` folder (or `extension/dist` in a dev checkout). The extension ID is always `mgmmgplfngnhcencbhpfagnjcnenflpm`.

The message protocol is described in [docs/protocol.md](docs/protocol.md).

## Run

```powershell
dotnet run --project src/OB2.App -c Release                      # the window
src\OB2.App\bin\Release\net8.0-windows\OB2.exe "https://…"       # opens Add Download pre-filled
src\OB2.App\bin\Release\net8.0-windows\OB2.exe get "https://…" --out D:\Downloads --sha256 <hex>   # headless
```

| Option | Meaning |
|--------|---------|
| `--data <dir>` | Use `<dir>` for settings, database, and logs (handy for testing) |
| `--minimized` | Start in the tray |
| `--from-host` | Started by host.exe for a browser capture (tray only, no first-run wizard) |

Without `portable.flag` next to `OB2.exe`, settings, the database, and logs go to `%APPDATA%\OB2\`. If OB2 is already running for the same data folder, a second `OB2.exe "https://…"` hands the URL to it and exits.

### Keyboard

| Key | Action |
|-----|--------|
| Ctrl+N | Add a download (pre-fills a URL from the clipboard) |
| Space | Pause / resume the selected downloads |
| Del | Delete the selected downloads (asks first) |
| Ctrl+Shift+P | Pause everything |
| Ctrl+F | Search |
| Enter / double-click | Open the finished file |

You can also drag a link or a URL from a browser onto the window.

## Manual testing server

```powershell
dotnet run --project tools/OB2.TestServer        # http://127.0.0.1:18080/
# e.g. http://127.0.0.1:18080/file/52428800/sample.zip?bps=1000000
```

## Database migrations

Schema scripts live in `src/OB2.Storage/Migrations/NNN_name.sql` and are embedded in the assembly. **Never edit an applied migration.** Add the next number instead. The app refuses to start if an applied script's checksum changes.
