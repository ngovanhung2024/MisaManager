# Third party notices

The active manager uses the native Windows/UIA3 path. No image-text recognition
runtime, GPU model, recognition worker or recognition package is shipped.

The Python runtime dependencies are pinned in `requirements.txt` and are used for
XML parsing, the Tk UI, bounded geometry/evidence handling, validation and source
packaging. The C# engine dependencies are pinned in the `src/*/packages.lock.json`
files and are used for UIA3, Win32 window evidence and native input.

Historical model files, simulator artifacts and old license notes may remain in
`models/`, `training/`, `artifacts/` or `licenses/` for audit purposes. They are
not included in the native source bundle and are not loaded by the manager.