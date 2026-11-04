param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Csv,
    [string]$ResultsRoot = 'harness/results',
    [ValidateRange(10, 60)][int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$resolvedCsv = [IO.Path]::GetFullPath($Csv)
$resolvedResults = [IO.Path]::GetFullPath($ResultsRoot)
$started = [DateTime]::UtcNow
$runDirectory = Join-Path $resolvedResults ([DateTime]::Now.ToString('yyyyMMdd_HHmmss_fff') + '_mode_hop_gui_launcher')
New-Item -ItemType Directory -Path (Join-Path $runDirectory 'artifacts') -Force | Out-Null
$config = [ordered]@{ Executable = $resolvedExecutable; ExecutableSha256 = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash; Csv = $resolvedCsv; TimeoutSeconds = $TimeoutSeconds; Check = 'Actual x64 WPF in-process integration and rendering' }
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'config.json') -Encoding UTF8
$status = 'FAILED'
$failure = $null
$childDirectory = $null
$process = $null
try {
    $process = Start-Process -FilePath $resolvedExecutable -ArgumentList @('--mode-hop-smoke', ('"' + $resolvedCsv + '"'), ('"' + $resolvedResults + '"')) -WorkingDirectory $repository -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Mode hop WPF integration timed out.' }
    $process.Refresh()
    $childDirectory = Get-ChildItem -LiteralPath $resolvedResults -Directory -Filter '*_mode_hop_gui_smoke' | Where-Object { $_.CreationTimeUtc -ge $started } | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $childDirectory) { throw 'WPF process did not write integration results.' }
    $metrics = Get-Content -LiteralPath (Join-Path $childDirectory.FullName 'metrics.json') -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $metrics.Status -ne 'COMPLETED') { throw "WPF integration failed: $($metrics.Error)" }
    $status = 'COMPLETED'
}
catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    $record = [ordered]@{ Status = $status; Failure = $failure; ChildResult = $(if ($childDirectory) { $childDirectory.FullName } else { $null }); StartedUtc = $started.ToString('o'); EndedUtc = [DateTime]::UtcNow.ToString('o') }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'metrics.json') -Encoding UTF8
    @('# Mode hop WPF launcher', '', "- Status: $status", "- Failure: $failure", "- Child results: $($record.ChildResult)", '', '## Decision', '', $(if ($status -eq 'COMPLETED') { 'ACCEPT' } else { 'REJECT' })) | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.md') -Encoding UTF8
}
Write-Output "RESULT=$runDirectory"
Write-Output "CHILD=$($record.ChildResult)"
Write-Output "STATUS=$status"
if ($status -ne 'COMPLETED') { Write-Output $failure; exit 1 }
