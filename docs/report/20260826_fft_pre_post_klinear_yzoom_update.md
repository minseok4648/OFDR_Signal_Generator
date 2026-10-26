# K-linearization 전·후 FFT 분리와 X/Y 그래프 확대 업데이트

## 요청사항

- 현재 S pol의 FFT 거리 데이터가 K-linearization 처리된 결과인지 확인한다.
- S pol 화면에 K-linearization 전 FFT와 K-linearization 후 FFT를 각각 표시한다.
- 모든 그래프에서 가로뿐 아니라 세로 범위도 마우스 드래그로 확대할 수 있게 한다.
- 하네스 완료 규칙에 따라 x64 빌드, 자동 테스트, 실제 WPF 시작, 대표 5M 전체 처리를 수행하고 성공/실패 이력을 보존한다.

## 확인 결과

기존 `IndependentOfdrProcessingEngine.Run`은 CH1 phase로 `LinearizeDetailed`를 먼저 실행한 뒤, 보간된 `XPolarization` 또는 `YPolarization`을 `BuildSpectrum`에 전달했다. 따라서 기존 `FFT 거리 데이터`는 이미 K-linearization 후 FFT였다. 다만 화면의 `Raw 전압`도 보간 후 배열을 표시했고 K 보정 전 FFT 화면이 없어서 사용자가 전후 결과를 직접 비교할 수 없었다.

Segment, Window, IFFT, Cross Correlation, 거리/Strain 계산은 기존과 동일하게 K-linearization 후 거리 FFT만 사용한다. 새 K 보정 전 FFT는 비교와 QC용 preview이며 후속 처리 입력을 바꾸지 않는다.

## 이슈와 원인

### K-linearization 전후 상태가 화면에서 구분되지 않음

- 이슈: `FFT 거리 데이터` 한 화면만 있어 K 보정 여부와 보정 효과를 화면에서 확인할 수 없었다.
- 원인: 처리 코어는 보정 후 FFT만 결과 모델에 보존했고, 기존 Raw preview도 보간 후 신호를 가리켰다.
- 해결방안: 원본 Ref/Sig에서 K 보정 전 FFT preview를 먼저 생성하고, 보정 후 거리 FFT와 별도 결과 필드·화면·성능 metric으로 분리한다.

### 세로축을 확대할 수 없음

- 이슈: 그래프를 드래그하면 X축만 확대되고 Y축은 표시 X 범위의 min/max로 계속 자동 계산됐다.
- 원인: 확대 기록이 X 시작/끝 index 두 값만 저장했고 선택 사각형도 plot 전체 높이로 고정돼 있었다.
- 해결방안: 확대 상태에 X index와 Y min/max를 함께 저장하고, 실제 드래그 사각형의 위·아래 좌표를 데이터 Y 값으로 역변환한다.

### 첫 대표 전체 실행에서 변환 CSV 잠금 발생

- 이슈: `20260826_172333_pair_0_4mm0000CH2` 실행이 알고리즘 진입 전 `0_0mm0000CH1.csv` 공유 위반으로 실패했다.
- 원인: 외부 `converted_channels` CSV가 다른 프로세스에 의해 읽기 공유가 허용되지 않는 방식으로 열린 상태였다. OFDR 처리, FFT 또는 데이터 크기 문제는 아니었다.
- 해결방안: 실패 결과를 삭제하지 않고 보존했다. 동일 측정의 원본 ISF 세 파일을 입력으로 사용해 파일 잠금 영향을 제거하고 같은 처리 코어 전체 경로를 재실행했다.

### GUI smoke script 첫 호출 실패

- 이슈: 현재 PowerShell 정책이 로컬 `.ps1` 직접 실행을 차단했다.
- 원인: 시스템 `ExecutionPolicy`가 스크립트 실행을 허용하지 않았다.
- 해결방안: 저장소의 검증 스크립트 한 건에만 `-ExecutionPolicy Bypass -File`을 적용해 다시 실행했다. 시스템 정책은 변경하지 않았다.

## 구현 내용

- `IndependentOfdrProcessingResult`에 `PreKReferenceSpectrum`, `PreKSignalSpectrum`을 추가했다.
- 원본 Ref/Sig FFT를 K-linearization 전에 순차 계산하고 positive half를 최대 10,000-point preview로 축약한다.
- 기존 Raw preview가 입력 파일의 원본 전압을 표시하도록 바로잡았다.
- 보정 전·후 시간을 각각 `pre_k_ref_sig_fft_sec`, `linearized_ref_sig_fft_sec`로 기록한다.
- S pol 순서를 `원본 Raw → K 보정 전 FFT → K 보정 후 거리 FFT → Segment → Window → IFFT → Cross Correlation → Peak 확대`로 개편했다.
- 큰 그래프 선택 항목도 K 보정 전·후 FFT를 분리해 총 9개 화면으로 갱신했다.
- 그래프 드래그 선택 사각형과 확대 기록을 X/Y 양축 범위로 확장했다. `한 단계 축소`와 `전체 보기`도 두 축 상태를 함께 복원한다.

## 검증 결과

### 빌드와 자동 테스트

- Visual Studio MSBuild, `Debug|x64`, 별도 `OutDir=build_verify_fft_compare_v5`
- 결과: warning 0, error 0
- MSTest 결과: 16/16 통과
- 추가 회귀 검증: nonlinear synthetic sweep에서 보정 전·후 FFT가 서로 다른 sample grid의 결과인지 확인하고, 화면 축 계약을 확인했다.

### 실제 5M 대표 세트 전체 실행

- 결과: `harness/results/20260826_172555_pair_0_4mm0000CH2`
- 상태: `COMPLETED`
- 입력: `0_0mm0000CH1.isf` / `0_0mm0000CH2.isf` / `0_4mm0000CH2.isf`
- 채널당 sample: 5,000,000
- FFT provider: Intel MKL x64
- 전체 wall time: 1.618 s
- Core total: 1.258 s
- peak working set: 759,316,480 bytes
- K 보정 전 FFT: 0.277 s
- K 보정 후 FFT: 0.171 s
- 보정 전/후 Sig preview: 각각 10,000 point
- 같은 preview index 기준 평균 절대차: 5.128658, 최대 절대차: 2144.349699
- 결론: 두 화면은 같은 결과의 중복 표시가 아니며, 실제 5M 데이터에서 K 보정 전·후 FFT가 구분된다. OOM과 MKL 배포 오류 없이 Segment 이후 전체 처리가 완료됐다.

### GUI runtime

- 결과: `harness/results/20260826_172643_gui_startup_smoke`
- 상태/결정: `COMPLETED` / `ACCEPT`
- WPF 프로세스가 메인 창을 생성하고 runtime binding을 연결한 뒤 5초 관찰 시간 동안 유지됐다.

## 완료 판정

코어 회귀 테스트, 실제 5M 전체 처리, WPF 시작 gate가 모두 성공해 이번 변경을 `ACCEPT`로 판정한다. 최초 CSV 잠금 실패와 PowerShell 정책 실패는 삭제하지 않고 결과 폴더 또는 본 보고서에 원인과 재실행 결과를 보존했다.

## 남은 확인 사항

- 세로 확대는 코드 경로와 WPF 시작까지 검증했다. 실제 사용자 조작에서 원하는 작은 amplitude 범위를 반복 확대하는 감각은 장비 PC 화면에서 확인한다.
- 거리와 Strain의 절대값은 중심 파장, fiber strain coefficient, distance geometry factor가 실제 장비값으로 확정된 뒤 별도 golden 회귀가 필요하다.
