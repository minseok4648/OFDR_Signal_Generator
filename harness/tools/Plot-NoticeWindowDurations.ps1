param([Parameter(Mandatory=$true)][string]$ComparisonDirectory)
$ErrorActionPreference='Stop'
$ComparisonDirectory=[IO.Path]::GetFullPath($ComparisonDirectory)
$data=Get-Content -LiteralPath (Join-Path $ComparisonDirectory 'plot_data.json') -Raw -Encoding UTF8|ConvertFrom-Json
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
$colors=@('#167F9B','#DC8B20','#C14D68')
function New-Chart([string]$title,[int]$height=1000){
    $c=New-Object Windows.Forms.DataVisualization.Charting.Chart
    $c.Width=1500;$c.Height=$height;$c.BackColor=[Drawing.Color]::White
    $t=$c.Titles.Add($title);$t.Font=New-Object Drawing.Font('Segoe UI',17)
    return $c
}
function Add-Area($chart,[string]$name,[int]$row,[double]$xmin,[double]$xmax,[double]$ymin,[double]$ymax,[string]$ytitle){
    $a=New-Object Windows.Forms.DataVisualization.Charting.ChartArea $name
    $a.Position.X=1;$a.Position.Y=7+29*$row;$a.Position.Width=98;$a.Position.Height=27
    $a.AxisX.Minimum=$xmin;$a.AxisX.Maximum=$xmax;$a.AxisY.Minimum=$ymin;$a.AxisY.Maximum=$ymax
    $a.AxisX.Title='Displayed distance (m), saved path setting 100 m';$a.AxisY.Title=$ytitle
    foreach($axis in @($a.AxisX,$a.AxisY)){
        $axis.MajorGrid.LineColor=[Drawing.Color]::Gainsboro;$axis.LabelStyle.Format='0.##'
        $axis.LabelStyle.Font=New-Object Drawing.Font('Segoe UI',10);$axis.TitleFont=New-Object Drawing.Font('Segoe UI',11)
    }
    $chart.ChartAreas.Add($a);return $a
}
function Add-Series($chart,[string]$name,[string]$area,[string]$color,[double[]]$x,[double[]]$y){
    $s=New-Object Windows.Forms.DataVisualization.Charting.Series $name
    $s.ChartType='Line';$s.ChartArea=$area;$s.Color=[Drawing.ColorTranslator]::FromHtml($color);$s.BorderWidth=2
    for($i=0;$i -lt $x.Length;$i++){
        $p=New-Object Windows.Forms.DataVisualization.Charting.DataPoint
        $p.XValue=$x[$i];$p.YValues=[double[]]@($y[$i]);[void]$s.Points.Add($p)
    }
    $chart.Series.Add($s);return $s
}
function X-Values($p){
    for($i=0;$i -lt $p.Values.Count;$i++){$p.XStart+($p.XEnd-$p.XStart)*$i/[math]::Max(1,$p.Values.Count-1)}
}
function Save-Chart($c,[string]$filename){
    try{$c.SaveImage((Join-Path $ComparisonDirectory $filename),[Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)}finally{$c.Dispose()}
}

$c=New-Chart 'Same-temperature repeatability: saved 20 / 40 / 168.08 ms results (all phase QC rejected)'
for($r=0;$r -lt 3;$r++){
    $h=$data.histories[$r];$s=$h.Summary
    [void](Add-Area $c "a$r" $r 0 50 -180 180 "$($h.Label): shift (GHz)")
    [void](Add-Series $c $h.Label "a$r" $colors[$r] (X-Values $h.Profile) $h.Profile.Values)
    $title=$c.Titles.Add(('{0}; 1024-bin gauge = {1:N3} m; Ref/Sig phase rejection = {2:N2}% / {3:N2}%' -f $h.Label,$s.GaugeM,$s.RefQcPercent,$s.SigQcPercent))
    $title.DockedToChartArea="a$r";$title.IsDockedInsideChartArea=$true;$title.Font=New-Object Drawing.Font('Segoe UI',11)
}
Save-Chart $c 'duration_profiles_common_scale.png'

$c=New-Chart 'Zoom of displayed 24-50 m: near zero does not establish a valid FUT measurement'
for($r=0;$r -lt 3;$r++){
    $h=$data.histories[$r]
    [void](Add-Area $c "a$r" $r 24 50 -5 5 "$($h.Label): shift (GHz)")
    [void](Add-Series $c $h.Label "a$r" $colors[$r] (X-Values $h.Profile) $h.Profile.Values)
}
Save-Chart $c 'duration_profiles_far_range_zoom.png'

$c=New-Chart 'Distance FFT preview envelopes: Ref blue, Sig orange (each normalized separately)'
for($r=0;$r -lt 3;$r++){
    $h=$data.histories[$r]
    [void](Add-Area $c "a$r" $r 0 50 -95 0 "$($h.Label): magnitude (dB)")
    foreach($role in @('ReferenceSpectrum','SignalSpectrum')){
        $p=$h.$role;$step=$h.Summary.DistanceBinM
        $n=[int][math]::Round($p.XEnd/$step)+1;$count=[int]($p.Values.Count/2)
        $x=New-Object double[] $count;$y=New-Object double[] $count
        for($i=0;$i -lt $count;$i++){
            $first=[math]::Floor([double]$i*$n/$count);$last=[math]::Floor([double]($i+1)*$n/$count)-1
            $x[$i]=($first+$last)*.5*$step;$y[$i]=[math]::Max($p.Values[2*$i],$p.Values[2*$i+1])
        }
        [void](Add-Series $c "$role$r" "a$r" $(if($role -eq 'ReferenceSpectrum'){'#167F9B'}else{'#DC8B20'}) $x $y)
    }
}
Save-Chart $c 'duration_distance_fft_envelopes.png'

$c=New-Chart 'Mode-hop-free sampled windows: raw phase QC worsens late; filtered control passes' 650
$a=Add-Area $c 'qc' 0 0.4 6.6 0 20 'Rejected anchors (%)'
$a.Position.Y=12;$a.Position.Height=72;$a.AxisX.Title='Original record time / file (0.8192 ms sampled at each position)';$a.AxisX.Interval=1
$a.AxisX.Minimum=0;$a.AxisX.Maximum=7;$a.AxisX.LabelStyle.IsEndLabelVisible=$true
foreach($w in 1..3){foreach($role in 0..1){
    $row=$data.bounded_phase|Where-Object{$_.Window -eq $w -and $_.Role -eq $role -and $_.Variant -eq 'raw100'}
    $label=New-Object Windows.Forms.DataVisualization.Charting.CustomLabel
    $center=($w-1)*2+$role+1;$label.FromPosition=$center-.5;$label.ToPosition=$center+.5
    $label.Text="$($row.StartMs) ms / $(if($role -eq 0){'Ref'}else{'Sig'})";$a.AxisX.CustomLabels.Add($label)
}}
foreach($variant in @('raw100','filtered20')){
    $series=New-Object Windows.Forms.DataVisualization.Charting.Series $variant
    $series.ChartType='Column';$series.ChartArea='qc';$series.Color=[Drawing.ColorTranslator]::FromHtml($(if($variant -eq 'raw100'){'#C14D68'}else{'#167F9B'}))
    $series.IsValueShownAsLabel=$true;$series.LabelFormat='0.##'
    foreach($w in 1..3){foreach($role in 0..1){
        $row=$data.bounded_phase|Where-Object{$_.Window -eq $w -and $_.Role -eq $role -and $_.Variant -eq $variant}
        $p=New-Object Windows.Forms.DataVisualization.Charting.DataPoint
        $p.XValue=($w-1)*2+$role+1;$p.YValues=[double[]]@($row.QcPercent)
        $p.AxisLabel="$($row.StartMs) ms / $(if($role -eq 0){'Ref'}else{'Sig'})";[void]$series.Points.Add($p)
    }}
    $c.Series.Add($series)
}
$legend=New-Object Windows.Forms.DataVisualization.Charting.Legend
$legend.Docking='Bottom';$legend.Font=New-Object Drawing.Font('Segoe UI',11);$c.Legends.Add($legend)
Save-Chart $c 'bounded_phase_qc.png'
'Created four charts from saved results; no original data reread.'
