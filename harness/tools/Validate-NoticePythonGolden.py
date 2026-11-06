"""Full 5M golden check of the independent Python implementation against G002."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import time
import traceback

spec = importlib.util.spec_from_file_location("notice", Path(__file__).with_name("Analyze-Notice50M.py"))
notice = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notice)
np, ROOT, write = notice.np, notice.ROOT, notice.write


def read_isf(path):
    payload = path.read_bytes()
    marker = payload.index(b":CURVE #")
    head = payload[:marker].decode("ascii")
    fields = {k: re.search(r"(?:^|;)(?::WFMPRE:)?"+k+r"\s+([^;]+)", head).group(1).strip() for k in
              ["BYT_NR", "BN_FMT", "BYT_OR", "NR_PT", "YMULT", "YZERO", "YOFF", "XINCR"]}
    if fields["BYT_NR"] != "2" or fields["BN_FMT"] != "RI" or fields["BYT_OR"] != "MSB":
        raise ValueError(f"Unsupported ISF encoding: {fields}")
    n = int(fields["NR_PT"])
    at = marker+len(b":CURVE #")
    digits = int(chr(payload[at]))
    bytecount = int(payload[at+1:at+1+digits])
    if bytecount != n*2 or n != 5000000:
        raise ValueError("ISF length is not the golden 5M contract")
    raw = np.frombuffer(payload, dtype=">i2", count=n, offset=at+1+digits)
    values = (raw.astype(np.float64)-float(fields["YOFF"]))*float(fields["YMULT"])+float(fields["YZERO"])
    return values, dict(path=str(path.resolve()), sha256=notice.digest(path), header=head, fields=fields, numeric_samples=n)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--output", type=Path, required=True)
    args = ap.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    c = json.loads((ROOT/"harness/configs/notice_50msps_python_4096_128_100m_23m.json").read_text(encoding="utf-8"))
    c.update(segment_size=1024, segment_hop=256, auxiliary_path_difference_m=18.2703,
             measurement_cable_m=8.7, analysis_start_m=3.5, sample_interval_s=40e-9,
             golden_reference="20260828_dual_aux_csharp_series_regression, 0.4 mm / repetition 0000 / CH2")
    write(out/"config.json", c)
    started = time.perf_counter()
    fft = None
    try:
        fft = notice.NativeMkl(ROOT/c["native_mkl_dll"])
        prepared, sources = [], []
        dataroot = Path(r"C:\Project\OFDR\Datas\OFDR Raw data")
        for prefix in ["0_0mm0000", "0_4mm0000"]:
            aux, ma = read_isf(dataroot/f"{prefix}CH1.isf")
            sig, ms = read_isf(dataroot/f"{prefix}CH2.isf")
            sources += [ma, ms]
            phase, anchors, qm = notice.phase_map(aux, fft, c["max_nonmonotonic_ratio"], c["sample_interval_s"])
            values = np.interp(np.linspace(0, phase[-1], len(sig)), phase, sig[anchors])
            prepared.append(dict(values=values, phase_span=qm["phase_span_rad"], metrics=qm))
            del aux, sig, phase, anchors
        write(out/"dataset_manifest.json", sources)
        rs, ss, span = notice.pair_spectra(*prepared, fft)
        p, axis = notice.profile(rs, ss, span, fft, c)
        p.to_csv(out/"profile.csv", index=False)
        inside = p[(p.distance_m >= 5.5) & (p.distance_m <= 6.5)]
        outside = p[((p.distance_m >= 3.7) & (p.distance_m <= 5.3)) |
                    ((p.distance_m >= 6.7) & (p.distance_m <= 8.4))]
        dx = axis["distance_m_per_bin"]
        x = np.arange(len(rs)//2)*dx
        # The archived aggregate calls this ReferenceFftSlope, but its numbers
        # actually match the archived SignalSpectrum preview. Match that trace
        # and that min/max display reduction explicitly, not a full-bin median.
        db = 20*np.log10(np.maximum(np.abs(ss[:len(ss)//2]), 1e-300)/np.max(np.abs(ss[:len(ss)//2])))
        early = float(np.median(db[(x >= 4) & (x <= 4.5)]))
        late = float(np.median(db[(x >= 7.5) & (x <= 8)]))
        preview = []
        for bucket in range(5000):
            part = db[bucket*len(db)//5000:(bucket+1)*len(db)//5000]
            mi, ma = int(np.argmin(part)), int(np.argmax(part))
            preview.extend([float(part[mi]), float(part[ma])] if mi <= ma else [float(part[ma]), float(part[mi])])
        px = np.linspace(0, x[-1], len(preview))
        preview = np.asarray(preview)
        pe = float(np.median(preview[(px >= 4) & (px <= 4.5)]))
        pl = float(np.median(preview[(px >= 7.5) & (px <= 8)]))
        golden = json.loads((ROOT/"harness/results/20260828_dual_aux_csharp_series_regression/metrics.json").read_text())
        expected = next(s for s in golden["Series"] if s["DisplacementMm"] == .4)
        metrics = dict(status="PASS", **axis, inside_median_ghz=float(inside.shift_ghz.median()),
                       outside_median_ghz=float(outside.shift_ghz.median()),
                       expected_inside_median_ghz=expected["InsideMedianGHz"],
                       expected_outside_median_ghz=expected["OutsideMedianGHz"],
                       early_band_median_db=early, late_band_median_db=late, slope_band_difference_db=late-early,
                       ui_preview_early_band_median_db=pe, ui_preview_late_band_median_db=pl,
                       ui_preview_slope_band_difference_db=pl-pe,
                       expected_slope_band_difference_db=golden["ReferenceFftSlope"]["DifferenceDb"],
                       golden_slope_source="Archived SignalSpectrum min/max preview; historical aggregate key ReferenceFftSlope is mislabeled",
                       native_fft_calls=fft.calls, source_samples_each=5000000,
                       reference_phase_qc=prepared[0]["metrics"], signal_phase_qc=prepared[1]["metrics"],
                       elapsed_seconds=time.perf_counter()-started,
                       analyzer_sha256=notice.digest(Path(notice.__file__)), script_sha256=notice.digest(Path(__file__)))
        metrics["pass_all"] = bool(abs(metrics["inside_median_ghz"]-expected["InsideMedianGHz"]) < .05 and
                                   abs(metrics["outside_median_ghz"]-expected["OutsideMedianGHz"]) < .05 and
                                   abs((pl-pe)-golden["ReferenceFftSlope"]["DifferenceDb"]) < .1 and
                                   all(p["metrics"]["phase_qc_passed"] for p in prepared))
        if not metrics["pass_all"]:
            write(out/"comparison_metrics.json", metrics)
            raise RuntimeError("Golden regression failed: see comparison_metrics.json")
        write(out/"metrics.json", metrics)
        (out/"summary.md").write_text("# PASS\n\nFull original 5M/channel processing reproduced the C# 0.4 mm plateau, outside zero baseline, and distance FFT slope.\n", encoding="utf-8")
        fig, ax = notice.plt.subplots(2, 1, figsize=(10, 7), constrained_layout=True)
        ax[0].plot(p.distance_m, p.shift_ghz, lw=1)
        ax[0].axvspan(5.5, 6.5, color="orange", alpha=.2)
        ax[0].set(xlabel="Distance (m)", ylabel="Shift (GHz)", title="Real 5M golden: 0.0 mm vs 0.4 mm, segment 1024 / hop 256")
        ix = np.flatnonzero((x >= 3.5) & (x <= 8.7))[::3]
        ax[1].plot(x[ix], db[ix], lw=.5)
        ax[1].set(xlabel="Distance (m)", ylabel="FFT amplitude (dB)", ylim=(-110, -45))
        for a in ax:
            a.grid(alpha=.2)
        fig.savefig(out/"golden_regression.png", dpi=150)
        notice.plt.close(fig)
        print(json.dumps(metrics, indent=2))
    except BaseException as exc:
        write(out/"metrics.json", dict(status="FAIL", error=repr(exc), elapsed_seconds=time.perf_counter()-started))
        (out/"summary.md").write_text("# FAIL\n\n"+traceback.format_exc(), encoding="utf-8")
        raise
    finally:
        if fft is not None:
            fft.close()


if __name__ == "__main__":
    main()
