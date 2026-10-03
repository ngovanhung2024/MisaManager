# MisaManager installer and rollback runbook

WiX 4.0.6 consumes the captured self-contained win-x64 payload. The per-user MSI
installs app and bridge below `%LOCALAPPDATA%\Programs\MisaManager`.
State under `%LOCALAPPDATA%\MisaManager` is not removed by uninstall.

## Stage

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
& .\scripts\release.ps1 -Stage -Installer -Version 0.1.11 -DotnetPath $dotnet
```

Use a version greater than the installed MSI; only major.minor.build controls MSI
upgrade order. The script prints the generated release directory. It includes
source, payload, health results, installer and release-manifest.json. Unsigned
artifacts are marked unsigned_internal, accepted for this personal tool. Optional
signing requires a real certificate; it is not a release gate in the current scope.

## Install or upgrade

Close the manager. Complete/reconcile any paused, blocked or uncertain
workflow first; do not delete session.json to bypass the maintenance gate.

```powershell
& .\scripts\install.ps1 -ReleaseDirectory 'C:\misa\out\releases\<generated-id>'
```

The script verifies hashes, holds the manager mutex, snapshots state, installs,
checks the installed executable hashes and runs no-input health checks. The active
deployment pointer is written only after success. Historical rollback releases
may still contain the retired TaskBoard, but current packages do not. Release
staging alone never changes the installed app.

## Roll back

```powershell
& .\scripts\rollback.ps1 -ExpectedAppVersion '<previous-version>'
```

Rollback accepts a healthy compatible previous MSI and byte-identical state since
upgrade. Changed state blocks rollback to prevent loss. Current scripts support
schema 1 only, with no schema migration. Failure-recovery logs and retained MSI/state
snapshots are under `%LOCALAPPDATA%\MisaManager\deployment\<id>`.

Rollback never undoes a voucher saved in MISA. Clean-machine acceptance was removed
by user instruction. Upgrade-failure and real native repair-restart evidence are
not implied by build or health checks.

## Retired task board

The Pending Task Board is no longer published. A major MSI upgrade removes the
old package's board files. Historical rollback packages remain unchanged.

## Runtime check

`release.ps1` fails when the app or bridge payload is framework-dependent (missing
`hostfxr.dll`/`coreclr.dll`/`System.Private.CoreLib.dll`), because the build host's
own .NET would hide that. `install.ps1`, rollback health checks, `owner-interlock.ps1`
and `start-manager.ps1` also launch the bridge with `--health-check` and report a
Vietnamese message when the .NET runtime is missing (`scripts/bridge-runtime.ps1`).
