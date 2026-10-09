# OB2

A personal Windows download manager. The requirements are in [OB2_PRD.md](OB2_PRD.md), and the build follows its six phases (§15).

**Status:** Phase 1 (project setup, database, configuration). There is no downloading yet.

## Layout

```
src/
  OB2.Core/      engine, models, interfaces (no UI references)
  OB2.Storage/   SQLite + migrations, settings.json, paths, logging, startup sequence
  OB2.App/       WPF shell and DI composition root (builds OB2.exe)
  OB2.Host/      Chrome Native Messaging host placeholder (builds host.exe, Phase 6)
  OB2.Shared/    IPC contract constants
tests/
  OB2.Core.Tests/  OB2.Storage.Tests/  OB2.Integration.Tests/
docs/
  decisions.md  backlog.md  phase-N-report.md
```

## Build and test

Needs the .NET 8 runtime and the .NET 8 or 9 SDK.

```powershell
dotnet build OB2.sln -c Release
dotnet test  OB2.sln -c Release
```

## Run

```powershell
dotnet run --project src/OB2.App -c Release
```

Without `portable.flag` next to `OB2.exe`, settings, the database, and logs go to `%APPDATA%\OB2\`. With the flag, they go beside the exe:

```
settings.json
data\ob2.db
data\logs\ob2-YYYYMMDD.log
```

## Database migrations

Schema scripts live in `src/OB2.Storage/Migrations/NNN_name.sql` and are embedded in the assembly. **Never edit an applied migration.** Add the next number instead. The app refuses to start if an applied script's checksum changes.
