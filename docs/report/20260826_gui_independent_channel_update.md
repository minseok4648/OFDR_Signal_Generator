# 2026-08-26 GUI 독립 채널·K-linearization·S pol 개편 보고서

> 이 문서는 독립 입력 GUI 1차 개편 당시 기록이다. 이후 Window 위치, IFFT 화면, Cross Correlation 입력/정규화, 거리·Strain 축은 `20260826_spol_window_ifft_distance_strain_update.md`로 변경되었다. 현재 처리 계약은 후속 보고서와 `PROCESSING.md`를 우선한다.

## 요청사항

- 왼쪽의 기존 CSV 불러오기, Acquisition, synthetic 신호 등 사용하지 않는 설정 화면을 제거한다.
- 왼쪽을 데이터 선택/변환과 현재 알고리즘 설정 탭으로 재구성한다.
- 오른쪽에 K-linearization 진단 그래프와 S pol 처리 그래프를 추가한다.
- Aux, Ref, Sig를 통합 세트가 아닌 독립 파일로 선택한다.
- Ref는 0.0 mm, Sig는 0.0 mm 초과 데이터로 검증한다.
- Hanning 등 Window를 선택하고 Ref/Sig Cross Correlation의 최고 peak와 확대 화면을 제공한다.
- 변경 후 대표 5M 데이터를 처음부터 끝까지 실행하고, 이슈·원인·해결·검증 결과를 하네스에 남긴다.

## 이슈

### 1. 기존 UI와 현재 실험 계약 불일치

기존 왼쪽 화면은 synthetic Raw/Ref 생성, acquisition 설정, 통합 CSV와 folder set 처리 흐름이 섞여 있었다. 사용자가 원하는 `Aux 0_0mm CH1 + Ref 0_0mm CH2 + Sig 0_4mm CH2` 조합을 명시적으로 만들기 어려웠다.

### 2. K-linearization 중간 상태를 확인할 수 없음

기존 결과 화면은 최종 stage 위주였고 CH1 mean 제거, analytic magnitude, wrapped/unwrapped phase, phase increment와 uniform grid를 직접 비교할 수 없었다.

### 3. 첫 5M 실행에서 대표 peak가 경계 `-64`로 선택됨

첫 전체 실행 `20260826_100059_pair_0_4mm0000CH2`는 예외와 OOM 없이 완료되었지만 자동 선택 segment의 peak lag가 IFFT 왼쪽 경계 `-64`였다. 사용자에게 보여 줄 대표 segment로는 정상적이지 않아 알고리즘 판정을 `REJECT`로 기록했다.

## 원인

- GUI ViewModel이 synthetic/integrated set 중심이어서 독립 입력 역할과 변형량/채널 관계를 표현하지 못했다.
- 기존 k-선형화 API는 최종 보간값만 반환하여 Hilbert 처리 중간 preview가 없었다.
- 자동 대표 segment를 `|peak lag|`가 가장 큰 순서로 골랐다. 이 기준은 신뢰도가 아니라 경계로부터 먼 위치를 선호하므로 `-segment_size` 경계 peak를 선택할 수 있다.

## 해결방안

- 공용 Core에 독립 채널 reader/converter와 `IndependentOfdrProcessingEngine`을 추가하고 GUI/CLI가 함께 사용하도록 했다.
- Aux/Ref/Sig 파일의 변형량, 반복, 채널, point 수, 시간축을 처리 전에 검증한다.
- `AuxKLinearizer.LinearizeDetailed`가 7개 진단 preview와 QC metric을 반환하도록 확장했다.
- Window 후 Ref/Sig FFT, segment Cross Correlation, peak marker/zoom/전체 lag profile을 생성한다.
- 대표 segment 선택을 `최대 |lag|`에서 `최대 correlation peak magnitude`로 변경했다.
- 5M full 배열은 처리 코어에만 두고 UI에는 최대 10,000 point min/max preview를 전달한다.

## 구현 내용

### GUI

- 왼쪽 `데이터 불러오기` 탭: Aux/Ref/Sig 개별 찾기, 개별 채널 CSV 변환, 출력 폴더, 입력 규칙 표시.
- 왼쪽 `알고리즘 설정` 탭: 편광, Window, segment 크기/간격, padding, 정규화/scale, QC 한계, preview 상한.
- 오른쪽 `K-linearization` 탭: CH1 Raw, 평균 제거, analytic magnitude, wrapped phase, unwrapped phase, phase increment, uniform phase grid.
- 오른쪽 `S pol` 탭: Raw, Window, FFT, segment, correlation, peak 확대, 전체 segment peak lag.
- 처리/읽기/변환은 background task이며 중복 실행 방지, 진행률, 취소, 한글 오류를 제공한다.

### 공유 Core와 CLI

- `OfdrChannelFileReader`: `.isf`, 3열 채널 CSV, 기존 5열 통합 CSV에서 역할 채널을 순차 읽기.
- `OfdrChannelCsvConverter`: 한 ISF를 `Index,Time_s,Voltage_V` CSV와 sidecar로 변환.
- `IndependentOfdrProcessingEngine`: 동일 k grid에서 Ref/Sig Window/FFT/streaming segment correlation 처리.
- `PreviewReducer.FromSelector`: 전체 진단 배열을 추가 복사하지 않고 min/max preview 생성.
- CLI `convert-channel`, `run-pair` 추가. GUI와 동일 config/schema/core 사용.

## 데이터 변환 검증

저장소 밖 `C:\Project\OFDR\Datas\OFDR Raw data\converted_channels`에 세 채널 CSV를 생성했다. 원본/CSV는 Git에 복사하지 않았다.

| 역할 | CSV | 행 | CSV SHA-256 |
| --- | --- | ---: | --- |
| Aux | `0_0mm0000CH1.csv` | 5,000,000 | `851AE7370B52CB5F4BF27E45EA7202556D76C88F606B1B4CAC4B103B7919390D` |
| Ref | `0_0mm0000CH2.csv` | 5,000,000 | `7B4320CD32D04627CA39BF338C566BBCF21ABE9C8C6EFBFAFCB89170ADD4FA13` |
| Sig | `0_4mm0000CH2.csv` | 5,000,000 | `B53EC2E4E6EF339EF0357A2D3964A220B8DF11D54A8C22BCB4EBECD0B492B591` |

세 원본은 모두 10,000,488 byte, 5,000,000 point, `XINCR=40 ns`이다. 전체 경로, 원본 SHA-256, header 계약은 G002 metadata와 각 외부 sidecar에 기록했다.

## 검증 결과

### 자동 테스트와 빌드

- x64 MSTest: 총 13, 성공 13, 실패 0, 건너뜀 0.
- 별도 `OutDir` x64 solution build: 성공, warning 0, error 0.
- 추가 test 범위: 독립 채널 CSV round-trip, 독립 Aux/Ref/Sig k-선형화와 correlation view 생성.
- 최초 검증 빌드는 작업 샌드박스가 `C:\Users\sk-das3\AppData\Local\Microsoft SDKs` 조회를 차단해 `MSB4184`로 중단되었다. 프로젝트 오류가 아니라 검증 권한 문제였으며, 동일 명령을 승인된 Visual Studio MSBuild 환경에서 다시 실행해 warning 0, error 0을 확인했다.
- 채널 sidecar schema는 실제 `DataContractJsonSerializer` 출력의 `Source` 중첩 구조와 .NET JSON 날짜 문자열 형식에 맞춰 검증했다. 하네스 JSON 24개는 모두 문법 검사를 통과했다.

### 첫 5M 관측 — REJECT

- 결과: `harness/results/20260826_100059_pair_0_4mm0000CH2`
- 상태: 처리 자체는 `COMPLETED`, OOM 없음, MKL x64 확인.
- 문제: 대표 peak lag `-64` bin(왼쪽 경계), peak magnitude `0.14149`.
- 판정: 자동 대표 segment 기준이 잘못되어 알고리즘 결과는 `REJECT`.

### 수정 후 같은 5M 입력 재실행 — ACCEPT

- 결과: `harness/results/20260826_100243_pair_0_4mm0000CH2`
- 입력: Aux `0_0mm0000CH1`, Ref `0_0mm0000CH2`, Sig `0_4mm0000CH2` 채널 CSV.
- Samples/channel: 5,000,000.
- Wall/read/core: 7.382 s / 4.960 s / 2.264 s.
- CH1 phase span: 1,375,715.205 rad, nonmonotonic ratio 0.
- Segment 수: 156,249.
- 자동 선택 segment: 83,948, 시작 FFT bin 2,686,336.
- 최고 peak: lag `-3` bin, magnitude `0.303776460`.
- Peak working set: 757,223,424 byte.
- FFT provider: Math.NET 5.0.0 Intel MKL x64.
- 판정: 동일 입력 full path가 예외/OOM 없이 끝났고 경계 오선택이 제거되어 `ACCEPT`.

## 남은 확인 사항

- CH3/Ypol 5M 독립 Ref/Sig 조합은 이번 GUI 개편의 대표 full run에 포함하지 않았다.
- 0.1~1.0 mm와 나머지 반복에 대해 peak lag/크기의 재현성 및 단조성을 평가해야 한다.
- 현재 lag bin은 물리 거리가 아니다. 보조간섭계 경로차를 사용한 거리 보정은 후속 hypothesis이다.
- WPF 화면은 XAML compile까지 자동 검증했다. 장시간 처리 중 실제 창 응답, 탭 전환, marker 가시성은 사용자 조작 smoke test를 반복한다.
- 기존 synthetic/integrated pipeline 코드는 하네스 회귀 호환성을 위해 남아 있으나 새 GUI에서는 노출하지 않는다.
