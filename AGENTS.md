# OFDR Codex 작업 규칙

이 저장소에서 Codex는 OFDR 신호처리 실험을 재현 가능하게 관리하는 하네스 작업자로 동작한다. 특정 그래프를 보기 좋게 만드는 것보다 동일 데이터·동일 config·동일 metric으로 결과를 반복 검증하는 것이 우선이다.

## 작업 루프

```text
Observation -> Hypothesis -> Minimal Experiment -> Golden Dataset Regression
            -> Interpretation -> Accept / Reject / Next Hypothesis
```

## 저장소 구조

- 저장소 루트가 OFDR 하네스 루트이다. 기존 `OFDR_Signal_Generator/` GUI 경로는 이동하지 않는다.
- 재사용 가능한 ISF/FFT/k-선형화 코드는 `OFDR_Signal_Core/`에 둔다.
- 자동 실행기는 `OFDR_Harness/`, 실험 config·dataset metadata·결과는 `harness/`에 둔다.
- 실패·취소 실험도 `harness/results/<experiment_id>/summary.md`에 기록하며 삭제하지 않는다.
- 원본 ISF와 변환 CSV는 Git에 넣지 않는다. dataset metadata에는 경로·헤더·SHA-256을 기록한다.
- 특정 변형량/반복 한 건에 맞춘 parameter를 전체 최적값으로 확정하지 않는다.

## 확인된 데이터

- 기본 원본 위치: `C:\Project\OFDR\Datas\OFDR Raw data`
- 파일 구성: 11 변형량(0.0~1.0 mm) × 3 반복 × CH1/2/3 = 99 ISF, 33 channel set
- CH1: 각 sweep의 보조간섭계, CH2: Xpol, CH3: Ypol
- 파일 헤더 기준: 채널당 5,000,000 point, 16-bit signed big-endian, 25 MS/s(`XINCR=40 ns`)
- 기존 측정 메모의 “채널당 2M”과 실제 파일 헤더가 다르므로 parser는 항상 ISF 헤더를 source of truth로 사용한다.
- 측정 구간 메모: 약 5.5~6.5 m, 변형구간 실측 1.03~1.05 m, Loctite 접착부에서는 peak가 깨질 수 있다.
- sweep 메모: rate 100 nm/s, range 16 nm/s(원 메모 단위 재확인 필요), 보조간섭계 물리 경로차 18.2703 m. spectral 교정의 광섬유 group index 시작값은 1.4682이며 실제 사양 확인 전 전역값으로 확정하지 않는다.

## 처리·검증 원칙

- 실제 데이터는 기준 Ref와 측정 Sig가 각각 동시 취득한 CH1 phase로 독립 k-선형화된 뒤 짧은 공통 상대 phase span에 맞춰져야 한다. 기준 CH1 하나를 다른 sweep의 Sig에 공통 적용하지 않는다.
- S pol 기본 순서는 `Raw → sweep별 k-선형화 → normalized dB 전체 FFT → 거리 segment → Window → zero-padded IFFT 국부 spectrum → magnitude Cross Correlation → fractional peak → spectral shift/Strain`이다. Window를 Raw 또는 전체 FFT 전에 옮기지 않는다.
- FFT/segment x축은 공통 CH1 phase span과 보조간섭계 물리 경로차 기반 거리(m), 결과 profile은 Rayleigh spectral shift(GHz)와 Strain(με)을 지원한다. 중심 파장, fiber strain coefficient와 distance geometry factor가 장비값으로 확인되기 전에는 절대 Strain 교정값으로 확정하지 않는다.
- 기본 분석 범위는 3.5~8.7 m, 알려진 변형 plateau 검증 범위는 5.5~6.5 m이다. Loctite 접착부 peak 손상 가능성을 별도 해석한다.
- 기본 CH1 nonmonotonic ratio 한계 `1e-5`는 현재 데이터의 시작값이지 전역 최적값이 아니다.
- 전체 5M 처리는 x64와 native MKL FFT가 필수이다. provider가 없으면 느린 fallback으로 숨기지 말고 실패로 기록한다.
- UI에는 최대 10,000-point preview만 전달하고 full segment 배열을 누적 보관하지 않는다.

## 완료 판정과 보고

- GUI의 XAML, DataContext, binding, command 또는 시작 경로를 변경한 작업은 solution build만으로 완료하지 않는다. 별도 x64 출력의 WPF 실행 파일을 실제로 시작해 main window와 runtime binding이 연결된 상태로 유지되는지 GUI startup smoke test를 수행한다.
- GUI startup smoke의 성공과 실패도 `harness/results/<experiment_id>`에 config, metrics, summary로 보존한다. Core/CLI 전체 구동은 WPF runtime 검증을 대체하지 않는다.
- 실제 5M 처리 경로를 변경한 작업은 대표 데이터 한 세트를 처음부터 끝까지 전체 구동한 뒤에만 완료로 판정한다.
- K-linearization/correlation/spectral 축을 변경한 작업은 실행 완료뿐 아니라 5.5~6.5 m 내부 plateau, 외부 zero baseline, K 보정 후 거리 FFT slope를 검증한다. 필요하면 `20260828_dual_aux_csharp_series_regression`의 0.1~0.9 mm 계열을 golden regression으로 사용한다.
- 전체 구동이 실패하면 완료로 보고하지 않고 실패 단계, 재현 조건, 원인을 찾아 수정한 후 같은 입력으로 다시 전체 구동한다.
- 모든 작업 보고서는 `docs/report` 아래에 요청사항, 이슈, 원인, 해결방안, 구현 내용, 검증 결과와 남은 확인 사항을 기록한다.
- 전체 구동의 성공과 실패 결과는 모두 `harness/results/<experiment_id>`에 config, metrics와 summary로 보존한다.
