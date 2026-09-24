# MISA native migration report

## Result

The active manager path is UIA3 plus Win32 native. The old image-text recognition
path, workers, model assets, simulator and packaging hooks were removed from the
source tree. `requirements.txt` contains no recognition runtime.

## Native ownership

- Win32 owns process/window lifecycle, HWND identity, foreground, DPI, bounds,
  focus, WM_GETTEXT and SendInput guards.
- UIA3 owns semantic controls, AutomationId, patterns, tabs, rows and grid editors.
- Every action target now comes from current UIA3 identity or a verified native
  Win32 child control. There is no coordinate-map fallback.
- Screenshot capture is evidence only and never supplies a target or value.

## Source checks

Python source compiles with `py_compile`; source bundles compute deterministic
hashes and compile every module before reload. The C# engine is built separately
with `build-engine.ps1`. No app launch or MISA workflow was run during this
migration.

## Verification state

The migration is source-ready. Live MISA behavior, focus timing and custom grid
coverage remain `unverified` until a user-authorized live run.


