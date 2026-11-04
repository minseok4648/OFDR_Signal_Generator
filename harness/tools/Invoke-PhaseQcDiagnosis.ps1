param(
    [string]$InputRunDirectory,
    [string]$ResultsDirectory,
    [int]$MaximumSamples = 250000
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $InputRunDirectory) {
    $InputRunDirectory = (Get-ChildItem -LiteralPath (Join-Path $workspace 'OFDR_Signal_Generator\bin\Debug\harness\results') -Directory -Filter '*prepared_ofdr_analysis*' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
}
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $workspace ('harness\results\' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff') + '_phase_qc_diagnosis') }
if ($MaximumSamples -lt 4096 -or $MaximumSamples -gt 250000) { throw 'MaximumSamples must be 4096..250000' }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
@{ operation='matched raw/stitched phase QC diagnosis'; input_run=$InputRunDirectory; maximum_unique_raw_samples_per_role=$MaximumSamples; production_settings_changed=$false; full_dataset_validation=$false; interpolation='diagnostic control only, not a production correction' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'config.json') -Encoding utf8
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $buildDir = Join-Path $workspace 'build_verify_four_channel'
    $env:PATH = $buildDir + ';' + $env:PATH
    [IO.Directory]::SetCurrentDirectory($buildDir)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $buildDir 'MathNet.Numerics.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $buildDir 'MathNet.Numerics.Providers.MKL.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $buildDir 'OFDR_Signal_Core.dll'))
    Add-Type -ReferencedAssemblies (Join-Path $buildDir 'OFDR_Signal_Core.dll'),(Join-Path $buildDir 'MathNet.Numerics.dll'),'System.Numerics' -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
public static class PhaseQcDiagnosis {
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    static double D(string s) { return double.Parse(s, C); }
    static double Q(double[] sorted, double q) { return sorted[(int)((sorted.Length-1)*q)]; }
    static double[] BaselineRemoved(double[] values) {
        var sums=new double[values.Length+1];for(int i=0;i<values.Length;i++)sums[i+1]=sums[i]+values[i];var output=new double[values.Length];
        for(int i=0;i<values.Length;i++){int a=Math.Max(0,i-128),b=Math.Min(values.Length,i+129);output[i]=values[i]-(sums[b]-sums[a])/(b-a);}return output;
    }
    static object Analyze(double[] values, int[] joins, double dt) {
        var fft = new FftEngine();
        var type = typeof(FftEngine).Assembly.GetType("OFDR_Signal_Core.Processing.AuxiliaryPhaseExtractor");
        var result = type.GetMethod("Extract", BindingFlags.Public|BindingFlags.Static).Invoke(null, new object[]{fft,values,0,CancellationToken.None,null,true});
        var rt = result.GetType(); var phase = (double[])rt.GetField("Phase").GetValue(result);
        var analytic = (Complex[])rt.GetField("Analytic").GetValue(result); var m = (KLinearizationMetrics)rt.GetField("Metrics").GetValue(result);
        var amplitude = analytic.Select(x=>x.Magnitude).ToArray(); var sorted = amplitude.OrderBy(x=>x).ToArray(); var p25=Q(sorted,.25);
        int rejected=0,negative=0,interior=0,interiorRejected=0,away=0,awayRejected=0,low=0,lowRejected=0,near8Rejected=0,near512Rejected=0,j=0,edge=Math.Min(512,values.Length/8);
        double last=phase[0]; var principal=new double[values.Length-1];
        for(int i=1;i<values.Length;i++) {
            var reject=phase[i]<=last; if(reject)rejected++;else last=phase[i];
            principal[i-1]=phase[i]-phase[i-1]; if(principal[i-1]<=0)negative++;
            if(amplitude[i]<=p25){low++;if(reject)lowRejected++;}
            while(j+1<joins.Length && joins[j+1]<=i)j++;
            int distance=int.MaxValue;
            if(joins.Length>0){distance=Math.Abs(i-joins[j]);if(j+1<joins.Length)distance=Math.Min(distance,Math.Abs(i-joins[j+1]));}
            if(reject&&distance<=8)near8Rejected++;if(reject&&distance<=512)near512Rejected++;
            if(i>=edge&&i<values.Length-edge){interior++;if(reject)interiorRejected++;if(distance>512){away++;if(reject)awayRejected++;}}
        }
        if(Math.Abs(rejected/(double)(values.Length-1)-m.NonmonotonicRatio)>1e-12)throw new InvalidOperationException("Anchor rejection diagnostic does not match production QC");
        Array.Sort(principal);
        // Spectral quantities are diagnostic summaries, with no filter applied to production data.
        var spectrum=new Complex[values.Length];var mean=values.Average();
        for(int i=0;i<values.Length;i++)spectrum[i]=new Complex((values[i]-mean)*(.5-.5*Math.Cos(2*Math.PI*i/(values.Length-1))),0);
        fft.ForwardInPlace(spectrum);double energy=0,nearNyquist=0;int peak=1;var bands=new double[5];
        for(int i=1;i<spectrum.Length/2;i++){var power=spectrum[i].Magnitude*spectrum[i].Magnitude;energy+=power;bands[Math.Min(4,(int)(10.0*i/spectrum.Length))]+=power;if(i>=spectrum.Length*.4)nearNyquist+=power;if(spectrum[i].Magnitude>spectrum[peak].Magnitude)peak=i;}
        double peakEnergy=0;for(int i=Math.Max(1,peak-2);i<=Math.Min(spectrum.Length/2-1,peak+2);i++)peakEnergy+=spectrum[i].Magnitude*spectrum[i].Magnitude;
        for(int i=0;i<bands.Length;i++)bands[i]/=energy;
        return new {Samples=values.Length,Phase=m,AdjacentNonpositiveRatio=negative/(double)(values.Length-1),InteriorAnchorRejectionRatio=interiorRejected/(double)Math.Max(1,interior),
            JoinCount=joins.Length,AnchorRejections=rejected,RejectedWithin8OfJoin=near8Rejected,RejectedWithin512OfJoin=near512Rejected,AwayFromJoins512Samples=away,AwayFromJoins512RejectionRatio=awayRejected/(double)Math.Max(1,away),
            AnalyticMagnitudeP01=Q(sorted,.01),AnalyticMagnitudeP25=p25,AnalyticMagnitudeMedian=Q(sorted,.5),AnalyticMagnitudeP99=Q(sorted,.99),LowestAmplitudeQuartileRejectionRatio=lowRejected/(double)Math.Max(1,low),RejectionsInLowestAmplitudeQuartileFraction=lowRejected/(double)Math.Max(1,rejected),
            InteriorEdgeMargin=edge,PhaseIncrementP01=Q(principal,.01),PhaseIncrementMedian=Q(principal,.5),PhaseIncrementP99=Q(principal,.99),DominantFrequencyHz=peak/(dt*spectrum.Length),PowerAbove0p4FsFraction=nearNyquist/energy,FiveEqualNyquistBandPowerFractions=bands,PeakFiveBinPowerFraction=peakEnergy/energy,Provider=fft.ProviderDescription};
    }
    public static object Run(string path,int first,int count,double dt,string expectedSha,string exportPath) {
        var before=new FileInfo(path);var bytes=before.Length;var modified=before.LastWriteTimeUtc;
        var aux=new double[count];var trigger=new double[count];var measurement=new double[count];int row=0,loaded=0;
        var header=new Dictionary<string,string>();
        using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(input)){string line;bool data=false;while((line=reader.ReadLine())!=null){
            if(!data){if(line.StartsWith("TIME,CH1,CH2,CH3,CH4"))data=true;else{int k=line.IndexOf(',');if(k>0)header[line.Substring(0,k)]=line.Substring(k+1);}continue;}
            if(string.IsNullOrWhiteSpace(line))continue;
            if(row>=first){var cells=line.Split(',');trigger[loaded]=D(cells[2]);aux[loaded]=D(cells[3]);measurement[loaded]=D(cells[4]);loaded++;if(loaded==count)break;}row++;
        }}if(loaded!=count)throw new InvalidDataException("Insufficient samples in requested source range");
        string hash;using(var sha=SHA256.Create())using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        var after=new FileInfo(path);if(bytes!=after.Length||modified!=after.LastWriteTimeUtc||!hash.Equals(expectedSha,StringComparison.OrdinalIgnoreCase))throw new IOException("Source changed or differs from applied dataset SHA-256");
        var keep=new List<double>();var originals=new List<int>();var joins=new List<int>();int prev=-2;
        for(int i=0;i<count;i++)if(trigger[i]<=1.8){if(keep.Count>0&&i!=prev+1)joins.Add(keep.Count);keep.Add(aux[i]);originals.Add(first+i);prev=i;}
        var interpolation=(double[])aux.Clone();int cursor=0;
        while(cursor<count){if(trigger[cursor]<=1.8){cursor++;continue;}int a=cursor;while(cursor<count&&trigger[cursor]>1.8)cursor++;int b=cursor;var left=a>0?aux[a-1]:b<count?aux[b]:0;var right=b<count?aux[b]:left;for(int i=a;i<b;i++)interpolation[i]=left+(right-left)*(i-a+1)/(b-a+1.0);}
        var spans=new List<Tuple<int,int>>();cursor=0;while(cursor<count){while(cursor<count&&trigger[cursor]>1.8)cursor++;int start=cursor;while(cursor<count&&trigger[cursor]<=1.8)cursor++;if(cursor-start>=2048)spans.Add(Tuple.Create(start,cursor-start));}
        var clean=new List<object>();foreach(var span in spans.OrderByDescending(x=>x.Item2).Take(3)){var values=new double[span.Item2];Array.Copy(aux,span.Item1,values,0,values.Length);var center=new double[512];Array.Copy(values,(values.Length-center.Length)/2,center,0,center.Length);clean.Add(new{SourceFirstIndex=first+span.Item1,Length=span.Item2,Analysis=Analyze(values,new int[0],dt),ShortCenter512=Analyze(center,new int[0],dt),BaselineRemovedDiagnosticOnly=Analyze(BaselineRemoved(values),new int[0],dt)});}
        int verified=0,auxErrors=0,sigErrors=0;bool hasMeasurement=false;
        if(!string.IsNullOrWhiteSpace(exportPath)&&File.Exists(exportPath))using(var r=new StreamReader(exportPath)){
            var columns=r.ReadLine().Split(',');int idx=Array.IndexOf(columns,"SourceIndex"),ac=Array.IndexOf(columns,"Auxiliary_V"),sc=Array.IndexOf(columns,"Signal_V");if(ac<0)ac=Array.IndexOf(columns,"Voltage_V");hasMeasurement=sc>=0;
            string line;while((line=r.ReadLine())!=null){var cells=line.Split(',');var original=int.Parse(cells[idx],C);if(original>=first+count)break;if(original<first)continue;if(verified>=originals.Count||originals[verified]!=original)throw new InvalidDataException("Export index mapping differs from source trigger mask");if(D(cells[ac])!=aux[original-first])auxErrors++;if(sc>=0&&D(cells[sc])!=measurement[original-first])sigErrors++;verified++;}
            if(verified!=originals.Count)throw new InvalidDataException("Export prefix is missing source-kept samples");
        }
        return new {SourcePath=path,SourceSha256=hash,Header=header,FirstIndex=first,RawSamples=count,RawEndExclusive=first+count,SampleIntervalSeconds=dt,ExcludedSamples=count-keep.Count,KeptSamples=keep.Count,
            ExportCheck=new{Path=exportPath,ComparedRows=verified,AuxiliaryMismatches=auxErrors,HasMeasurement=hasMeasurement,MeasurementMismatches=sigErrors},
            Raw=Analyze(aux,new int[0],dt),Stitched=Analyze(keep.ToArray(),joins.ToArray(),dt),StitchedBaselineRemovedDiagnosticOnly=Analyze(BaselineRemoved(keep.ToArray()),joins.ToArray(),dt),InterpolatedGapsDiagnosticOnly=Analyze(interpolation,new int[0],dt),CleanLowTriggerSpans=clean,
            IdealSinusoidControl=Analyze(Enumerable.Range(0,4096).Select(i=>Math.Cos(2*Math.PI*32*i/4096)).ToArray(),new int[0],dt)};
    }
}
'@
    $results = @()
    foreach ($role in @('Ref','Signal')) {
        $metaPath = Join-Path $InputRunDirectory $(if ($role -eq 'Ref') {'ref_preparation.json'} else {'signal_preparation.json'})
        $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
        $sourceFolder = Split-Path -Parent $meta.SourcePath
        $exportPath = if ($role -eq 'Ref') { Join-Path $sourceFolder 'Ref_tek0003ALL_sweep01.csv' } else { Join-Path $sourceFolder 'tek0005ALL_stitched_diagnosed_0_2545000.csv' }
        $observations = [PhaseQcDiagnosis]::Run($meta.SourcePath, $meta.Sweep.StartIndex, $MaximumSamples, $meta.SampleIntervalSeconds, $meta.SourceSha256, $exportPath)
        $results += @{ role=$role; applied_metadata=$meta; observations=$observations }
    }
    @{ status='COMPLETED'; elapsed_seconds=$watch.Elapsed.TotalSeconds; peak_working_set_bytes=[Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64; results=$results } | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'metrics.json') -Encoding utf8
    'Status: COMPLETED. Matched source windows only, no production thresholds or signal values modified. Interpolated-gap result is a diagnostic control, not an approved correction. Applied metadata preserves the original full-range QC failure.' | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8
    $ResultsDirectory
}
catch {
    @{ status='FAILED'; error=$_.ToString(); elapsed_seconds=$watch.Elapsed.TotalSeconds } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'metrics.json') -Encoding utf8
    ('Status: FAILED' + [Environment]::NewLine + $_.Exception.ToString()) | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8
    throw
}
