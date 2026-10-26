# Independent channel Ref/Sig processing

- Status: `COMPLETED`
- Recorded: 2026-08-26T10:02:50.7432175+09:00
- Wall time: 7.382 s
- Aux: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv`
- Ref: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH2.csv`
- Sig: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH2.csv`
- Polarization: Xpol (CH2)
- Samples: 5000000
- Segment count: 156249
- Read time: 4.960 s
- CH1 nonmonotonic ratio: 0
- Selected peak lag: -3 bin
- FFT provider: Math.NET Numerics Configuration: Version 5.0.0 Built for .NET Framework 4.8 Linear Algebra Provider: Intel MKL (x64; revision 15; MKL 2022.0) Fourier Transform Provider: Intel MKL (x64; revision 15; MKL 2022.0) Sparse Solver Provider: Intel MKL (x64; revision 15; MKL 2022.0) Max Degree of Parallelism: 24 Parallelize Elements: 300 Parallelize Order: 64 Check Distribution Parameters: True Thread-Safe RNGs: True Operating System: Microsoft Windows 10.0.19045  Operating System Architecture: X64 Framework: .NET Framework 4.8.9310.0 Process Architecture: X64 Processor Architecture: AMD64 Processor Identifier: Intel64 Family 6 Model 151 Stepping 2, GenuineIntel 

## Interpretation

Auxiliary CH1, baseline Ref, and displaced Sig completed through independent loading, K-linearization, windowed FFT, segmented Ref×Sig cross correlation, peak selection, and zoom preview generation.

## Decision

`ACCEPT`. With representative segment selection changed to maximum correlation peak magnitude, the same 5M inputs completed with a non-boundary peak at -3 bin and no OOM.
