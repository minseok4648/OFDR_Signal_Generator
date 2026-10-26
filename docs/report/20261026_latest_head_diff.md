# 최신 커밋 대비 작업 폴더 Diff

## 요청사항

가장 최근 기준의 diff를 작성한다. 비교 기준은 로컬 최신 커밋 `94edd5621735439696111d801375b1e97f5c860a`(2026-03-12 15:23:33 +09:00), 비교 대상은 2026-10-26 작성 시점의 작업 폴더이다. 원격 저장소의 최신 여부는 확인하지 않았다.

## 이슈·원인

기존 추적 파일 수정뿐 아니라 Core, Harness, 테스트, config, dataset metadata, 실행 결과와 보고서가 미추적 상태이다. 일반 `git diff HEAD`만 저장하면 이 신규 파일들이 누락된다. 최신 알고리즘 변경만이 아니라 기준 커밋 이후 누적 변경 전체를 포함해야 한다.

## 해결방안

`20261026_latest_head.diff`에 추적 파일의 HEAD 대비 변경과 미추적 프로젝트 파일의 신규 추가 diff를 함께 저장한다. Git binary patch를 사용해 이미 추적 중인 `build_verify_update1`의 EXE/PDB 변경도 보존한다. 이 바이너리가 최신 소스의 빌드 결과임을 보증하지는 않는다.

로컬 도구 설정인 `.serena/`, Git이 무시하는 원본·변환 데이터 및 빌드 산출물, diff 파일 자체는 제외한다. 기존 실패·취소 실험의 config/metrics/summary는 포함한다. 실제 Git index와 소스 코드는 변경하지 않는다.

## 구현 내용: 기준 → 현재

| 영역 | 기준 커밋 대비 현재 변경 |
| --- | --- |
| 솔루션·배포 | GUI 단일 프로젝트에서 Core/Harness/net48 MSTest를 포함하는 구성으로 확장. Debug/Release x64, win-x64 런타임, GUI의 MKL 직접 의존성 및 native DLL 배포 검사를 추가 |
| 공용 Core | ISF 헤더 기반 signed big-endian 읽기, 독립 채널/통합 CSV 변환과 읽기, dataset scan, JSON 계약, FFT, k-선형화, preview 축소 및 처리 엔진 추가 |
| K-linearization | Ref와 Sig를 각 sweep의 동시 취득 CH1으로 독립 보정한 뒤 짧은 공통 상대 phase span으로 맞춤. 입력 채널·반복·변형량·시간축 및 CH1 QC 검사 추가 |
| S pol | 전체 FFT → 거리 segment → Window → zero-padded IFFT → magnitude correlation → fractional peak → GHz/Strain profile 경로 추가. normalized dB FFT와 제한된 lag 탐색 사용 |
| 물리축·기본 config | schema v3, 물리 경로차 18.2703 m, group index 시작값 1.4682, 분석 3.5~8.7 m, plateau 5.5~6.5 m. Hann/1024/hop 256/padding 16, ±180 GHz, -140 dB floor |
| GUI | MainWindow의 DataContext를 OfdrWorkspaceViewModel로 연결하고 종료 시 Dispose. 기준 CH1/Ref/측정 CH1/Sig 입력, K 진단, S pol 단계별 그래프·확대 화면, X/Y 사각형 확대 및 복원 추가 |
| 레거시 경로 | 기존 MainViewModel, synthetic 파이프라인, 단계별 처리·CSV 로더·검증기도 수정. 전체 diff에는 이 누적 변경도 포함 |
| 메모리·성능 | UI preview 최대 10,000 point, segment 상세 배열 누적 대신 선택 segment 재계산, 대용량 처리의 x64/native MKL 요구사항 추가 |
| 하네스·재현성 | CLI, baseline config/schema, dataset manifest·golden metadata, GUI startup smoke 스크립트, 성공·실패 실행 기록과 사용법 추가 |
| 테스트·문서 | CoreTests에 ISF/CSV/FFT, 독립 sweep 보정, Window 순서, correlation 정규화, 입력 검증 등의 19개 테스트 메서드 추가. 처리 계약·metric·보고 체계 추가 |

## 검증 결과

이번 작업은 diff와 보고서 생성이다. 빌드, 단위 테스트, 실제 5M 처리 및 WPF startup smoke를 새로 실행하지 않았다. 아래 값은 저장된 2026-08-28 기록이며 현재 소스를 재검증한 결과로 해석하지 않는다.

| 보존된 근거 | 기록 내용 |
| --- | --- |
| `20260828_dual_aux_klinear_spectral_shift_fix.md` | 별도 x64 빌드 경고/오류 0, MSTest 19/19 통과 기록 |
| `harness/results/20260828_112245_pair_0_4mm0000CH2` | 4입력 각 5M, MKL x64, COMPLETED, 4.545 s, 487 segments |
| `harness/results/20260828_dual_aux_csharp_series_regression` | CH2/0000, 0.1~0.9 mm ACCEPT. 0.4 mm plateau 중앙값 약 25.985 GHz, 외부 중앙값 범위 ±0.116 GHz 이내, 거리 FFT 구간 변화 -10.385 dB |
| `harness/results/20260828_112304_gui_startup_smoke` | COMPLETED, ProcessStayedAlive=true, 약 8초 관찰 |

패치 구조와 재현성은 `git apply --numstat` 및 현재 작업 폴더에 대한 `git apply --reverse --check`로 확인한다. 실제 적용은 수행하지 않는다. 파일 수와 해시를 포함한 생성 결과는 함께 제공하는 `20261026_latest_head_diff_manifest.json`에 기록한다. manifest와 diff 자체는 패치에서 제외한다.

## 남은 확인 사항

- 실제 장비의 group index, 중심 파장, strain coefficient, distance geometry factor 확인 전 절대 Strain 교정은 미확정이다.
- CH3, 반복 0001/0002 및 1.0 mm의 동일 config 회귀는 기존 보고서에서 후속 확인 사항으로 남아 있다.
- 이 diff는 2026-03-12 커밋부터 현재까지의 전체 누적 변경이다. 마지막 2026-08-28 보고서 이후만의 증분을 뜻하지 않는다.
- 실행 가능 상태의 재판정은 저장소 규칙에 따른 새 빌드·5M full run·GUI startup smoke가 필요하다.
