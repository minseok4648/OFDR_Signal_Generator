# OFDR 작업 보고서 보관 규칙

이 폴더는 GUI, 하네스, 처리 코어, 문서 업데이트 이력을 남기는 공간이다.

각 업데이트 보고서는 다음 항목을 포함한다.

- 요청사항
- 이슈
- 원인
- 해결방안
- 구현 내용
- 검증 결과
- 남은 확인 사항

원본 ISF, 변환 CSV, 대용량 FFT 산출물은 이 폴더에 복사하지 않는다. 대용량 데이터는 `harness/datasets`의 manifest와 metadata로 추적하고, 실행 결과 요약과 metric/config snapshot은 `harness/results/<experiment_id>`에 남긴다.

실제 5M 처리 경로를 수정한 경우 단위 테스트와 빌드만으로 완료 처리하지 않는다. 대표 데이터 한 세트를 전체 구동하고, 실패하면 원인 수정 후 같은 입력을 다시 전체 구동하여 성공 결과를 남긴다.

## 보고서 목록

- `20261026_latest_head_diff.md`: 최신 로컬 커밋 대비 누적 변경 diff, 포함 범위와 기존 검증 근거
- `20260825_csv_file_processing_update.md`: 통합 CSV 직접 처리와 사용법 추가
- `20260825_mkl_gui_fullrun_fix.md`: MKL 배포 오류 수정과 첫 5M full run
- `20260826_gui_independent_channel_update.md`: 독립 Aux/Ref/Sig GUI, K-linearization/S pol 그래프, 5M 이슈 수정 및 재검증
- `20260826_progress_binding_runtime_fix.md`: ProgressPercent runtime binding 시작 오류와 GUI 검증 gate 보강
- `20260826_spol_window_ifft_distance_strain_update.md`: S pol 7단계 처리, 거리/Strain 교정, 큰 그래프, 5M 재검증
- `20260826_fft_pre_post_klinear_yzoom_update.md`: K 보정 전·후 FFT 분리, X/Y 사각형 확대, CSV 잠금 실패와 ISF 5M 재검증
- `20260828_dual_aux_klinear_spectral_shift_fix.md`: 기준/측정 CH1 독립 k-선형화, dB 거리 FFT slope, sub-bin GHz profile, 0.1~0.9 mm series와 GUI 재검증
- `PROJECT_USAGE.md`: 현재 GUI와 CLI의 사용자 지침
