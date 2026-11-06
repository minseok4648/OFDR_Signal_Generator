param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
$out = Join-Path $RunDirectory 'artifacts'
New-Item -ItemType Directory -Path $out -Force | Out-Null
function Read-Json([string]$name) { Get-Content -LiteralPath (Join-Path $RunDirectory $name) -Raw -Encoding UTF8 | ConvertFrom-Json }
$rows = foreach ($role in 0..1) { foreach ($window in 1..3) {
    $context = Read-Json "file${role}_w${window}_context.json"
    foreach ($variant in @('raw500','filtered50','filtered20','unfiltered50','unfiltered20','unfiltered2.5','filtered2.5')) {
        $j = Read-Json "file${role}_w${window}_${variant}_aux.json"
        [pscustomobject]@{File=$(if($role -eq 0){'tek0028'}else{'tek0029'});Role=$role;Window=$window;Variant=$variant
            StartIndex=$context.start_index;EndExclusive=$context.end_index_exclusive;ModeHopFraction=$context.mode_hop_high_fraction
            Samples=$j.Phase.SampleCount;QcPercent=100*$j.Phase.NonmonotonicRatio;InteriorQcPercent=100*$j.Values.interior_nonmonotonic_ratio
            PeakMHz=$j.Values.spectral_peak_hz/1e6;PhaseMeanMHz=$j.Values.phase_average_hz/1e6;AcRmsMillivolts=1000*$j.Values.ac_rms_v
            Above10MHzPercent=100*$j.Values.energy_above_10mhz_fraction;PowerRetainedPercent=100*$j.Values.ac_power_retained_fraction
            Decision=$j.Decision}
    }
}}
$rows | Export-Csv -LiteralPath (Join-Path $out 'sampling_comparison.csv') -NoTypeInformation -Encoding UTF8
$pairs = foreach ($file in Get-ChildItem -LiteralPath $RunDirectory -Filter '*.json' | Where-Object {$_.BaseName -match '^(pair_w|control_mismatched)'}) {
    $j=Read-Json $file.Name
    [pscustomobject]@{Name=$file.BaseName;QcPassed=$j.Metrics.phase_qc_passed;SpanGHz=$j.Metrics.available_optical_span_ghz
        SearchHalfWidthGHz=$j.Metrics.effective_maximum_shift_ghz;GaugeMeters=$j.Metrics.gauge_m;GaugeBins=$j.Config.SegmentSize
        CorrelationMedian=$j.Metrics.correlation_peak_median;SeparationAvailableSegments=$j.Metrics.peak_separation_available_segments
        Segments=$j.Metrics.segments;ShiftP10GHz=$j.Metrics.shift_p10_ghz;ShiftMedianGHz=$j.Metrics.shift_median_ghz;ShiftP90GHz=$j.Metrics.shift_p90_ghz
        BoundaryFraction=$j.Metrics.boundary_peak_fraction;AbsoluteOpticalAlignmentVerified=0;CalibratedTemperatureOrStrain=0}
}
$pairs | Export-Csv -LiteralPath (Join-Path $out 'pair_comparison.csv') -NoTypeInformation -Encoding UTF8
$sources=foreach($i in 0..1){$s=Read-Json "source_$i.json";[pscustomobject]@{Path=$s.Path;Sha256=$s.Sha256;NumericSamples=$s.NumericSamples;DeclaredSamples=$s.DeclaredSamples;SampleIntervalSeconds=$s.SampleIntervalSeconds}}
$sources | Export-Csv -LiteralPath (Join-Path $out 'source_scope.csv') -NoTypeInformation -Encoding UTF8
$quantization = foreach($role in 0..1){foreach($w in 1..3){
    $j=Read-Json "file${role}_w${w}_waveform_preview.json"
    foreach($channel in @('aux','sig')){
        $values=@($j."500MS_${channel}_v" | Sort-Object -Unique)
        $deltas=for($i=1;$i -lt $values.Count;$i++){$values[$i]-$values[$i-1]}
        [pscustomobject]@{Role=$role;Window=$w;Channel=$channel;PreviewSamples=$j."500MS_${channel}_v".Count
            MinimumVoltage=$values[0];MaximumVoltage=$values[-1];DistinctPreviewLevels=$values.Count
            MinimumPositiveVoltageStep=($deltas|Measure-Object -Minimum).Minimum;Scope='First 10 us of already selected window; not full-record vertical-range validation'}
    }
}}
$quantization | Export-Csv -LiteralPath (Join-Path $out 'voltage_levels_preview.csv') -NoTypeInformation -Encoding UTF8

Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
function New-Chart([string]$title,[string]$xTitle,[string]$yTitle) {
    $c=New-Object Windows.Forms.DataVisualization.Charting.Chart
    $c.Width=1400;$c.Height=650;$c.BackColor=[Drawing.Color]::White
    $a=New-Object Windows.Forms.DataVisualization.Charting.ChartArea 'main'
    $a.AxisX.Title=$xTitle;$a.AxisY.Title=$yTitle
    $a.AxisX.LabelStyle.Format='0.##';$a.AxisY.LabelStyle.Format='0.##'
    $a.AxisX.MajorGrid.LineColor=[Drawing.Color]::Gainsboro;$a.AxisY.MajorGrid.LineColor=[Drawing.Color]::Gainsboro
    $a.AxisX.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',11);$a.AxisY.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',11)
    $a.AxisX.TitleFont=New-Object Drawing.Font('Segoe UI',12);$a.AxisY.TitleFont=New-Object Drawing.Font('Segoe UI',12)
    $c.ChartAreas.Add($a);$h=$c.Titles.Add($title);$h.Font=New-Object Drawing.Font('Segoe UI',16)
    $l=New-Object Windows.Forms.DataVisualization.Charting.Legend;$l.Docking='Bottom';$l.Font=New-Object Drawing.Font('Segoe UI',11);$c.Legends.Add($l)
    return $c
}
function New-Series([string]$label,[string]$color,[string]$type='Line') {
    $s=New-Object Windows.Forms.DataVisualization.Charting.Series $label;$s.ChartType=$type;$s.Color=[Drawing.ColorTranslator]::FromHtml($color);$s.BorderWidth=2;return $s
}
function Add-Point($series,[double]$x,[double]$y,[string]$label='') {
    $p=New-Object Windows.Forms.DataVisualization.Charting.DataPoint;$p.XValue=$x;$p.YValues=[double[]]@($y);$p.AxisLabel=$label;[void]$series.Points.Add($p)
}
function Save-Chart($chart,[string]$name) { try {$chart.SaveImage((Join-Path $out $name),[Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)} finally {$chart.Dispose()} }
$chart=New-Chart 'Phase QC on mode-hop-free windows (same original samples)' 'Window / file: starts at 5, 20, 35 ms' 'Rejected anchors (%)'
$chart.ChartAreas[0].AxisX.Interval=1;$chart.ChartAreas[0].AxisY.Minimum=0;$chart.ChartAreas[0].AxisY.Maximum=85
$variants=@('raw500','unfiltered20','filtered20');$labels=@('500 MS/s original','20 MS/s: skip only','20 MS/s: filter then resample');$colors=@('#376996','#E18C37','#189984')
for($v=0;$v -lt 3;$v++){
    $s=New-Series $labels[$v] $colors[$v] 'Column';$s.IsValueShownAsLabel=$true;$s.LabelFormat='0.##'
    foreach($w in 1..3){foreach($r in 0..1){$row=$rows|Where-Object{$_.Window -eq $w -and $_.Role -eq $r -and $_.Variant -eq $variants[$v]};Add-Point $s ($s.Points.Count+1) $row.QcPercent "W$w $($row.File)"}}
    $chart.Series.Add($s)
}
Save-Chart $chart 'phase_qc_comparison.png'

$preview=Read-Json 'file1_w1_waveform_preview.json'
$chart=New-Chart 'tek0029: weak auxiliary signal, W1 (diagnostic filter control)' 'Time from selected window start (us)' 'CH3 (mV)'
$chart.ChartAreas[0].AxisX.Minimum=3.2;$chart.ChartAreas[0].AxisX.Maximum=4.2
foreach($rate in @(500,20)){
    $s=New-Series "$rate MS/s $(if($rate -eq 500){'original'}else{'filtered control'})" $(if($rate -eq 500){'#A7B6C2'}else{'#087F77'})
    $x=$preview."${rate}MS_time_us";$y=$preview."${rate}MS_aux_v"
    for($i=0;$i -lt $x.Count;$i++){if($x[$i] -ge 3.2 -and $x[$i] -le 4.2){Add-Point $s $x[$i] (1000*$y[$i])}}
    $chart.Series.Add($s)
}
Save-Chart $chart 'auxiliary_noise_control.png'

$chart=New-Chart 'Distance FFT after independent k-linearization: W2, filtered 20 MS/s' 'Provisional distance (m), auxiliary travel-path difference 200 m' 'Normalized magnitude (dB)'
$chart.ChartAreas[0].AxisX.Minimum=0;$chart.ChartAreas[0].AxisX.Maximum=50;$chart.ChartAreas[0].AxisY.Minimum=-80;$chart.ChartAreas[0].AxisY.Maximum=0
$pair=Read-Json 'pair_w2_filtered20.json';$role=0
foreach($name in @('ReferenceDistanceFft','SignalDistanceFft')){
    $s=New-Series $(if($role -eq 0){'tek0028'}else{'tek0029'}) $(if($role -eq 0){'#376996'}else{'#C76C35'})
    $p=$pair.$name;for($i=0;$i -lt $p.Values.Count;$i++){$x=$p.XStart+($p.XEnd-$p.XStart)*$i/($p.Values.Count-1);if($x -le 50){Add-Point $s $x $p.Values[$i]}}
    $chart.Series.Add($s);$role++
}
Save-Chart $chart 'distance_fft_diagnostic.png'

$chart=New-Chart 'Short-window spectral shift: insufficient bandwidth for a temperature/strain claim' 'Provisional distance (m)' 'Diagnostic shift (GHz)'
$chart.ChartAreas[0].AxisX.Minimum=0;$chart.ChartAreas[0].AxisX.Maximum=30;$chart.ChartAreas[0].AxisY.Minimum=-.45;$chart.ChartAreas[0].AxisY.Maximum=.45
foreach($w in 1..3){$j=Read-Json "pair_w${w}_filtered20.json";$s=New-Series "W$w" $colors[$w-1];for($i=0;$i -lt $j.DistancesMeters.Count;$i++){if($j.DistancesMeters[$i] -le 30){Add-Point $s $j.DistancesMeters[$i] $j.SpectralShiftGHz[$i]}};$chart.Series.Add($s)}
Save-Chart $chart 'short_window_shift_not_calibrated.png'
@'
# Partial 500 MS/s analysis artifacts

Figures and tables use the recorded bounded-window metrics only. No original CSV is reread here.
QC is rejected running-maximum phase anchors, not waveform clipping or a temperature error percentage.
20/50 MS/s variants are diagnostic anti-alias-filtered resampling, not new physical acquisitions.
The spectral-shift figure is NOT accepted temperature or strain: optical span is below 1 GHz,
each distance gauge contains only four FFT bins, optical start frequency and temperature delta are unknown.
The mismatched-time control also yields a high correlation peak, so peak height alone is not acceptance.
All distances use the provisional 200 m travel-path difference. The strongest peak is not a verified FUT endpoint.
'@ | Set-Content -LiteralPath (Join-Path $out 'README.md') -Encoding UTF8
Write-Output $out
