# Tools

접합 CSV 저장 검증 출력은 `build_verify_aux_export_v1/OFDR_Signal_Generator.exe`이다. WPF smoke를 이 실행 파일과 `-TimeoutSeconds 60`으로 실행하면 접합 전체 4,528,098행과 마지막 진단 구간 1,500,000행을 실제 저장·재검증한다. 검증용 대용량 CSV는 검증 후 제거하고 metadata, 행 수, SHA-256 증거는 보존한다. 일반 앱에서 사용자가 저장한 CSV는 자동 삭제하지 않는다.

그래프 확대/축소 수정의 검증 출력은 `build_verify_graph_zoom_v1/OFDR_Signal_Generator.exe`이다. 아래 WPF smoke의 `-Executable`에 이 파일을 지정하면 원본·접합·8개 진단 그래프 및 기존 SignalPlot의 조작을 함께 검증한다. 실제 OS 마우스 주입 대신 실제 WPF 프로세스의 동일 조작 메서드와 렌더링을 검사한다.

## 전체 신호 / Mode hop 검토 검증

접합 후 보조간섭계 자체의 위상 품질 검토는 다음 명령으로 재현한다. `--quality-config`를 생략하면 접합 신호 전체를 분석한다. 실행 완료와 신호 QC 판정은 별개이며 `REJECT_PHASE_QC`도 정상적인 검토 결과로 보존한다.

```powershell
.\build_verify_aux_quality_v1\OFDR_Harness.exe inspect-mode-hop `
  --csv 'C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\tek0000ALL.csv' `
  --config .\harness\configs\baseline_mode_hop_preview.json `
  --quality-config .\harness\configs\baseline_auxiliary_quality.json --golden
```

접합 기준 0~1.2초의 기본 검사 config를 제공한다. 이 범위는 실제 한 스윕으로 확정한 값이 아니다. GUI에서는 전체 접합 파형을 본 뒤 분석 범위를 직접 정한다. 새 WPF 검증은 동일 smoke 스크립트의 `-Executable`에 `build_verify_aux_quality_v1/OFDR_Signal_Generator.exe`를 지정한다.

Tektronix 원본 CSV의 CH2는 High 트리거, CH3는 보조간섭계로 읽는다. 기존 CH1/CH2/CH3 편광 처리 입력과 별개이다.

```powershell
.\build_verify_mode_hop_v2\OFDR_Harness.exe preview-mode-hop `
  --csv 'C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\tek0000ALL.csv' `
  --config .\harness\configs\baseline_mode_hop_preview.json --golden

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\harness\tools\Invoke-ModeHopPreviewSmoke.ps1 `
  -Executable .\build_verify_mode_hop_v2\OFDR_Signal_Generator.exe `
  -Csv 'C:\Project\OFDR\Datas\OFDR Raw data\NoticeData\tek0000ALL.csv'
```

`--golden`은 G003의 SHA-256, 5M 샘플, 4초, 1.8 V에서 755개 구간·471,902개 제외 샘플을 검증한다. 다른 Tektronix CSV에는 이 옵션을 생략한다. WPF smoke는 G003 전용이며 실제 창과 binding, 렌더링 PNG, 명령, 구간 선택, 설정, 취소·실패 후 재로딩을 프로세스 내부에서 검증한다. OS 마우스 주입과 파일 선택 대화상자 탐색은 포함하지 않는다.

성공·실패·취소 결과는 `harness/results`에 보존한다. 설정의 1.2초는 사용자 메모의 약값이며 자동 스윕 검출값이 아니다. CSV 원본은 복사본도 Git에 넣지 않는다.

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
