# OB2

A personal, IDM-style download manager for Windows. It does segmented, resumable downloads, has queues and a scheduler, and comes with a Chrome/Edge extension that hands browser downloads to the app. It ships as a portable folder that needs no installer and no .NET install.

## Features

- **Fast, resumable downloads**: 1–32 connections per file. Pause/resume survives closing the app or a crash. Retries with backoff, and a fallback to one connection for servers that don't support ranges.
- **Integrity**: MD5 / SHA-256 checks, safe file names, Mark-of-the-Web on downloaded files.
- **Organization**: categories with per-type folders; queues with concurrency limits, weekday schedules, and after-completion actions (sleep, shut down, run a command).
- **Window and tray**: download list with per-connection progress, tray icon, Windows notifications, global or per-download speed limits.
- **Chrome and Edge integration**: captures downloads with their cookies and referrer. If OB2 can't be reached, the browser downloads normally. Shift-click to bypass capture; "Download with OB2" is on every link's right-click menu.
- **Portable**: settings, history, and logs stay next to `OB2.exe`.

## Tech stack

| Part | Stack |
|------|-------|
| Desktop app | C# 14, .NET 10 (LTS), WPF, CommunityToolkit.Mvvm, H.NotifyIcon |
| Download engine | `HttpClient` / `SocketsHttpHandler`, token-bucket rate limiting, sparse files |
| Storage | SQLite (Microsoft.Data.Sqlite, WAL) with versioned migrations, JSON settings |
| Logging | Serilog (rolling files; cookies and URL queries are redacted) |
| Browser bridge | Native Messaging host for Chrome and Edge (`host.exe`) ↔ app over a per-user named pipe |
| Browser extension | Manifest V3 (one build for Chrome and Edge), TypeScript 5, esbuild |
| Tests | xUnit, FluentAssertions, Kestrel test server; Jest with jsdom for the extension |
| Packaging | Single-file self-contained publish, PowerShell |

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`) and, for the extension, Node.js 22.

```powershell
dotnet build OB2.sln -c Release
dotnet test  OB2.sln -c Release
dotnet run --project src/OB2.App -c Release

# Browser extension (Chrome and Edge)
cd extension; npm ci; npm test; npm run build

# Portable release (OB2.exe, host.exe, chrome-extension/, zip + SHA256SUMS)
powershell -ExecutionPolicy Bypass -File build/publish.ps1 -IncludeHost
```

To set up Chrome or Edge: in OB2, open **Options > Browser** and click **Register**. Then go to `chrome://extensions` (or `edge://extensions`), turn on Developer mode, click **Load unpacked**, and pick the `chrome-extension` folder.

## Project layout

```
src/        OB2.Core (engine) · OB2.Storage · OB2.App (WPF) · OB2.Host (Native Messaging) · OB2.Shared (protocol, IPC)
extension/  MV3 extension for Chrome and Edge
tests/      unit, storage, and integration tests
build/      publish script and the release README
docs/       protocol, decisions, backlog, per-phase reports
```

## Docs

- [OB2_PRD.md](OB2_PRD.md): requirements and the six-phase plan
- [docs/protocol.md](docs/protocol.md): the extension ↔ host ↔ app protocol
- [docs/decisions.md](docs/decisions.md): design decisions
- `docs/phase-N-report.md`: what each phase delivered, with test results
