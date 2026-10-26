# Independent channel Ref/Sig processing

- Status: `COMPLETED`
- Recorded: 2026-08-28T11:12:17.1326348+09:00
- Wall time: 4.374 s
- Aux: `C:\Project\OFDR\Datas\OFDR Raw data\0_0mm0000CH1.isf`
- Signal Aux: `C:\Project\OFDR\Datas\OFDR Raw data\0_7mm0000CH1.isf`
- Ref: `C:\Project\OFDR\Datas\OFDR Raw data\0_0mm0000CH2.isf`
- Sig: `C:\Project\OFDR\Datas\OFDR Raw data\0_7mm0000CH2.isf`
- Polarization: Xpol (CH2)
- Samples: 5000000
- Pre-K FFT preview points (Ref / Sig): 10000 / 10000
- K-linearized distance FFT preview points (Ref / Sig): 10000 / 10000
- Segment count: 487
- Read time: 0.405 s
- CH1 nonmonotonic ratio: 0
- Signal CH1 nonmonotonic ratio: 1.80000036E-06
- Selected peak lag: 427.712197 bin
- Selected spectral shift: 63.8804765 GHz
- Selected segment center: 6.00266086 m
- Selected peak strain: 423.432842 microstrain
- Expected applied strain from stage displacement: 673.076923 microstrain
- Distance scale: 4.17223763E-05 m/FFT bin
- Strain scale: 0.989994781 microstrain/lag bin
- FFT provider: Math.NET Numerics Configuration: Version 5.0.0 Built for .NET Framework 4.8 Linear Algebra Provider: Intel MKL (x64; revision 15; MKL 2022.0) Fourier Transform Provider: Intel MKL (x64; revision 15; MKL 2022.0) Sparse Solver Provider: Intel MKL (x64; revision 15; MKL 2022.0) Max Degree of Parallelism: 24 Parallelize Elements: 300 Parallelize Order: 64 Check Distribution Parameters: True Thread-Safe RNGs: True Operating System: Microsoft Windows 10.0.19045  Operating System Architecture: X64 Framework: .NET Framework 4.8.9310.0 Process Architecture: X64 Processor Architecture: AMD64 Processor Identifier: Intel64 Family 6 Model 151 Stepping 2, GenuineIntel 

## Interpretation

Auxiliary CH1, baseline Ref, and displaced Sig completed through independent loading, pre-K FFT preview generation, CH1 phase K-linearization, K-linearized distance FFT, spatial segmentation, segment windowing, local-spectrum IFFT, Ref/Sig cross correlation, peak selection, and zoom preview generation.
Distance and strain values use the recorded calibration factors. Center wavelength, fiber strain coefficient, and distance geometry factor must be confirmed before treating the values as an absolute calibration.
