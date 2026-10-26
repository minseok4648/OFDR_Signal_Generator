# 이중 CH1 K-linearization·Rayleigh Spectral Shift 수정 보고서

## 요청사항

- K-linearization 후 FFT가 정상적인 케이블 구간의 OTDR형 slope로 보이지 않는 원인을 재확인한다.
- 기준 그림처럼 5.5~6.5 m 변형 구간에서 변형량에 따른 분산 Rayleigh spectral shift가 계단 형태로 나오도록 처리한다.
- 현재 GUI에 수정된 K-linearization 진단, dB 거리 FFT, 최종 spectral shift/Strain 그래프를 연결한다.
- 하네스 규칙에 따라 실패 실험, 원인, 해결, 단위 테스트, 실제 5M 전체 실행과 GUI 시작 결과를 보존한다.

## 관측과 기존 결과 재판정

기존 3입력 경로는 `0_0mm CH1` 하나의 phase grid를 기준 Ref뿐 아니라 다른 시간에 취득한 `0_4mm Sig`에도 공통 적용했다. 실행은 완료됐지만 `20260826_172555_pair_0_4mm0000CH2`의 전체 lag profile은 ±11,700 με 범위에서 불규칙했고, 기대 shift 한 lag 이내 비율은 약 5.7%였다. 따라서 과거 결과는 프로그램 실행 gate에는 통과했으나 사용자가 요구한 분산 spectral-shift 결과에는 실패했으며 현재 baseline에서 `REJECT`로 재분류한다.

K 보정 후 FFT preview도 linear magnitude로 표시했다. 케이블 구간의 신호는 존재했지만 큰 peak와 dynamic range에 눌려 완만한 감쇠 slope를 화면에서 판별하기 어려웠다.

## 이슈·원인·해결방안

### 1. 서로 다른 sweep에 기준 CH1 하나만 사용

- 이슈: Ref/Sig의 국부 Rayleigh pattern 상관 peak가 거리마다 무작위 lag로 선택됐다.
- 원인: sweep마다 레이저 시간-주파수 비선형성이 조금씩 달라진다. Sig와 동시 취득한 CH1을 무시하고 기준 CH1 시간축으로 Sig를 보간하면 두 국부 spectrum의 주파수 축이 일치하지 않는다.
- 해결: `기준 CH1+Ref`와 `측정 CH1+Sig`를 각각 Hilbert phase로 독립 k-선형화한 뒤 두 phase span 중 짧은 공통 상대 phase 범위로 재보간한다.

### 2. correlation 전체 범위의 integer peak 선택

- 이슈: 저에너지 구간이나 periodic alias가 IFFT 경계 근처 peak로 선택되고 shift가 한 bin 단위로 계단화됐다.
- 원인: 물리적으로 가능한 shift 범위 제한과 sub-bin 추정이 없었다. Segment 64/padding 2는 기준 그림 수준의 GHz 분해능에도 부족했다.
- 해결: Hann/segment 1024/hop 256/padding 16을 baseline으로 사용하고 ±180 GHz에 해당하는 lag 안에서 real normalized correlation의 최댓값만 찾는다. 최고점과 양옆 세 점에 포물선 보간을 적용해 fractional lag를 계산한다.

### 3. 18.2703 m 해석과 spectral 축

- 이슈: 18.2703 m를 바로 optical OPD로 사용한 첫 probe는 0.4 mm 구간이 약 -38.17 GHz로 나와 기준 그림과 맞지 않았다.
- 원인: 제공값은 광섬유 물리 경로차로 해석해야 하며 spectral span에는 group index가 필요했다. 상관 부호 정의도 기존 구현과 기준 그림 방향이 달랐다.
- 해결: 물리 경로차 18.2703 m와 보조간섭계 광섬유 group index 시작값 1.4682로 `sweep_span_Hz = c·phase_span/(2π·n_g·L)`을 계산한다. `Sig × conj(Ref)` correlation의 positive peak를 기준으로 GHz/lag를 기록한다.
- 실패 probe: `harness/results/20260828_dual_aux_probe_0_4mm0000CH2_s1024_p16` (`NEEDS_CALIBRATION`)
- 교정 probe: `harness/results/20260828_dual_aux_probe_0_4mm0000CH2_group14682`

### 4. 거리 FFT의 slope가 보이지 않음

- 이슈: K 보정 후 거리 FFT가 flat/noise처럼 보여 K-linearization 실패로 오인할 수 있었다.
- 원인: linear magnitude 전체 범위를 한 Y축에 표시해 약한 distributed backscatter의 dB 차이가 압축됐다.
- 해결: K 보정 전·후 FFT를 각각 최대값 기준 `20 log10(|FFT|/max)`로 바꾸고 -140 dB floor를 적용한다. 원본 5M 배열은 계속 UI에 전달하지 않고 10,000-point min/max envelope만 표시한다.

### 5. 이전 완료 gate가 의미 오류를 걸러내지 못함

- 이슈: 과거 변경은 x64 build, WPF startup, 5M 완주를 통과했지만 최종 profile의 물리적 형태가 비정상이었다.
- 원인: gate가 예외/OOM/binding 여부와 한 segment peak만 확인했고, 알려진 변형 구간 내부 plateau와 외부 zero baseline, 변형량 series의 단조성은 수용 기준에 없었다.
- 해결: 이번부터 내부/외부 중앙값·MAD, FFT 구간 slope, 0.1~0.9 mm series를 golden metric으로 추가했다. 실행 성공만으로 `ACCEPT`하지 않고 제공 기준 그림의 근사 plateau와 비교한다.

### 6. GUI smoke 첫 실행의 PowerShell 정책 실패

- 이슈: 스크립트를 직접 호출한 첫 smoke 명령은 `running scripts is disabled`로 GUI를 시작하지 못했다.
- 원인: 시스템 PowerShell execution policy가 로컬 `.ps1` 직접 실행을 차단했다.
- 해결: 시스템 정책을 변경하지 않고 해당 프로세스에만 `-ExecutionPolicy Bypass`를 적용해 같은 최종 실행 파일을 다시 검증했다. launcher 실패와 성공 결과를 모두 하네스에 남긴다.

## 구현 내용

- `IndependentOfdrInput`에 `SignalAuxiliary`를 추가하고 네 파일의 변형량·반복·채널·길이·시간축을 검증한다.
- `AuxKLinearizer.LinearizeSweepsDetailed`가 두 sweep을 독립 보정하고 공통 상대 phase span으로 맞춘다.
- config schema version 3에 `AuxiliaryGroupIndex`, `MaximumSpectralShiftGHz`, `AnalysisStart/EndMeters`, `FftDbFloor`를 추가했다.
- `IndependentOfdrProcessingEngine`은 normalized dB 거리 FFT, 분석 구간 3.5~8.7 m, sub-bin peak, GHz/lag, spectral-shift profile을 생성한다.
- 기본 상세 segment는 강한 외부 peak가 아니라 변형 구간 5.5~6.5 m 중앙에 가장 가까운 위치를 선택한다.
- GUI 입력을 기준 CH1/Ref/측정 CH1/Sig 네 행으로 바꾸고 K 탭에 두 CH1의 phase/QC, S pol에 화면 9 spectral shift와 화면 10 Strain을 추가했다.
- CLI `run-pair`에 `--sig-aux`를 필수화해 GUI와 같은 공용 코어/config를 사용한다.

관련 처리 순서는 다음과 같다.

```text
각 sweep CH1 Hilbert phase → 각 Ref/Sig 독립 uniform-phase 보간 → 공통 상대 phase span
→ 전체 FFT → normalized dB 거리 preview → 거리 Segment → Hann Window
→ zero-padded IFFT → magnitude 평균 제거 → 제한 범위 normalized Cross Correlation
→ fractional peak → 거리별 Rayleigh spectral shift(GHz) → 계수 기반 Strain(με)
```

## 검증 결과

### 빌드·자동 테스트

- Visual Studio MSBuild `Debug|x64`, 별도 `build_verify_dual_aux_v4`: 경고 0, 오류 0.
- net48 MSTest: 19/19 통과.
- 신규 회귀: 서로 다른 비선형 phase를 가진 두 sweep이 각 CH1 보정 후 동일 normalized-k 신호를 복원, dB FFT/floor, spectral profile, 잘못된 측정 CH1 조합 거부.

### 대표 5M 전체 실행

- 결과: `harness/results/20260828_112245_pair_0_4mm0000CH2`
- 입력: 기준 `0_0mm0000CH1+CH2`, 측정 `0_4mm0000CH1+CH2`, 각 5,000,000 point.
- FFT provider/process: Intel MKL x64 / x64.
- 기준/측정 CH1 nonmonotonic ratio: 0 / 0.
- 전체 wall time: 4.545 s, segment 487, OOM 없음.
- 5.5~6.5 m spectral shift 중앙값/MAD: 25.984624 / 0.198599 GHz.
- 외부 baseline 중앙값/MAD: -0.026767 / 0.106003 GHz.
- 제공 그림의 0.4 mm 근사 plateau 26 GHz와 차이: -0.015376 GHz.
- K 보정 후 FFT 중앙값은 4.0~4.5 m -83.312 dB, 7.5~8.0 m -93.697 dB로 약 -10.385 dB slope를 보였다.

### 0.1~0.9 mm series

| 변형량 (mm) | 5.5~6.5 m 중앙값 (GHz) | 내부 MAD (GHz) | 외부 중앙값 (GHz) | 제공 그림 근사값 (GHz) |
| ---: | ---: | ---: | ---: | ---: |
| 0.1 | 3.579421 | 0.251795 | -0.054550 | 3.6 |
| 0.2 | 8.314081 | 0.222849 | 0.017290 | 8.3 |
| 0.3 | 15.535049 | 0.346658 | 0.022184 | 15.5 |
| 0.4 | 25.984624 | 0.198599 | -0.026767 | 26.0 |
| 0.5 | 37.308954 | 0.304586 | 0.090979 | 37.3 |
| 0.6 | 50.196928 | 0.430747 | 0.006168 | 50.2 |
| 0.7 | 64.040217 | 0.496267 | -0.065155 | 64.0 |
| 0.8 | 77.752904 | 0.591480 | 0.050895 | 77.7 |
| 0.9 | 91.937076 | 0.673671 | 0.115157 | 91.9 |

집계: `harness/results/20260828_dual_aux_csharp_series_regression`. 각 행은 별도 full-run `summary.md/config.json/metrics.json`을 유지한다.

### GUI runtime

- 첫 launcher: execution policy로 시작 전 실패, `20260828_111310_gui_smoke_launcher_policy_failure`에 보존.
- 재실행: 1차 성공 `harness/results/20260828_111327_gui_startup_smoke`, 최종 v4 성공 `harness/results/20260828_112304_gui_startup_smoke`. 최종 WPF 실행 파일이 8초 관찰 동안 main window/binding 상태를 유지해 `ACCEPT`.
- 그래프 컨트롤은 드래그 사각형의 X/Y 범위를 모두 저장하므로 가로와 세로를 함께 확대하고 단계별 복원할 수 있다.

## 판정

K-linearization 처리 계약, dB 거리 FFT 표시, CH2/반복 0000의 0.1~0.9 mm 분산 spectral shift와 GUI 시작 gate를 `ACCEPT`한다. 비정상 원데이터에서도 케이블 구간 slope와 알려진 변형 구간 plateau가 재현됐다.

## 남은 확인 사항

- group index 1.4682는 현재 결과와 기준 그림을 재현하는 보조간섭계 광섬유 시작값이다. 실제 fiber 사양으로 확인한 뒤 golden config를 확정한다.
- 중심 파장 1550 nm와 strain coefficient 0.78이 장비값으로 확인되지 않았으므로 GHz 결과는 검증됐지만 Strain(με) 절대값은 `NEEDS_MORE_TEST`이다.
- Ypol(CH3), 반복 0001/0002, 1.0 mm는 같은 config의 후속 회귀 대상이다.
- Loctite 접착부와 강한 반사 peak의 spike는 자동 보정으로 숨기지 않고 profile/QC 해석 대상으로 유지한다.

## 처리 방법 참고

보조간섭계 Hilbert phase의 균일 주파수 재표본화와 거리 segment의 Window-IFFT/국부 Rayleigh pattern cross-correlation 구성은 다음 공개 논문의 처리 흐름과 교차 확인했다.

- <https://www.mdpi.com/1424-8220/19/17/3660>
- <https://www.mdpi.com/1424-8220/21/14/4632>
- <https://www.mdpi.com/1424-8220/25/22/7044>
