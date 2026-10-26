# OFDR 하네스 Metric

## 입력과 변환

| Metric | 의미 |
| --- | --- |
| `sample_count`, `rows_written` | 입력/CSV의 전체 sample 수 |
| `source_sha256`, `csv_sha256` | 원본과 변환물 재현성 hash |
| `sample_spacing_sec` | 입력 시간축 간격 |
| `aux/signal_aux/ref/signal_id` | 기준 CH1·측정 CH1·Ref·Sig의 변형량·반복·채널 식별자 |

## K-linearization

| Metric | 의미 |
| --- | --- |
| `reference_aux_phase_span_rad`, `signal_aux_phase_span_rad` | 기준/측정 CH1의 unwrapped analytic phase 범위 |
| `common_phase_span_rad` | 두 sweep 중 짧은 공통 상대 phase 범위 |
| `reference/signal_aux_median_phase_increment_rad` | 각 진행 방향을 반영한 phase 증가량 중앙값 |
| `reference/signal_aux_nonmonotonic_ratio` | 각 sweep에서 진행 방향과 반대이거나 비증가인 phase sample 비율 |
| `accepted_anchor_count` | uniform phase 보간에 사용한 CH1 anchor 수 |
| `sweep_direction` | 검출한 sweep 진행 방향(`+1` 또는 `-1`) |

기본 nonmonotonic ratio 한계는 `1e-5`이다. 한 세트의 값만으로 한계를 완화하지 않는다.

## Ref/Sig Cross Correlation

| Metric | 의미 |
| --- | --- |
| `segment_count` | FFT spectrum에서 처리한 overlapping segment 수 |
| `selected_segment_index` | 기본 변형 구간 중앙에 가장 가까운 대표 segment |
| `segment_start_fft_bin` | 선택 segment의 첫 FFT bin |
| `peak_lag_bins` | 제한된 correlation 탐색 범위에서 포물선 보간한 fractional peak 위치 |
| `peak_spectral_shift_ghz` | fractional peak lag를 GHz로 환산한 Rayleigh spectral shift |
| `peak_magnitude` | 평균 제거·전체 에너지 정규화된 국부 magnitude correlation의 최고값 |
| `segment_center_distance_m` | 선택 segment 중심의 교정 거리 |
| `peak_strain_microstrain` | 선택 peak lag를 교정 계수로 바꾼 Strain |
| `expected_applied_strain_microstrain` | 파일명 stage 변위 / 변형구간 길이로 계산한 기계적 비교 기준 |
| `distance_m_per_fft_bin` | CH1 phase span과 보조간섭계 OPD로 계산한 거리 간격 |
| `strain_microstrain_per_lag` | 중심 파장·strain coefficient·국부 스펙트럼 길이로 계산한 lag 환산값 |
| `spectral_shift_ghz_per_lag` | CH1 공통 phase span·물리 경로차·group index·padding으로 계산한 GHz/lag |
| `maximum_correlation_search_lag` | `MaximumSpectralShiftGHz`에서 환산한 peak 탐색 반경 |

`peak_lag_bin` 자체는 거리 위치가 아니다. 거리 위치는 segment 중심, 변형은 lag 환산값으로 구분한다. peak가 correlation 경계와 일치하면 선택 기준, padding, segment 구성과 신호 품질을 확인한다.

## 성능

- `read_channels_sec`
- `k_linearization_sec`
- `pre_k_ref_sig_fft_sec`
- `linearized_ref_sig_fft_sec`
- `segment_window_ifft_correlation_sec`
- `total_processing_sec`
- `peak_working_set_bytes`
- `fft_provider`

절대 시간은 동일 PC, 동일 입력 hash, 동일 config, 동일 빌드에서만 비교한다.

`pre_k_ref_sig_fft_sec`는 비교용 원본 Ref/Sig FFT 두 건의 시간을, `linearized_ref_sig_fft_sec`는 CH1 위상 보간 후 Segment 입력으로 사용하는 Ref/Sig 거리 FFT 두 건의 시간을 뜻한다.

## Golden baseline

### G001 통합 CH1/2/3 baseline — 2026-08-25

| 항목 | 값 |
| --- | ---: |
| Set | `0_0mm0000` |
| Samples/channel | 5,000,000 |
| CH1 nonmonotonic ratio | 0 |
| Xpol / Ypol segment 수 | 156,249 / 156,249 |
| Core total | 2.873 s |
| Peak working set | 934,547,456 bytes |
| Provider | Math.NET 5.0.0 + Intel MKL x64 |

### G002 이전 단일 CH1/Ref/Sig 거리·Strain baseline — 2026-08-26 (`REJECT`)

| 항목 | 값 |
| --- | ---: |
| Aux / Ref / Sig | `0_0mm0000CH1` / `0_0mm0000CH2` / `0_4mm0000CH2` |
| Samples/channel | 5,000,000 |
| Window / Segment / Hop / Padding | Hann / 64 / 32 / 2 |
| CH1 phase span | 1,375,715.205 rad |
| CH1 median phase increment | 0.278828316 rad |
| CH1 nonmonotonic ratio | 0 |
| 처리 거리 범위 | 5.5~6.5 m |
| Segment 수 | 749 |
| 거리 간격 | 4.17221821e-5 m/FFT bin |
| Strain 간격 | -186.050189 με/lag bin |
| 선택 segment 중심 | 5.68621189 m |
| 선택 Peak lag / Strain / magnitude | -1 bin / 186.050189 με / 0.712160962 |
| 전체 구간 lag 중앙값 / 환산 | -2 bin / 372.100379 με |
| Stage 기준 Strain | 384.615385 με (`0.4 mm / 1.04 m`) |
| Core total | 1.003 s |
| 전체 CLI wall / 읽기 | 6.024 s / 4.930 s |
| Peak working set | 757,276,672 bytes |
| Provider | Math.NET 5.0.0 + Intel MKL x64 |

최종 baseline은 `harness/results/20260826_114338_pair_0_4mm0000CH2`이다. Window-before-FFT/complex phase-only 계열의 이전 실행은 현재 알고리즘 baseline이 아니다. 이번 변경 중 `20260826_113823...`과 `20260826_114135...`는 각각 bin별 phase normalization 및 complex local spectrum 상관 문제로 `REJECT`되었고 결과는 삭제하지 않았다.

### G003 이전 단일 CH1 K 보정 전·후 FFT 비교 baseline — 2026-08-26 (`REJECT`)

| 항목 | 값 |
| --- | ---: |
| Result | `20260826_172555_pair_0_4mm0000CH2` |
| Input | 원본 ISF, Aux/Ref/Sig 각 5,000,000 sample |
| K 보정 전/후 Sig preview | 10,000 / 10,000 point |
| 전후 preview 평균/최대 절대차 | 5.128658 / 2144.349699 |
| K 보정 전 FFT | 0.2768792 s |
| K 보정 후 거리 FFT | 0.1714558 s |
| Core total / CLI wall | 1.2578673 s / 1.618 s |
| Peak working set | 759,316,480 bytes |
| Provider | Math.NET 5.0.0 + Intel MKL x64 |

G002/G003은 한 번의 기준 CH1 위상을 Ref와 Sig에 공통 적용하고 전체 correlation에서 integer peak를 고른 과거 계약이다. 실행 자체는 보존하지만 2026-08-28 분산 spectral-shift 검증에서 5.5~6.5 m 변형 plateau를 복원하지 못해 현재 알고리즘 baseline에서는 `REJECT`로 재분류한다.

### G004 이중 CH1 Rayleigh spectral-shift baseline — 2026-08-28

| 항목 | 값 |
| --- | ---: |
| 대표 결과 | `20260828_112245_pair_0_4mm0000CH2` |
| 입력 | 기준 `0_0mm0000CH1+CH2`, 측정 `0_4mm0000CH1+CH2` |
| Samples/channel | 5,000,000 |
| Window / Segment / Hop / Padding | Hann / 1024 / 256 / 16 |
| 분석 / 변형 범위 | 3.5~8.7 m / 5.5~6.5 m |
| 기준 / 측정 CH1 nonmonotonic ratio | 0 / 0 |
| 기준 / 측정 phase span | 1,375,715.205 / 1,375,721.130 rad |
| 거리 간격 | 4.17221821e-5 m/FFT bin |
| sweep span / spectral 간격 | 2,447.025379 GHz / 0.149354576 GHz/lag |
| FFT 표시 | 최대값 기준 normalized dB, floor -140 dB |
| 4.0~4.5 m → 7.5~8.0 m FFT 중앙값 변화 | -10.385 dB |
| 5.5~6.5 m spectral shift 중앙값 / MAD | 25.984624 / 0.198599 GHz |
| 외부 baseline 중앙값 / MAD | -0.026767 / 0.106003 GHz |
| 제공 기준 그림의 0.4 mm 근사값과 차이 | -0.015376 GHz (`26 GHz` 기준) |
| Segment 수 | 487 |
| 전체 CLI wall | 4.545 s |
| Provider | Math.NET 5.0.0 + Intel MKL x64 |

0.1~0.9 mm series의 변형 구간 중앙값은 각각 `3.579, 8.314, 15.535, 25.985, 37.309, 50.197, 64.040, 77.753, 91.937 GHz`이다. 결과와 config snapshot은 `harness/results/20260828_dual_aux_csharp_series_regression` 및 각 `*_pair_*` 폴더에 보존한다.

## 판정

- 실행 상태: `COMPLETED`, `FAILED`, `CANCELLED`
- 알고리즘 판정: `ACCEPT`, `REJECT`, `NEEDS_MORE_TEST`
- 실행이 예외 없이 끝났더라도 결과 선택 기준이 요구사항을 충족하지 않으면 `REJECT`로 기록한다.
- GUI/runtime 및 처리 경로는 최종 5M 실행으로 `ACCEPT`할 수 있다. 다만 중심 파장, fiber strain coefficient, 거리 geometry factor가 장비값으로 확인되기 전 절대 거리/Strain 교정 판정은 `NEEDS_MORE_TEST`이다.
