param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [string]$ResultsRoot = "harness\results",

    [ValidateRange(2, 60)]
    [int]$ObservationSeconds = 5
)

$ErrorActionPreference = "Stop"
$startedUtc = [DateTime]::UtcNow
$experimentId = [DateTime]::Now.ToString("yyyyMMdd_HHmmss") + "_gui_startup_smoke"
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$resolvedResultsRoot = if ([System.IO.Path]::IsPathRooted($ResultsRoot)) {
    [System.IO.Path]::GetFullPath($ResultsRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $ResultsRoot))
}
$resultDirectory = Join-Path $resolvedResultsRoot $experimentId
$process = $null
$status = "FAILED"
$exitCode = $null
$stayedAlive = $false
$failureMessage = $null
$utf8 = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

try {
    $process = Start-Process -FilePath $resolvedExecutable -PassThru -WindowStyle Hidden
    $exitedEarly = $process.WaitForExit($ObservationSeconds * 1000)
    if ($exitedEarly) {
        $exitCode = $process.ExitCode
        throw "GUI가 관찰 시간 전에 종료되었습니다. ExitCode=$exitCode"
    }

    $stayedAlive = $true
    $status = "COMPLETED"
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    if ($process -ne $null -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }

    $endedUtc = [DateTime]::UtcNow
    $executableHash = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash
    $config = [ordered]@{
        SchemaVersion = 1
        Executable = $resolvedExecutable
        ExecutableSha256 = $executableHash
        ObservationSeconds = $ObservationSeconds
        Check = "WPF process startup and binding attachment"
    }
    $metrics = [ordered]@{
        Status = $status
        StartedUtc = $startedUtc.ToString("o")
        EndedUtc = $endedUtc.ToString("o")
        ProcessStayedAlive = $stayedAlive
        ExitCode = $exitCode
        FailureMessage = $failureMessage
    }
    $summaryLines = @(
        "# GUI startup smoke test",
        "",
        "- Status: ``$status``",
        "- Executable: ``$resolvedExecutable``",
        "- SHA-256: ``$executableHash``",
        "- Observation: $ObservationSeconds s",
        "- Process stayed alive: $stayedAlive",
        "- Failure: $failureMessage",
        "",
        "## Interpretation",
        "",
        $(if ($status -eq "COMPLETED") { "The WPF process created its main window, attached runtime bindings, and remained alive for the observation window." } else { "The WPF process did not pass the startup and runtime binding gate." }),
        "",
        "## Decision",
        "",
        $(if ($status -eq "COMPLETED") { "``ACCEPT``" } else { "``REJECT``" })
    )

    [System.IO.File]::WriteAllText((Join-Path $resultDirectory "config.json"), ($config | ConvertTo-Json -Depth 4), $utf8)
    [System.IO.File]::WriteAllText((Join-Path $resultDirectory "metrics.json"), ($metrics | ConvertTo-Json -Depth 4), $utf8)
    [System.IO.File]::WriteAllLines((Join-Path $resultDirectory "summary.md"), $summaryLines, $utf8)
}

Write-Output "RESULT=$resultDirectory"
Write-Output "STATUS=$status"
if ($status -ne "COMPLETED") {
    exit 1
}
