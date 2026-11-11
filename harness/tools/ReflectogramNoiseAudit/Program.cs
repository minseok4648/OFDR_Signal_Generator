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

internal static class Program
{
    private const double C = 299792458, Ng = 1.4682, Tau = .979e-6, Grid = .004;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly List<Dictionary<string, string>> Cases = new List<Dictionary<string, string>>();
    private static FftEngine fft;
    private static string output;
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew(); string error = null;
        JsonContractSerializer.Write(Path.Combine(output, "config.json"), new Dictionary<string, string> {
            ["inputs"] = string.Join("|", args.Skip(1)), ["scope"] = "read-only FFT-stage attribution, not full correlation or exact PDF reproduction",
            ["normal_mode"] = "production AuxKLinearizer each sweep independently, no global window, diagnostic QC retained; whole unmasked and CH2-masked joined",
            ["mask"] = "CH2 > 1.8 V + 100 us tail", ["segmented"] = "every continuous range >= 1 ms; AUX 1-12 MHz zero-phase filter; 2 us guard; 65536 points; same phase for Rectangular/Hann",
            ["distance"] = "tau_aux=.979us; ng=1.4682; alternative GUI default 18.2703m axis also exported for normal mode",
            ["power"] = "one-sided |FFT|^2 * 2 / (N * sum(window^2) * distance_bin); average linear power before dB",
            ["noise_roi_m"] = "26-35", ["common_grid_m"] = Grid.ToString(CultureInfo.InvariantCulture), ["averages"] = "1,4,16,all", ["ui_changes"] = "none" });
        try
        {
            fft = new FftEngine();
            if (!Environment.Is64BitProcess || !fft.NativeProviderAvailable) throw new Exception("x64 native MKL required");
            foreach (var path in args.Skip(1)) { ProcessFile(path); GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
        finally
        {
            JsonContractSerializer.Write(Path.Combine(output, "cases.json"), Cases);
            JsonContractSerializer.Write(Path.Combine(output, "metrics.json"), new Dictionary<string, string> { ["status"] = error == null ? "COMPLETED" : "FAILED", ["error"] = error,
                ["seconds"] = clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture), ["provider"] = fft?.ProviderDescription,
                ["peak_working_set"] = Process.GetCurrentProcess().PeakWorkingSet64.ToString() });
            File.WriteAllText(Path.Combine(output, "summary.md"), "# Reflectogram noise-width attribution\n\nStatus: " + (error == null ? "COMPLETED" : "FAILED")
                + "\n\nFull raw CSV inputs; native FFT; production general-mode single-sweep phase extraction. Controlled segment power-average experiments use an explicit assumed document configuration, not the unavailable PDF generation script. Normal Ref/Sig common-span pairing and downstream correlation are outside this isolated test. No production algorithm or GUI settings changed.\n\n" + error);
        }
        return error == null ? 0 : 1;
    }
    private static void ProcessFile(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        Console.WriteLine("READ " + id);
        var raw = new TekScopeCsvReader().Read(path, Token);
        if (raw.IsPartialRead || !raw.HasFourChannels) throw new Exception("Full four-channel input required");
        JsonContractSerializer.Write(Path.Combine(output, id + "_manifest.json"), new Dictionary<string, string> { ["path"] = path, ["sha256"] = raw.Sha256, ["samples"] = raw.SampleCount.ToString(), ["dt"] = raw.SampleIntervalSeconds.ToString("R", CultureInfo.InvariantCulture) });
        JsonContractSerializer.Write(Path.Combine(output, id + "_header.json"), raw.Header);
        var mask = new ModeHopPreviewEngine().Detect(raw, new ModeHopSettings { ThresholdVoltage = 1.8, ExclusionTailSeconds = .0001 }, Token);
        JsonContractSerializer.Write(Path.Combine(output, id + "_mask.json"), mask);
        General(id + "_whole_unmasked", raw.Auxiliary, raw.Measurement);
        {
            var stitched = new AuxiliaryQualityEngine().Stitch(raw, mask, Token);
            var measurement = stitched.OriginalIndices.Select(i => raw.Measurement[i]).ToArray();
            General(id + "_masked_joined", stitched.Values, measurement);
        }
        GC.Collect();
        var ranges = new List<Tuple<int, int>>(); int cursor = 0;
        foreach (var r in mask.Ranges) { if ((r.StartIndex - cursor) * raw.SampleIntervalSeconds >= .001) ranges.Add(Tuple.Create(cursor, r.StartIndex)); cursor = r.EndIndexExclusive; }
        if ((raw.SampleCount - cursor) * raw.SampleIntervalSeconds >= .001) ranges.Add(Tuple.Create(cursor, raw.SampleCount));
        var sums = new[] { new double[10001], new double[10001] }; int used = 0;
        var stage = new StageComparisonEngine(); var spans = new List<double>();
        foreach (var range in ranges)
        {
            var a = raw.Auxiliary.Skip(range.Item1).Take(range.Item2 - range.Item1).ToArray();
            var m = raw.Measurement.Skip(range.Item1).Take(range.Item2 - range.Item1).ToArray();
            var settings = new DocumentStageSettings { Enabled = true, BandpassEnabled = true };
            var pair = DocumentStagePreprocessor.Process(new OfdrChannelData { Values = a, SampleIntervalSeconds = raw.SampleIntervalSeconds }, new OfdrChannelData { Values = m, SampleIntervalSeconds = raw.SampleIntervalSeconds }, settings, true, 16, Token);
            using (var plan = stage.Prepare(pair.Auxiliary, pair.Measurement, new StageComparisonSettings(), 16, Token))
            {
                var x = stage.Interpolate(plan, 65536, plan.Span, Token); spans.Add(plan.Span);
                var dz = C / (2 * Ng * Math.Abs(plan.Span) / (2 * Math.PI * Tau) * x.Length / (x.Length - 1.0));
                for (var w = 0; w < 2; w++)
                {
                    var spectrum = new Complex[x.Length]; double energy = 0;
                    for (var i = 0; i < x.Length; i++) { var weight = w == 0 ? 1 : IndependentOfdrProcessingEngine.WindowValue("Hann", i, x.Length); spectrum[i] = x[i] * weight; energy += weight * weight; }
                    fft.ForwardInPlace(spectrum);
                    var power = Power(spectrum, dz, energy);
                    if (used == 0) Save(id + "_segment_" + (w == 0 ? "rect" : "hann") + "_native", power, dz, plan.Span, plan.Metrics.NonmonotonicRatio);
                    for (var j = 0; j < sums[w].Length; j++) { var t = j * Grid / dz; int k = (int)t; if (k + 1 >= power.Length) throw new Exception("Common distance grid outside spectrum"); sums[w][j] += power[k] + (power[k + 1] - power[k]) * (t - k); }
                }
            }
            used++;
            if (used == 1 || used == 4 || used == 16 || used == ranges.Count)
                for (var w = 0; w < 2; w++) Save(id + "_" + (w == 0 ? "rect" : "hann") + "_power_mean_" + used, sums[w].Select(p => p / used).ToArray(), Grid, spans.Average(), 0);
            Console.WriteLine(id + " SEGMENT " + used + "/" + ranges.Count);
        }
        JsonContractSerializer.Write(Path.Combine(output, id + "_segment_ranges.json"), ranges.Select((r,i)=> new Dictionary<string,string> { ["start"] = r.Item1.ToString(), ["end"] = r.Item2.ToString(), ["phase_span"] = spans[i].ToString("R",CultureInfo.InvariantCulture) }).ToList());
    }
    private static void General(string name, double[] a, double[] m)
    {
        Console.WriteLine("GENERAL " + name + " n=" + a.Length);
        var result = new AuxKLinearizer(fft).LinearizeDetailed(a, m, m, 1e-5, 16, Token, allowFailedPhaseQcForDiagnostics:true);
        var x = result.Linearized.XPolarization;
        result.Linearized.Reference = null; result.Linearized.YPolarization = null;
        var spectrum = new Complex[x.Length]; for (var i = 0; i < x.Length; i++) spectrum[i] = x[i];
        fft.ForwardInPlace(spectrum);
        var phase = result.Linearized.Metrics.PhaseSpanRadians;
        var dz = C / (2 * Ng * phase / (2 * Math.PI * Tau) * x.Length / (x.Length - 1.0));
        Save(name, Power(spectrum, dz, x.Length), dz, phase, result.Linearized.Metrics.NonmonotonicRatio);
        var guiDz = Math.PI * 18.2703 / phase;
        Save(name + "_gui18m_axis", Power(spectrum, guiDz, x.Length), guiDz, phase, result.Linearized.Metrics.NonmonotonicRatio);
        GC.Collect();
    }
    private static double[] Power(Complex[] spectrum, double dz, double windowEnergy)
    {
        var p = new double[spectrum.Length / 2]; double scale = 2 / (spectrum.Length * windowEnergy * dz);
        for (var i = 0; i < p.Length; i++) { var mag = spectrum[i].Magnitude; p[i] = mag * mag * scale; }
        p[0] *= .5; return p;
    }
    private static void Save(string name, double[] power, double dz, double span, double qc)
    {
        var noise = power.Skip((int)Math.Ceiling(26 / dz)).Take((int)Math.Floor(35 / dz) - (int)Math.Ceiling(26 / dz) + 1).Select(Db).OrderBy(v=>v).ToArray();
        Func<double, double> q = f => noise[(int)Math.Round(f * (noise.Length - 1))];
        var row = new Dictionary<string,string> { ["case"] = name, ["distance_bin_m"] = dz.ToString("R",CultureInfo.InvariantCulture), ["phase_span"] = span.ToString("R",CultureInfo.InvariantCulture), ["phase_rejection_ratio"] = name.Contains("_power_mean_") ? "not_applicable" : qc.ToString("R",CultureInfo.InvariantCulture),
            ["noise_bins"] = noise.Length.ToString(), ["noise_min_db"] = noise.First().ToString("R",CultureInfo.InvariantCulture), ["noise_max_db"] = noise.Last().ToString("R",CultureInfo.InvariantCulture),
            ["noise_p05_db"] = q(.05).ToString("R",CultureInfo.InvariantCulture), ["noise_p95_db"] = q(.95).ToString("R",CultureInfo.InvariantCulture), ["noise_p50_db"] = q(.5).ToString("R",CultureInfo.InvariantCulture),
            ["width_p95_p05_db"] = (q(.95)-q(.05)).ToString("R",CultureInfo.InvariantCulture), ["width_max_min_db"] = (noise.Last()-noise.First()).ToString("R",CultureInfo.InvariantCulture) };
        Cases.Add(row); JsonContractSerializer.Write(Path.Combine(output,"cases.json"),Cases);
        var fullMax = power.Max();
        using(var writer = new StreamWriter(Path.Combine(output,name+".csv")))
        {
            writer.WriteLine("distance_m,power_density_v2_per_m,db_density,db_global_max");
            for(var i=0;i<power.Length && i*dz<=40;i++) writer.WriteLine(string.Join(",", new[] {i*dz,power[i],Db(power[i]),Db(power[i]/fullMax)}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))));
        }
        Console.WriteLine(name + " width90=" + row["width_p95_p05_db"] + " extremes=" + row["width_max_min_db"]);
    }
    private static double Db(double value) => 10 * Math.Log10(Math.Max(1e-300,value));
}
