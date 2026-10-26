# 2026-08-25 CSV 파일 직접 처리 GUI 업데이트

## 요청사항

- 모든 작업 및 업데이트 사항을 `docs/report` 아래에 남긴다.
- 업데이트 내용은 요청사항, 이슈, 원인, 해결방안을 포함한다.
- 기존에 만든 하네스 구조 기준으로 동작하도록 유지한다.
- 현재 프로젝트 사용방법을 한글로 작성한다.
- ISF 파일뿐 아니라 변환된 통합 CSV 파일도 GUI에서 직접 선택해 원하는 그래프를 순차적으로 볼 수 있게 구현한다.

## 이슈

- 기존 GUI는 ISF Set을 스캔하고 변환할 수 있었지만, 변환된 CSV를 다시 OFDR 실데이터 처리 파이프라인에 직접 넣는 UI가 없었다.
- 처리 코어의 공개 진입점도 `OfdrChannelSet` 기반 ISF 직접 처리만 제공했다.
- 통합 CSV는 이미 `Index,Time_s,CH1_Ref_V,CH2_Xpol_V,CH3_Ypol_V` 형식으로 생성되지만, GUI Stage 그래프와 연결되는 reader/processing 경계가 비어 있었다.

## 원인

- 초기 구현 범위가 “ISF 폴더 Scan → ISF 읽기 → CSV 변환 → ISF 직접 처리”에 집중되어 있었다.
- CSV 변환 결과는 재현성과 외부 검증용 artifact로 먼저 다루었고, GUI 처리 입력으로 재사용하는 경로는 아직 추가되지 않았다.

## 해결방안

- 코어 프로젝트에 `ConvertedOfdrCsvReader`를 추가해 통합 CSV를 CH1/CH2/CH3 배열로 읽는다.
- 코어 처리 엔진에 `RunConvertedCsv` 진입점을 추가해 CSV 입력도 기존 ISF 처리와 같은 k-linearization/FFT/Segment/IFFT/Metric 흐름을 사용하게 한다.
- GUI `ISF Dataset` 탭에 `변환 CSV 파일`, `CSV 찾기...`, `CSV 처리` UI를 추가한다.
- CSV 처리 결과는 기존 `Stages` 탭에 그대로 연결해 사용자가 그래프를 순차적으로 확인하도록 한다.

## 구현 내용

- 추가 파일
  - `OFDR_Signal_Core/Models/ConvertedCsvModels.cs`
  - `OFDR_Signal_Core/Services/ConvertedOfdrCsvReader.cs`
  - `docs/report/README.md`
  - `docs/report/PROJECT_USAGE.md`
  - `docs/report/20260825_csv_file_processing_update.md`
- 변경 파일
  - `OFDR_Signal_Core/Processing/OfdrProcessingEngine.cs`
  - `OFDR_Signal_Generator/ViewModels/MainViewModel.cs`
  - `OFDR_Signal_Generator/MainWindow.xaml`

## 동작 흐름

```text
변환 CSV 선택
  -> CSV header/column 검증
  -> CH1/CH2/CH3 voltage 배열 로드
  -> CH1 기반 k-linearization
  -> CH1 Ref FFT
  -> Xpol/Ypol FFT
  -> Segment별 Raw x conj(Ref)
  -> zero-padded IFFT
  -> Final Metric Profile
  -> Stages 탭 그래프 표시
```

## 검증 결과

- `Debug | x64` 별도 OutDir 빌드를 실행해 Core, GUI, Harness, Tests 프로젝트가 모두 컴파일되는 것을 확인했다.
- 첫 빌드는 샌드박스의 `C:\Users\sk-das3\AppData\Local\Microsoft SDKs` 접근 제한 때문에 실패했다. 권한 상승 빌드로 재검증했다.
- 컴파일 중 `CsvSetMetadata.RowCount`가 `long`, `List<T>` capacity가 `int`라서 생긴 타입 변환 오류를 발견했고, `int.MaxValue` 기준 clamp로 수정했다.
- 최종 빌드 결과: 경고 0개, 오류 0개.
- `dotnet test OFDR_Signal_Core.Tests\OFDR_Signal_Core.Tests.csproj -c Debug -p:Platform=x64 --no-restore` 실행 결과: 11개 통과, 0개 실패.
- 검증용 출력 폴더 `build_verify_csv_file_ui`는 빌드 후 삭제했다.

## 추가 검증 계획

- 작은 통합 CSV reader/processing 경계는 `ConvertedCsvReaderFeedsProcessingEngine` 단위 테스트로 확인했다.
- GUI 화면에서 `CSV 처리` 버튼 클릭 후 Stage 그래프가 표시되는지는 실제 앱 실행으로 한 번 더 확인한다.
- 실제 5M CSV는 장시간 처리이므로 첫 full-set 실행 결과를 harness baseline으로 남긴다.

## 남은 확인 사항

- 실제 5M CSV 전체 구동은 `20260825_mkl_gui_fullrun_fix.md`와 `harness/results/20260825_134112_csv_0_0mm0000`에 완료 기록을 남겼다.
- 측정된 CSV 읽기 4.105 s, Core 처리 2.283 s, peak working set 약 1.05 GiB를 첫 CSV baseline으로 사용한다.
- 현재 측정에서는 CSV parsing이 전체 wall time의 가장 큰 부분이지만 5M 한 세트가 6.667 s에 완료되어 즉시 binary cache를 추가하지 않는다.
- 실행 중인 기존 GUI를 닫고 `Debug | x64` 재빌드 또는 재실행하여 새 MKL 배포 구성을 로드해야 한다.
