# 2026-08-25 GUI MKL 배포 오류 및 5M CSV 전체 구동 검증

## 요청사항

- 데이터 수가 많은 실제 5M CSV에서 화면 일부만 확인하고 완료하지 않는다.
- 한 세트를 전체 구동하여 정상 종료 여부를 확인한 뒤에만 완료로 판단한다.
- 정상 동작하지 않으면 이슈와 원인을 찾고 수정한다.
- 요청사항, 이슈, 원인, 해결방안과 검증 결과를 `docs/report`에 기록한다.

## 이슈

- GUI에서 변환 CSV 처리 중 `Native MKL FFT provider is required for data larger than 1,048,576 samples.` 오류가 발생했다.
- 약 30% 진행 상태에서 CH1 analytic FFT 진입 시 처리가 중단됐다.
- 별도 OutDir 빌드는 성공했지만 일반 `bin/Debug` 재빌드는 실행 중인 GUI가 `OFDR_Signal_Core.dll`과 PDB를 잠그고 있어 실패했다.

## 원인

- `OFDR_Signal_Core`는 SDK-style `PackageReference` 프로젝트이고 GUI는 기존 형식의 .NET Framework WPF 프로젝트다.
- 이 조합에서 Core의 NuGet runtime 자산이 GUI 출력 폴더로 완전하게 전달되지 않았다.
- 오류 당시 GUI 출력 폴더는 다음과 같이 불완전했다.
  - `MathNet.Numerics.dll`: 있음
  - `MathNet.Numerics.Providers.MKL.dll`: 없음
  - `runtimes/win-x64/native/libMathNetNumericsMKL.dll`: 있음
  - 실행 파일 옆의 `libMathNetNumericsMKL.dll`: 없음
- 따라서 `Control.TryUseNativeMKL()`이 실패했고 1,048,576 point 초과 입력에 설정된 안전 차단이 동작했다.
- 일반 출력 폴더 재빌드 실패는 코드 오류가 아니라 실행 중인 GUI와 Visual Studio debugger의 파일 잠금이다.

## 해결방안

- GUI 프로젝트가 `MathNet.Numerics 5.0.0`, `MathNet.Numerics.Providers.MKL 5.0.0`, `MathNet.Numerics.MKL.Win-x64 3.0.0`을 직접 참조하도록 변경했다.
- GUI 빌드 후 native MKL DLL을 실행 파일과 같은 폴더로 복사하는 `DeployMathNetMklNativeRuntime` target을 추가했다.
- provider DLL 또는 native DLL이 없으면 빌드가 성공한 것처럼 끝나지 않도록 명시적인 MSBuild 오류를 추가했다.
- `FftEngine` 오류 메시지에 x64 여부, provider DLL 존재 여부, native DLL 존재 여부와 초기화 실패 원인을 포함했다.
- Harness에 `run-csv` 명령을 추가해 GUI와 동일한 `ConvertedOfdrCsvReader` 및 `RunConvertedCsv` 경로를 실제 CSV로 재현 가능하게 검증하도록 했다.

## 구현 변경

- `OFDR_Signal_Generator/OFDR_Signal_Generator.csproj`
  - GUI 직접 MKL PackageReference 추가
  - native DLL root 배포 및 누락 시 build fail target 추가
- `OFDR_Signal_Core/Processing/FftEngine.cs`
  - MKL 초기화 실패 원인 보존
  - 5M 차단 오류를 한글과 배포 진단 정보로 개선
- `OFDR_Harness/Program.cs`
  - `run-csv --csv <path> [--config <path>] [--results <folder>]` 추가
  - 성공과 실패 모두 `harness/results/<experiment_id>`에 config, metrics, summary 저장

## 전체 구동 검증

- 입력: `C:\Project\OFDR\Datas\OFDR Raw data\converted_csv\0_0mm0000.csv`
- 입력 크기: 334,539,305 bytes
- 입력 행 수: 5,000,000
- config: `harness/configs/baseline_real_5m.json`
- 실행 결과: `COMPLETED`
- 실행 기록: `harness/results/20260825_134112_csv_0_0mm0000`
- 전체 wall time: 6.667 s
- CSV 읽기: 4.105 s
- Core 처리: 2.283 s
- peak working set: 1,127,374,848 bytes, 약 1.05 GiB
- FFT provider: Intel MKL x64 revision 15, MKL 2022.0
- CH1 nonmonotonic ratio: 0
- CH1 phase span: 1,375,715.205 rad
- Xpol Segment: 156,249
- Ypol Segment: 156,249
- CSV load부터 CH1 k-linearization, CH1/Xpol/Ypol FFT, Segment IFFT, Metric/peak detection, compact 결과 저장까지 완료했다.

## 빌드 및 테스트 결과

- 별도 `OutDir` x64 solution build: 경고 0, 오류 0
- 검증 출력 폴더에서 `MathNet.Numerics.dll`, `MathNet.Numerics.Providers.MKL.dll`, `libiomp5md.dll`, `libMathNetNumericsMKL.dll`을 확인했다.
- MSTest: 전체 11, 통과 11, 실패 0, 건너뜀 0
- 일반 `bin/Debug` 재빌드:
  - MKL provider/native DLL 복사까지 확인
  - 실행 중인 `OFDR_Signal_Generator`가 기존 Core DLL을 잠가 최종 교체 단계에서 실패
  - 실행 중인 프로세스 강제 종료는 사용자 작업 손실 가능성이 있어 수행하지 않음

## 정상 동작 판정

- 알고리즘 및 MKL 배포 수정본은 별도 x64 출력 폴더에서 실제 5M CSV 전체 경로를 완주했으므로 `정상`으로 판정한다.
- 처리 중 OOM, MKL fallback, QC 실패, Xpol/Ypol 중단은 발생하지 않았다.
- 이후 변경도 단위 테스트만으로 완료 처리하지 않고, 대표 5M 한 세트의 `run-csv` 또는 동등한 full-set Harness 실행을 완료 조건으로 사용한다.

## 남은 확인 사항

- 현재 실행 중인 GUI는 수정 전 Core DLL을 메모리에 로드한 상태다.
- GUI를 닫고 Visual Studio에서 `Debug | x64`로 다시 빌드한 뒤 실행해야 수정된 DLL이 적용된다.
- Visual Studio가 PDB를 계속 잠그면 디버깅을 중지한 뒤 `빌드 > 솔루션 다시 빌드`를 실행한다.
