# GUI startup smoke absolute-path failure

- Status: `FAILED`
- Process started: `false`
- Reproduction: pass an absolute path to `-ResultsRoot`
- Failure: `System.NotSupportedException: The given path's format is not supported.`

## Cause

`Invoke-GuiStartupSmoke.ps1` always passed `ResultsRoot` through `Join-Path (Get-Location)`. An already rooted Windows path was therefore combined a second time before `Path.GetFullPath`.

## Resolution

Check `Path.IsPathRooted` first. Rooted paths now go directly to `GetFullPath`; relative paths are still resolved from the current directory.

## Decision

`REJECT`. The GUI process was not started, so the runtime gate must be rerun after the harness fix.
