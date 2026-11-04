param(
    [string]$InputRunDirectory,
    [string]$ResultsDirectory,
    [int]$WindowCount = 10,
    [int]$WindowSamples = 16384
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $InputRunDirectory) { $InputRunDirectory = (Get-ChildItem -LiteralPath (Join-Path $workspace 'OFDR_Signal_Generator\bin\Debug\harness\results') -Directory -Filter '*prepared_ofdr_analysis*' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName }
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $workspace ('harness\results\' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff') + '_auxiliary_window_scan') }
if ($WindowCount -lt 2 -or $WindowSamples -lt 1024 -or [long]$WindowCount * $WindowSamples -gt 250000) { throw 'Use at least two windows, at least 1024 samples per window and at most 250000 unique samples per role.' }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
@{ operation='distributed first-sweep CH3 phase QC'; input_run=$InputRunDirectory; windows=$WindowCount; raw_samples_per_window=$WindowSamples; maximum_unique_raw_samples_per_role=($WindowCount*$WindowSamples); processing='count full first-sweep CH2 mask, analyze CH3 only in selected windows'; threshold_voltage=1.8; qc_ratio_limit=1e-5; full_recording_processing=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'config.json') -Encoding utf8
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $build = Join-Path $workspace 'build_verify_qc_preflight'
    $env:PATH = $build + ';' + $env:PATH
    [IO.Directory]::SetCurrentDirectory($build)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $build 'MathNet.Numerics.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $build 'MathNet.Numerics.Providers.MKL.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $build 'OFDR_Signal_Core.dll'))
    Add-Type -ReferencedAssemblies (Join-Path $build 'OFDR_Signal_Core.dll'),'System.Numerics' -TypeDefinition @'
using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Globalization;using System.Reflection;using System.Security.Cryptography;using System.Threading;
using OFDR_Signal_Core.Models;using OFDR_Signal_Core.Processing;using OFDR_Signal_Core.Services;
public static class AuxiliaryWindowScan {
    static readonly CultureInfo C=CultureInfo.InvariantCulture;
    static KLinearizationMetrics Phase(FftEngine fft,double[] values){var type=typeof(FftEngine).Assembly.GetType("OFDR_Signal_Core.Processing.AuxiliaryPhaseExtractor");var result=type.GetMethod("Extract",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{fft,values,0,CancellationToken.None,null,false});return (KLinearizationMetrics)result.GetType().GetField("Metrics").GetValue(result);}
    public static object Run(string metadataPath,int count,int length){
        var meta=JsonContractSerializer.Read<PreparedSweepMetadata>(metadataPath);var first=meta.Sweep.StartIndex;var end=meta.Sweep.EndIndexExclusive;var dt=meta.SampleIntervalSeconds;
        if((end-first-length)/(count-1)<length)throw new ArgumentException("Windows must not overlap or exceed the sweep.");
        var starts=Enumerable.Range(0,count).Select(i=>first+(int)((long)i*(end-first-length)/(count-1))).ToArray();
        var windows=new double[count][];var clean=new List<double>[count];var keptStarts=new int[count];var keptEnds=new int[count];
        for(int i=0;i<count;i++){windows[i]=new double[length];clean[i]=new List<double>();}
        var before=new FileInfo(meta.SourcePath);var bytes=before.Length;var modified=before.LastWriteTimeUtc;int row=0,kept=0,current=0,loaded=0;
        using(var input=new FileStream(meta.SourcePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(input)){string line;bool data=false;
            while((line=reader.ReadLine())!=null){if(!data){if(line.StartsWith("TIME,CH1,CH2,CH3,CH4"))data=true;continue;}if(string.IsNullOrWhiteSpace(line))continue;if(row>=end)break;
                if(row>=first){var cells=line.Split(',');bool keep=double.Parse(cells[2],C)<=1.8;
                    while(current+1<count&&row>=starts[current+1])current++;
                    if(row>=starts[current]&&row<starts[current]+length){int offset=row-starts[current];if(offset==0)keptStarts[current]=kept;var value=double.Parse(cells[3],C);windows[current][offset]=value;if(keep)clean[current].Add(value);loaded++;if(offset==length-1)keptEnds[current]=kept+(keep?1:0);}
                    if(keep)kept++;
                }row++;
            }
        }
        if(loaded!=count*length||row!=end)throw new InvalidDataException("Sampled window rows are incomplete.");
        string hash;using(var sha=SHA256.Create())using(var file=new FileStream(meta.SourcePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        var after=new FileInfo(meta.SourcePath);if(bytes!=after.Length||modified!=after.LastWriteTimeUtc||hash!=meta.SourceSha256)throw new IOException("Source changed or does not match applied SHA-256.");
        var output=new List<object>();var fft=new FftEngine();int passed=0;
        for(int i=0;i<count;i++){var raw=Phase(fft,windows[i]);KLinearizationMetrics stitched=null;bool pass=false;if(clean[i].Count>=4){stitched=Phase(fft,clean[i].ToArray());pass=stitched.NonmonotonicRatio<=1e-5&&stitched.AcceptedAnchorCount>=2&&stitched.PhaseSpanRadians>=2*Math.PI;}if(pass)passed++;
            output.Add(new{Number=i+1,SourceFirstIndex=starts[i],SourceEndExclusive=starts[i]+length,SweepRelativeStartMilliseconds=(starts[i]-first)*dt*1000,CompressedStartMilliseconds=keptStarts[i]*dt*1000,CompressedEndMilliseconds=keptEnds[i]*dt*1000,Raw=raw,Stitched=stitched,Decision=pass?"PASS_PHASE_QC":"REJECT_PHASE_QC"});}
        return new{Role=meta.Role,SourcePath=meta.SourcePath,SourceSha256=hash,SourceHeader=meta.SourceHeader,SampleIntervalSeconds=dt,SweepPeriodSeconds=(end-first)*dt,WindowCount=count,WindowSamples=length,UniqueRawSamples=count*length,FirstSweepRetainedSamples=kept,PassedWindows=passed,Windows=output};
    }
    public static string SavedPairBlock(string directory){return PreparedSweepQcPolicy.PairBlockReason(JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(directory,"ref_preparation.json")),JsonContractSerializer.Read<PreparedSweepMetadata>(Path.Combine(directory,"signal_preparation.json")),1e-5);}
}
'@
    $entries = @()
    foreach ($name in @('ref_preparation.json','signal_preparation.json')) { $entries += [AuxiliaryWindowScan]::Run((Join-Path $InputRunDirectory $name),$WindowCount,$WindowSamples) }
    $block = [AuxiliaryWindowScan]::SavedPairBlock($InputRunDirectory)
    @{ status='COMPLETED'; elapsed_seconds=$watch.Elapsed.TotalSeconds; peak_working_set_bytes=[Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64; saved_metadata_preflight_block=$block; actual_arrays_required_for_preflight=$false; results=$entries } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'metrics.json') -Encoding utf8
    ('Status: COMPLETED. Distributed sampled windows only. Production QC is unchanged. First-sweep CH2 mask was counted to report compressed selection coordinates. Full-record FFT and full real-data OFDR processing were not run.' + [Environment]::NewLine + 'Saved input preflight:' + [Environment]::NewLine + $block) | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8
    $ResultsDirectory
} catch {
    @{ status='FAILED'; error=$_.ToString(); elapsed_seconds=$watch.Elapsed.TotalSeconds } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'metrics.json') -Encoding utf8
    ('Status: FAILED' + [Environment]::NewLine + $_.Exception.ToString()) | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8
    throw
}
