# OFDR UI ISF 5M Update

## 목표

- 99개 ISF를 33개 CH1/2/3 set으로 스캔한다.
- 선택 set 또는 전체 valid set을 통합 CSV로 변환한다.
- CH1 k-선형화 후 CH2/CH3를 각각 5M 전체 처리한다.
- GUI가 장시간 작업 중 응답하며 취소·진행률·실패 원인을 표시한다.

## UI 흐름

1. `ISF Dataset` 탭에서 input/output folder를 선택한다.
2. `Scan`으로 set ID, 변형량, 반복, 채널 존재, point 수, validation을 확인한다.
3. `Convert Selected/All`은 `.partial`을 거쳐 CSV와 sidecar를 생성한다.
4. `Process Selected`는 CSV가 아닌 ISF를 직접 읽어 k-선형화와 X/Y 처리를 실행한다.
5. Stages 탭에서 polarization과 segment index를 선택해 preview 또는 상세 segment를 확인한다.

## 데이터 형식과 처리 단계

- 파일명: `<변형량>mm<4자리 반복>CH<1|2|3>.isf` (예: `0_0mm0000CH1.isf`)
- 현재 실측 계약: signed 16-bit, MSB first, 5,000,000 point, `XINCR=40 ns`
- 통합 CSV: `Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V`
- sidecar: set ID, 변형량/반복, 소스 절대경로·크기·SHA-256·전체 header, CSV 행 수·SHA-256, 변환 버전/UTC
- 처리: CH1 mean 제거 → Hilbert analytic phase → unwrap/QC → uniform normalized-k 보간 → Ref/Sig 전체 FFT → 거리 segmentation → Window → zero-padded IFFT 국부 spectrum → magnitude Cross Correlation → peak/Strain
- 표시 축: normalized-k sample, 거리(m), lag bin, Strain(με). 거리/Strain은 교정 config와 함께 결과에 기록한다.

## 오류 상태

- 채널 누락/중복, header·time-axis 불일치: set invalid
- payload 길이/encoding/endian 불일치: read failure
- stale output: overwrite 확인 필요
- 저장공간 부족: 변환 시작 전 failure
- MKL x64 provider 없음: 5M 처리 차단
- CH1 nonmonotonic ratio 초과: k-linearization QC failure
- 취소: partial 산출물 제거, 결과 summary 보존

## 완료 체크리스트

- [x] 공유 core, x64 solution, MKL FFT 경계
- [x] ISF parser/scanner, 통합 CSV와 sidecar
- [x] CH1 k-선형화, X/Y streaming processing
- [x] scan/convert/run/evaluate CLI
- [x] WPF ISF Dataset 탭과 비동기 command
- [x] 단위 테스트 및 실제 33-set scan
- [x] 실제 golden set 5M 변환·X/Y 성능 baseline

## 2026-08-25 검증 기록

- scan: 99 files, 33 complete/valid sets, 전부 5,000,000 point 및 40 ns
- golden set: `0_0mm0000`, CH1/2/3 hash는 `harness/datasets/golden/G001_20260805_0_0mm0000/metadata.json` 참조
- CSV: 5,000,000 rows, 334,539,305 bytes, SHA-256 `6494BC5D95B9CCAF38F66D58E7FDAF21FFC3ABFAC5E02B72105DF77C94E26EBB`
- 처리: MKL x64, CH1 nonmonotonic ratio 0, X/Y 각 156,249 segments, OOM 없음
- 최초 core baseline: 2.87 s, peak working set 934,547,456 bytes. 절대 시간은 동일 PC/동일 build 조건에서만 비교한다.
- 자동 테스트: x64 MSTest 10/10 통과. GUI 장시간 응답성은 실제 사용자 조작 smoke test를 별도로 반복한다.

초기 2026-08-25 범위에서는 물리 거리 보정을 제외했으나, 2026-08-26 S pol 후속 요청에서 OPD 기반 거리와 계수 기반 Strain 축을 추가했다.

## 2026-08-26 독립 입력 GUI 개편

초기 계획의 CH1/2/3 통합 set UI는 하네스/CLI 호환 경로로 유지하되, 현재 GUI 기본 계약은 독립 Aux/Ref/Sig 입력으로 변경했다.

- 한 파일 변환: `Index,Time_s,Voltage_V` 채널 CSV와 sidecar
- 입력 조합: Aux=0.0 mm CH1, Ref=0.0 mm CH2/CH3, Sig=>0.0 mm 동일 채널/반복
- 새 화면: 왼쪽 데이터/알고리즘 탭, 오른쪽 K-linearization/S pol 탭
- 새 CLI: `convert-channel`, `run-pair`
- 새 golden: `G002_20260826_independent_0_4mm0000CH2`
- 후속 최종 full run: `harness/results/20260826_114338_pair_0_4mm0000CH2`, 5M × 3채널, MKL x64, OOM 없음, 5.5~6.5 m 749 segments, 구간 lag 중앙값 -2 bin

## 2026-08-26 S pol 7단계·물리축 후속

- Window 위치를 Raw/전체 FFT 전에서 `FFT 후 선택 segment`로 이동했다.
- 화면을 Raw, FFT, Segment, Window Segment, IFFT, Cross Correlation, Peak 확대의 7단계로 고정했다.
- Cross Correlation은 국부 Rayleigh magnitude pattern의 평균 제거와 전체 에너지 정규화를 사용한다.
- CH1 phase span과 18.2703 m OPD로 거리 간격을 계산하고 기본 5.5~6.5 m만 처리한다.
- 중심 파장/strain coefficient/배율/offset으로 lag를 με로 환산한다.
- S pol 하단에서 K 보정 전·후 FFT를 포함한 9개 그래프 중 하나를 선택해 크게 볼 수 있다.
- x64 build warning 0/error 0, MSTest 15/15, WPF startup smoke `20260826_114505_gui_startup_smoke` ACCEPT.

## 2026-08-26 K 보정 전·후 FFT와 X/Y 확대 후속

- 기존 거리 FFT가 CH1 phase K-linearization 이후 계산된 결과임을 코드와 5M 대표 실행으로 확인했다.
- 원본 Ref/Sig FFT preview를 K-linearization 전에 추가하고, 기존 결과는 `K 보정 후 거리 FFT`로 명확히 구분했다.
- Segment 이후 단계는 계속 K 보정 후 거리 FFT만 사용한다.
- 그래프 확대 상태를 X index뿐 아니라 Y min/max까지 저장하여 사각형 드래그로 두 축을 함께 확대하고 단계별로 복원한다.
- 최종 검증: x64 build warning 0/error 0, MSTest 16/16, 5M full run `20260826_172555_pair_0_4mm0000CH2` COMPLETED, WPF startup smoke `20260826_172643_gui_startup_smoke` ACCEPT.

## 2026-08-28 이중 CH1 k-선형화·분산 spectral shift 후속

2026-08-26의 `Aux/Ref/Sig` 3입력과 한 CH1 공통 보정은 실행 이력으로 유지하지만 현재 처리 계약에서는 대체되었다.

- 입력을 `기준 CH1 + Ref + 측정 CH1 + Sig` 네 파일로 변경했다.
- Ref와 Sig를 각자 동시 취득한 CH1 analytic phase로 독립 k-선형화한 뒤 짧은 공통 상대 phase span으로 맞춘다.
- 18.2703 m를 물리 경로차로, group index 1.4682를 spectral 교정 시작값으로 사용한다.
- 거리 FFT는 최대값 기준 normalized dB로 표시해 케이블 구간의 감쇠 slope를 확인한다.
- 기본 처리 config를 Hann / segment 1024 / hop 256 / padding 16 / 분석 3.5~8.7 m / 최대 ±180 GHz로 변경했다.
- correlation은 제한 범위의 real peak를 찾고 3점 포물선 보간으로 fractional lag와 GHz shift를 계산한다.
- S pol에 거리별 Rayleigh spectral shift와 Strain을 추가해 10개 그래프를 제공한다.
- 0.1~0.9 mm CH2/반복 0000의 5M series가 `3.579~91.937 GHz` 계단 응답을 복원했다.
- 최종 검증: x64 build warning 0/error 0, MSTest 19/19, 대표 full run `20260828_112245_pair_0_4mm0000CH2`, WPF startup smoke `20260828_112304_gui_startup_smoke` ACCEPT.

상세 요청·이슈·원인·해결과 검증 수치는 `docs/report/20260828_dual_aux_klinear_spectral_shift_fix.md`에 기록한다. 과거 변경 맥락은 `docs/report/20260826_gui_independent_channel_update.md`와 `docs/report/20260826_spol_window_ifft_distance_strain_update.md`에 보존한다.
