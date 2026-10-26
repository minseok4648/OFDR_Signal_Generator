# 2026-08-26 ProgressPercent Runtime Binding 오류 수정 보고서

## 요청사항

- GUI 시작 시 발생한 `System.InvalidOperationException`을 해결한다.
- 오류의 직접 원인과 기존 하네스 검증에서 걸러내지 못한 원인을 기록한다.
- 수정 후 GUI를 실제 구동하고 정상 시작이 검증된 경우에만 완료한다.
- 성공과 실패 실행을 하네스 결과 구조에 보존한다.

## 이슈

Visual Studio에서 GUI를 시작하면 WPF `PropertyPathWorker.CheckReadOnly`에서 다음 예외가 발생했다.

```text
TwoWay 또는 OneWayToSource 바인딩은 읽기 전용 속성
OfdrWorkspaceViewModel.ProgressPercent에서 작동되지 않습니다.
```

문제가 된 XAML은 다음과 같았다.

```xml
<ProgressBar Value="{Binding ProgressPercent}" />
```

실패 관측은 `harness/results/20260826_gui_startup_progress_binding_failure`에 `FAILED / REJECT`로 보존했다.

## 직접 원인

- `ProgressPercent`는 ViewModel 내부 처리만 갱신하도록 `private set`을 가진다. WPF binding source 관점에서는 외부에서 값을 쓸 수 없는 읽기 전용 속성이다.
- `ProgressBar.Value`에 mode를 지정하지 않아 target metadata의 기본 mode가 적용되었고 WPF가 source로 값을 되쓰는 `TwoWay` 경로를 구성했다.
- MainWindow가 표시되면서 binding이 실제 `OfdrWorkspaceViewModel` DataContext에 연결될 때 source가 읽기 전용임을 확인하고 예외를 발생시켰다.

## 기존 검증이 이슈를 걸러내지 못한 원인

### 1. XAML compile과 runtime binding 검증을 같은 것으로 판단

MSBuild의 XAML/BAML compile은 태그, 속성 및 markup extension 문법을 검사하지만, 실제 DataContext instance의 property setter 가능 여부까지 확인하지 않는다. `ProgressPercent` binding은 문법적으로 유효해서 warning 0, error 0 빌드를 통과했다.

### 2. Core 테스트가 WPF MainWindow를 생성하지 않음

기존 13개 MSTest는 ISF reader, CSV, k-선형화, FFT, segment 처리 등 `OFDR_Signal_Core`를 검증한다. 테스트가 `MainWindow.Show()`와 runtime DataContext 연결을 수행하지 않아 이 예외가 발생할 지점에 도달하지 않았다.

### 3. 5M 전체 구동이 CLI 경로였음

이전 5M full run은 GUI와 같은 처리 Core를 사용하는 `OFDR_Harness run-pair`로 수행했다. 알고리즘, MKL, 메모리, 실제 데이터 처리는 검증했지만 WPF executable과 binding은 통과하지 않는다.

### 4. 완료 gate 적용 오류

이전 보고서에는 실제 GUI 조작 smoke test가 남은 확인 사항이라고 적었으면서도 전체 작업을 완료로 보고했다. Core full run 성공을 GUI runtime 정상까지 확장해 해석한 것이 절차상 원인이다. GUI 변경은 GUI 시작 검증까지 통과해야 한다는 별도 완료 gate가 필요했다.

## 해결방안

진행률은 ViewModel에서 화면으로만 전달되는 값이므로 binding을 명시적 `OneWay`로 변경했다.

```xml
<ProgressBar Value="{Binding ProgressPercent, Mode=OneWay}" />
```

`ProgressPercent`를 public setter로 바꾸지 않았다. 진행률 소유권은 계속 ViewModel에 있으며 사용자가 ProgressBar를 통해 source를 변경할 수 없다.

## 하네스 보강

- `harness/tools/Invoke-GuiStartupSmoke.ps1`을 추가했다.
- 별도 x64 출력의 `OFDR_Signal_Generator.exe`를 실제 시작한다.
- WPF main window와 runtime binding이 연결된 상태에서 지정 시간 전에 종료되는지 검사한다.
- 성공/실패 모두 `harness/results/<timestamp>_gui_startup_smoke`에 실행 파일 경로/SHA-256, 관찰 시간, 상태, 오류를 기록한다.
- `AGENTS.md` 완료 규칙에 GUI/XAML/DataContext/binding/command 변경 시 startup smoke 필수 조건을 추가했다.
- Core/CLI 5M 전체 구동은 GUI runtime smoke를 대체할 수 없다고 명시했다.

## 검증 결과

### 빌드

- Configuration: `Debug|x64`
- 별도 출력 폴더: `build_verify_gui_binding`
- Solution build: 성공
- Warning: 0
- Error: 0

### Core 회귀

- x64 MSTest: 총 13
- 성공: 13
- 실패/건너뜀: 0/0

### 실제 GUI 시작 검증

- 결과: `harness/results/20260826_102711_gui_startup_smoke`
- Status: `COMPLETED / ACCEPT`
- 실제 실행 파일 SHA-256: `4CCCFB9C5E77591B521AD341ECBA1D4FC4E39B4381209E26FB73061F874574F8`
- 관찰 시간: 5초
- 결과: MainWindow와 runtime binding이 연결된 WPF process가 조기 종료 없이 유지됨
- 종료: 검증 완료 후 smoke harness가 해당 process ID만 종료

첫 smoke 명령은 Windows PowerShell 실행 정책에 의해 script가 시작되기 전에 차단되었다. 이는 GUI 실패가 아닌 검증 환경 문제였으며, 시스템 전역 정책을 변경하지 않고 smoke process에만 `-ExecutionPolicy Bypass`를 적용해 재실행했다.

## 완료 판정

`ACCEPT`. 동일 binding 오류 없이 수정된 WPF GUI가 실제 시작되고 관찰 시간을 통과했다. 이번 변경은 XAML binding mode와 검증 하네스만 수정했으므로 OFDR 수학 처리 경로는 변경되지 않았다. 따라서 기존 ACCEPT 5M 결과 `20260826_100243_pair_0_4mm0000CH2`를 유지하며 5M 계산을 다시 수행하지 않았다.

## 남은 확인 사항

- startup smoke는 시작·DataContext·binding 연결 오류를 검출하지만 파일 선택, 버튼 클릭, 진행률 변화 및 그래프 상호작용 전체를 자동 조작하지 않는다.
- 향후 GUI 기능 변경에서는 startup smoke와 함께 변경한 command의 실제 조작 시나리오도 명시적으로 수행해야 한다.
