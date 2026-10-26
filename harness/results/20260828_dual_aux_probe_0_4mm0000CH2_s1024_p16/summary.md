# Dual auxiliary minimal experiment

- Status: `COMPLETED`
- Decision: `NEEDS_CALIBRATION`
- Reference CH1 phase span: 1375715.205021536 rad
- Signal CH1 phase span: 1375721.129843297 rad
- Common phase span: 1375715.205021536 rad
- Segment / hop / padded: 1024 / 256 / 16384
- Spectral resolution: 0.219282389 GHz/lag
- Deformation region median / MAD: -38.165497 / 0.303306 GHz
- Outside region median / MAD: 0.004510 / 0.158258 GHz
- Wall time: 2.436 s

## Interpretation

Reference and signal were independently re-sampled with the CH1 acquired in the same sweep. The correlation search was limited to the configured physical spectral-shift range and the peak was interpolated below one lag.

The plateau/noise-shape hypothesis was accepted, but this first probe treated 18.2703 m as optical OPD and used the opposite display sign. It is retained as an intermediate result and is not the accepted calibration baseline.
