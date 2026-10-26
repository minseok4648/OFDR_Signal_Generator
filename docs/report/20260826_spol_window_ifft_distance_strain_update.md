# 2026-08-26 S pol Window·IFFT·거리·Strain 화면 업데이트 보고서

## 요청사항

- S pol 처리를 `Raw → FFT → Segment → Window → IFFT → Cross Correlation → Peak 확대` 순서로 변경한다.
- Window는 Raw 전압이나 전체 FFT 전에 적용하지 않고, FFT 후 선택한 거리 segment에 적용한다.
- 측정 메모를 바탕으로 FFT/segment x축을 거리(m), peak profile y축을 Strain(με)으로 바꿀 수 있는 교정 계수를 추가한다.
- 측정 기본 범위 5.5~6.5 m, 변형구간 길이 1.03~1.05 m, stage 변위(mm), 보조간섭계 OPD 18.2703 m, Sampling 25 MS/s 등을 설정과 결과에 남긴다.
- S pol 하단에서 원하는 그래프를 선택해 크게 본다.
- 변경된 실제 5M 경로를 전체 실행하고 문제가 있으면 원인을 고쳐 같은 입력으로 재검증한다.

## 이슈와 원인

### 1. 기존 Window 위치가 요청 알고리즘과 다름

기존 독립 처리 코드는 k-선형화된 full Ref/Sig에 Window를 적용한 뒤 FFT했다. 이 방식에서는 화면의 Window 결과가 거리 segment Window가 아니며, `FFT → 거리 구간 선택 → Window → IFFT 국부 spectrum` 흐름을 확인할 수 없었다.

### 2. IFFT 국부 스펙트럼 화면이 없음

이전 S pol 화면은 segment에서 곧바로 correlation 결과로 넘어갔다. 사용자는 Window 적용 segment와 IFFT 이후 국부 Rayleigh spectrum을 분리해 검토할 수 없었다.

### 3. 첫 실제 5M 실행에서 정규화 peak가 불안정

첫 후속 실행 `20260826_113823_pair_0_4mm0000CH2`는 OOM 없이 끝났지만 자동 선택 peak가 `+13 bin`, `-2418.65 με`였다. 기존 `NormalizeCrossCorrelation`은 각 frequency bin을 `|Sig|×|Ref|`로 나누는 phase-only 방식이어서 에너지가 작은 bin의 phase noise까지 같은 가중치로 확대했다.

### 4. 전체 에너지 정규화만 적용한 두 번째 실행도 phase 민감

두 번째 실행 `20260826_114135_pair_0_4mm0000CH2`는 peak가 `+4 bin`으로 줄었지만, complex local spectrum을 직접 상관해 Ref/Sig의 국부 phase 차이에 민감했다. 사용자가 화면에서 비교하는 Rayleigh spectrum의 크기 패턴 이동과도 입력 정의가 달랐다.

### 5. 빌드와 GUI smoke 하네스 이슈

- 샌드박스 내부 `dotnet build`는 Windows SDK 사용자 경로 접근이 거부되어 `MSB4184`가 발생했다.
- 권한 환경의 .NET CLI 빌드는 legacy WPF XAML 생성 단계를 수행하지 못해 `InitializeComponent` 관련 오류가 발생했다.
- Visual Studio MSBuild 첫 실행은 WPF 프로젝트에 `win-x64` RuntimeIdentifier가 없어 NuGet runtime asset 해석을 중단했다.
- GUI smoke 스크립트는 절대 `ResultsRoot`도 현재 디렉터리에 다시 결합해 `Path.GetFullPath` 전에 잘못된 경로를 만들었다.

## 해결방안

### 처리 순서

```text
CH1 phase 기반 k-linearized Ref/Sig
  → Window 적용 전 전체 FFT
  → 양의 거리 영역에서 Segment 선택
  → Segment에 Rectangular/Hann/Hamming/Blackman Window 적용
  → zero-padded IFFT로 국부 Rayleigh spectrum 생성
  → 국부 spectrum magnitude의 평균 제거
  → Ref/Sig Cross Correlation
  → Ref/Sig 전체 에너지로 정규화
  → fftshift, peak lag/magnitude, Strain 계산
```

OFDR에서 전체 주파수 sweep을 거리 domain으로 바꾸고, 공간 구간을 선택한 뒤 역변환한 국부 spectrum의 이동을 correlation으로 추정하는 순서는 [Froggatt의 분포 측정 논문](https://opg.optica.org/ao/abstract.cfm?origin=search&uri=ao-37-10-1735), [OFDR shape sensing 처리 예](https://opg.optica.org/abstract.cfm?uri=oe-31-1-396), [분포형 strain 처리 예](https://pmc.ncbi.nlm.nih.gov/articles/PMC5516813/)를 참고했다.

### 거리축

```text
Δk_sweep     = CH1_phase_span / auxiliary_OPD
distance/bin = π × auxiliary_OPD × geometry_factor / CH1_phase_span
distance     = offset + FFT_bin × distance/bin
```

입력한 18.2703 m를 왕복을 포함한 optical path difference로 해석한다. 실제 값이 물리 팔 길이라면 `DistanceGeometryFactor`로 장비 구성을 교정한다. `RestrictToMeasurementRange=true`이면 segment 중심이 기본 5.5~6.5 m에 포함되는 경우만 correlation/Strain profile을 계산한다.

### Strain축

```text
local_Δk/lag = Δk_sweep / local_spectrum_length
k_center     = 2π / center_wavelength
strain(με)   = offset - lag × local_Δk/lag / (k_center × strain_coefficient)
               × 10^6 × scale
stage 기준   = displacement_mm × 1000 / deformation_length_m
```

중심 파장 `1550 nm`와 strain coefficient `0.78`은 제공된 메모에 없는 시작값이다. GUI에서 수정 가능하며 실제 Laser/Fiber 교정값 확인 전 절대 Strain 정확도로 확정하지 않는다. 변형 길이는 범위 중앙값 `1.04 m`를 기본값으로 사용한다.

## 구현 내용

- `IndependentOfdrProcessingEngine`의 full Window를 제거하고 FFT segment Window와 실제 zero-padded IFFT를 추가했다.
- streaming 구간도 매 segment의 Window → IFFT → 평균 제거 magnitude → energy-normalized correlation을 수행하며 전체 중간 배열은 보관하지 않는다.
- 선택 segment에는 Raw segment, Window segment, Ref/Sig IFFT, correlation, zoom과 peak marker를 별도로 반환한다.
- 거리/Strain config schema를 version 2로 올리고 OPD, geometry, offsets, 중심 파장, strain coefficient, 측정 범위, 변형 길이, sweep/sampling 메모를 snapshot에 포함했다.
- S pol 화면을 7단계 순서로 재배치하고 `거리별 Strain 결과`를 추가했다.
- 하단 큰 그래프 선택기에 Raw, FFT, Segment, Window, IFFT, Correlation, Peak 확대, Strain 8개 항목을 추가했다.
- WPF 프로젝트에 `win-x64` RuntimeIdentifier를 명시해 MKL runtime asset restore/deploy 계약을 고정했다.
- GUI smoke 스크립트가 절대/상대 결과 경로를 모두 처리하도록 수정했다.

## 측정 메모 반영

| 항목 | 적용 |
| --- | --- |
| 측정 구간 | 기본 5.5~6.5 m 처리 제한 |
| 변형구간 | 기본 1.04 m, 수정 가능 |
| 변형 인가 | 파일명 displacement mm로 기계적 기준 Strain 계산 |
| Loctite | 접착부 peak 손상 가능성을 GUI/사용법에 경고 |
| 채널 | CH1 Aux, CH2 Xpol, CH3 Ypol |
| Point 수 | 메모 2M보다 실제 ISF header 5M 우선 |
| Sweep rate | 100 nm/s config snapshot |
| Sweep range | 메모 `16 nm/s`를 임시 `16 nm`로 입력, 단위 재확인 필요 |
| Aux OPD | 18.2703 m |
| Sampling | 25 MS/s |

## 검증 결과

### 빌드와 자동 테스트

- Visual Studio MSBuild, `Debug|x64`, 별도 `build_verify_spol_v3`: warning 0, error 0.
- MKL 배포 확인: `MathNet.Numerics.Providers.MKL.dll`, `libiomp5md.dll`, `libMathNetNumericsMKL.dll` 존재.
- MSTest: 총 15, 통과 15, 실패 0.
- 추가 회귀:
  - Window 종류를 바꿔도 full FFT가 동일함.
  - Hann Window가 선택 segment에만 적용됨.
  - IFFT와 correlation 길이가 padding 계약과 일치함.
  - 동일 국부 spectrum의 energy-normalized correlation이 zero lag, magnitude 1을 반환함.

### 실패/수정 실행

| 결과 | 상태 | 관측 | 판정 |
| --- | --- | --- | --- |
| `20260826_113823_pair_0_4mm0000CH2` | 처리 완료 | bin별 phase normalization, 선택 +13 bin | REJECT |
| `20260826_114135_pair_0_4mm0000CH2` | 처리 완료 | global energy normalization, complex 상관 +4 bin | REJECT |
| `20260826_gui_startup_smoke_absolute_path_failure` | 시작 전 실패 | 절대 결과 경로 재결합 | REJECT |

### 최종 실제 5M 실행

- 결과: `harness/results/20260826_114338_pair_0_4mm0000CH2`
- 입력: Aux `0_0mm0000CH1`, Ref `0_0mm0000CH2`, Sig `0_4mm0000CH2`.
- Samples/channel: 5,000,000, CH1 nonmonotonic ratio 0.
- 처리 범위/segment: 5.5~6.5 m, 749개.
- 거리 간격: `4.17221821e-5 m/FFT bin`.
- Strain 간격: `-186.050189 με/lag bin`.
- 자동 선택: 중심 5.68621189 m, peak -1 bin, 186.050189 με, magnitude 0.712160962.
- 전체 범위 lag 중앙값: -2 bin = 약 372.100379 με.
- Stage 기준: `0.4 mm / 1.04 m` = 384.615385 με.
- Core/CLI wall/read: 1.003 s / 6.024 s / 4.930 s.
- Peak working set: 757,276,672 bytes, OOM 없음.
- FFT: Math.NET 5.0.0 Intel MKL x64.
- 판정: 처리/runtime 경로 `ACCEPT`. 절대 물리 교정은 `NEEDS_MORE_TEST`.

### GUI runtime

- 결과: `harness/results/20260826_114505_gui_startup_smoke`.
- 실제 WPF process가 5초 관찰 동안 유지되고 XAML/DataContext/binding 시작 예외가 없었다.
- 판정: `ACCEPT`.

## 남은 확인 사항

- 실제 Laser 중심 파장, Fiber strain coefficient, 18.2703 m 값의 OPD/물리 팔 길이 의미를 장비 문서로 확인해야 한다.
- Sweep range 메모 `16 nm/s`의 단위를 확인해야 한다.
- 5.5~6.5 m의 절대 위치 ground truth와 distance geometry factor/offset을 기준 reflector로 교정해야 한다.
- Loctite 접착부와 변형구간 경계를 기록해 손상 peak를 QC mask 또는 별도 상태로 표시하는 후속 작업이 필요하다.
- CH3/Ypol, 다른 반복, 0.1~1.0 mm 전체 변형량에서 거리별 Strain의 반복성·선형성을 같은 config로 평가해야 한다.
