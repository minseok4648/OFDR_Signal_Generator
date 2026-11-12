using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using OFDR_Signal_Core.Models;
using OFDR_Signal_Core.Processing;
using OFDR_Signal_Core.Services;

[DataContract] public sealed class Source {
    [DataMember] public string Path, Hash, State, Id, Error;
    [DataMember] public int Samples, Start, Count, Masked;
    [DataMember] public double Dt, LowMHz, HighMHz;
    [DataMember] public Dictionary<string,string> Header;
    [DataMember] public List<ReflectogramRangeMetric> Ranges;
    [DataMember(EmitDefaultValue=false)] public Dictionary<string,double> RawDiagnostics;
}
[DataContract] public sealed class Pair {
    [DataMember] public string Id, Kind, Reference, Signal, Error;
    [DataMember] public DocumentStageResult Curve;
    [DataMember] public double ReferenceQc, SignalQc;
}
[DataContract] public sealed class Case {
    [DataMember] public string Id, Directory;
    [DataMember] public List<Source> Sources = new List<Source>();
    [DataMember] public List<Pair> Pairs = new List<Pair>();
}
internal static partial class Program {
    static readonly CancellationToken Token = CancellationToken.None;
    static readonly ReflectogramSettings Settings = new ReflectogramSettings { ResampleCount=262144, ZeroPadFactor=1, GridMeters=.005, EndMeters=36, MinimumRangeMilliseconds=.01 };
    static readonly Dictionary<string,Source> Known = new Dictionary<string,Source>();
    static readonly List<Case> Cases = new List<Case>();
    static string Output, Seed, InputRoot;
    static double SearchPm=40;
    static IndependentOfdrProcessingConfig BaseConfig;
    static List<Case> SeedCases;
    static string State(string path) {
        var n=System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if(n.StartsWith("ref")) return "ref";
        if(n.Contains("heat") || n.StartsWith("100c") || n.StartsWith("temp")) return "heat";
        if(n.Contains("cool")) return "cool";
        if(n.Contains("bend")) return "bending";
        return "unknown";
    }
    static string Hash(string path) {using(var f=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
    static void Save<T>(string name,T value) => JsonContractSerializer.Write(Path.Combine(Output,name),value);
    static double[] Slice(double[] a,int first,int count) {var b=new double[count];Array.Copy(a,first,b,0,count);return b;}
    static void Bin(string name,double[] values) {using(var w=new BinaryWriter(File.Create(name))) foreach(var v in values)w.Write(v);}
    static double[] Bin(string name) {var b=File.ReadAllBytes(name);var a=new double[b.Length/8];Buffer.BlockCopy(b,0,a,0,b.Length);return a;}
    static IndependentOfdrProcessingConfig Config() {var c=BaseConfig==null?DistributedComparisonEngine.DefaultConfig():JsonContractSerializer.Clone(BaseConfig);c.StageComparison.OutputSamples=262144;c.StageComparison.Document.MaximumShiftPm=SearchPm;return c;}
    static Source Process(string path) {
        var hash=Hash(path);
        if(Known.TryGetValue(hash,out var existing)) return new Source {Path=path,Hash=hash,Id=existing.Id,State=State(path),Samples=existing.Samples,Dt=existing.Dt,Start=existing.Start,Count=existing.Count,Masked=existing.Masked,Header=existing.Header,Ranges=existing.Ranges,Error=existing.Error,LowMHz=existing.LowMHz,HighMHz=existing.HighMHz};
        var source=new Source {Path=path,Hash=hash,Id="F"+(Known.Count+1).ToString("D3"),State=State(path)};
        Known.Add(hash,source);var dir=Path.Combine(Output,"files",source.Id);Directory.CreateDirectory(dir);
        var prior=Seed==null?null:Path.Combine(Seed,"files",source.Id,"manifest.json");
        if(prior!=null && File.Exists(prior)) {
            var cached=JsonContractSerializer.Read<Source>(prior);
            // Only successful old records can be reused. They already passed the unchanged
            // 1024-sample post-guard requirement in every range; the new eligibility floor
            // therefore changes no successful numerical result.
            if(cached.Hash==hash && cached.Error==null) {
                foreach(var file in Directory.GetFiles(Path.GetDirectoryName(prior)))File.Copy(file,Path.Combine(dir,Path.GetFileName(file)),true);
                cached.Path=path;cached.State=State(path);Known[hash]=cached;Console.WriteLine("VERIFIED CACHE "+cached.Id);return cached;
            }
        }
        try {
            Console.WriteLine("READ "+source.Id+" "+path);
            var raw=new TekScopeCsvReader().Read(path,Token);
            if(raw.IsPartialRead || !raw.HasFourChannels || raw.Sha256!=hash)throw new Exception("Complete four-channel stable source required");
            source.Samples=raw.SampleCount;source.Dt=raw.SampleIntervalSeconds;source.Header=raw.Header;
            if(RecordBendingDiagnostics)source.RawDiagnostics=BendingRawDiagnostics(raw);
            var mask=new ModeHopPreviewEngine().Detect(raw,new ModeHopSettings {ThresholdVoltage=1.8,ExclusionTailSeconds=.0001},Token);
            JsonContractSerializer.Write(Path.Combine(dir,"mask.json"),mask);
            var ranges=new List<Tuple<int,int>>();int cursor=0;
            var minimumWithGuards=1024+2*(int)Math.Ceiling(2e-6/source.Dt);
            Action<int,int> add=(lo,hi)=>{if((hi-lo)*source.Dt>=.00001 && hi-lo>=minimumWithGuards)ranges.Add(Tuple.Create(lo,hi));};
            foreach(var r in mask.Ranges){add(cursor,r.StartIndex);source.Masked+=r.EndIndexExclusive-r.StartIndex;cursor=r.EndIndexExclusive;}add(cursor,raw.SampleCount);
            if(ranges.Count==0)throw new Exception("No unmasked continuous range >=10 us and >=1024 samples");
            var longest=ranges.OrderByDescending(r=>r.Item2-r.Item1).First();source.Start=longest.Item1;source.Count=longest.Item2-longest.Item1;
            Bin(Path.Combine(dir,"auxiliary_samples.bin"),Slice(raw.Auxiliary,source.Start,source.Count));
            Bin(Path.Combine(dir,"signal.bin"),Slice(raw.Measurement,source.Start,source.Count));
            var reflect=new ReflectogramEngine();var stage=new StageComparisonEngine();
            var d=Config().StageComparison.Document;
            source.LowMHz=d.LowMHz;source.HighMHz=d.ResolveHighMHz(source.Dt);
            var power=new double[(int)Math.Round(Settings.EndMeters/Settings.GridMeters)+1];source.Ranges=new List<ReflectogramRangeMetric>();
            foreach(var r in ranges) {
                int count=r.Item2-r.Item1;
                var input=DocumentStagePreprocessor.Process(new OfdrChannelData {Values=Slice(raw.Auxiliary,r.Item1,count),SampleIntervalSeconds=source.Dt},new OfdrChannelData {Values=Slice(raw.Measurement,r.Item1,count),SampleIntervalSeconds=source.Dt},d,true,16,Token);
                using(var plan=stage.Prepare(input.Auxiliary,input.Measurement,new StageComparisonSettings(),16,Token)) {
                    var k=stage.Interpolate(plan,Settings.ResampleCount,plan.Span,Token);
                    var step=Math.Abs(plan.Span)/(2*Math.PI*Settings.AuxiliaryDelayMicroseconds*1e-6)/(k.Length-1);
                    double dz;var p=reflect.SpectrumPower(k,step,Settings,out dz);
                    ReflectogramEngine.AddOnGrid(power,p,dz,Settings.GridMeters);
                    source.Ranges.Add(new ReflectogramRangeMetric {StartIndex=r.Item1,EndIndexExclusive=r.Item2,PhaseSpanRadians=plan.Span,RejectedAnchorRatio=plan.Metrics.NonmonotonicRatio,PhaseQcPassed=plan.Metrics.NonmonotonicRatio<=Settings.PhaseQcLimit,CorrectedNativeBinMeters=dz});
                }
            }
            using(var w=new StreamWriter(Path.Combine(dir,"reflectogram.csv"))) {
                w.WriteLine("distance_m,power_V2_per_m,db_re_1V2_per_m");
                for(int i=0;i<power.Length;i++){power[i]/=ranges.Count;w.WriteLine(string.Join(",",new[]{i*Settings.GridMeters,power[i],ReflectogramEngine.Db(power[i])}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))));}
            }
        }catch(Exception ex){source.Error=ex.ToString();Console.WriteLine("FILE FAILED "+source.Id+" "+ex.Message);}
        JsonContractSerializer.Write(Path.Combine(dir,"manifest.json"),source);GC.Collect();return source;
    }
    static Tuple<OfdrChannelData,OfdrChannelData> Input(Source s) {
        var dir=Path.Combine(InputRoot??Output,"files",s.Id);
        var m=new PreparedSweepMetadata {SourcePath=s.Path,SourceSha256=s.Hash,SourceHeader=s.Header,RowCount=s.Count,SelectedStartIndex=0,SelectedEndIndexExclusive=s.Count,SampleIntervalSeconds=s.Dt,Sweep=new SweepRange {StartIndex=s.Start,EndIndexExclusive=s.Start+s.Count},ExcludedRanges=new List<ModeHopRange>()};
        Func<string,int,OfdrChannelData> ch=(name,n)=>new OfdrChannelData {SourcePath=s.Path,MeasurementId=s.Id,ChannelNumber=n,SampleIntervalSeconds=s.Dt,Preparation=m,Values=Bin(Path.Combine(dir,name+".bin"))};
        return Tuple.Create(ch("auxiliary_samples",3),ch("signal",4));
    }
    static void Compare(Case item,Source r,Source s,string kind) {
        var p=new Pair {Id="P"+(item.Pairs.Count+1).ToString("D3"),Kind=kind,Reference=r.Id,Signal=s.Id};item.Pairs.Add(p);
        if(r.Hash==s.Hash){p.Error="IDENTICAL_SHA256_NOT_INDEPENDENT";return;}
        if(r.Error!=null || s.Error!=null){p.Error="SOURCE_PROCESSING_FAILED";return;}
        var previous=SeedCases?.FirstOrDefault(c=>c.Id==item.Id);
        var sameSources=previous!=null && previous.Sources.Any(x=>x.Id==r.Id && x.Hash==r.Hash) && previous.Sources.Any(x=>x.Id==s.Id && x.Hash==s.Hash);
        var old=sameSources?previous.Pairs.FirstOrDefault(x=>x.Id==p.Id && x.Reference==r.Id && x.Signal==s.Id && x.Kind==kind && x.Error==null && x.Curve!=null):null;
        if(old!=null){p.Curve=old.Curve;p.ReferenceQc=old.ReferenceQc;p.SignalQc=old.SignalQc;Console.WriteLine("VERIFIED PAIR CACHE "+item.Id+" "+p.Id);return;}
        Console.WriteLine("PAIR "+item.Id+" "+p.Id+" "+kind+" "+Path.GetFileName(r.Path)+" / "+Path.GetFileName(s.Path));
        try {
            var a=Input(r);var b=Input(s);var input=new IndependentOfdrInput {Auxiliary=a.Item1,Reference=a.Item2,SignalAuxiliary=b.Item1,Signal=b.Item2};
            using(var result=new StageComparisonEngine().Run(input,Config(),Token)) {
                p.Curve=result.DocumentStages;p.ReferenceQc=result.KLinearization.NonmonotonicRatio;p.SignalQc=result.SignalKLinearization.NonmonotonicRatio;
            }
        }catch(Exception ex){p.Error=ex.ToString();Console.WriteLine("PAIR FAILED "+ex.Message);}
        GC.Collect();
    }
    static int Main(string[] args) {
        if(args.Length>0 && args[0]=="--enhanced-fiber-261112")return EnhancedFiber261112(args[1],args[2]);
        if(args.Length>0 && args[0]=="--bending-261112")return Bending261112(args[1],args[2]);
        if(args.Length>0 && args[0]=="--bending-phase-probe")return BendingPhaseProbe(args[1],args[2]);
        if(args.Length>0 && args[0]=="--search-sensitivity")return Sensitivity(args[1],args[2]);
        if(args.Length>0 && args[0]=="--confirmed-200m-23m")return Recalibrate(args[1],args[2]);
        var watch=Stopwatch.StartNew();Output=Path.GetFullPath(args[1]);Directory.CreateDirectory(Output);string error=null;
        if(args.Length>2){Seed=Path.GetFullPath(args[2]);SeedCases=JsonContractSerializer.Read<List<Case>>(Path.Combine(Seed,"cases.json"));}
        var provider=new FftEngine();
        Save("config.json",new Dictionary<string,object> {{"input",args[0]},{"verified_success_seed",Seed},{"minimum_range_samples_before_guard","1024 + 2*ceil(2us/header_dt)"},{"reflectogram_config","reflectogram_config.json"},{"zncc_config","zncc_config.json"},{"native_provider",provider.ProviderDescription},{"policy","All full CSVs; SHA dedup; per-directory labels only; all distinct ref x heat; unique ref-ref/heat-heat controls; cooling surrogate explicitly separate; unknown first-file comparisons descriptive only; longest continuous range for ZNCC; independent AUX per sweep; phase-QC failures diagnostic only; no absolute temperature/strain calibration. Optional seed reuses successful full-source results only after current full SHA verification, under identical numerical configuration and deterministic file IDs; all failures are recalculated. No successful old range failed the unchanged post-guard limit."}});
        Save("reflectogram_config.json",Settings);Save("zncc_config.json",Config());File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        var coreHash=Hash(typeof(FftEngine).Assembly.Location);File.WriteAllText(Path.Combine(Output,"core_sha256.txt"),coreHash);
        try {
            if(!Environment.Is64BitProcess || !provider.NativeProviderAvailable)throw new Exception("x64 native MKL required");
            if(Seed!=null && (File.ReadAllText(Path.Combine(Seed,"core_sha256.txt")).Trim()!=coreHash || new[]{"reflectogram_config.json","zncc_config.json"}.Any(name=>File.ReadAllText(Path.Combine(Seed,name))!=File.ReadAllText(Path.Combine(Output,name)))))throw new Exception("Cached Core or numerical config differs; refuse reuse");
            foreach(var group in Directory.GetFiles(args[0],"*.csv",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).GroupBy(Path.GetDirectoryName)) {
                var item=new Case {Id="C"+(Cases.Count+1).ToString("D2"),Directory=group.Key};Cases.Add(item);
                Console.WriteLine("CASE "+item.Id+" "+group.Key);
                foreach(var path in group)item.Sources.Add(Process(path));
                var unique=item.Sources.GroupBy(x=>x.Hash).Select(x=>x.First()).ToList();
                var refs=unique.Where(x=>x.State=="ref").ToList();var heats=unique.Where(x=>x.State=="heat").ToList();var cools=unique.Where(x=>x.State=="cool").ToList();
                foreach(var r in refs)foreach(var s in heats)Compare(item,r,s,"ref_heat");
                foreach(var list in new[]{refs,heats,cools})for(int i=0;i<list.Count;i++)for(int j=i+1;j<list.Count;j++)Compare(item,list[i],list[j],list[i].State+"_repeat");
                if(refs.Count==0)foreach(var r in cools)foreach(var s in heats)Compare(item,r,s,"cool_heat_surrogate");
                foreach(var s in unique.Where(x=>x.State=="cool"))foreach(var r in refs)Compare(item,r,s,"ref_cool");
                if(heats.Count==0 && cools.Count==0 && unique.Count>1 && refs.Count<2)foreach(var s in unique.Skip(1))Compare(item,unique[0],s,"unlabelled_comparison");
                Save("cases.json",Cases);Console.WriteLine("CASE DONE "+item.Id);GC.Collect();GC.WaitForPendingFinalizers();
            }
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally {
            Save("cases.json",Cases);var failures=Known.Values.Count(x=>x.Error!=null)+Cases.Sum(c=>c.Pairs.Count(p=>p.Error!=null));
            Save("metrics.json",new Dictionary<string,object> {{"status",error!=null?"FAILED":failures>0?"COMPLETED_WITH_RECORDED_FAILURES":"COMPLETED"},{"error",error},{"seconds",watch.Elapsed.TotalSeconds},{"directories",Cases.Count},{"files",Cases.Sum(c=>c.Sources.Count)},{"unique_files",Known.Count},{"pairs",Cases.Sum(c=>c.Pairs.Count)},{"failures",failures},{"provider",provider.ProviderDescription},{"peak_working_set_bytes",System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}});
            File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null?"BATCH FINISHED":"FAILED")+"\n\nFull inputs and native MKL. See cases.json, metrics.json, config.json and docs/report for limitations. All per-source and per-pair errors are preserved.\n"+error);
        }
        return error==null?0:1;
    }
    static int Sensitivity(string sourceRun,string resultRun) {
        Output=Path.GetFullPath(resultRun);InputRoot=Path.GetFullPath(sourceRun);SearchPm=400;Directory.CreateDirectory(Output);
        BaseConfig=JsonContractSerializer.Read<IndependentOfdrProcessingConfig>(Path.Combine(InputRoot,"zncc_config.json"));
        var watch=Stopwatch.StartNew();string error=null;var fft=new FftEngine();
        Save("config.json",new Dictionary<string,string> {{"source_run",InputRoot},{"source_cases_sha256",Hash(Path.Combine(InputRoot,"cases.json"))},{"scope","Same full-source-prepared arrays; only maximum search changed from 40 to 400 pm, capped by common bandwidth and overlap. All explicit ref-heat pairs plus same-directory ref/heat repeats. Reflectogram is unchanged and is not recomputed."},{"core_sha256",Hash(typeof(FftEngine).Assembly.Location)}});
        Save("zncc_config.json",Config());File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        try {
            if(!Environment.Is64BitProcess || !fft.NativeProviderAvailable || File.ReadAllText(Path.Combine(InputRoot,"core_sha256.txt")).Trim()!=Hash(typeof(FftEngine).Assembly.Location))throw new Exception("Native Core provenance mismatch");
            var sourceMetrics=JsonContractSerializer.Read<Dictionary<string,object>>(Path.Combine(InputRoot,"metrics.json"));
            if(!Equals(sourceMetrics["status"],"COMPLETED"))throw new Exception("Source full batch must be completed without errors");
            foreach(var old in JsonContractSerializer.Read<List<Case>>(Path.Combine(InputRoot,"cases.json")).Where(c=>c.Pairs.Any(p=>p.Kind=="ref_heat"))) {
                var item=new Case {Id=old.Id,Directory=old.Directory,Sources=old.Sources};Cases.Add(item);
                foreach(var p in old.Pairs.Where(p=>p.Kind=="ref_heat" || p.Kind=="ref_repeat" || p.Kind=="heat_repeat"))Compare(item,old.Sources.First(s=>s.Id==p.Reference),old.Sources.First(s=>s.Id==p.Signal),p.Kind);
                Save("cases.json",Cases);
            }
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally {
            var failures=Cases.Sum(c=>c.Pairs.Count(p=>p.Error!=null));Save("cases.json",Cases);
            Save("metrics.json",new Dictionary<string,object> {{"status",error!=null || failures>0?"FAILED":"COMPLETED"},{"error",error},{"pair_failures",failures},{"pairs",Cases.Sum(c=>c.Pairs.Count)},{"seconds",watch.Elapsed.TotalSeconds},{"provider",fft.ProviderDescription}});
            File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null && failures==0?"COMPLETED":"FAILED")+"\n\nSearch-bound sensitivity on exactly the existing native full-source prepared arrays. Requested 400 pm; physical bandwidth/overlap caps remain. Same Core and all other numerical settings. No reflection or absolute temperature calibration changes.\n"+error);
        }
        return error==null?0:1;
    }
    static int Recalibrate(string sourceRun,string resultRun) {
        Output=Path.GetFullPath(resultRun);InputRoot=Path.GetFullPath(sourceRun);Directory.CreateDirectory(Output);
        BaseConfig=JsonContractSerializer.Read<IndependentOfdrProcessingConfig>(Path.Combine(InputRoot,"zncc_config.json"));
        var d=BaseConfig.StageComparison.Document;var oldTau=d.AuxiliaryDelayMicroseconds;d.AuxiliaryDelayMicroseconds=200*d.GroupIndex/299792458.0*1e6;d.EndMeters=23;
        var ratio=d.AuxiliaryDelayMicroseconds/oldTau;var watch=Stopwatch.StartNew();string error=null;var fft=new FftEngine();
        Save("config.json",new Dictionary<string,object> {{"source_run",InputRoot},{"source_cases_sha256",Hash(Path.Combine(InputRoot,"cases.json"))},{"confirmed_auxiliary_path_difference_m",200.0},{"confirmed_measurement_length_m",23.0},{"heated_position_and_length","unknown"},{"group_index_is_provisional",true},{"axis_scale_ratio",ratio},{"scope","User-confirmed geometry update. All ZNCC pairs recomputed from the unchanged complete-source-prepared longest continuous arrays. Reflectogram FFT coefficients are unchanged: distance coordinates multiply by tau_new/tau_old and power density divides by the same factor, preserving integrated power. Full-source native processing provenance is retained in source_run."}});
        var reflect=JsonContractSerializer.Read<ReflectogramSettings>(Path.Combine(InputRoot,"reflectogram_config.json"));reflect.AuxiliaryDelayMicroseconds=d.AuxiliaryDelayMicroseconds;reflect.GridMeters*=ratio;reflect.EndMeters*=ratio;
        Save("reflectogram_config.json",reflect);Save("zncc_config.json",Config());File.WriteAllText(Path.Combine(Output,"core_sha256.txt"),Hash(typeof(FftEngine).Assembly.Location));File.WriteAllText(Path.Combine(Output,"summary.md"),"# RUNNING\n");
        try {
            var sm=JsonContractSerializer.Read<Dictionary<string,object>>(Path.Combine(InputRoot,"metrics.json"));
            if(!Environment.Is64BitProcess || !fft.NativeProviderAvailable || !Equals(sm["status"],"COMPLETED") || File.ReadAllText(Path.Combine(InputRoot,"core_sha256.txt")).Trim()!=Hash(typeof(FftEngine).Assembly.Location))throw new Exception("Complete native source provenance required");
            foreach(var old in JsonContractSerializer.Read<List<Case>>(Path.Combine(InputRoot,"cases.json"))) {
                var item=new Case {Id=old.Id,Directory=old.Directory,Sources=old.Sources};Cases.Add(item);
                foreach(var s in item.Sources)foreach(var r in s.Ranges)r.CorrectedNativeBinMeters*=ratio;
                foreach(var p in old.Pairs)Compare(item,old.Sources.First(s=>s.Id==p.Reference),old.Sources.First(s=>s.Id==p.Signal),p.Kind);
                Save("cases.json",Cases);
            }
        }catch(Exception ex){error=ex.ToString();Console.Error.WriteLine(error);}
        finally {
            var failures=Cases.Sum(c=>c.Pairs.Count(p=>p.Error!=null));Save("cases.json",Cases);
            Save("metrics.json",new Dictionary<string,object> {{"status",error!=null || failures>0?"FAILED":"COMPLETED"},{"error",error},{"failures",failures},{"files",Cases.Sum(c=>c.Sources.Count)},{"unique_files",Cases.SelectMany(c=>c.Sources).Select(s=>s.Hash).Distinct().Count()},{"directories",Cases.Count},{"pairs",Cases.Sum(c=>c.Pairs.Count)},{"seconds",watch.Elapsed.TotalSeconds},{"provider",fft.ProviderDescription}});
            File.WriteAllText(Path.Combine(Output,"summary.md"),"# "+(error==null && failures==0?"COMPLETED":"FAILED")+"\n\nUser confirmed AUX 200 m and measurement 23 m. Recomputed every ZNCC pair with the exact new delay and 23 m endpoint from unchanged full-source-prepared arrays. Reflectogram coordinate/density conversion is recorded separately; original full native FFT results are preserved.\n"+error);
        }
        return error==null?0:1;
    }
}



