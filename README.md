# OFDR 독립 채널 분석 도구

.NET Framework 4.8 WPF GUI, 공용 처리 코어, C# 회귀 하네스를 한 저장소에서 관리한다. 현재 GUI의 기본 작업 단위는 `기준 CH1 + Ref + 측정 CH1 + Sig` 네 파일이며, 변형량별 CH1/2/3을 하나의 통합 CSV로 묶을 필요가 없다.

```text
기준 CH1(0.0 mm) -> Hilbert 위상/unwrap/QC -> Ref를 균일 normalized-k로 보간 ─┐
기준 Ref(0.0 mm, CH2/CH3) ───────────────────────────────────────────────────┤
측정 CH1(>0.0 mm) -> Hilbert 위상/unwrap/QC -> Sig를 균일 normalized-k로 보간 ├-> 공통 위상 범위
측정 Sig(>0.0 mm, 같은 채널) ─────────────────────────────────────────────────┘   -> dB 거리 FFT
   -> 거리 Segment -> Window -> zero-padded IFFT 국부 Rayleigh 스펙트럼
   -> 제한 범위 Cross Correlation -> sub-bin Peak -> Spectral Shift/Strain
```

예를 들어 다음 네 파일을 한 번에 선택한다.

- 기준 보조 간섭계: `0_0mm0000CH1.isf` 또는 동일 이름의 채널 CSV
- Ref: `0_0mm0000CH2.isf` 또는 채널 CSV
- 측정 보조 간섭계: `0_4mm0000CH1.isf` 또는 채널 CSV
- Sig: `0_4mm0000CH2.isf` 또는 채널 CSV

기준 CH1과 Ref는 같은 0.0 mm/반복이어야 하고, 측정 CH1과 Sig는 같은 변형량/반복이어야 한다. Ref와 Sig는 동일 편광 채널(CH2 또는 CH3)이어야 한다.

## 구성

```text
OFDR_Signal_Generator/       WPF GUI
OFDR_Signal_Core/            ISF/채널 CSV, FFT, k-선형화, 독립 Ref/Sig 처리
OFDR_Harness/                자동 변환·처리·평가 CLI
OFDR_Signal_Core.Tests/      net48 MSTest
harness/                     config, dataset metadata, schema, 실행 결과
docs/report/                 요청·이슈·원인·해결·검증 보고서와 사용법
```

## GUI

왼쪽은 두 탭으로 구성된다.

- `데이터 불러오기`: 기준 CH1, Ref, 측정 CH1, Sig 파일을 각각 선택하고 ISF를 채널 CSV로 개별 변환한다. Ref 또는 Sig를 고르면 같은 폴더의 대응 CH1을 자동 제안한다.
- `알고리즘 설정`: Window, segment 크기/간격, zero padding, spectral-shift 탐색 범위, k-선형화 QC, 분석/변형 거리와 교정 계수를 설정한다.

오른쪽은 다음 그래프 탭을 제공한다.

- `K-linearization`: 기준/측정 CH1의 Raw, analytic phase, unwrap, phase increment, 공통 uniform phase grid와 각 QC를 표시한다.
- `S pol`: 원본 Raw → K-linearization 전 FFT → k-선형화 후 normalized dB 거리 FFT → 선택 segment → Window segment → IFFT 국부 스펙트럼 → 제한 범위 Cross Correlation → peak 확대 → 거리별 Rayleigh spectral shift → 거리별 Strain을 표시한다. 화면 하단에서 10개 그래프 중 하나를 크게 볼 수 있다.

전체 5M 배열은 UI에 직접 바인딩하지 않고 최대 10,000 point min/max preview만 표시한다. 자세한 조작법은 [프로젝트 사용방법](docs/report/PROJECT_USAGE.md)을 참조한다.

모든 그래프는 원하는 영역을 사각형으로 드래그하면 X축과 Y축을 함께 확대한다. `한 단계 축소`는 직전 X/Y 범위로 돌아가고 `전체 보기`는 전체 X 범위와 자동 Y 범위를 복원한다.

## 빌드

5M 실데이터는 `x64`와 Intel MKL FFT provider가 필수이다. 실행 중인 GUI가 기본 출력 폴더를 잠글 수 있으므로 자동 검증에는 별도 `OutDir`을 사용한다.

```powershell
dotnet restore .\OFDR_Signal_Generator.sln
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  .\OFDR_Signal_Generator.sln /t:Build /p:Configuration=Debug /p:Platform=x64 `
  /p:OutDir="$PWD\build_verify_gui\"
```

Visual Studio에서도 솔루션 구성을 `Debug | x64` 또는 `Release | x64`로 선택하고 전체 솔루션을 빌드한다.

GUI/XAML 변경은 빌드 성공만으로 완료하지 않는다. 별도 출력의 WPF 실행 파일을 실제 시작해 runtime binding 연결을 확인한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\harness\tools\Invoke-GuiStartupSmoke.ps1 `
  -Executable .\build_verify_gui\OFDR_Signal_Generator.exe `
  -ResultsRoot .\harness\results `
  -ObservationSeconds 5
```

## 독립 채널 CLI

```powershell
# ISF 한 파일을 3열 채널 CSV로 변환
.\build_verify_gui\OFDR_Harness.exe convert-channel `
  --input 'C:\Project\OFDR\Datas\OFDR Raw data\0_0mm0000CH1.isf' `
  --output 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels'

# GUI와 같은 이중 CH1 Ref/Sig 처리
.\build_verify_gui\OFDR_Harness.exe run-pair `
  --aux 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv' `
  --sig-aux 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH1.csv' `
  --ref 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH2.csv' `
  --sig 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH2.csv' `
  --channel 2 `
  --config .\harness\configs\baseline_independent_pair_5m.json `
  --results .\harness\results
```

채널 CSV 계약은 `Index,Time_s,Voltage_V`이다. 기존 통합 CSV(`CH1_Ref_V`, `CH2_Xpol_V`, `CH3_Ypol_V`)도 읽기 호환성은 유지하지만 새 변환의 기본 형식은 독립 채널 CSV이다.

## 데이터와 축 주의사항

- 현재 원본은 99 ISF, 33 channel set이며 실제 헤더는 채널당 5,000,000 point, signed 16-bit big-endian, `XINCR=40 ns`이다.
- 기존 측정 메모의 “2M”과 다르므로 항상 ISF 헤더를 source of truth로 사용한다.
- 거리축은 두 CH1 중 짧은 공통 위상 범위와 보조간섭계 물리 경로차 18.2703 m로 계산한다. 기본 분석 범위는 3.5~8.7 m, 알려진 변형 구간은 5.5~6.5 m이다.
- spectral 축은 보조간섭계 광섬유 group index 기본값 `1.4682`를 적용하며, FFT는 최대값 기준 dB로 표시한다. `MaximumSpectralShiftGHz=180` 범위 안에서만 peak를 찾고 3점 포물선 보간으로 sub-bin shift를 계산한다.
- Strain축은 중심 파장, 광섬유 strain 계수, 배율/offset으로 환산한다. 기본 `1550 nm`, `0.78`은 시작값이며 실제 Laser/Fiber 교정값 확인 전에는 절대값으로 확정하지 않는다.
- 변형구간 기본 길이는 1.04 m이며 파일명의 stage 변위로 기계적 기준 strain을 함께 계산한다. Sweep range 메모 `16 nm/s`는 range 단위가 `nm`인지 재확인해야 한다.
- 실제 5M 처리 경로 변경은 대표 데이터 전체 구동 후에만 완료로 판정한다. 실행 이력은 `harness/results`, 상세 변경 보고서는 `docs/report`에 보존한다.
