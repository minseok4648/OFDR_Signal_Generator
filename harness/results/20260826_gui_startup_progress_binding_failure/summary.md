# GUI startup binding failure

- Status: `FAILED`
- Configuration: `Debug|x64`
- Detection: user started the WPF GUI under the Visual Studio debugger
- Exception: `System.InvalidOperationException`
- Message: TwoWay or OneWayToSource binding cannot write to the read-only `OfdrWorkspaceViewModel.ProgressPercent` property.
- Source binding: `ProgressBar.Value="{Binding ProgressPercent}"`

## Cause

`ProgressBar.Value` uses a default binding mode that attempted to update its source. `ProgressPercent` intentionally has a private setter because progress is owned by the ViewModel, so WPF rejected the runtime binding when the main window was shown.

## Harness gap

The previous gate compiled XAML and ran Core/CLI tests but never created and showed `MainWindow`. Binding source writability is checked when the binding is attached to the runtime DataContext, not during BAML compilation. The 5M CLI full run also bypassed WPF entirely, so it could not detect this startup failure.

## Decision

`REJECT`. GUI startup was not operational even though Core processing and the solution build had passed.
