# Tools

실행기는 `OFDR_Harness.exe`이다. `scan`, `convert`, `run`, `evaluate`, `convert-channel`, `run-pair`가 모두 `OFDR_Signal_Core`를 사용하므로 GUI와 알고리즘 계약이 같다.

GUI 변경은 Core/CLI 회귀만으로 완료하지 않는다. 빌드 후 다음 시작 smoke test로 WPF 창 생성과 runtime binding 연결을 확인한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\harness\tools\Invoke-GuiStartupSmoke.ps1 `
  -Executable .\build_verify_gui\OFDR_Signal_Generator.exe `
  -ResultsRoot .\harness\results `
  -ObservationSeconds 5
```

프로세스가 관찰 시간 전에 종료되면 실패하며, 성공/실패 모두 `harness/results/<timestamp>_gui_startup_smoke`에 config, metrics, summary를 남긴다. 이 검사는 실제 데이터 버튼 조작을 대신하지 않지만 XAML compile에서 검출되지 않는 DataContext 및 binding 연결 시점의 시작 오류를 차단한다.

`Executable`과 `ResultsRoot`에는 절대 경로와 현재 디렉터리 기준 상대 경로를 모두 사용할 수 있다.

현재 PC는 PowerShell script 실행 정책이 기본적으로 제한되어 있으므로 위 명령처럼 해당 smoke process에만 `ExecutionPolicy Bypass`를 적용한다. 시스템 전역 실행 정책은 변경하지 않는다.
