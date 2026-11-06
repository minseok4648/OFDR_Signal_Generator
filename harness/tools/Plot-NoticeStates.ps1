param([Parameter(Mandatory=$true)][string]$ComparisonDirectory,
    [string]$StateComparisonTitle='Saved 15 ms results: within-state vs Heating-Cooling (H = Heating, C = Cooling)',
    [string]$WithinPairName='pair_0_1_raw', [string]$WithinPairLabel='H1-H2',
    [string]$BetweenPairName='pair_0_3_raw', [string]$BetweenPairLabel='H1-C1')
$ErrorActionPreference='Stop'
$ComparisonDirectory=[IO.Path]::GetFullPath($ComparisonDirectory)
$data=Get-Content -LiteralPath (Join-Path $ComparisonDirectory 'plot_data.json') -Raw -Encoding UTF8|ConvertFrom-Json
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
$colors=@('#167F9B','#38885D','#7662A8','#D15A3C','#DB9B20')
function Chart([string]$title,[int]$height=1000){
    $c=New-Object Windows.Forms.DataVisualization.Charting.Chart;$c.Width=1500;$c.Height=$height;$c.BackColor=[Drawing.Color]::White
    $t=$c.Titles.Add($title);$t.Font=New-Object Drawing.Font('Segoe UI',16)
    $l=New-Object Windows.Forms.DataVisualization.Charting.Legend;$l.Docking='Bottom';$l.Font=New-Object Drawing.Font('Segoe UI',10);$c.Legends.Add($l)
    return $c
}
function Area($c,[string]$id,[int]$row,[double]$xmin,[double]$xmax,[double]$ymin,[double]$ymax,[string]$ylabel,[bool]$single=$false){
    $a=New-Object Windows.Forms.DataVisualization.Charting.ChartArea $id
    $a.Position.X=1;$a.Position.Y=7+28*$row;$a.Position.Width=98;$a.Position.Height=$(if($single){78}else{26})
    $a.AxisX.Minimum=$xmin;$a.AxisX.Maximum=$xmax;$a.AxisY.Minimum=$ymin;$a.AxisY.Maximum=$ymax
    $a.AxisX.Title='Provisional distance (m), travel-path difference 200 m';$a.AxisY.Title=$ylabel
    foreach($ax in @($a.AxisX,$a.AxisY)){$ax.MajorGrid.LineColor=[Drawing.Color]::Gainsboro;$ax.LabelStyle.Format='0.##';$ax.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',10);$ax.TitleFont=New-Object Drawing.Font('Segoe UI',11)}
    $c.ChartAreas.Add($a);return $a
}
function Series($c,[string]$id,[string]$area,[string]$color,$x,$y,[string]$legend=''){
    $s=New-Object Windows.Forms.DataVisualization.Charting.Series $id;$s.ChartType='Line';$s.ChartArea=$area;$s.Color=[Drawing.ColorTranslator]::FromHtml($color);$s.BorderWidth=2
    if($legend){$s.LegendText=$legend}else{$s.IsVisibleInLegend=$false}
    for($i=0;$i -lt $x.Count;$i++){$p=New-Object Windows.Forms.DataVisualization.Charting.DataPoint;$p.XValue=$x[$i];$p.YValues=[double[]]@($y[$i]);[void]$s.Points.Add($p)}
    $c.Series.Add($s)
}
function Save($c,[string]$name){try{$c.SaveImage((Join-Path $ComparisonDirectory $name),[Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)}finally{$c.Dispose()}}

$c=Chart $StateComparisonTitle
[void](Area $c 'same' 0 0 48 -45 45 'Within: shift (GHz)')
[void](Area $c 'different' 1 0 48 -45 45 'Between: shift (GHz)')
[void](Area $c 'zoom' 2 8 24 -3 3 'Zoom: shift (GHz)')
$i=0
foreach($h in $data.Histories|Where-Object{$_.Kind -in @('within','between')}){
    Series $c $h.Label $(if($h.Kind -eq 'within'){'same'}else{'different'}) $colors[$i] $h.X $h.Y $h.Label
    Series $c "$($h.Label)-zoom" 'zoom' $colors[$i] $h.X $h.Y
    $i++
}
Save $c 'same_vs_different_saved_profiles.png'

$c=Chart 'Bounded 4.6 ms: correlation peaks alone do not establish temperature discrimination' 650
[void](Area $c 'corr' 0 0 48 0 1.02 'Normalized correlation peak' $true)
$specs=@(@($WithinPairName,$WithinPairLabel,'#167F9B'),@($BetweenPairName,$BetweenPairLabel,'#D15A3C'),@('control_independent_stationary_noise','Independent noise control','#A0A0A0'))
foreach($s in $specs){$p=$data.Bounded|Where-Object{$_.Label -eq $s[0]};Series $c $s[0] 'corr' $s[2] $p.X $p.Correlation $s[1]}
Save $c 'bounded_correlation_controls.png'

$c=Chart 'Positive control: imposed +18.7 GHz in a known 2 m segment; original CSVs not used' 650
[void](Area $c 'shift' 0 8 22 -1 20 'Recovered spectral shift (GHz)' $true)
Series $c 'synthetic' 'shift' '#167F9B' $data.Synthetic.DistancesMeters $data.Synthetic.SpectralShiftGHz 'Recovered shift'
Series $c 'expected' 'shift' '#D15A3C' @(8,12,12,14,14,22) @(0,0,18.7,18.7,0,0) 'Imposed shift'
$c.Series['expected'].BorderDashStyle='Dash'
Save $c 'synthetic_known_shift_control.png'

$c=Chart 'CH4: energy concentrated around the strongest distance-FFT peak (bounded original windows)' 650
$a=Area $c 'energy' 0 0 6 0 100 'Energy within peak +/- 0.25 m (%)' $true
$a.AxisX.Title='Acquisition';$a.AxisX.Interval=1
$s=New-Object Windows.Forms.DataVisualization.Charting.Series 'Energy';$s.ChartType='Column';$s.ChartArea='energy';$s.Color=[Drawing.ColorTranslator]::FromHtml('#167F9B');$s.IsVisibleInLegend=$false;$s.IsValueShownAsLabel=$true;$s.LabelFormat='0.0'
$i=1
foreach($r in $data.Sources){
    $p=New-Object Windows.Forms.DataVisualization.Charting.DataPoint;$p.XValue=$i;$p.YValues=[double[]]@([double]$r.'energy_fraction_within_peak_plusminus_0.25m'*100);[void]$s.Points.Add($p)
    $label=New-Object Windows.Forms.DataVisualization.Charting.CustomLabel;$label.FromPosition=$i-.5;$label.ToPosition=$i+.5;$label.Text=$r.File.Replace('.csv','');$a.AxisX.CustomLabels.Add($label);$i++
}
$c.Series.Add($s);Save $c 'dominant_distance_energy.png'
'Created four verified-data plots; no original CSV reread.'
