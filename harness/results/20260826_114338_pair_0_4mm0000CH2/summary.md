# Independent channel Ref/Sig processing

- Status: `COMPLETED`
- Recorded: 2026-08-26T11:43:44.6103301+09:00
- Wall time: 6.024 s
- Aux: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv`
- Ref: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH2.csv`
- Sig: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH2.csv`
- Polarization: Xpol (CH2)
- Samples: 5000000
- Segment count: 749
- Read time: 4.930 s
- CH1 nonmonotonic ratio: 0
- Selected peak lag: -1 bin
- Selected segment center: 5.68621189 m
- Selected peak strain: 186.050189 microstrain
- Expected applied strain from stage displacement: 384.615385 microstrain
- Distance scale: 4.17221821E-05 m/FFT bin
- Strain scale: -186.050189 microstrain/lag bin
- FFT provider: Math.NET Numerics Configuration: Version 5.0.0 Built for .NET Framework 4.8 Linear Algebra Provider: Intel MKL (x64; revision 15; MKL 2022.0) Fourier Transform Provider: Intel MKL (x64; revision 15; MKL 2022.0) Sparse Solver Provider: Intel MKL (x64; revision 15; MKL 2022.0) Max Degree of Parallelism: 24 Parallelize Elements: 300 Parallelize Order: 64 Check Distribution Parameters: True Thread-Safe RNGs: True Operating System: Microsoft Windows 10.0.19045  Operating System Architecture: X64 Framework: .NET Framework 4.8.9310.0 Process Architecture: X64 Processor Architecture: AMD64 Processor Identifier: Intel64 Family 6 Model 151 Stepping 2, GenuineIntel 

## Interpretation

Auxiliary CH1, baseline Ref, and displaced Sig completed through independent loading, K-linearization, full FFT, spatial segmentation, segment windowing, local-spectrum IFFT, Ref/Sig cross correlation, peak selection, and zoom preview generation.
Distance and strain values use the recorded calibration factors. Center wavelength, fiber strain coefficient, and distance geometry factor must be confirmed before treating the values as an absolute calibration.

## Decision

`ACCEPT` for pipeline/runtime operation. The final 5M path completed without OOM using native MKL. The selected segment peak is `-1 bin` and the 5.5–6.5 m profile median is `-2 bin`, corresponding to approximately `372.10 με` with the provisional factors versus the mechanical reference `384.62 με`. Absolute strain remains calibration-dependent because the actual center wavelength, fiber strain coefficient, distance geometry, and the recorded sweep-range unit still require equipment confirmation.
