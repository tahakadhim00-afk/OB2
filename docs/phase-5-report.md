# Phase 5 Report: Portable EXE and Clean-Machine Test

**Date:** 2026-10-10
**Branch:** `phase-5` (from `phase-4-rc`) · **Tag:** `phase-5-rc`
**Environment:** Windows 11 Pro (10.0.26300), .NET SDK 9.0.301, .NET runtime 8.0.17. Windows Sandbox is not enabled on this PC and no VM was available.

## Exit criteria

| Criterion | Result | Evidence |
|-----------|--------|----------|
| AC-8: the zip runs on a clean Windows 11 VM with no .NET installed | **Pending manual** | Needs a clean machine. Prepared: [build/clean-machine-checklist.md](../build/clean-machine-checklist.md) and a Windows Sandbox config ([build/sandbox/OB2-clean-test.wsb](../build/sandbox/OB2-clean-test.wsb)) that unpacks the zip and records whether `C:\Program Files\dotnet` exists. Closest check done here: the unpacked zip started and reached the tray with `PATH` limited to `System32` and `DOTNET_ROOT` pointing at a folder that doesn't exist, so no installed runtime could be found (see below). |
| Phase 4 smoke tests (add, download, pause, resume, queue) on the clean VM | **Pending manual** | Steps 4–8 of the checklist. |
| Second check on Windows 10 1809+ | **Pending manual** (if a VM is available) | Same checklist. |
| VirusTotal result recorded before the zip is shared | **Pending manual** | Uploading the zip publishes it to a third party, so the owner does it. SHA-256 of this build is below; search it on virustotal.com, or upload the zip. |

Nothing here could be verified on a clean machine, so the build is tagged `phase-5-rc`, not `phase-5-stable`.

## Build output

`powershell -ExecutionPolicy Bypass -File build/publish.ps1 -Version 0.5.0`

| File | Size | SHA-256 |
|------|-----:|---------|
| `OB2.exe` (single-file, self-contained, win-x64, **not compressed**) | 158.3 MB | `cd214518ee284d02d6223642500e1985195fd2a9dc9b9327c93231fae560b1b4` |
| `OB2-portable.zip` (OB2.exe, portable.flag, README.txt) | 64.6 MB | `89ed708e0b2b326d3cae888ff7d445938e00739599fb702a84e724449d4c23d1` |

Compression inside the exe is off (decision D-56). A compressed exe is 70 MB, but it unpacks its assemblies into private memory at every start: ~98 MB with 10 downloads against ~36 MB uncompressed, which would fail AC-12. The zip compresses the exe to almost the same download size.

## Startup time (§16.1)

Logged by the app on every launch (`Startup: tray ready … ms after process start`, decision D-48). Measured on the uncompressed single-file exe in a portable folder, tray-only, with no dotnet on `PATH`. The first ("cold") run had just deleted `%TEMP%\.net\OB2`, where the native WPF libraries are unpacked.

| Run | Series 1 | Series 2 |
|-----|---------:|---------:|
| Cold (native libraries unpacked) | 1392 ms | 1707 ms |
| Warm | 898 ms | 1190 ms |
| Warm | 1416 ms | 1359 ms |

So 0.9–1.7 s to tray-ready. The unpacked zip on its first start: 1177 ms. Single-file packaging does not cause a noticeable delay at this size.

## Portable smoke test (this PC)

The zip was unpacked to a new folder and started with `--minimized`, a reduced `PATH`, and an invalid `DOTNET_ROOT`. Within 6 s: `settings.json` and `data\ob2.db`, `data\ob2.lock`, `data\logs\ob2-YYYYMMDD.log` were created **next to OB2.exe** (portable mode), and the log showed `OB2 starting; data root … (portable)` and `Startup: tray ready 1177 ms`.

## Delivered

| Requirement | Where |
|-------------|-------|
| Single-file self-contained publish (§12.4) | `build/publish.ps1` (`-p:PublishSingleFile=true --self-contained`, native libraries included, `DebugType=none`) |
| `OB2-portable.zip` with exe, README, `portable.flag`; `SHA256SUMS.txt` | `build/publish.ps1`, `build/README.txt` |
| First-run wizard sets the save folder (Flow D steps 1–2) | `Views/FirstRunWindow`, `App.RunFirstRunWizard`. Shown only while `folders.defaultSaveFolder` is null and the start is not tray-only (decision D-46). Creates the chosen folder and shows the error if it can't; shows whether the app runs portable and where its data lives. |
| Fallback to `%APPDATA%\OB2` when the exe folder is read-only (§12.3) | `AppPaths.Resolve` probes the folder with a temporary file. `AppPaths.FellBackFromReadOnly` is set, and the bootstrapper logs a warning (decision D-45). |
| Startup time measured and recorded (§16.1) | `App.LogStartupTime`, table above |
| Clean-machine procedure | `build/clean-machine-checklist.md`, `build/sandbox/OB2-clean-test.wsb` |

## Tests

```
Passed!  - Failed: 0, Passed: 135, Skipped: 0, Total: 135 - OB2.Core.Tests.dll
Passed!  - Failed: 0, Passed:  38, Skipped: 0, Total:  38 - OB2.Storage.Tests.dll
Passed!  - Failed: 0, Passed:  55, Skipped: 2, Total:  57 - OB2.Integration.Tests.dll
```

New: `AppPathsTests.Read_only_portable_folder_falls_back_to_appdata` (flag present, probe says read-only → `%APPDATA%\OB2`, `IsPortable` false, `FellBackFromReadOnly` true) and `Default_writability_probe_leaves_no_files_behind` (the real probe on a writable folder → portable, only `portable.flag` left in the folder).

## For the owner

1. Enable Windows Sandbox (`Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM`, then reboot), run `build/publish.ps1`, double-click `build/sandbox/OB2-clean-test.wsb`, and follow the checklist. If Sandbox rejects the relative `HostFolder`, replace it with the absolute path of the `publish` folder.
2. The first-run wizard is a visible window, so it was not opened during this session. Checklist steps 2–3 cover it.
3. VirusTotal: search the zip's SHA-256 above, or upload `publish/OB2-portable.zip`, and add the detection count here.
