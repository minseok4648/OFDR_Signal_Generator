# Dual-sweep CH1 C# series regression

- Status: `COMPLETED`
- Decision: `ACCEPT`
- Input: repetition 0000, CH2/Xpol, 0.0 mm reference, 0.1~0.9 mm signals
- Samples: 5,000,000 per channel
- Config: Hann / segment 1024 / hop 256 / padding 16 / analysis 3.5~8.7 m / ±180 GHz
- K-linearization: reference and signal sweeps use their own simultaneously acquired CH1, then a common relative-phase span

## Observation

The 5.5~6.5 m medians were 3.579, 8.314, 15.535, 25.985, 37.309, 50.197, 64.040, 77.753, and 91.937 GHz. These reproduce the approximate 3.6~91.9 GHz plateaus in the user-supplied reference image. Outside medians remained within ±0.116 GHz.

The 0.4 mm K-linearized distance FFT decreased by 10.385 dB from the 4.0~4.5 m band to the 7.5~8.0 m band, retaining the expected cable-region distributed-backscatter slope.

## Interpretation

The former single-auxiliary grid did not align independently acquired sweeps. Per-sweep CH1 phase correction, common relative-phase cropping, physical-path/group-index spectral calibration, restricted correlation search, and fractional peak interpolation restore the distributed Rayleigh spectral-shift profile.

## Decision

`ACCEPT` for CH2/repetition 0000 K-linearization and spectral-shift processing. Absolute strain remains `NEEDS_MORE_TEST` until center wavelength and fiber strain coefficient are confirmed.

