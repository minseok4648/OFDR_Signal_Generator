param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$ResultsDirectory,
    [double]$ThresholdVoltage = 1.8
)
$ErrorActionPreference = 'Stop'
$InputPath = [IO.Path]::GetFullPath($InputPath)
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
function Save-Json($Name, $Value) {
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory $Name), ($Value | ConvertTo-Json -Depth 12), $utf8)
}
Save-Json 'config.json' ([ordered]@{
    InputPath = $InputPath; ThresholdVoltage = $ThresholdVoltage
    Scope = 'Scan CH1 only across the record to establish all trigger boundaries; hash all file bytes. No CH3 phase/FFT or OFDR analysis.'
    Rule = 'A complete sweep is bounded by two observed CH1 <= threshold to CH1 > threshold transitions. An initially High sample is not an observed rising edge.'
})
[IO.File]::WriteAllText((Join-Path $ResultsDirectory 'summary.md'), '# Sweep read diagnosis' + "`n`nStatus: RUNNING`n", $utf8)
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
public sealed class SweepReadDiagnosisResult {
    public string[] Header;
    public string Sha256;
    public long FileBytes;
    public long Rows;
    public double SampleIntervalSeconds;
    public long DeclaredRows;
    public double FirstTime;
    public double LastTime;
    public double FirstCH1;
    public double MinimumCH1 = double.PositiveInfinity;
    public double MaximumCH1 = double.NegativeInfinity;
    public bool StartsHigh;
    public bool EndsHigh;
    public List<long> RisingIndices = new List<long>();
    public List<long> FallingIndices = new List<long>();
}
public static class SweepReadDiagnosis {
    public static SweepReadDiagnosisResult Scan(string path, double threshold) {
        var c = CultureInfo.InvariantCulture;
        var before = new FileInfo(path); var bytes = before.Length; var modified = before.LastWriteTimeUtc;
        var r = new SweepReadDiagnosisResult { FileBytes = bytes }; var header = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1048576)) {
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1048576, true)) {
                string line; bool data = false, previous = false; int ch1 = -1, columns = 0;
                while ((line = reader.ReadLine()) != null) {
                    if (!data) {
                        header.Add(line); var cells = line.Split(',');
                        if (cells[0] == "Sample Interval") r.SampleIntervalSeconds = double.Parse(cells[1], c);
                        if (cells[0] == "Record Length") r.DeclaredRows = checked((long)double.Parse(cells[1], c));
                        if (cells[0] == "TIME") { ch1 = Array.IndexOf(cells, "CH1"); columns = cells.Length; data = true; if (ch1 < 0) throw new InvalidDataException("Missing CH1."); }
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var values = line.Split(',');
                    if (values.Length != columns) throw new InvalidDataException("Wrong column count at sample " + r.Rows);
                    var v = double.Parse(values[ch1], c);
                    if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidDataException("Nonfinite CH1 at sample " + r.Rows);
                    bool high = v > threshold;
                    if (r.Rows == 0) { r.StartsHigh = high; r.FirstCH1 = v; r.FirstTime = double.Parse(values[0], c); }
                    else {
                        if (!previous && high) r.RisingIndices.Add(r.Rows);
                        if (previous && !high) r.FallingIndices.Add(r.Rows);
                    }
                    r.MinimumCH1 = Math.Min(r.MinimumCH1, v); r.MaximumCH1 = Math.Max(r.MaximumCH1, v);
                    previous = high; r.LastTime = double.Parse(values[0], c); r.Rows++;
                }
                r.EndsHigh = previous; r.Header = header.ToArray();
                if (!data || r.Rows != r.DeclaredRows) throw new InvalidDataException("Record length mismatch.");
            }
            stream.Position = 0;
            using (var sha = SHA256.Create()) r.Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        var after = new FileInfo(path);
        if (after.Length != bytes || after.LastWriteTimeUtc != modified) throw new IOException("Input changed during diagnosis.");
        return r;
    }
}
'@
    $scan = [SweepReadDiagnosis]::Scan($InputPath, $ThresholdVoltage)
    $complete = [Math]::Max(0, $scan.RisingIndices.Count - 1)
    $metrics = [ordered]@{
        Status = 'COMPLETED'; InputPath = $InputPath; Sha256 = $scan.Sha256; FileBytes = $scan.FileBytes
        Header = $scan.Header; SampleCount = $scan.Rows; DeclaredSamples = $scan.DeclaredRows
        SampleIntervalSeconds = $scan.SampleIntervalSeconds; DurationSeconds = $scan.Rows * $scan.SampleIntervalSeconds
        OriginalFirstTime = $scan.FirstTime; OriginalLastRoundedTime = $scan.LastTime
        FirstCH1 = $scan.FirstCH1; MinimumCH1 = $scan.MinimumCH1; MaximumCH1 = $scan.MaximumCH1
        StartsHigh = $scan.StartsHigh; EndsHigh = $scan.EndsHigh
        RisingIndices = @($scan.RisingIndices.ToArray()); FallingIndices = @($scan.FallingIndices.ToArray())
        RisingSeconds = @($scan.RisingIndices | ForEach-Object { $_ * $scan.SampleIntervalSeconds })
        FallingSeconds = @($scan.FallingIndices | ForEach-Object { $_ * $scan.SampleIntervalSeconds })
        CompleteSweepCount = $complete; PhaseSamplesValidated = 0; FullOFDRRun = $false
        ElapsedSeconds = $timer.Elapsed.TotalSeconds; PeakWorkingSetBytes = [Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
    }
    Save-Json 'metrics.json' $metrics
    $summary = "# Sweep read diagnosis`n`nStatus: COMPLETED`n`nInput: $InputPath`nSHA-256: $($scan.Sha256)`n`n"
    $summary += "Samples: $($scan.Rows); duration: $($metrics.DurationSeconds) s; initial CH1 High: $($scan.StartsHigh).`n"
    $summary += "Observed rising indices: $($scan.RisingIndices -join ', '); falling indices: $($scan.FallingIndices -join ', ').`n"
    $summary += "Complete sweeps under the current observed-rise-to-next-rise rule: $complete.`n`n"
    $summary += "Only CH1 was scanned for trigger diagnosis. CH3 phase quality and end-to-end OFDR were not evaluated. No input or application code was modified.`n"
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'summary.md'), $summary, $utf8)
    [pscustomobject]$metrics | Select-Object Status, SampleCount, DurationSeconds, FirstCH1, StartsHigh, RisingIndices, RisingSeconds, FallingIndices, FallingSeconds, CompleteSweepCount, ElapsedSeconds | ConvertTo-Json
} catch {
    Save-Json 'metrics.json' ([ordered]@{ Status = 'FAILED'; Error = $_.Exception.ToString(); ElapsedSeconds = $timer.Elapsed.TotalSeconds })
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'summary.md'), "# Sweep read diagnosis`n`nStatus: FAILED`n`n$($_.Exception)`n", $utf8)
    throw
}
