using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

internal static partial class Program {
    static bool RecordBendingDiagnostics;
    static int BendingPhaseProbe(string sourceRun,string resultRun) {
        InputRoot=Path.GetFullPath(sourceRun);Output=Path.GetFullPath(resultRun);Directory.CreateDirectory(Output);string error=null;
        var rows=new List<Dictionary<string,object>>();var stage=new StageComparisonEngine();var reflect=new ReflectogramEngine();
        var rs=JsonContractSerializer.Read<ReflectogramSettings>(Path.Combine(InputRoot,"reflectogram_config.json"));
        Save("config.json",new Dictionary<string,string> {{"source_run",InputRoot},{"scope","All currently completed sources in manifest; same longest continuous raw arrays. AUX-only filter scan 1-12,2-10,3-8,4-7 MHz. Measurement not bandpass-filtered. No threshold relaxation or coordinate fitting."}});File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        try {
            if(!new FftEngine().NativeProviderAvailable)throw new Exception("Native MKL required");
            foreach(var s in JsonContractSerializer.Read<List<Source>>(Path.Combine(InputRoot,"dataset_manifest.json"))) {
                if(s.Error!=null)continue;var input=Input(s);
                foreach(var band in new[]{new[]{1.0,12.0},new[]{2.0,10.0},new[]{3.0,8.0},new[]{4.0,7.0}}) {
                    var d=new DocumentStageSettings {Enabled=true,BandpassEnabled=true,LowMHz=band[0],HighMHz=band[1]};
                    var x=DocumentStagePreprocessor.Process(input.Item1,input.Item2,d,true,16,Token);
                    using(var plan=stage.Prepare(x.Auxiliary,x.Measurement,new StageComparisonSettings(),16,Token)) {
                        var k=stage.Interpolate(plan,rs.ResampleCount,plan.Span,Token);double dz;var p=reflect.SpectrumPower(k,Math.Abs(plan.Span)/(2*Math.PI*rs.AuxiliaryDelayMicroseconds*1e-6)/(k.Length-1),rs,out dz);
                        var power=new double[7201];ReflectogramEngine.AddOnGrid(power,p,dz,.005);var peak=Enumerable.Range(100,power.Length-100).OrderByDescending(i=>power[i]).First();
                        rows.Add(new Dictionary<string,object> {{"id",s.Id},{"state",s.State},{"low_mhz",band[0]},{"high_mhz",band[1]},{"phase_qc",plan.Metrics.NonmonotonicRatio},{"span_rad",plan.Span},{"peak_m",peak*.005},{"peak_db",ReflectogramEngine.Db(power[peak])}});
                        using(var w=new StreamWriter(Path.Combine(Output,s.Id+"_"+band[0]+"_"+band[1]+".csv"))) {w.WriteLine("distance_m,power,db");for(int i=0;i<power.Length;i++)w.WriteLine(string.Join(",",new[]{i*.005,power[i],ReflectogramEngine.Db(power[i])}.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture))));}
                    }
                }
            }
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        Save("metrics.json",new Dictionary<string,object> {{"status",error==null?"COMPLETED":"FAILED"},{"error",error},{"conditions",rows.Count}});Save("phase_scan.json",rows);File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null?"COMPLETED":"FAILED")+"\n\nAUX phase/filter sensitivity only. No physical validity inferred from desired graph shape.\n"+error);return error==null?0:1;
    }
    static Dictionary<string,double> BendingRawDiagnostics(ModeHopRecording raw) {
        var m=new Dictionary<string,double>();
        foreach(var channel in new[]{Tuple.Create("AUX",raw.Auxiliary),Tuple.Create("Measurement",raw.Measurement)}) {
            double sum=0,squares=0,lo=double.PositiveInfinity,hi=double.NegativeInfinity;int n=0;
            foreach(var v in channel.Item2)if(ModeHopSettings.IsFinite(v)){sum+=v;squares+=v*v;lo=Math.Min(lo,v);hi=Math.Max(hi,v);n++;}
            var prefix=channel.Item1+"_";
            m[prefix+"finite_count"]=n;m[prefix+"mean_v"]=sum/n;m[prefix+"ac_rms_v"]=Math.Sqrt(Math.Max(0,squares/n-Math.Pow(sum/n,2)));m[prefix+"min_v"]=lo;m[prefix+"max_v"]=hi;
            m[prefix+"at_extrema_fraction"]=channel.Item2.Count(v=>v==lo || v==hi)/(double)n;
        }
        m["CH2_above_1_8v_fraction"]=raw.Trigger.Count(v=>v>1.8)/(double)raw.SampleCount;
        int edges=0;for(int i=1;i<raw.SampleCount;i++)if(raw.SweepTrigger[i-1]<=1.8 && raw.SweepTrigger[i]>1.8)edges++;
        m["CH1_rising_edges"]=edges;m["source_start_seconds"]=raw.SourceStartSeconds;return m;
    }
    static int Bending261112(string input,string output) {
        Output=Path.GetFullPath(output);Directory.CreateDirectory(Output);RecordBendingDiagnostics=true;SearchPm=400;
        BaseConfig=DistributedComparisonEngine.DefaultConfig();var d=BaseConfig.StageComparison.Document;
        d.AuxiliaryDelayMicroseconds=200*d.GroupIndex/299792458.0*1e6;d.EndMeters=15;
        Settings.AuxiliaryDelayMicroseconds=d.AuxiliaryDelayMicroseconds;
        var fft=new FftEngine();var watch=Stopwatch.StartNew();string error=null;
        var item=new Case {Id="BEND_261112",Directory=Path.GetFullPath(input)};Cases.Add(item);
        Save("config.json",new Dictionary<string,object> {{"input",item.Directory},{"reference_end_inclusive","20261112_100742_335"},{"bending_start_inclusive","20261112_101121_000"},{"auxiliary_path_difference_m",200.0},{"auxiliary_source","Prior user-confirmed AUX value retained; ng=1.4682 remains provisional."},{"patch_cords_m","5 + 10"},{"connector_order","not confirmed at execution start; both 5m and 10m candidates inspected"},{"jig_region_m","approximately 2-3 from input, user description; distance origin not independently registered"},{"measurement_length_m",15.0},{"primary_search_half_width_pm",400.0},{"primary_comparisons","All distinct Ref x Bending, Ref x Ref, Bending x Bending. Per-sweep own AUX; same processing settings. AUTO/NORMAL acquisition mode is confounded with state and recorded explicitly."}});
        Save("reflectogram_config.json",Settings);Save("zncc_config.json",Config());File.WriteAllText(Path.Combine(Output,"core_sha256.txt"),Hash(typeof(FftEngine).Assembly.Location));File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        try {
            if(!Environment.Is64BitProcess || !fft.NativeProviderAvailable)throw new Exception("x64 native MKL required");
            foreach(var path in Directory.GetFiles(input,"*.csv",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal)) {
                var stamp=Path.GetFileName(path).Substring(0,19);
                var state=string.CompareOrdinal(stamp,"20261112_100742_335")<=0?"ref":string.CompareOrdinal(stamp,"20261112_101121_000")>=0?"bending":"unlabelled_gap";
                var s=Process(path);s.State=state;item.Sources.Add(s);
                JsonContractSerializer.Write(Path.Combine(Output,"files",s.Id,"manifest.json"),s);Save("dataset_manifest.json",item.Sources);
            }
            var unique=item.Sources.GroupBy(s=>s.Hash).Select(g=>g.First()).ToArray();var refs=unique.Where(s=>s.State=="ref").ToArray();var bends=unique.Where(s=>s.State=="bending").ToArray();
            Console.WriteLine("SOURCES COMPLETE refs="+refs.Length+" bends="+bends.Length);
            foreach(var r in refs)foreach(var s in bends){Compare(item,r,s,"ref_bending");if(item.Pairs.Count%20==0)Save("cases.json",Cases);}
            foreach(var list in new[]{refs,bends})for(int i=0;i<list.Length;i++)for(int j=i+1;j<list.Length;j++){Compare(item,list[i],list[j],list[i].State+"_repeat");if(item.Pairs.Count%20==0)Save("cases.json",Cases);}
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally {
            Save("cases.json",Cases);Save("dataset_manifest.json",item.Sources);var failures=item.Sources.Count(s=>s.Error!=null)+item.Pairs.Count(p=>p.Error!=null);
            Save("metrics.json",new Dictionary<string,object> {{"status",error!=null || failures>0?"FAILED":"COMPLETED"},{"error",error},{"files",item.Sources.Count},{"references",item.Sources.Count(s=>s.State=="ref")},{"bending",item.Sources.Count(s=>s.State=="bending")},{"pairs",item.Pairs.Count},{"failures",failures},{"seconds",watch.Elapsed.TotalSeconds},{"provider",fft.ProviderDescription},{"peak_working_set_bytes",System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}});
            File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null && failures==0?"COMPLETED":"FAILED")+"\n\n261112 full-source bending comparison. See config, metrics, manifests, case curves and report. All failed inputs/pairs are preserved.\n"+error);
        }
        return error==null?0:1;
    }
}

