using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

public static class EnhancedFiberCoreProbe
{
    static double[] Read(string path) { var b=File.ReadAllBytes(path); var a=new double[b.Length/8]; Buffer.BlockCopy(b,0,a,0,b.Length); return a; }
    static double Median(IEnumerable<double> source) { var a=source.Where(x=>!double.IsNaN(x)&&!double.IsInfinity(x)).OrderBy(x=>x).ToArray(); return a.Length==0?double.NaN:(a[(a.Length-1)/2]+a[a.Length/2])/2; }
    static StageComparisonSettings Setting(int variant)
    {
        var s=new StageComparisonSettings { RemoveAuxiliaryMean=false, PhaseOrder=AuxiliaryPhaseOrder.StablePhaseSort,
            Grid=RelativePhaseGrid.IndependentEndpoints,WindowPlacement=DiagnosticWindowPlacement.GlobalSignal,
            RemoveMeasurementMean=true,CenterLocalMagnitudes=false,OutputSamples=524288,AuxiliaryOffset=40,MeasurementOffset=1 };
        if(variant==1) s.RemoveAuxiliaryMean=true;
        if(variant==2) s.Grid=RelativePhaseGrid.CommonSpan;
        if(variant>=3) {s.RemoveAuxiliaryMean=true;s.Grid=RelativePhaseGrid.CommonSpan;}
        if(variant>=4) s.PhaseOrder=AuxiliaryPhaseOrder.IncreasingAnchors;
        if(variant>=5) s.WindowPlacement=DiagnosticWindowPlacement.DistanceSegment;
        if(variant>=6) {s.AuxiliaryOffset=0;s.MeasurementOffset=0;}
        return s;
    }
    static void Execute(string output,string name,int signal,int variant,int hop,Dictionary<int,double[]> aux,Dictionary<int,double[]> main,
                        StageComparisonEngine engine,Dictionary<string,StageComparisonEngine.PhasePlan> cache,int reference=4)
    {
        var folder=Path.Combine(output,name);Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"summary.md"),"# RUNNING\n");
        var c=new IndependentOfdrProcessingConfig { StageComparisonEnabled=true,AllowFailedPhaseQcForDiagnostics=true,UsePhysicalAxes=false,
            NormalizeCrossCorrelation=false,WindowFunction="Hann",SegmentSize=4096,SegmentHop=hop,SegmentIfftZeroPadFactor=8,MaxPreviewPoints=2048,
            StageComparison=Setting(variant) };
        JsonContractSerializer.Write(Path.Combine(folder,"config.json"),c);
        var watch=Stopwatch.StartNew();Console.WriteLine(name);
        try {
            var plans=new StageComparisonEngine.PhasePlan[2];var ids=new int[]{reference,signal};
            for(int j=0;j<2;j++) {var key=ids[j]+":"+c.StageComparison.PhaseKey;
                if(!cache.ContainsKey(key))cache[key]=engine.Prepare(aux[ids[j]],main[ids[j]],c.StageComparison,2048,CancellationToken.None);
                plans[j]=cache[key];}
            var input=new IndependentOfdrInput { Auxiliary=Channel(aux[reference],3,reference),SignalAuxiliary=Channel(aux[signal],3,signal),Reference=Channel(main[reference],4,reference),Signal=Channel(main[signal],4,signal) };
            using(var r=engine.RunPrepared(input,c,plans[0],plans[1],CancellationToken.None,null,false)) {
                using(var w=new StreamWriter(Path.Combine(folder,"profile.csv"))) {w.WriteLine("profile_index,fft_bin,lag_samples");
                    for(int i=0;i<r.NotebookProfile.Shifts.Length;i++)w.WriteLine(i+","+r.NotebookProfile.Positions[i]+","+r.NotebookProfile.Shifts[i].ToString("R",CultureInfo.InvariantCulture));}
                JsonContractSerializer.Write(Path.Combine(folder,"result.json"),r);
                var all=r.NotebookProfile.Shifts.Where(x=>!double.IsNaN(x)&&!double.IsInfinity(x)).ToArray();
                var m=new Dictionary<string,object>();m["status"]="COMPLETED";m["valid_points"]=all.Length;m["median_lag"]=Median(all);m["median_abs_lag"]=Median(all.Select(Math.Abs));
                m["elapsed_seconds"]=watch.Elapsed.TotalSeconds;m["provider"]=engine.ProviderDescription;
                JsonContractSerializer.Write(Path.Combine(folder,"metrics.json"),m);
                File.WriteAllText(Path.Combine(folder,"summary.md"),"# COMPLETED\n\nActual Core StageComparisonEngine; diagnostic lag, no physical calibration or baseline subtraction.\n");
            }
        } catch(Exception ex) {JsonContractSerializer.Write(Path.Combine(folder,"metrics.json"),new Dictionary<string,string>{{"status","FAILED"},{"error",ex.ToString()}});File.WriteAllText(Path.Combine(folder,"summary.md"),"# FAILED\n\n"+ex);throw;}
    }
    static OfdrChannelData Channel(double[] a,int channel,int number) {return new OfdrChannelData {Values=a,SampleIntervalSeconds=4e-8,ChannelNumber=channel,MeasurementId="tek"+number.ToString("D4")+"/CH"+channel};}
    static void ExportSpectra(string prepared,string output,StageComparisonEngine engine,int[] ids,string selection,int variant,int count)
    {
        foreach(int id in ids) {
            var aux=Read(Path.Combine(prepared,selection,id+"_aux.bin"));var main=Read(Path.Combine(prepared,selection,id+"_main.bin"));
            var c=new IndependentOfdrProcessingConfig {StageComparison=Setting(variant),StageComparisonEnabled=true,AllowFailedPhaseQcForDiagnostics=true,UsePhysicalAxes=false,WindowFunction="Hann"};
            using(var p=engine.Prepare(aux,main,c.StageComparison,2048,CancellationToken.None)) {
                var n=count>0?count:p.Metrics.SampleCount;c.StageComparison.OutputSamples=n;
                var values=engine.Interpolate(p,n,p.Span,CancellationToken.None);var spectrum=engine.Spectrum(values,c,CancellationToken.None);
                using(var w=new BinaryWriter(File.Create(Path.Combine(output,id+"_spectrum.bin"))))foreach(var z in spectrum){w.Write(z.Real);w.Write(z.Imaginary);}
                JsonContractSerializer.Write(Path.Combine(output,id+"_phase.json"),new Dictionary<string,double>{{"first",p.First},{"span",p.Span},{"adjacent_reverse_ratio",p.AdjacentReverseRatio},{"raw_input_count",main.Length},{"phase_input_count",p.Metrics.SampleCount},{"output_count",n}});
                JsonContractSerializer.Write(Path.Combine(output,id+"_config.json"),c);
                JsonContractSerializer.Write(Path.Combine(output,id+"_diagnostics.json"),p.Diagnostics);
            }
        }
    }
    public static int Main(string[] args)
    {
        var prepared=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
        var status="FAILED";string error=null;var watch=Stopwatch.StartNew();
        File.WriteAllText(Path.Combine(output,"summary.md"),"# RUNNING\n");
        try {
            var check=new FftEngine();if(!Environment.Is64BitProcess||!check.NativeProviderAvailable)throw new Exception("x64 native MKL is mandatory: "+check.NativeProviderFailure);
            JsonContractSerializer.Write(Path.Combine(output,"config.json"),new Dictionary<string,object>{{"prepared",prepared},{"core_assembly",typeof(StageComparisonEngine).Assembly.Location},{"native_provider",check.ProviderDescription},{"signals",args.Length>3?args[3]:"5,6,7"},{"user_state",args.Length>3?"Explicit file selection; roles not assumed":"unchanged repetitions; reference 4"},{"zero_baseline_subtraction",false}});
            var engine=new StageComparisonEngine();
            if(args.Length>2 && args[2]=="export") {ExportSpectra(prepared,output,engine,args.Length>3?args[3].Split(',').Select(int.Parse).ToArray():new int[]{4,5,6,7},args.Length>4?args[4]:"masked",args.Length>5?int.Parse(args[5]):0,args.Length>6?int.Parse(args[6]):524288);status="COMPLETED";return 0;}
            if(args.Length>2 && args[2]=="pair") {
                var ids=args[3].Split(',').Select(int.Parse).ToArray();if(ids.Length!=2)throw new ArgumentException("pair needs two file IDs");
                var selection=args.Length>4?args[4]:"masked";var variant=args.Length>5?int.Parse(args[5]):0;var hop=args.Length>6?int.Parse(args[6]):50;
                var aux=new Dictionary<int,double[]>();var main=new Dictionary<int,double[]>();var cache=new Dictionary<string,StageComparisonEngine.PhasePlan>();
                foreach(var id in ids.Distinct()){aux[id]=Read(Path.Combine(prepared,selection,id+"_aux.bin"));main[id]=Read(Path.Combine(prepared,selection,id+"_main.bin"));}
                Execute(output,selection+"_v"+variant+"_"+ids[0]+"vs"+ids[1],ids[1],variant,hop,aux,main,engine,cache,ids[0]);
                foreach(var p in cache.Values)p.Dispose();status="COMPLETED";return 0;
            }
            foreach(var selection in new[]{"masked","guarded","continuous"}) {
                var aux=new Dictionary<int,double[]>();var main=new Dictionary<int,double[]>();var cache=new Dictionary<string,StageComparisonEngine.PhasePlan>();
                for(int id=4;id<=7;id++){aux[id]=Read(Path.Combine(prepared,selection,id+"_aux.bin"));main[id]=Read(Path.Combine(prepared,selection,id+"_main.bin"));}
                var variants=selection=="masked"?new int[]{0,1,2,3,4,5,6}:new int[]{0,5};
                foreach(int variant in variants)foreach(int sig in new[]{5,6,7}) Execute(output,selection+"_v"+variant+"_4vs"+sig,sig,variant,512,aux,main,engine,cache);
                if(selection=="masked") {
                    Execute(output,"masked_v0_4vs4_self",4,0,50,aux,main,engine,cache);
                    foreach(int sig in new[]{5,6,7})Execute(output,"masked_v0_4vs"+sig+"_fullhop",sig,0,50,aux,main,engine,cache);
                }
                foreach(var p in cache.Values)p.Dispose();
                aux.Clear();main.Clear();GC.Collect();GC.WaitForPendingFinalizers();
            }
            status="COMPLETED";
        } catch(Exception ex) {error=ex.ToString();Console.Error.WriteLine(error);}
        finally {JsonContractSerializer.Write(Path.Combine(output,"metrics.json"),new Dictionary<string,object>{{"status",status},{"error",error},{"elapsed_seconds",watch.Elapsed.TotalSeconds},{"peak_working_set_bytes",Process.GetCurrentProcess().PeakWorkingSet64}});File.WriteAllText(Path.Combine(output,"summary.md"),"# "+status+"\n\nNo baseline subtraction. QC failures remain visible.\n\n"+(error??"See individual cases."));}
        return status=="COMPLETED"?0:1;
    }
}
