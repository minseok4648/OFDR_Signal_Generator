"""Validate C# against the supplied notebook's original Python class, using native MKL.

No source notebook cells are executed. Only the identified ofdr methods are called.
The historical index/lag plot is deliberately kept separate from physical calibration.
"""
from __future__ import annotations
import argparse
import importlib.util
import json
from pathlib import Path
import time
import traceback
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("notice", Path(__file__).with_name("Analyze-Notice50M.py"))
notice = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notice)
np, pd, plt, write = notice.np, notice.pd, notice.plt, notice.write


def original_processor(source_dir, fft):
    spec = importlib.util.spec_from_file_location("supplied_ofdr", source_dir / "ofdr.py")
    source = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(source)

    def hilbert(values):
        n = len(values)
        transformed = fft.fft(values)
        multiplier = np.zeros(n)
        multiplier[0] = 1
        multiplier[1:(n + 1)//2] = 2
        if n % 2 == 0:
            multiplier[n//2] = 1
        return fft.fft(transformed * multiplier, inverse=True)

    # Keep original sorting/interpolation, Hann, segmentation and peak extraction.
    # Replace only FFT execution so full-record work satisfies the native MKL rule.
    source.hilbert = hilbert
    original_fft = SimpleNamespace(fft=lambda x: fft.fft(x),
                                   ifft=lambda x, n=None: fft.fft(x, inverse=True, n=n),
                                   fftshift=np.fft.fftshift)
    class NumpyProxy:
        fft = original_fft
        def __getattr__(self, name):
            return getattr(np, name)
    source.np = NumpyProxy()
    return source.ofdr()


def raw_crop(path, source, config):
    header = []
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            header.append(line.rstrip())
            if line.startswith("TIME,"):
                break
        else:
            raise ValueError("Missing TIME header")
    # Independent parser; validate the complete source record and its hash again.
    frame = pd.read_csv(path, skiprows=len(header)-1)
    if len(frame) != source["Samples"] or notice.digest(path) != source["Sha256"]:
        raise ValueError("Source count/hash differs from the C# run")
    first = int(np.flatnonzero(frame.CH1.to_numpy() > config["TriggerVoltage"])[0])
    start = first + config["StartOffsetSamples"]
    stop = start + config["InputSamples"]
    if stop > len(frame):
        raise ValueError("Incomplete notebook crop")
    aux = frame.CH3.to_numpy()[start+config["AuxiliarySliceStart"]:stop-config["MeasurementSliceStart"]].copy()
    sig = frame.CH4.to_numpy()[start+config["MeasurementSliceStart"]:stop-config["AuxiliarySliceStart"]].copy()
    if not np.isfinite(aux).all() or not np.isfinite(sig).all():
        raise ValueError("Nonfinite notebook crop")
    return aux, sig, first, start


def main():
    ap = argparse.ArgumentParser(__doc__)
    ap.add_argument("--result", type=Path, required=True)
    ap.add_argument("--source-dir", type=Path, default=Path(r"D:\project\OFDR\signal processing\OFDR"))
    ap.add_argument("--native-dll", type=Path, default=notice.ROOT/"build_verify_261106_notebook/libMathNetNumericsMKL.dll")
    args = ap.parse_args()
    result = args.result.resolve()
    out = result / "python_validation"
    out.mkdir(exist_ok=False)
    c = json.loads((result/"config.json").read_text(encoding="utf-8-sig"))
    manifest = json.loads((result/"dataset_manifest.json").read_text(encoding="utf-8-sig"))
    write(out/"config.json", dict(source_directory=str(args.source_dir), native_dll=str(args.native_dll),
                                 klinear_max_abs_tolerance=2e-7, profile_lag_max_abs_tolerance=.05,
                                 original_source_sha256=notice.digest(args.source_dir/"ofdr.py"),
                                 original_notebook_sha256=notice.digest(args.source_dir/"30-CSV-TO_OFDR.ipynb"),
                                 reproduction_config=c))
    started = time.perf_counter()
    fft = None
    try:
        fft = notice.NativeMkl(args.native_dll)
        processor = original_processor(args.source_dir, fft)
        signals, checks = [], []
        for source in manifest:
            print("Original Python k-linearization:", source["Path"], flush=True)
            aux, sig, trigger, start = raw_crop(Path(source["Path"]), source, c)
            original = processor.k_lin_1ch(aux, sig)
            del aux, sig
            actual = np.fromfile(result/(source["Key"]+"_klinear.bin"), dtype="<f8")
            error = np.abs(actual-original)
            check = dict(key=source["Key"], samples=len(actual), max_abs_error=float(error.max()),
                         rms_error=float(np.sqrt(np.mean(error**2))), trigger_index=trigger, crop_start=start,
                         trigger_matches=trigger == source["Processing"]["TriggerIndex"],
                         crop_matches=start == source["Processing"]["CropStartIndex"])
            check["pass"] = bool(actual.shape == original.shape and error.max() <= 2e-7 and check["trigger_matches"] and check["crop_matches"])
            checks.append(check)
            signals.append(original)
            print(check, flush=True)
        profile_checks = []
        fig, ax = plt.subplots(figsize=(19.2, 9.81))
        for i, label in enumerate(c["Labels"]):
            print("Original Python full profile:", label, flush=True)
            position, shifts = processor.ofdr_spectral_shift_1ch(signals[0], signals[i+1],
                segment_size=c["SegmentSize"], step=c["SegmentStep"], zero_pad_factor=c["ZeroPadFactor"],
                bin=[c["FirstBin"], c["StopBin"]], interpol=True)
            actual = pd.read_csv(result/f"measurement_{i}_profile.csv")
            a = actual.shift_samples.to_numpy()
            valid = np.isfinite(shifts)
            error = np.abs(a[valid]-shifts[valid])
            profile_checks.append(dict(label=label, points=len(shifts), valid_points=int(valid.sum()),
                max_abs_lag_error=float(error.max()), rms_lag_error=float(np.sqrt(np.mean(error**2))),
                edge_nan_mask_matches=bool(np.array_equal(np.isnan(a), np.isnan(shifts))),
                positions_match=bool(np.array_equal(position, actual.fft_bin)),
                plateau_index_2600_2900_median=float(np.nanmedian(a[2600:2901])),
                baseline_index_600_1400_median=float(np.nanmedian(a[600:1401])),
                passed=bool(error.max() <= .05 and np.array_equal(np.isnan(a), np.isnan(shifts)) and np.array_equal(position, actual.fft_bin))))
            pd.DataFrame(dict(fft_bin=position, shift_samples=shifts)).to_csv(out/f"original_{i}_profile.csv", index=False)
            ax.plot(a, label=label)
        ax.legend()
        fig.savefig(result/"temperature_reproduction.png", dpi=100)
        ax.set(xlabel="Profile index (FFT bin = index x 50)", ylabel="Local-spectrum correlation lag (samples)",
               title="261106 notebook reproduction: 60 / 80 / 100 C versus tek0002")
        fig.savefig(result/"temperature_reproduction_labeled.png", dpi=100)
        plt.close(fig)
        metrics = dict(status="PASS" if all(v["pass"] for v in checks) and all(v["passed"] for v in profile_checks) else "FAIL",
                       signal_checks=checks, profile_checks=profile_checks, native_fft_calls=fft.calls,
                       elapsed_seconds=time.perf_counter()-started)
        write(out/"metrics.json", metrics)
        (out/"summary.md").write_text("# "+metrics["status"]+"\n\nOriginal supplied ofdr.py methods were evaluated against complete original CSV inputs, with native MKL FFT substitution only.\n\n"+
            "All k-linearized values, all profile positions, NaN edge masks and fractional lags were compared. The plot uses C# outputs.\n", encoding="utf-8")
        print(json.dumps(metrics, indent=2))
        if metrics["status"] != "PASS":
            raise RuntimeError("Original Python comparison failed")
    except BaseException:
        if not (out/"metrics.json").exists():
            write(out/"metrics.json", dict(status="FAIL", error=traceback.format_exc()))
        (out/"summary.md").write_text("# FAIL\n\n"+traceback.format_exc(), encoding="utf-8")
        raise
    finally:
        if fft is not None:
            fft.close()


if __name__ == "__main__":
    main()
