# OFDR Harness

- `configs/`: synthetic/real baseline processing 설정
- `datasets/`: 외부 원본을 가리키는 manifest와 golden metadata
- `experiments/`: hypothesis와 실행 메모
- `results/`: 실행별 summary/config/metrics; 실패 결과도 보존
- `schemas/`: config와 dataset metadata 계약
- `tools/`: CLI 사용 보조 문서

대용량 ISF/CSV/profile artifact는 저장소 밖 또는 ignored `artifacts/`에 둔다.

현재 GUI와 동일한 독립 입력 회귀는 `configs/baseline_independent_pair_5m.json`과 CLI `convert-channel`, `run-pair`를 사용한다. `run-pair`에는 기준 `--aux/--ref`와 측정 `--sig-aux/--sig` 네 파일을 전달한다. config는 두 sweep CH1의 독립 k-선형화·공통 상대 phase span, normalized dB 거리 FFT, 거리 segment → Window → IFFT → 제한 범위 magnitude Cross Correlation → sub-bin Rayleigh spectral shift/Strain 교정을 포함한다. 채널 CSV/sidecar 계약은 `schemas/channel_csv_sidecar.schema.json`, 처리 config version 3 계약은 `schemas/independent_processing_config.schema.json`에 정의한다.

현재 C# golden series는 `results/20260828_dual_aux_csharp_series_regression`에서 0.1~0.9 mm/CH2/반복 0000 결과와 각 full-run 폴더를 연결한다. 18.2703 m는 물리 경로차, 1.4682는 보조간섭계 광섬유 group index 시작값으로 기록한다.
