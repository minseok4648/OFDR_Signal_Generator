param(
    [string]$InputDirectory = 'D:\project\OFDR\signal processing\261106_RAW',
    [string]$SourceDirectory = 'D:\project\OFDR\signal processing\OFDR',
    [string]$ExperimentId = ((Get-Date -Format 'yyyyMMdd_HHmmss') + '_261106_notebook_temperature'),
    [string]$PythonExecutable = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$buildDirectory = Join-Path $repoRoot 'build_verify_261106_notebook'
$resultDirectory = Join-Path $repoRoot ('harness\results\' + $ExperimentId)
if ($ExperimentId -ne [IO.Path]::GetFileName($ExperimentId) -or $ExperimentId -in '.', '..') {
    throw 'ExperimentId must be a single directory name.'
}
if (Test-Path -LiteralPath $resultDirectory) { throw "Experiment already exists: $resultDirectory" }
if (-not (Test-Path -LiteralPath $PythonExecutable)) { throw 'Specify -PythonExecutable with x64 Python and NumPy/SciPy/pandas/Matplotlib.' }

Push-Location $repoRoot
try {
    & dotnet build OFDR_Harness/OFDR_Harness.csproj -c Release -p:Platform=x64 "-p:OutputPath=$buildDirectory\" --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
    & (Join-Path $buildDirectory 'OFDR_Harness.exe') reproduce-notebook-temperature `
        --input $InputDirectory --config harness/configs/261106_notebook_temperature.json --experiment $ExperimentId
    if ($LASTEXITCODE -ne 0) { throw "Full C# processing failed; see $resultDirectory" }
    $env:MPLCONFIGDIR = Join-Path $repoRoot '.analysis_cache\matplotlib'
    $env:PYTHONIOENCODING = 'utf-8'
    & $PythonExecutable harness/tools/Validate-NotebookTemperature.py --result $resultDirectory `
        --source-dir $SourceDirectory --native-dll (Join-Path $buildDirectory 'libMathNetNumericsMKL.dll')
    if ($LASTEXITCODE -ne 0) { throw "Original Python comparison failed; see $resultDirectory\python_validation" }
    Write-Output "Verified graph: $resultDirectory\temperature_reproduction.png"
}
finally { Pop-Location }
