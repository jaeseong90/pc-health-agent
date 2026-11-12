# PC Health Agent

직원 PC(Windows 10/11)의 CPU·메모리·디스크 부족 상황과 그때 실행 중이던 프로세스를 기록합니다. 기록한 데이터로 **PC 업그레이드(RAM/SSD)나 교체가 필요한지 판정**하는 경량 에이전트입니다.

- **서버 없음:** 데이터는 PC 안에만 저장하고, 필요할 때 `export`로 zip을 만들어 전달합니다.
- **가벼움:** .NET 10 Native AOT 빌드 기준 exe 7.6MB, 메모리(작업 집합) 약 20MB, CPU 사용 전체의 0.001% 미만(시간당 약 0.4초), 디스크 기록 분당 약 150바이트입니다. (16스레드 노트북에서 기본 설정으로 3분 측정)
- **개인정보 보호:** 창 제목·문서명·URL·키 입력·화면은 수집하지 않습니다. 지정한 프로세스명은 마스킹합니다.

## 파일럿 진행 절차

1. **빌드:** 개발 PC에서 `.\publish.ps1`을 실행하면 `artifacts\win-x64\PcAgent.exe`가 만들어집니다.
2. **설치:** 대상 PC에 exe를 복사한 뒤, **관리자 권한 cmd**에서 아래를 실행합니다.
   ```
   PcAgent.exe install
   ```
   - `C:\Program Files\PcAgent`에 복사되고 Windows 서비스 `PcAgent`로 등록됩니다.
   - 서비스는 '지연된 자동 시작'이라 부팅 직후에는 부하를 주지 않습니다.
3. **수집:** 평소처럼 2주 이상 사용합니다. 상태는 `PcAgent.exe status`로 확인할 수 있습니다.
4. **내보내기:** 각 PC에서 아래를 실행하면 바탕화면에 `PcHealth_<PC명>_<시각>.zip`이 생깁니다.
   ```
   PcAgent.exe export --days 14
   ```
   - 일반 사용자 권한으로도 됩니다.
   - 관리자 권한으로 실행하면 부팅 시간 등 일부 이벤트 로그가 더 포함됩니다.
5. **취합:** zip들을 한 폴더에 모은 뒤 아래를 실행합니다.
   ```
   PcAgent.exe merge <폴더>
   ```
   PC별 1행짜리 `fleet_summary_*.csv`가 만들어지고, 조치가 시급한 순(점수 높은 순)으로 정렬됩니다.
6. **제거:** `PcAgent.exe uninstall`을 실행합니다. 수집 데이터까지 지우려면 `--purge`를 붙입니다.

## 명령어

| 명령 | 설명 |
|---|---|
| `install` / `uninstall [--purge]` | 서비스 설치·제거 (관리자 권한) |
| `status` | 서비스 상태, 수집 기간·용량, 마지막 측정값 |
| `export [--days N] [--out 폴더]` | 진단 리포트 zip 생성 (기본 30일, 바탕화면) |
| `merge <폴더> [--out 파일]` | 여러 PC의 zip을 요약 CSV 하나로 합치기 |
| `inventory` | 하드웨어 사양 출력(JSON) |
| `run` | 서비스 없이 콘솔에서 수집 (테스트용) |
| `--data-dir <폴더>` | 데이터 폴더 지정 (기본 `%ProgramData%\PcAgent`) |

## 내보내기 파일 구성

| 파일 | 내용 |
|---|---|
| `report.html` | 진단 리포트. 브라우저로 열며, 판정·권장 조치·사용 현황·사양·일별 추이·원인 프로세스·안정성을 담습니다 |
| `summary.csv` | PC 1대 요약 1행 (`merge`에서 사용) |
| `summary.json` | 사양 + 분석 결과 전체 |
| `metrics_minute.csv` | 1분 단위 CPU/메모리/디스크 지표 |
| `pressure_events.csv`, `pressure_processes.csv` | 자원 부족 이벤트와 그 시점의 상위 프로세스 |
| `event_logs.csv` | 블루스크린, 비정상 종료, 앱 크래시·응답 없음, 메모리 부족 경고, 부팅 시간 |

## 동작 방식

```
[Windows 서비스 PcAgent — 효율 모드, 낮은 우선순위]
  5초마다 측정 ─ CPU(GetSystemTimes), 메모리/커밋(GlobalMemoryStatusEx),
                 하드 페이지 폴트·시스템 디스크 활성/큐(PDH)
     ├─ 1분 단위로 집계 → data\metrics-YYYYMMDD.csv
     └─ 자원 부족이 30초 이상 지속되면
          → 상위 프로세스 스냅샷(CPU%·메모리·I/O) → data\pressure-YYYYMMDD.jsonl
            (유형별 10분 쿨다운, 하루 최대 48회)

[export 실행 시에만]
  하드웨어 사양(SMBIOS·레지스트리·디스크 IOCTL·TPM) + Windows 이벤트 로그 조회
  → 판정 엔진 → zip
```

경량화를 위해 이렇게 설계했습니다.
- **WMI, DB, 네트워크를 쓰지 않습니다.** 대신 커널 API를 직접 호출합니다.
- **사양과 이벤트 로그는 상시 수집하지 않습니다.** 내보낼 때만 조회합니다.
- **자원 부족 판정은 진입·해제 기준을 따로 둡니다(히스테리시스).** 순간 스파이크는 무시하고, 상태가 경계값 근처에서 오르내려도 이벤트가 폭주하지 않습니다.
- **에이전트 자신의 우선순위를 낮춥니다.** CPU 우선순위 '낮음', 효율 모드(EcoQoS), 메모리 우선순위 '낮음'으로 실행됩니다.
- **로컬 데이터는 자동으로 정리합니다.** 보존 기간 90일, 용량 상한 200MB를 넘으면 오래된 것부터 지웁니다.

## 개인정보

- **수집하는 것:** 프로세스 이름, 실행 파일 경로, 게시자, CPU/메모리/I/O 사용량, 시스템 지표, 하드웨어 사양, 안정성 이벤트
- **수집하지 않는 것:** 창 제목, 문서·파일 이름, 웹 주소, 키 입력, 화면, 명령줄 인수
- **경로:** `C:\Users\홍길동\...`처럼 사용자 계정이 드러나는 부분은 항상 `%USERPROFILE%\...`로 바꿔 저장합니다.
- **마스킹:** `maskProcessPatterns`에 맞는 프로세스는 `[masked-xxxxxx]`로 저장하고, 경로와 게시자는 지웁니다.
  - 해시는 PC별 salt를 쓰므로 원래 이름을 역추적할 수 없습니다.
  - 설정을 바꾸면 이미 저장된 데이터에도 내보낼 때 적용됩니다.
- **데이터 폴더 권한:** SYSTEM과 관리자만 쓸 수 있고, 일반 사용자는 읽기만 됩니다. 본인 PC 데이터를 직접 확인하고 내보낼 수 있습니다.

## 판정 기준 (`UpgradeAdvisor`)

기본은 업무시간(평일 9~18시) 데이터로 판정하고, 업무시간 데이터가 4시간 미만이면 전체 시간으로 판정합니다. 성능 기반 판정에는 8시간 이상의 관측이 필요합니다.

| 조건 | 권장 조치 |
|---|---|
| 메모리 부족 비율 ≥ 5% (또는 Windows 메모리 부족 경고 2회 이상, 또는 RAM ≤ 4GB) | **메모리 증설** (목표 용량, 빈 슬롯 안내). 온보드 메모리면 **PC 교체** |
| 메모리 부족 비율 2~5% | 메모리 사용 관찰 |
| 메모리 부족 이벤트의 60% 이상에서 같은 프로그램이 1위 | 해당 **프로그램 점검** (증설 전에 확인) |
| 시스템 디스크가 HDD | **SSD 교체** (디스크 포화 3% 이상이면 우선순위 높음) |
| CPU 포화 비율 ≥ 10% + (추정 연식 5년 이상, 구형 CPU, 2코어 이하 중 하나) | **PC 교체** |
| CPU 포화 비율 ≥ 10% (위 조건 해당 없음) | CPU 사용 검토 / 프로그램 점검 |
| Windows 10 + Windows 11 요구사항 미충족 (TPM 2.0, UEFI, CPU 세대 등) | **PC 교체** |
| Windows 10 + 요구사항 충족 | Windows 11 업그레이드 |
| 블루스크린 2회 이상 / 비정상 종료 3회 이상 | 안정성 점검 |
| 시스템 드라이브 여유 공간 < 10% 또는 < 10GB | 디스크 공간 확보 |

판정 결과는 교체 권장 / 업그레이드 권장 / 점검 필요 / 양호 / 데이터 부족 중 하나입니다. 조치 점수는 '높음' 40점, '보통' 15점으로 계산하고 최대 100점입니다. 임계값은 파일럿 결과를 보고 `src/PcAgent/Analysis/UpgradeAdvisor.cs`에서 조정하세요.

> Windows 11 CPU 지원 여부는 모델명 기반 추정입니다(Intel 8세대 이상, AMD Ryzen 2000 이상). 최종 확인은 'PC 상태 검사' 앱으로 하세요. 추정 연식은 BIOS 날짜 기준이라, BIOS를 업데이트했다면 실제보다 짧게 나옵니다.

## 설정 (`%ProgramData%\PcAgent\config.json`)

설치할 때 기본값으로 생성됩니다. 파일을 수정하면 5분 안에 서비스 재시작 없이 반영됩니다. 주요 항목은 아래와 같습니다.

```jsonc
{
  "sampleIntervalSeconds": 5,          // 측정 주기
  "retentionDays": 90,                 // 보존 기간
  "maxDataSizeMb": 200,                // 용량 상한
  "cpuHighPercent": 90, "cpuRecoverPercent": 70, "cpuSustainSeconds": 30,
  "memoryAvailableLowPercent": 10,     // 가용 메모리 ≤ 10% 또는
  "commitHighPercent": 90,             // 커밋 ≥ 90% → 메모리 부족
  "diskActiveHighPercent": 90,
  "topProcessCount": 8,                // 스냅샷 시 CPU/메모리/I/O 각 상위 N개
  "snapshotCooldownMinutes": 10, "maxSnapshotsPerDay": 48,
  "includeProcessPaths": true,         // false 면 경로 미저장
  "maskProcessPatterns": ["KakaoTalk*", "Telegram*", "Discord*", "Steam*", "*torrent*"],
  "workStartHour": 9, "workEndHour": 18, "workdaysOnly": true
}
```

## 개발

```powershell
dotnet build
dotnet test                                    # 단위 테스트
dotnet run --project src/PcAgent -- inventory  # 사양 확인
dotnet run --project src/PcAgent -- run --data-dir .\devdata
.\publish.ps1                                  # 배포용 단일 exe
```

- **구조:** `Collection`(측정·감지), `Storage`(파일 저장), `Inventory`(사양), `EventLogs`, `Analysis`(판정), `Export`(리포트), `Hosting`(서비스·설치)
- **.NET 버전:** .NET 10 LTS(지원 종료 2028-11-14)를 씁니다. 빌드하려면 .NET 10 SDK가 필요하며, `global.json`이 10.0.x SDK로 고정합니다. 버전을 바꿀 때는 `Directory.Build.props`의 `NetVersion`, `global.json`, 패키지 버전을 함께 바꿉니다.
- **Native AOT:** 빌드 PC에 Visual Studio의 'C++를 사용한 데스크톱 개발'이 있으면 `publish.ps1`이 자동으로 AOT 빌드합니다. 코드는 AOT 호환이며, 빌드 시 AOT/트리밍 분석 경고가 0건입니다.
- **배포 보안:** 서비스가 SYSTEM 권한으로 실행되므로, 사내 배포 전에 코드 서명을 권장합니다. 백신·EDR이 오탐하면 보안팀에 예외 등록을 요청하세요.

## 라이선스

[MIT License](LICENSE)
