param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $RunDirectory 'metrics.json'))) { throw 'A completed comparison run is required.' }
$artifactDirectory = Join-Path $RunDirectory 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
$sampling = @()
foreach ($window in 0..2) {
    foreach ($role in @('reference','signal')) {
        foreach ($variant in @('continuous','filtered_20ms','unfiltered_2.5ms_offset0','filtered_2.5ms')) {
            $path = Join-Path $RunDirectory "notice_high/w${window}_${role}_${variant}.json"
            $item = Read-Json $path
            $sampling += [pscustomobject]@{
                Window=$window+1; Role=$role; Variant=$variant; Samples=$item.Phase.SampleCount
                QcPercent=100*$item.Phase.NonmonotonicRatio; InteriorQcPercent=100*$item.Values.interior_nonmonotonic_ratio
                FringeMHz=$item.Values.phase_average_hz/1e6; PowerRetainedPercent=100*$item.Values.ac_power_retained_fraction
                PhaseErrorRmsRad=$item.Values.phase_error_rms_rad; PhaseErrorP95Rad=$item.Values.phase_error_p95_abs_rad
                SourceP99MHz=$item.Values.spectral_p99_hz/1e6; Decision=$item.Decision
            }
        }
    }
}
$sampling | Export-Csv -LiteralPath (Join-Path $artifactDirectory 'sampling_comparison.csv') -NoTypeInformation -Encoding UTF8
$pairs = foreach ($group in @('etri','notice_high','notice_low')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $RunDirectory $group) -Filter '*_pair.json') {
        $item = Read-Json $file.FullName
        [pscustomobject]@{ Dataset=$group; Experiment=$file.BaseName; PathMeters=$item.Config.AuxiliaryPathDifferenceMeters
            GaugeMeters=$item.Metrics.gauge_m; HopMeters=$item.Metrics.hop_m; RefQcPercent=100*$item.Metrics.reference_phase_qc_ratio
            SigQcPercent=100*$item.Metrics.signal_phase_qc_ratio; OpticalSpanGHz=$item.Metrics.available_optical_span_ghz
            EffectiveMaximumShiftGHz=$item.Metrics.effective_maximum_shift_ghz; MedianCorrelation=$item.Metrics.correlation_peak_median
            MedianPeakSeparation=$item.Metrics.peak_separation_median; MedianShiftGHz=$item.Metrics.shift_median_ghz
            SearchBoundaryFraction=$item.Metrics.boundary_peak_fraction; AbsoluteFrequencyAlignmentVerified=$item.Metrics.absolute_frequency_alignment_verified }
    }
}
$pairs | Export-Csv -LiteralPath (Join-Path $artifactDirectory 'pair_comparison.csv') -NoTypeInformation -Encoding UTF8
$sources = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $RunDirectory 'sources') -Filter '*.json') {
    $item = Read-Json $file.FullName
    [pscustomobject]@{ Source=$item.Path; FsMSps=1e-6/$item.SampleIntervalSeconds; DeclaredSamples=$item.DeclaredSamples
        NumericSamples=$item.NumericSamples; TriggerRowsScanned=$item.TriggerRowsScanned; SHA256=$item.Sha256
        Ranges=($item.SourceRanges | ForEach-Object { "[$($_[0]),$($_[1]))" }) -join '; ' }
}
$sources | Export-Csv -LiteralPath (Join-Path $artifactDirectory 'source_scope.csv') -NoTypeInformation -Encoding UTF8

# Standard .NET chart renderer; no raw signal samples are reread to make these figures.
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
function Save-Chart([string]$name, [string]$title, [string]$yTitle, [string[]]$variants, [string[]]$labels, [string]$field) {
    $chart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
    try {
        $chart.Width=1400; $chart.Height=650; $chart.BackColor=[Drawing.Color]::White
        $area=New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea 'main'
        $area.AxisX.Interval=1; $area.AxisX.MajorGrid.Enabled=$false
        $area.AxisY.Title=$yTitle; $area.AxisY.Minimum=0; $area.AxisY.LabelStyle.Format='0.##'
        $area.AxisY.MajorGrid.LineColor=[Drawing.Color]::LightGray
        $area.AxisX.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',12)
        $area.AxisY.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',11)
        $chart.ChartAreas.Add($area)
        $heading=$chart.Titles.Add($title); $heading.Font=New-Object Drawing.Font('Segoe UI',16)
        $legend=New-Object System.Windows.Forms.DataVisualization.Charting.Legend
        $legend.Docking='Bottom'; $legend.Font=New-Object Drawing.Font('Segoe UI',11); $chart.Legends.Add($legend)
        $colors=@([Drawing.Color]::FromArgb(0,104,171),[Drawing.Color]::FromArgb(0,150,120),[Drawing.Color]::FromArgb(215,90,45))
        for($v=0;$v -lt $variants.Length;$v++) {
            $series=New-Object System.Windows.Forms.DataVisualization.Charting.Series $labels[$v]
            $series.ChartType='Column'; $series.Color=$(if($variants[$v] -eq 'filtered_20ms'){$colors[1]}elseif($variants[$v] -like '*2.5ms*'){$colors[2]}else{$colors[0]}); $series.IsValueShownAsLabel=$true; $series.LabelFormat='0.##'
            foreach($window in 1..3) { foreach($role in @('reference','signal')) {
                $row=$sampling | Where-Object { $_.Window -eq $window -and $_.Role -eq $role -and $_.Variant -eq $variants[$v] }
                $point=New-Object System.Windows.Forms.DataVisualization.Charting.DataPoint
                $point.XValue=$series.Points.Count+1; $point.YValues=[double[]]@([double]$row.$field)
                $point.AxisLabel="W$window $(if($role -eq 'reference'){'Ref'}else{'Sig'})"
                [void]$series.Points.Add($point)
            }}
            $chart.Series.Add($series)
        }
        $chart.SaveImage((Join-Path $artifactDirectory $name),[Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)
    } finally { $chart.Dispose() }
}
Save-Chart 'phase_qc_comparison.png' 'Same source windows: auxiliary phase QC after sampling controls' 'Interior rejected samples (%)' @('continuous','filtered_20ms','unfiltered_2.5ms_offset0') @('Historical 50 MS/s','Virtual 20 MS/s (filtered)','Virtual 2.5 MS/s (unfiltered)') 'InteriorQcPercent'
Save-Chart 'retained_signal_power.png' 'Anti-alias filtering: retained auxiliary AC power' 'Power retained (%)' @('filtered_20ms','filtered_2.5ms') @('Virtual 20 MS/s','Virtual 2.5 MS/s') 'PowerRetainedPercent'
@'
# Acquisition comparison tables

- `source_scope.csv`: original paths, SHA-256, headers' sampling rates and exact numeric ranges.
- `sampling_comparison.csv`: same-source sampling controls, phase errors and remaining AC power.
- `pair_comparison.csv`: production-pipeline diagnostic correlations, physical window widths and available optical span.
- PNGs are generated from saved metrics; no additional raw samples are read.
- Virtual 20 MS/s includes a known anti-alias filter; it is not a physical-scope measurement.
- Completed processing and a visually flat profile are not evidence of calibrated strain or temperature.
'@ | Set-Content -LiteralPath (Join-Path $artifactDirectory 'README.md') -Encoding UTF8
Write-Output $artifactDirectory
