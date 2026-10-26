# OFDR 독립 채널 분석 GUI 사용방법

## 1. 실행 전 확인

1. Visual Studio 구성 관리자를 `Debug | x64` 또는 `Release | x64`로 선택한다.
2. 솔루션 전체를 빌드한다. `OFDR_Signal_Core`가 건너뛰어 Core DLL을 찾지 못하면 구성 관리자에서 Core의 `빌드`를 체크한 뒤 다시 빌드한다.
3. 실행 폴더에 다음 MKL 파일이 있는지 확인한다.
   - `MathNet.Numerics.Providers.MKL.dll`
   - `libiomp5md.dll`
   - `libMathNetNumericsMKL.dll`
4. 이전 GUI가 실행 중이면 종료한 뒤 빌드한다. 실행 중인 프로그램이 기본 `bin\Debug` 파일을 잠글 수 있다.

5M 실데이터에서 MKL 초기화에 실패하면 managed FFT로 자동 전환하지 않고 오류를 표시한다.

## 2. 입력 파일 선택

왼쪽 `데이터 불러오기` 탭에서 기준 sweep 두 파일과 측정 sweep 두 파일을 각각 선택한다.

| 입력 | 선택 규칙 | 예시 |
| --- | --- | --- |
| 기준 보조 간섭계 | Ref와 동시 취득한 0.0 mm CH1 | `0_0mm0000CH1.isf` |
| Ref | 변형 없음(0.0 mm), CH2 또는 CH3 | `0_0mm0000CH2.isf` |
| 측정 보조 간섭계 | Sig와 동시 취득한 같은 변형량 CH1 | `0_4mm0000CH1.isf` |
| Sig | 변형 있음(0.0 mm 초과), Ref와 같은 채널 | `0_4mm0000CH2.isf` |

`찾아보기`에서 `.isf` 또는 `.csv`를 고를 수 있다. 네 파일을 같은 확장자로 맞출 필요는 없다. Ref 또는 Sig를 먼저 고르면 같은 폴더·변형량·반복의 `CH1` 파일이 존재할 때 대응 보조간섭계 경로를 자동 입력한다.

검증 규칙은 다음과 같다.

- 기준 Aux는 반드시 CH1/0.0 mm이고 Ref와 반복 번호가 같아야 한다.
- 측정 Aux는 반드시 CH1이고 Sig와 변형량·반복 번호가 같아야 한다.
- Ref는 반드시 0.0 mm이다.
- Sig는 0.0 mm보다 커야 한다.
- Ref와 Sig는 둘 다 CH2이거나 둘 다 CH3이어야 한다.
- 기준 Aux/Ref가 한 쌍이고 측정 Aux/Sig가 한 쌍이다. 기준과 측정 쌍의 반복 번호는 서로 달라도 되지만 각 쌍 안에서는 같아야 한다.
- sample 수와 시간축이 같아야 한다.

규칙에 맞지 않으면 `전체 처리 시작` 전에 이해하기 쉬운 한글 오류를 표시한다.

## 3. ISF 한 파일을 채널 CSV로 변환

1. `CSV 출력 폴더`를 선택한다. 기본 폴더는 원본 아래 `converted_channels`이다.
2. 기준 Aux, Ref, 측정 Aux 또는 Sig 행의 `채널 CSV 변환`을 누른다.
3. 결과 파일은 원본 ISF와 같은 이름의 `.csv`와 `.metadata.json`이다.

CSV 형식은 다음 세 열이다.

```text
Index,Time_s,Voltage_V
```

변환 중에는 `.partial` 파일을 사용하며 완료된 후에만 최종 CSV가 남는다. 같은 원본 hash로 만든 최신 CSV는 건너뛰고, 오래된 CSV는 덮어쓰기 확인 후 교체한다.

## 4. 알고리즘 설정

왼쪽 `알고리즘 설정` 탭에는 현재 처리에 필요한 값만 있다.

- `편광`: CH2/Xpol 또는 CH3/Ypol. Ref/Sig 파일 채널과 같아야 한다.
- `Window`: Rectangular, Hann(Hanning), Hamming, Blackman.
- `Segment 크기`: 한 Cross Correlation에 사용하는 FFT bin 수.
- `Segment 간격`: 다음 segment 시작까지 이동하는 FFT bin 수.
- `Zero padding 배수`: segment IFFT 길이 배수.
- `최대 Rayleigh Spectral Shift`: correlation peak를 찾을 ±GHz 범위. 기본 180 GHz.
- `정규화`: 평균 제거한 Ref/Sig 국부 Rayleigh 크기 스펙트럼의 Cross Correlation을 전체 에너지로 정규화할지 여부.
- `Cross Correlation 배율`: correlation 결과에 적용하는 scale.
- `CH1 비단조 비율 한계`: k-선형화 QC 기준. 기본 `1e-5`.
- `그래프 최대 point`: 각 전체 그래프 preview의 상한. 기본 10,000.

거리/spectral 축 계수는 다음과 같다.

- `보조간섭계 물리 경로차`: 기본 18.2703 m. optical path가 아니라 광섬유 물리 경로차로 해석한다.
- `보조간섭계 광섬유 Group Index`: 기본 1.4682. sweep span과 GHz/lag 계산에 사용한다.
- `거리 기하 계수`: 장비 간섭계 구성에 따른 보정 배율. 기본 1.
- `거리 Offset`: 표시 거리 전체에 더하는 값.
- `거리별 Spectral shift 처리를 분석 구간으로 제한`: 기본 켬. 기본 3.5~8.7 m segment를 처리한다.
- `변형 시작/종료 거리`: 기본 5.5~6.5 m. 기본 상세 segment와 plateau 검증 범위이다.

Strain 계수는 다음과 같다.

- `중심 파장`: 기본 1550 nm. 실제 Laser 값을 확인한다.
- `Strain 계수 κₑ`: 기본 0.78. 실제 Fiber의 strain-optic 계수를 확인한다.
- `Strain 배율/Offset`: 교정 후 값의 배율과 offset.
- `변형구간 길이`: 기본 1.04 m. 파일명의 stage 변위에서 기준 Strain을 계산할 때 사용한다.

측정 메모의 5.5~6.5 m, 변형길이 1.03~1.05 m, Sweep rate 100 nm/s, Sampling 25 MS/s를 기본값으로 넣었다. `Sweep range 16 nm/s`는 range 단위가 rate와 같아 GUI에서는 `16 nm`로 임시 해석했으므로 원 장비 설정을 확인해야 한다. 메모의 채널당 2M과 실제 ISF header 5M이 다를 때는 header를 우선한다.

처음에는 검증된 baseline 값(Hann, Segment 1024, 간격 256, padding 16, ±180 GHz, 정규화)을 사용하고, parameter를 바꾼 경우 config와 결과를 별도 실험으로 기록한다.

## 5. 전체 처리

1. 네 입력과 설정을 확인한다.
2. `전체 처리 시작`을 누른다.
3. 진행률에서 기준 CH1/Ref, 측정 CH1/Sig 읽기, 두 sweep k-선형화, FFT, segment 처리를 확인한다.
4. 중단하려면 `취소`를 누른다. 완료되지 않은 결과는 현재 화면 결과를 덮어쓰지 않는다.

5M 데이터는 UI thread 밖에서 처리한다. 그래프는 전체 배열 대신 축약 preview를 사용한다.

## 6. K-linearization 탭

오른쪽 `K-linearization` 탭에서 다음 그래프를 순서대로 확인한다.

1. 기준 CH1의 Raw, 평균 제거, Hilbert analytic magnitude, wrapped/unwrapped phase, phase increment.
2. 기준 Ref가 보간된 공통 상대 phase grid.
3. 측정 CH1의 Raw, unwrapped phase, phase increment.
4. 측정 Sig가 보간된 공통 상대 phase grid.

Ref와 Sig는 각각 동시 취득한 CH1으로 독립 보정된 뒤 두 phase span 중 짧은 공통 범위로 맞춰진다. 화면의 QC 요약에서 두 sweep 각각의 방향, phase span, nonmonotonic ratio, accepted anchor 수를 확인한다. 어느 한쪽이라도 nonmonotonic ratio가 설정값을 초과하면 처리를 실패로 기록한다.

## 7. S pol 탭

오른쪽 `S pol` 탭에서 다음 결과를 확인한다.

1. `Raw 전압`: 입력 파일에서 읽은 K 보정 전 Ref/Sig 원신호 비교.
2. `FFT (K 보정 전)`: 원본 sample 간격에서 계산한 Ref/Sig normalized dB FFT. 보정 전후 비교와 QC용이다.
3. `FFT 거리 데이터 (K 보정 후)`: 각 sweep CH1의 analytic phase로 Ref/Sig를 독립 보간하고 공통 phase 범위로 맞춘 뒤 계산한 normalized dB FFT. 이후 Segment 처리의 실제 입력이며 케이블 구간의 OTDR형 감쇠 slope도 이 화면에서 확인한다.
4. `선택 Segment`: K 보정 후 거리 FFT의 같은 거리 complex segment magnitude.
5. `Window 적용 Segment`: 선택 segment에만 Rectangular/Hann/Hamming/Blackman Window를 적용한 결과.
6. `IFFT 국부 스펙트럼`: Window segment를 zero padding한 뒤 IFFT한 Ref/Sig 국부 Rayleigh 스펙트럼.
7. `Cross Correlation`: 평균 제거한 국부 스펙트럼 magnitude 패턴의 정규화 상관. ±최대 spectral shift 안에서 최고 peak를 찾고 포물선 보간한 위치를 표시한다.
8. `Peak 확대`: 최고 peak 주변 lag 구간 확대.
9. `거리별 Rayleigh Spectral Shift`: 각 거리 segment의 fractional peak를 GHz로 환산한 최종 분산 profile.
10. `거리별 Strain`: spectral shift를 중심 파장/strain 계수로 환산한 profile.

처리 범위 제한이 켜져 있으면 기본 3.5~8.7 m profile을 표시하고 알려진 변형 plateau는 5.5~6.5 m에서 확인한다. Loctite 접착부와 강한 반사점에서는 국부 spike가 날 수 있다.

처리가 끝나면 변형 구간 중앙에 가장 가까운 segment가 자동 선택된다. 다른 구간을 보려면 `Segment 번호`를 입력하고 `Segment 확인`을 누른다. 번호 범위는 `0`부터 `segment_count - 1`이다.

S pol 화면 맨 아래 `선택 그래프 크게 보기`에서 Raw, K 보정 전 FFT, K 보정 후 거리 FFT, Segment, Window, IFFT, Correlation, 확대 Correlation, 거리별 spectral shift, 거리별 Strain 중 하나를 선택하면 520 높이의 큰 그래프로 다시 표시된다. Ref/Sig 비교가 있는 화면은 두 곡선을 함께 표시하고 Correlation 화면은 peak marker도 유지한다.

모든 그래프에서 확대할 구간을 사각형으로 드래그한다. 선택 사각형의 가로 범위는 X축, 세로 범위는 Y축 확대 범위가 된다. `한 단계 축소`는 바로 전 X/Y 범위로 돌아가며 `전체 보기`는 전체 X 범위와 자동 Y 범위를 복원한다.

## 8. CLI로 같은 처리 재현

```powershell
OFDR_Harness.exe run-pair `
  --aux "C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv" `
  --sig-aux "C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH1.csv" `
  --ref "C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH2.csv" `
  --sig "C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_4mm0000CH2.csv" `
  --channel 2 `
  --config "harness\configs\baseline_independent_pair_5m.json" `
  --results "harness\results"
```

GUI와 CLI는 같은 reader, config, FFT, k-선형화, Ref/Sig 처리 코어를 사용한다. CLI 결과는 `harness/results/<experiment_id>`의 `summary.md`, `config.json`, `metrics.json`에 남는다.

## 9. 결과 해석 주의사항

- FFT/segment의 x축은 물리축 사용 시 거리(m), 비활성화 시 FFT bin이다. Cross Correlation x축은 lag bin이다.
- `peak lag`는 위치가 아니라 국부 spectrum shift이다. 위치는 segment 중심 거리, 최종 광학 결과는 spectral shift(GHz), 계수 환산 결과는 Strain(με)으로 따로 확인한다.
- 파일명 stage 변위 / 변형구간 길이로 표시되는 기준 Strain은 비교 기준이며 광학 교정값을 자동 확정하지 않는다.
- Loctite 접착 위치에서는 Rayleigh pattern peak가 깨질 수 있으므로 단일 자동 선택 segment만 보지 말고 거리별 profile과 인접 segment를 함께 확인한다.
- Ref/Sig 조합을 바꾸면 파일 경로와 hash를 함께 기록한다.
- 0.1~0.9 mm/반복 0000/CH2는 같은 config로 계단 응답을 확인했다. Ypol(CH3), 다른 반복, 1.0 mm는 별도 회귀 검증 대상이다.
