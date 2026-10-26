# OFDR 처리 계약

## 현재 GUI의 독립 채널 경로

현재 GUI는 기준 sweep과 측정 sweep에서 각각 취득한 CH1을 포함해 네 파일을 독립적으로 받는다.

| 역할 | 조건 | 예시 |
| --- | --- | --- |
| 기준 보조 간섭계 | Ref와 같은 0.0 mm/반복, CH1 | `0_0mm0000CH1.csv` |
| Ref | 0.0 mm, CH2 또는 CH3 | `0_0mm0000CH2.csv` |
| 측정 보조 간섭계 | Sig와 같은 변형량/반복, CH1 | `0_4mm0000CH1.csv` |
| Sig | 0.0 mm 초과, Ref와 동일 채널/반복 | `0_4mm0000CH2.csv` |

각 입력은 `.isf` 또는 독립 채널 `.csv`가 가능하다. 기존 5열 통합 CSV도 예상 채널 열만 선택해 읽을 수 있다.

## 1. 입력 검증

- 파일명 `<변형량>mm<4자리 반복>CH<1|2|3>`에서 변형량, 반복, 채널을 해석한다.
- 기준 Aux는 CH1/0.0 mm이고 Ref와 반복 번호가 같아야 한다.
- 측정 Aux는 CH1이고 Sig와 변형량·반복 번호가 같아야 한다.
- Ref는 0.0 mm이어야 한다.
- Sig는 0.0 mm보다 커야 한다.
- Ref와 Sig는 모두 CH2 또는 모두 CH3이고 반복 번호가 같아야 한다.
- 네 입력의 point 수와 시간축이 일치해야 한다.
- 실데이터 1,048,576 sample 초과 처리는 x64 native MKL provider가 아니면 중단한다.

## 2. ISF와 채널 CSV

`IsfWaveformReader`는 SCPI `:CURVE #<n><length>` block과 preamble을 검사한다.

```text
Voltage_V = (rawCode - YOFF) × YMULT + YZERO
Time_s    = XZERO + (Index - PT_OFF) × XINCR
```

현재 실데이터 계약은 signed 16-bit, big-endian, 5,000,000 point, `XINCR=40 ns`이다. point 수를 코드나 측정 메모로 고정하지 않고 헤더값을 사용한다.

새 독립 채널 CSV 형식은 다음과 같다.

```text
Index,Time_s,Voltage_V
```

변환은 `.partial`에 먼저 기록한 뒤 성공 시 최종 파일로 이동한다. sidecar에는 역할 식별 정보, 원본 경로/크기/SHA-256/ISF 헤더, 행 수, CSV SHA-256, 변환 버전과 시간이 기록된다.

## 3. K-linearization

```text
기준 CH1 + Ref                         측정 CH1 + Sig
  -> mean 제거                           -> mean 제거
  -> FFT Hilbert analytic signal          -> FFT Hilbert analytic signal
  -> wrapped phase / unwrap               -> wrapped phase / unwrap
  -> sweep 방향 판정 / anchor QC          -> sweep 방향 판정 / anchor QC
  -> 기준 Ref의 uniform phase 보간         -> 측정 Sig의 uniform phase 보간
                    -> 두 phase span 중 짧은 공통 상대 phase 범위로 재보간
```

기본 `aux_nonmonotonic_ratio` 한계는 `1e-5`이다. 대표 데이터 관측값에서 시작한 QC 기준이지 모든 장비와 sweep의 전역 최적값은 아니다.

기준 CH1 하나를 두 측정에 공통 적용하지 않는다. 두 sweep의 시간-주파수 비선형성이 조금만 달라도 국부 Rayleigh pattern 위치가 달라지므로 Ref와 Sig를 각자 동시 취득한 CH1으로 보정한 뒤 공통 상대 phase 범위에 맞춘다. GUI는 기준/측정 CH1의 Raw, 위상, 증가량, 공통 grid와 QC를 각각 표시한다.

## 4. Ref/Sig 처리

```text
k-linearized Ref/Sig on common relative-phase span
  -> Window 적용 전 Ref/Sig 전체 FFT
  -> 최대값 기준 normalized dB 거리 FFT preview
  -> 양의 거리 영역에서 overlapping FFT segment 선택
  -> 선택 Window(Rectangular/Hann/Hamming/Blackman)를 각 complex segment에 적용
  -> zero-padded IFFT로 Ref/Sig 국부 Rayleigh 스펙트럼 생성
  -> 각 국부 스펙트럼 magnitude의 평균 제거
  -> Ref/Sig magnitude pattern Cross Correlation
  -> 전체 에너지 정규화 + fftshift
  -> 설정된 최대 spectral-shift 범위 안에서 real correlation 최고 peak 검색
  -> 3점 포물선 보간으로 fractional lag와 GHz shift 계산
  -> segment별 Rayleigh spectral shift와 Strain 계산
  -> 대표 segment 선택 및 상세 재계산
```

Window는 Raw 전압에 적용하지 않으며 전체 FFT 전에도 적용하지 않는다. 거리 영역에서 선택한 FFT segment에만 적용한 뒤 IFFT한다. `NormalizeCrossCorrelation=true`는 frequency bin마다 나누는 phase-only normalization이 아니라, 평균 제거한 Ref/Sig 국부 magnitude trace의 전체 에너지로 한 번 정규화한다.

자동 대표 segment는 알려진 변형 구간(`MeasurementStartMeters`~`MeasurementEndMeters`)의 중앙에 가장 가까운 segment를 선택한다. 외부의 강한 반사점이나 접착부가 단순히 correlation magnitude가 크다는 이유로 기본 상세 화면을 차지하지 않게 하기 위함이다. 사용자는 GUI에서 다른 segment 번호를 입력해 상세 화면을 다시 계산할 수 있다.

S pol 화면은 다음을 제공한다.

1. 입력 파일에서 읽은 원본 Ref/Sig Raw 전압
2. K-linearization 전 원본 Ref/Sig FFT 비교용 preview
3. 두 sweep CH1의 독립 K-linearization 후 Ref/Sig normalized dB 거리 FFT
4. K-linearization 후 거리 FFT에서 선택한 segment의 Ref/Sig magnitude
5. Window 적용 segment
6. zero-padded IFFT 국부 스펙트럼
7. Cross Correlation과 최고 peak marker
8. peak 주변 확대
9. 거리별 Rayleigh spectral shift(GHz)
10. 거리별 Strain(με)

K-linearization 전 FFT는 비교와 QC 전용이다. Segment, Window, IFFT, Cross Correlation, spectral shift/Strain 계산은 모두 3번 K-linearization 후 FFT를 입력으로 사용한다. 화면 하단의 선택 목록에서 1~10번 그래프를 크게 볼 수 있다.

그래프 영역을 사각형으로 드래그하면 선택한 X/Y 범위를 동시에 확대한다. 확대 기록에는 X/Y 범위를 함께 저장하므로 `한 단계 축소`도 두 축을 동시에 복원한다.

## 5. 메모리와 표시 경계

- 기준 CH1/Ref와 측정 CH1/Sig의 full-length 입력과 처리에 필요한 spectrum만 작업 중 유지한다.
- segment별 Raw/Ref/Window/IFFT/Cross 배열은 누적하지 않고 peak 값과 lag만 저장한다.
- 선택 segment만 full spectrum에서 다시 계산한다.
- GUI preview는 최대 10,000 point min/max envelope이다. 5M 배열을 WPF collection에 바인딩하지 않는다.
- 성능 metric에는 읽기/단계별 시간, 전체 시간, peak working set, segment 수, FFT provider를 기록한다.

## 6. 거리와 Strain 환산

18.2703 m는 보조간섭계 광섬유의 물리 경로차로 해석한다. 보조간섭계 위상과 spectral 축은 group index를 포함해 다음과 같이 계산한다.

```text
sweep_span_Hz          = c × common_phase_span / (2π × group_index × physical_path_difference)
spectral_shift_Hz/lag  = sweep_span_Hz / local_spectrum_length
distance/bin           = π × physical_path_difference × geometry_factor / common_phase_span
distance               = distance_offset + FFT_bin × distance/bin
```

거리식은 보조간섭계와 측정 광섬유에 같은 group index가 적용되어 상쇄되는 구성을 가정한다. 물리 경로차/굴절률 정의가 장비 구성과 다르면 `geometry_factor`를 별도 교정해야 한다. 현재 group index 시작값은 `1.4682`이다.

국부 스펙트럼의 lag는 다음 시작식으로 Strain으로 바꾼다.

```text
center_frequency_Hz = c / center_wavelength
strain(με)          = offset + spectral_shift_Hz
                      / (center_frequency_Hz × strain_coefficient)
                      × 10^6 × scale
```

기본값은 물리 경로차 `18.2703 m`, group index `1.4682`, 중심 파장 `1550 nm`, strain coefficient `0.78`, geometry/scale `1`, offset `0`이다. spectral shift의 부호는 현재 correlation 정의(`Sig × conj(Ref)`)를 따른다. 중심 파장과 strain coefficient는 제공된 측정 메모에 없으므로 실제 장비/광섬유 값으로 확인해야 한다.

기본 분석 범위는 전체 baseline을 함께 볼 수 있는 `3.5~8.7 m`이고 알려진 변형 구간은 `5.5~6.5 m`이다. `RestrictToMeasurementRange=true`이면 segment 중심이 분석 범위에 들어오는 경우만 Cross Correlation/profile에 포함한다. 기본 최대 탐색 범위는 ±180 GHz이다. 기계적 비교 기준은 `stage_displacement / deformation_length`이며 변형구간 길이 기본값은 `1.04 m`이다.

표시 축은 다음과 같다.

- K-linearization: normalized-k sample 또는 phase(rad)
- FFT/segment: 거리(m), 물리축 비활성화 시 FFT bin
- IFFT: 국부 스펙트럼 sample
- Cross Correlation: lag bin
- Spectral shift profile: 거리(m) / GHz
- Strain profile: 거리(m) / microstrain(με)

측정 메모의 `Sweep rate 100 nm/s`, `Sampling rate 25 MS/s`, `Sweep range 16 nm/s`는 config snapshot에 보존한다. range의 `nm/s`는 rate 단위와 중복되므로 GUI와 문서에서는 `16 nm`로 임시 해석하되 원 메모 단위를 반드시 재확인한다. ISF point 수는 메모의 2M이 아니라 실제 header 5M을 우선한다.

## 레거시 호환 경로

폴더 scanner, CH1/2/3 통합 CSV, 기존 synthetic feature metric(`Absolute/Signed Peak Offset`, `Peak Amplitude`, `Peak-Center Delta`) 코드는 기존 하네스 회귀를 위해 유지한다. 현재 GUI의 기본 입력과 화면은 독립 기준 CH1/Ref/측정 CH1/Sig 경로를 사용한다.
