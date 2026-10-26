# Independent channel Ref/Sig processing

- Status: `COMPLETED`
- Recorded: 2026-08-26T11:38:29.0464931+09:00
- Wall time: 5.863 s
- Aux: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv`
- Ref: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH2.csv`
- Sig: `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH2.csv`
- Polarization: Xpol (CH2)
- Samples: 5000000
- Segment count: 749
- Read time: 4.806 s
- CH1 nonmonotonic ratio: 0
- Selected peak lag: 13 bin
- Selected segment center: 6.12946835 m
- Selected peak strain: -2418.65246 microstrain
- Expected applied strain from stage displacement: 384.615385 microstrain
- Distance scale: 4.17221821E-05 m/FFT bin
- Strain scale: -186.050189 microstrain/lag bin
- FFT provider: Math.NET Numerics Configuration: Version 5.0.0 Built for .NET Framework 4.8 Linear Algebra Provider: Intel MKL (x64; revision 15; MKL 2022.0) Fourier Transform Provider: Intel MKL (x64; revision 15; MKL 2022.0) Sparse Solver Provider: Intel MKL (x64; revision 15; MKL 2022.0) Max Degree of Parallelism: 24 Parallelize Elements: 300 Parallelize Order: 64 Check Distribution Parameters: True Thread-Safe RNGs: True Operating System: Microsoft Windows 10.0.19045  Operating System Architecture: X64 Framework: .NET Framework 4.8.9310.0 Process Architecture: X64 Processor Architecture: AMD64 Processor Identifier: Intel64 Family 6 Model 151 Stepping 2, GenuineIntel 

## Interpretation

Auxiliary CH1, baseline Ref, and displaced Sig completed through independent loading, K-linearization, full FFT, spatial segmentation, segment windowing, local-spectrum IFFT, Ref/Sig cross correlation, peak selection, and zoom preview generation.
Distance and strain values use the recorded calibration factors. Center wavelength, fiber strain coefficient, and distance geometry factor must be confirmed before treating the values as an absolute calibration.

## Decision

`REJECT`. The 5M execution itself completed without OOM, but the selected peak was `+13 bin` (`-2418.65 με`) while the stage-displacement reference is `384.62 με`. Review found that the existing normalization divided every frequency bin independently, which amplified low-energy phase noise. The correlation normalization was changed to one Ref/Sig energy denominator and the same input must be rerun before acceptance.
