# Clean-machine checklist (AC-8, Phase 5)

Run on a fresh Windows 11 VM (and Windows 10 1809+ if available) with **no .NET installed**.
Windows Sandbox works: enable the feature, run `build/publish.ps1`, double-click `build/sandbox/OB2-clean-test.wsb`.

| # | Step | Expected |
|---|------|----------|
| 1 | `C:\OB2\dotnet-check.txt` (Sandbox) or `dir "C:\Program Files\dotnet"` | Empty / not found: no .NET on the machine |
| 2 | Double-click `OB2.exe` | SmartScreen may warn (unsigned): More info → Run anyway. First-run wizard asks for the save folder |
| 3 | Choose a folder, Start | Main window opens; `settings.json` and `data\` appear next to `OB2.exe` (portable mode) |
| 4 | Ctrl+N, paste `https://speed.hetzner.de/100MB.bin` (or any large file), Download Now | Row shows progress, speed, 8 connection bars |
| 5 | Space (pause), Space (resume) | Pauses, then continues from the same percentage |
| 6 | Close the window, open it from the tray, Exit, start `OB2.exe` again, Resume | Download continues from its saved progress |
| 7 | Scheduler → New queue, add 2 items with Download Later, Start now | Queue runs both, respects its limit |
| 8 | Finish a download | Notification and Download Complete dialog; Open Folder shows the file |
| 9 | Copy the folder to a read-only location (e.g. a share without write access) and run it | Starts; data goes to `%APPDATA%\OB2` instead |

Record the Windows version, results, and the SHA-256 of the zip (from `publish/SHA256SUMS.txt`) in `docs/phase-5-report.md`.

## VirusTotal (before sharing the zip)

Upload `publish/OB2-portable.zip` at https://www.virustotal.com (or search its SHA-256) and record the detection count in the report. Unsigned single-file .NET programs sometimes get 1–3 heuristic hits; see README.txt for the user-facing note.
