# 0_0mm0000

- Status: `COMPLETED`
- Decision: `NEEDS_MORE_TEST`
- Displacement: 0.0 mm
- Repetition: 0000
- Recorded: 2026-08-25T11:06:19.5532128+09:00
- FFT provider: Math.NET Numerics Configuration: Version 5.0.0 Built for .NET Framework 4.8 Linear Algebra Provider: Intel MKL (x64; revision 15; MKL 2022.0) Fourier Transform Provider: Intel MKL (x64; revision 15; MKL 2022.0) Sparse Solver Provider: Intel MKL (x64; revision 15; MKL 2022.0) Max Degree of Parallelism: 24 Parallelize Elements: 300 Parallelize Order: 64 Check Distribution Parameters: True Thread-Safe RNGs: True Operating System: Microsoft Windows 10.0.19045  Operating System Architecture: X64 Framework: .NET Framework 4.8.9310.0 Process Architecture: X64 Processor Architecture: AMD64 Processor Identifier: Intel64 Family 6 Model 151 Stepping 2, GenuineIntel 
- CH1 phase span: 1375715.21 rad
- CH1 nonmonotonic ratio: 0
- Xpol segments: 156249
- Ypol segments: 156249

## Interpretation

Baseline completed. Physical distance calibration is not applied; inspect normalized-k/FFT/lag-bin repeatability before accepting an algorithm change.

Hypothesis check: release the interpolated 5M final profiles after preview/detection construction. The run completed with the same QC and feature counts; serialization still included segment working arrays and was refined once more.
