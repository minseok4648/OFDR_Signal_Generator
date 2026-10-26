# GUI smoke launcher failure

- Status: `FAILED`
- GUI process started: false
- Failure stage: PowerShell script launcher
- Cause: local execution policy blocked direct `.ps1` invocation
- Resolution: use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...` without changing the machine policy
- Successful reruns: `harness/results/20260828_111327_gui_startup_smoke`, final v4 `harness/results/20260828_112304_gui_startup_smoke`

## Decision

`REJECT` this launcher attempt. The final executable itself was evaluated by the recorded successful rerun.
