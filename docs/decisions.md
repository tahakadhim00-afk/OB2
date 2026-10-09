# Decisions

Choices made where the PRD says *(decide)*, leaves a detail open, or where the build deviates from §13. Newest last.

| # | Phase | Decision | Reason |
|---|-------|----------|--------|
| D-1 | 1 | The project has its own git repo at `OB2/`. | The enclosing folder sits inside an unrelated repo rooted at the user profile. Phase branches and tags must not mix with it. |
| D-2 | 1 | The PRD stays at the repo root as `OB2_PRD.md` rather than `docs/PRD.md` (§7.3). | That is the file the owner works from; a copy would drift. |
| D-3 | 1 | Target `net8.0` / `net8.0-windows` as §6.2 specifies. Built with the .NET 9 SDK (9.0.301); tests run on the .NET 8 runtime. | Follows §13. **Note:** .NET 8 support ends 2026-11-10. Moving to .NET 10 LTS is logged in the backlog for the owner to approve. |
| D-4 | 1 | FluentAssertions is pinned to **7.2.2** (Apache-2.0), not 8.x. | FluentAssertions 8 uses a commercial licence. 7.x has every assertion this project needs. |
| D-5 | 1 | NuGet versions are pinned centrally in `Directory.Packages.props` (§11.1). | One place to review on update. |
| D-6 | 1 | Logging setup (`LogConfigurator`) and the startup sequence (`StorageBootstrapper`) live in `OB2.Storage`, not `OB2.App`. | The exit criteria need log rolling and the full startup tested without WPF (§0). |
| D-7 | 1 | Data layout: `<root>\settings.json`, `<root>\data\ob2.db`, `<root>\data\logs\ob2-YYYYMMDD[_NNN].log`. `<root>` is the exe folder when `portable.flag` exists, otherwise `%APPDATA%\OB2`. | §10.2 says settings and `data/` sit beside the exe; logs are data. |
| D-8 | 1 | `schema_version(version, name, checksum, applied_at)`. Each migration runs in its own `BEGIN IMMEDIATE` transaction with its version row. The runner refuses to start if an applied migration was edited (SHA-256 over line-ending-normalized text) or if the DB has a version this build doesn't know. | A failed migration leaves the DB at the last good version. Editing `001_init.sql` instead of adding `002_*.sql` is caught immediately. A git `autocrlf` difference is not treated as an edit. |
| D-9 | 1 | Migrations are embedded resources, not loose files. | The single-file exe (§12) needs no extra files. |
| D-10 | 1 | SQLite runs with `journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, 30 s busy timeout. | WAL stays consistent after a crash. NORMAL can lose the last commit on power loss; the engine reconciles segments against the `.pgpart` file anyway (§11.3). |
| D-11 | 1 | Default user agent is a current Chrome UA string. | Some servers refuse unknown agents; captured downloads send the browser's UA anyway. User-editable (FR-30). |
| D-12 | 1 | Per-category save folders live only in `categories.save_folder`. `settings.json` holds the default save folder. | Avoids two sources of truth for FR-18/FR-29. |
| D-13 | 1 | `defaultSaveFolder` is `null` until the first-run wizard (Phase 5) or the user sets it. | Lets Phase 5 detect first run without a separate flag. |
| D-14 | 1 | Settings loading is forgiving: comments and trailing commas allowed, unknown keys ignored, out-of-range values clamped and logged, an unparseable file backed up as `settings.json.corrupt-<UTC timestamp>` and replaced by defaults. | §4 Reliability: one bad record must not stop the app. |
| D-15 | 1 | Atomic writes use a temp file in the target folder, `Flush(true)`, then `File.Move(overwrite: true)` (MoveFileEx replace). | Same-volume rename is atomic on NTFS; flushing first avoids a zero-length file after power loss. |
| D-16 | 1 | UI strings live in `OB2.App/Resources/Strings.xaml` (a WPF ResourceDictionary). | Externalized for translation (NFR Localization) with no code generator. |
| D-17 | 1 | Analyzer rules: warnings are errors; `CA1716` is off (the `OB2.Shared` name is fixed by §7.3); `CA1848` is a suggestion. | Keeps the build strict without fighting the mandated project names. |
| D-18 | 1 | The app manifest declares PerMonitorV2 DPI awareness, `asInvoker`, and long-path awareness. | NFR Accessibility (125%/150%), AC-9 (no admin prompt), deep download folders. |
