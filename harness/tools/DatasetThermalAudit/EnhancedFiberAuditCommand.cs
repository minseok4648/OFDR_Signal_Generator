using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

internal static partial class Program {
    static int EnhancedFiber261112(string input,string output) {
        Output=Path.GetFullPath(output);Directory.CreateDirectory(Output);RecordBendingDiagnostics=true;SearchPm=400;
        BaseConfig=DistributedComparisonEngine.DefaultConfig();var d=BaseConfig.StageComparison.Document;
        d.AuxiliaryDelayMicroseconds=200*d.GroupIndex/299792458.0*1e6;d.EndMeters=21;
        Settings.AuxiliaryDelayMicroseconds=d.AuxiliaryDelayMicroseconds;
        var fft=new FftEngine();var watch=Stopwatch.StartNew();string error=null;int failures=0;
        var item=new Case {Id="ENHANCED_FIBER_261112",Directory=Path.GetFullPath(input)};Cases.Add(item);
        Save("config.json",new Dictionary<string,object> {
            {"input",item.Directory},{"reference_end_inclusive","20261112_110059_999"},
            {"tension_start_inclusive","20261112_111948_541"},
            {"boundary_policy","User explicitly confirmed 11:19:48 file is after tension. 11:00:00.760 file included in Ref. Report also checks exclusion of first tension acquisition as sensitivity."},
            {"auxiliary_path_difference_m",200.0},{"measurement_length_m",21.0},
            {"jig_region","Approximately after 10 m; exact position, gauge length and extension unknown. Do not impose a 10-11 m strain plateau."},
            {"primary_search_half_width_pm",400.0},
            {"comparisons","All distinct acquisition pairs, including reference and tension repeat controls and boundary sensitivity; top directory CSV only."},
            {"calibration","AUX 200 m from prior user confirmation; ng=1.4682, lambda=1550 nm provisional; physical distance origin unregistered."}});
        Save("reflectogram_config.json",Settings);Save("zncc_config.json",Config());File.WriteAllText(Path.Combine(Output,"core_sha256.txt"),Hash(typeof(FftEngine).Assembly.Location));File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        try {
            if(!Environment.Is64BitProcess || !fft.NativeProviderAvailable)throw new Exception("x64 native MKL required");
            foreach(var path in Directory.GetFiles(input,"*.csv",SearchOption.TopDirectoryOnly).OrderBy(p=>p,StringComparer.Ordinal)) {
                var stamp=Path.GetFileName(path).Substring(0,19);
                var state=string.CompareOrdinal(stamp,"20261112_110059_999")<=0?"ref":string.CompareOrdinal(stamp,"20261112_111948_541")>=0?"tension":"boundary";
                var s=Process(path);s.State=state;item.Sources.Add(s);
                JsonContractSerializer.Write(Path.Combine(Output,"files",s.Id,"manifest.json"),s);Save("dataset_manifest.json",item.Sources);
            }
            var unique=item.Sources.GroupBy(s=>s.Hash).Select(g=>g.First()).ToArray();
            Console.WriteLine("SOURCES COMPLETE "+unique.Length);
            for(int i=0;i<unique.Length;i++)for(int j=i+1;j<unique.Length;j++) {
                var a=unique[i];var b=unique[j];var kind=a.State==b.State?a.State+"_repeat":a.State+"_"+b.State;
                Compare(item,a,b,kind);if(item.Pairs.Count%20==0)Save("cases.json",Cases);
            }
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally {
            Save("cases.json",Cases);Save("dataset_manifest.json",item.Sources);failures=item.Sources.Count(s=>s.Error!=null)+item.Pairs.Count(p=>p.Error!=null);
            Save("metrics.json",new Dictionary<string,object> {{"status",error!=null || failures>0?"FAILED":"COMPLETED"},{"error",error},{"files",item.Sources.Count},{"references",item.Sources.Count(s=>s.State=="ref")},{"tension",item.Sources.Count(s=>s.State=="tension")},{"boundary",item.Sources.Count(s=>s.State=="boundary")},{"pairs",item.Pairs.Count},{"failures",failures},{"seconds",watch.Elapsed.TotalSeconds},{"provider",fft.ProviderDescription},{"peak_working_set_bytes",System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}});
            File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null && failures==0?"COMPLETED":"FAILED")+"\n\nEnhanced fiber 21 m, jig after approximately 10 m. Full sources and all pairs. Physical validity is evaluated separately in report.\n"+error);
        }
        return error==null && failures==0?0:1;
    }
}
