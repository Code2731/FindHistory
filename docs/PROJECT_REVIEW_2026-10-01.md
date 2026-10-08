# FindHistory 프로젝트 리뷰

검토일: 2026-10-01
검토 대상: `codex/activity-heatmap` 브랜치 작업 트리 (origin/main 동일 지점 + 미커밋 변경)
검토 방법: 전체 소스·테스트·벤치마크·문서 정독, Release 빌드 및 스모크 테스트 실제 실행

> 최신 재리뷰는 **9. 2026-10-04 재리뷰**에 있습니다. 현재 `codex/activity-heatmap`은 main 대비 7커밋이 모두 커밋·푸시되었고 빌드 경고 0·스모크 테스트 PASS 상태입니다. 이전 이슈 해소 현황(9.2), 신규 이슈(9.3), 테스트 커버리지(9.4), 권장 순서(9.5)를 함께 확인하세요.

## 1. 검증 결과 (실제 실행)

| 항목 | 명령 | 결과 |
|---|---|---|
| SDK 환경 | `dotnet --info` | .NET SDK 10.0.102, Windows 10.0.26200 (win-x64) |
| 빌드 | `dotnet build -c Release` | 경고 0 / 오류 0, `bin\Release\net10.0-windows\FindHistory.dll` |
| 스모크 테스트 | `dotnet run --project tests\FindHistory.SmokeTests -c Release` | PASS (로그·저장·검색·날짜 경계/DST·활동 집계·진단·기존 DB 이관·DB 이동/전환 복구·설정·실시간 감시) |
| Git 상태 | `git status`, `rev-list --count origin/main..HEAD` | `codex/activity-heatmap` = origin/main과 동일 커밋(0 ahead), 변경 8개 파일 미커밋 |

> 최우선 확인 사항: 마지막 커밋은 `4864ad8`(v1.0.2 핫픽스)이고, 히트맵 + 조합형 필터 기능 전체(569줄 추가)가 커밋되지 않은 작업 트리 상태다. 대상 파일: `MainWindow.xaml`, `ViewModels/MainViewModel.cs`, `Services/RecentDatabase.cs`, `Models/RecentItem.cs`, `ViewModels/Commands.cs`, `tests/FindHistory.SmokeTests/Program.cs`, `README.md`, `docs/ROADMAP.md`. 현재 브랜치는 `origin/main`과 동일 지점이므로 이 기능은 어디에도 저장돼 있지 않다.

## 2. 프로젝트 개요

- 정체성: Windows Recent Items(`.lnk`/`.url`)를 감시해 로컬 SQLite에 누적하고 검색하는 WPF 데스크톱 앱. v1.0.2, 프라이버시 우선(네트워크 없음).
- 스택: C# / .NET 10 (net10.0-windows), WPF + MVVM, Microsoft.Data.Sqlite 10.0.12, SQLite FTS5(trigram).
- 규모: 앱 코드 약 2,400줄 + 테스트 361줄 + 벤치마크 239줄 + 문서 2종.
- 구조 (관심사 분리 양호):

```text
Models/       기록·필터·활동·진단 record + 날짜범위 팩토리
Services/     RecentDatabase / RecentItemsMonitor / ShortcutResolver
              AppSettingsService / AppLogService / AutoStartService
ViewModels/   MainViewModel(857줄) / Commands(RelayCommand·AsyncCommand)
Views         MainWindow / StorageSettingsWindow / DiagnosticsWindow
tests/        콘솔 기반 스모크 테스트(의존성 0)
benchmarks/   7회 중앙값 측정 하네스
docs/         ROADMAP, PERFORMANCE
```

## 3. 잘 된 점 (유지할 자산)

1. 강건성 설계가 일관적 — `AppLogService`가 IO 예외를 삼켜 로그 실패가 앱을 막지 않고, `ShortcutResolver`는 COM/IO 예외 시 `null` 반환, FileSystemWatcher 오류 시 복구 스캔, 전역 예외 핸들러 등록, 단일 인스턴스 뮤텍스까지 갖춤.
2. 날짜/시간 처리 정확 — 로컬 달력 → UTC 반개구간 `[start, end)` 변환을 `HistoryDateRangeFactory`로 일원화하고 DST 시작/종료일을 테스트로 검증. 저장은 `"O"`(왕복 ISO-8601, `Z`)라 문자열 정렬이 곧 시간 정렬.
3. 검색 설계가 근거 기반 — FTS5 trigram + 선택도 프로브 후 LIKE 폴백(`IsSelectiveFtsQueryAsync`), `*.pdf` 같은 정확 확장자는 `(extension, last_seen_utc)` 복합 인덱스 경로, LIKE 메타문자(`% _ \`) 이스케이프, 전부 매개변수화 쿼리로 SQL 인젝션 없음.
4. 쓰기 성능 — 배치 Upsert 단일 트랜잭션 + prepared command 재사용, WAL + `synchronous=NORMAL`, 벤치마크로 처리량/회귀 측정 가능.
5. 데이터 안전 — 설정은 temp 파일 후 원자적 `File.Move`, DB 이동 시 `wal_checkpoint(TRUNCATE)` 후 이동 + sidecar 정리, DB 전환 시 `recent_items` 존재 검증, 실패 시 롤백 로직이 VM에 구현.
6. 통계 최적화 — `history_stats` 1행 + 트리거 유지로 매 검색의 전체 `COUNT/SUM` 제거(문서화된 개선).
7. 문서화 — 한/영 README, 단계별 검증 게이트가 있는 ROADMAP, 재현 가능한 성능 리포트.

## 4. 이슈 및 리스크 (심각도순)

### 높음

- H1. 기능 작업이 미커밋 상태 — 히트맵/필터 569줄이 작업 트리에만 존재해 유실 위험이 있다. 커밋/푸시가 필수다.
- H2. 매 시작마다 전체 테이블 스캔 INSERT가 실행됨 — `Services/RecentDatabase.cs`의 `InitializeCoreAsync` 스키마 블록에서 다음 두 문이 이관 여부와 무관하게 매 실행마다 동작한다.
  ```sql
  INSERT OR IGNORE INTO open_events (recent_item_id, opened_utc, source_link_path, is_estimated)
  SELECT id, last_seen_utc, source_link_path, 1 FROM recent_items;      -- 시작마다 O(N)
  INSERT OR REPLACE INTO history_stats (id, unique_items, total_open_count)
  SELECT 1, COUNT(*), COALESCE(SUM(open_count), 0) FROM recent_items;   -- 시작마다 O(N)
  ```
  100만 건 규모에서는 시작 지연과 불필요한 쓰기 트랜잭션이 된다. `PRAGMA user_version` 또는 `schema_migrations` 테이블로 1회만 실행하도록 게이팅 권장.
- H3. DB 접근 전면 직렬화 + `Pooling=false` — 모든 작업이 단일 `_databaseGate`를 통과하고 매 작업마다 새 연결을 연다. 초기 대량 스캔이 게이트를 잡는 동안 UI 검색이 대기한다. WAL을 쓰고 있으므로 읽기 전용 연결의 동시성을 열 여지가 있다. `Pooling=false`는 `MoveToAsync`의 파일 이동 안전성 때문으로 보이나 그 이유를 주석/문서로 명시할 것.

### 중간

- M1. FTS 갱신 트리거가 모든 UPDATE에 반응 — `EnsureSearchIndexAsync`의 `AFTER UPDATE ON recent_items` 트리거는 재등장 시 `last_seen_utc`/`open_count`만 갱신돼도(`display_name`/`target_path` 불변) FTS delete + insert를 수행한다. `AFTER UPDATE OF display_name, target_path ON recent_items`로 축소하면 쓰기 증폭이 줄어든다.
- M2. `GetDailyActivityAsync`가 범위 내 모든 이벤트를 C#으로 적재 — `ORDER BY opened_utc`로 전체 행을 읽어 `TimeZoneInfo.ConvertTime`으로 날짜 버킷팅한다. 시간대/DST 정확성을 위한 선택이지만 이벤트가 많으면 비용이 크다. `date(opened_utc, '<offset>')` 기반 SQL 집계 + DST 경계일 보정, 또는 페이지네이션 검토.
- M3. `GetDiagnosticsAsync`의 이벤트 카운트가 매번 스캔 — `(SELECT COUNT(*) FROM open_events)`와 `WHERE is_estimated = 1` 두 서브쿼리가 전체 스캔이다. `history_stats`처럼 유지 카운터 또는 진단 창에서만 지연 계산 권장.
- M4. DB 전환 검증이 "테이블 존재"만 확인 — 동명 테이블이지만 스키마가 다른 파일을 통과시킨 뒤 런타임에 실패할 수 있다. `user_version` 또는 `pragma table_info` 기반 핵심 컬럼 검증 추가.
- M5. FileSystemWatcher `InternalBufferSize` 기본값(8KB) — 대량 바로가기 생성 시 버퍼 오버플로 → Error → 전체 재검사(복구 횟수 증가). 64KB 상향으로 복구 빈도 감소 가능.
- M6. 검증/자동화 인프라 부재 — `.sln`, `.github/workflows`, publish 프로파일이 없다. 루트 `dotnet build`는 앱만 빌드하고 테스트/벤치마크는 경로를 명시해야 한다. 솔루션 + CI(빌드 → 스모크 테스트) 권장.
- M7. 버전/문서 불일치 — csproj는 1.0.2인데 README 다운로드 절은 `FindHistory-v1.0-win-x64.zip`을 참조한다. ROADMAP은 v1.0.2 릴리즈 보류, v1.1의 "사용자 확인" 2건 미완. `PERFORMANCE_2026-09-24.md` 상단 환경은 .NET 8.0.23이고 하단에 .NET 10 재검증 addendum가 붙어 있다. 문서 통합 권장.

### 낮음 (개선 여지)

- L1. `AsyncCommand.Execute`가 `async void` — `execute()`에서 예외가 나면 전역 핸들러로만 로깅된다. 공통 try/catch + 로그 권장(현재는 각 명령 내부에서 catch 중).
- L2. `DispatcherUnhandledException` 핸들러가 `args.Handled`를 설정하지 않음 — 로깅 후에도 앱이 종료된다. 의도라면 주석으로 명시.
- L3. 히트맵 클릭 시 이중 리로드 — `SelectActivityDate`가 `SpecificDate`와 `SelectedDateRange`를 연속 설정해 각각 `ScheduleReload`를 유발한다. 디바운스로 흡수되지만 불필요하다.
- L4. `MainViewModel`이 `Application.Current.Dispatcher`에 직접 의존 — 헤드리스 단위 테스트 불가(스모크 테스트는 VM을 우회). 규모가 커지면 디스패처 추상화 고려.
- L5. 문자열 하드코딩 — VM/XAML에 한국어 문자열이 직접 박혀 있다(`CountText`, `ExistsText` 포함). 영문 UI 계획이 있다면 리소스 분리.
- L6. `EstimatedEvents` 불변식 — "`last_seen_utc`에는 항상 대응 이벤트가 존재한다"는 가정 위에 시작 시 이관 INSERT가 no-op이 된다. 이 불변식을 명시적 테스트로 고정 권장(현 테스트는 첫 이관만 검증).

## 5. 미커밋 기능(히트맵 + 조합형 필터) 평가 — 양호

- 데이터 계층: `GetDailyActivityAsync`가 이벤트를 로컬 시간대 기준으로 버킷팅하고 `ContainsEstimated`를 전파한다. 스모크 테스트에 날짜별 집계, 추정 플래그, 폴더 LIKE 메타문자 리터럴 처리, 하위 폴더 포함, extension/exists AND 조합 케이스가 추가돼 커버리지가 좋다.
- 필터: `HistoryFilters`를 `SearchCoreAsync`에 파라미터로 주입한다. 폴더 구분자까지 LIKE 접두사에 포함해 `Work`와 `Work-old`를 정확히 구분하고, `% _ \` 이스케이프 처리를 확인했다.
- UI: 16주 히트맵(로그 스케일 level 0~4, 오늘/선택 테두리, 미래 날짜 비활성), 필터 칩 개별 해제/전체 초기화, 히트맵 셀에 `AutomationProperties.Name` 지정까지 반영했다.
- 남은 것: ROADMAP에 명시된 실제 앱에서의 히트맵 클릭/조합 필터 사용자 확인 2건.

## 6. 권장 다음 단계 (우선순위)

1. 즉시 — 미커밋 변경을 커밋하고 `codex/activity-heatmap` 브랜치를 푸시 (H1).
2. v1.1 마감 전 — 시작 시 마이그레이션 게이팅(H2), FTS UPDATE 트리거 `OF` 축소(M1). 둘 다 회귀 테스트 추가와 함께.
3. 릴리즈 전 — DB 검증 강화(M4), `.sln` + CI(M6), 버전/README/ROADMAP/PERFORMANCE 문서 정합화(M7).
4. 여유 시 — 읽기/쓰기 동시성(H3), 활동 집계 SQL화(M2), 진단 카운터(M3), Watcher 버퍼(M5).

## 7. 후속 구현 가능 항목

- H1 커밋/푸시 정리
- H2 마이그레이션 게이팅 + 회귀 테스트
- M1 FTS 트리거 축소 + 회귀 테스트
- M6 솔루션 + CI 워크플로

## 8. 2026-10-02 진행 기록

- `df972d0` — 히트맵·조합형 필터 및 리뷰 문서를 `codex/activity-heatmap` 원격 브랜치에 보존.
- DB schema `user_version=1` 도입: 기존 날짜 이벤트 이관과 초기 통계 행 생성을 한 트랜잭션에서 한 번만 실행. 미래 버전 DB는 거부.
- FTS UPDATE 트리거를 `display_name`/`target_path` 실제 변경 때만 실행하도록 교체.
- 반복 초기화 후 이벤트 중복·통계 보존 및 기존 트리거 교체 회귀 검증을 추가.
- DB 전환 시 `recent_items` 필수 컬럼과 스키마 버전을 읽기 전용으로 검사하도록 강화.
- `.sln`과 Windows Actions 빌드·스모크 워크플로 추가, README ZIP 안내와 성능 보고서 런타임 구간을 명확히 정리.
- 10,000건 합성 배치 중 검색 대기를 두 차례 측정: 유휴 검색 2.24–2.94 ms, 대기 검색 2.60–2.65 s. 실제 251건 수집 저장 27–32 ms와 구분해 기록.
- 최대 30개의 이름 있는 검색 조건을 로컬 설정에 저장·복원·삭제하도록 구현. 상대 기간은 적용 시점에 재계산하고 지정 날짜는 고정.

## 9. 2026-10-04 재리뷰 (커밋 기준 전체 감사)

검토 대상: `codex/activity-heatmap` 브랜치 (main 대비 7커밋, 19개 파일, +1,973 / −41줄)
검토 방법: 전체 소스·테스트·벤치마크·CI 정독 후 Release 빌드와 스모크 테스트 직접 실행

### 9.1 검증 결과 (실제 실행)

| 항목 | 명령 | 결과 |
|---|---|---|
| 빌드 | `dotnet build FindHistory.sln -c Release` | 경고 0 / 오류 0 |
| 스모크 테스트 | `dotnet run --project tests/FindHistory.SmokeTests -c Release --no-build` | PASS (로그·저장/복원/내보내기·검색·날짜 경계/DST·활동 집계·진단·기존 DB 이관·DB 이동/전환 복구·설정·실시간 감시) |
| Git 상태 | `git status` | working tree clean, `origin/codex/activity-heatmap`와 동기화 |

대상 커밋: `df972d0`(히트맵·조합 필터) → `e56858c`(마이그레이션 게이팅·FTS 트리거) → `39076af`(DB 검증·CI) → `61d6255`(저장 검색·경합 벤치) → `ce0278a`(WAL 프로토타입) → `8cf4267`(백업/복원) → `253f5b9`(CSV/JSON 내보내기)

### 9.2 이전 리뷰 이슈 해소 현황

| 항목 | 상태 | 근거 |
|---|---|---|
| H1 미커밋 기능 유실 위험 | 해소 | 7개 커밋 푸시, working tree clean |
| H2 시작 시 O(N) 마이그레이션 | 해소 | `RecentDatabase.cs:177-210` — `user_version=1` 게이팅 + 단일 트랜잭션, 미래 버전 거부 |
| H3 전면 직렬화 + `Pooling=false` | 부분 | `RecentDatabase.cs:65-66`에 `Pooling=false` 사유 주석, 경합 벤치마크로 대기 수치 문서화. 게이트 직렬화 자체는 유지 |
| M1 FTS 트리거 과다 갱신 | 해소 | `RecentDatabase.cs:1053-1063` — `AFTER UPDATE OF display_name, target_path` + `WHEN` + DROP/CREATE 교체 |
| M2 활동 집계 전체 적재 | 잔존 | `RecentDatabase.cs:514-560` — 범위 전체 행을 읽어 C#에서 날짜 버킷팅 |
| M3 진단 COUNT 전체 스캔 | 잔존 | `RecentDatabase.cs:456-490` — `open_events` COUNT 서브쿼리 2회 |
| M4 DB 전환 검증 부실 | 해소 | `RecentDatabase.cs:765-815` — 테이블 + `user_version` + `recent_items` 필수 컬럼 검증 |
| M5 Watcher 버퍼 8KB | 잔존 | `Services/RecentItemsMonitor.cs` 미변경 |
| M6 sln/CI 부재 | 해소 | `FindHistory.sln`, `.github/workflows/windows-ci.yml` (빌드 → 스모크) |
| M7 문서 정합성 | 부분 | README ZIP 안내 수정됨. `PERFORMANCE_2026-09-24.md` 런타임 구간 혼재는 잔존 |
| L1 `AsyncCommand` async void | 잔존 | `ViewModels/Commands.cs:38-56` |
| L2 `args.Handled` 미설정 | 잔존 | `App.xaml.cs:147-148` |
| L3 히트맵 이중 리로드 | 잔존 | `MainViewModel.cs:865-875` (140ms 디바운스로 흡수) |

### 9.3 신규 이슈 (심각도순)

#### 중간

- **R-M1. `MoveDatabaseAsync` 롤백이 보호되지 않음 + 창 영구 비활성 위험** — `ViewModels/MainViewModel.cs:482-495`의 catch에서 `await _database.MoveToAsync(previousPath)`가 try 없이 실행된다. 롤백이 실패하면 예외가 catch를 벗어나 `TryRestoreSettingsPath`(490)가 실행되지 않아 **설정은 실패한 목적지를, DB는 원위치를 가리키는 불일치**가 남는다. 예외는 `StorageSettingsWindow.xaml.cs:50-52`의 `async void` 핸들러로 전파되어 `IsEnabled = true` 복구도 건너뛴다(창 영구 비활성). 같은 성격의 `UseDatabaseAsync`(619-627)는 롤백을 `try/catch`로 감싸므로 일관성이 없다.
- **R-M2. `ScheduleReload`만 예외를 처리하지 않음** — `MainViewModel.cs:765-777`은 `OperationCanceledException`만 catch한다. 검색 중 SQLite 오류 등은 `UnobservedTaskException`으로만 남고 사용자 피드백(`StatusText`)이 없다. `ScheduleActivityReload`(804-807)는 일반 예외를 로깅하므로 동일하게 맞출 필요가 있다.
- **R-M3. 저장 검색 적용 시 `DateRanges.First` 예외 가능** — `MainViewModel.cs:100-102`. `settings.json`에 목록에 없는 `CalendarDayCount`(예: 14)가 있으면 `InvalidOperationException` → 바인딩 오류로 조용히 실패. `FirstOrDefault(...) ?? DateRanges[0]` 폴백 권장.
- **R-M4. `AppSettingsService` 로드/저장 강건성** — `Services/AppSettingsService.cs:56-76`이 `UnauthorizedAccessException`(IOException 하위 아님)을 처리하지 않는다. 또한 `AddSavedSearch`/`RemoveSavedSearch`(30-54)는 `Save()` 이전에 메모리 상태를 갱신하므로 저장 실패 시 **메모리·디스크 불일치**가 남는다.
- **R-M5. 폴더 필터의 `Path.GetFullPath` 예외 경로** — `Services/RecentDatabase.cs:851`. 손상된 저장 검색의 폴더 문자열이면 `ArgumentException`이 R-M2와 겹쳐 조용히 실패한다.

#### 낮음

- **R-L1. `RecentDatabase.Dispose`(`52-63`)의 세마포어 경합** — `Wait(); Release(); Dispose();` 사이에 다른 스레드가 게이트를 잡으면 `ObjectDisposedException` 가능. 대용량 내보내기 중이면 종료 시 UI가 블로킹된다.
- **R-L2. 롤백 시 `NotifyDatabaseLocationChanged()` 미호출** — 성공 경로(`MainViewModel.cs:476`)에서만 호출되어, 롤백까지 실패하는 경로에서 경로·용량 바인딩이 실제와 어긋난다.
- **R-L3. 설정을 이동보다 먼저 저장** — `MainViewModel.cs:474`에서 `SetDatabasePath` 후 `MoveToAsync`. 그 사이 프로세스가 종료되면 다음 시작 시 빈 DB가 생성된다.
- **R-L4. 활동 집계 시퀀스 가드 부재** — `LoadActivityAsync`(`811-852`)는 `LoadAsync`의 `_loadSequence` 가드가 없어 레이스에서 이전 결과가 덮어쓸 수 있다(실해 미미).
- **R-L5. `CalculateActivityLevel`(`854-863`) 상대 스케일** — 최대값이 1이면 단일 이벤트도 level 4가 된다. 절대 임계 병용 검토.
- **R-L6. `SearchText` setter 들여쓰기** — `MainViewModel.cs:293` `ClearSavedSearchSelection();` 정렬 어긋남.
- **R-L7. `SaveSearchDialog` 입력 검증** — `SaveSearchDialog.cs:46-50` 공백 이름 시 안내 없이 포커스만 이동. 이름 중복 검사·`MaxLength` 없음.
- **R-L8. 내보내기가 DB 게이트를 전 구간 점유** — `RecentDatabase.cs:297-359`. H3 잔존 항목으로, 벤치마크로 대기 수치가 문서화된 상태.
- **R-L9. 종료 중 `Application.Current.Dispatcher` NRE 가능성** — `MainViewModel.cs:963-976` 핸들러에 null 가드 권장.
- **R-L10. `UnsafeRelaxedJsonEscaping`**(`RecentDatabase.cs:399`) — 로컬 내보내기 전용이라 타당하나 용도를 문서에 명시 권장.

### 9.4 테스트 커버리지 평가

좋은 커버리지 (스모크 테스트 확인):
- 날짜 범위 `[start, end)` 경계와 종료 직전 이벤트, DST 시작 23시간·종료 25시간
- 검색어 + 확장자 + 존재 여부 + 폴더(하위 폴더 포함) AND 조합, 폴더 LIKE 메타문자 리터럴
- 확장자 와일드카드 대소문자, 3자 미만 LIKE 폴백, 다중 검색어 AND
- CSV 수식 주입 접두, CSV/JSON 내보내기 산출물, DB 이동/전환 실패 복구
- 잘못된 DB·필수 컬럼 누락 DB·미래 스키마 버전 거부, 백업/복원, 저장 검색 라운드트립

갭:
- `MainViewModel` 로직(필터 조합·저장 검색 적용·디바운스 순서)은 `Application.Current.Dispatcher` 의존으로 헤드리스 검증 불가 (기존 L4)
- 저장 검색 **30개 상한** 및 초과 동작 미검증
- `.before-restore-*.bak` 안전 사본 누적 정책·정리 미검증
- `SaveSearchDialog`/`StorageSettingsWindow` 핸들러의 `IsEnabled` 복구 경로(R-M1) 미검증
- `CalculateActivityLevel` 레벨 산정, `FilterChips` 조합 미검증

### 9.5 권장 다음 단계

1. 즉시 — R-M1: `MoveDatabaseAsync` 롤백을 `UseDatabaseAsync`와 동일하게 `try/catch`로 보호하고, `StorageSettingsWindow`의 이동/전환 핸들러를 `try/finally { IsEnabled = true; }`로 통일.
2. v1.1/v1.2 마감 전 — R-M2(예외 로깅), R-M3(`FirstOrDefault` 폴백), R-M4(설정 원복·예외 확대) 소규모 패치와 회귀 테스트.
3. 릴리즈 전 — R-L3(설정 저장 순서), M2/M3(SQL 집계·진단 카운터), M5(Watcher 버퍼 64KB).
4. 여유 시 — H3(읽기 전용 연결/내보내기 게이트 분리), R-L5(히트맵 절대 임계), R-L7(저장 검색 입력 검증).

## 10. 2026-10-09 현재 코드 재확인

9장은 당시 브랜치의 검토 기록입니다. 아래 상태는 main의 후속 구현과 회귀 테스트를 기준으로 합니다. 실제 UI 사용자 확인을 대신하지 않습니다.

| 항목 | 현재 상태와 근거 |
| --- | --- |
| R-M1 / R-L2 / R-L3 | 이동은 목적지 검증 후 설정 저장. 실패하면 원본을 보존. VM은 실제 경로로 설정 복구와 바인딩 갱신. 이동·전환 창은 `finally`에서 활성화. `MoveToAsync`, `MoveDatabaseAsync`, `UseDatabaseAsync`에서 확인. |
| R-M2 / R-M3 / R-M5 | 검색 예외를 로그와 상태에 반영. 저장 기간은 `SavedSearchDateRangeResolver`로 폴백. 폴더 정규화 실패는 명시적 예외로 전달. |
| R-M4 / R-L7 | 설정 저장 성공 후 메모리 갱신. 손상·접근 오류를 기본 설정으로 숨기지 않음. 저장 검색의 공백·중복·64자·30개 제한 테스트 존재. |
| R-L1 / R-L4 / R-L9 | DB 종료는 비동기로 진행 작업 대기. 새 작업 거부. 활동 결과에 시퀀스 검사. UI 이벤트 전달에 종료·null 검사. |
| R-L5 | `ActivityLevelCalculator`가 상대 레벨과 절대 임계 중 작은 값을 사용. 1·4·10·25회 경계 테스트 존재. |
| M2 | 일반 활동 범위는 로컬 날짜별 UTC 경계를 만든 뒤 SQL 집계. DST 23·25시간 테스트 통과. 366일 초과 범위의 이벤트 순회는 유지. |
| M3 / M5 | 이벤트·추정 이벤트 수는 `history_stats`와 트리거로 유지. 진단은 카운터 조회. 감시 버퍼는 64 KiB. |
| L1 | `AsyncCommand`의 UI 호출은 공통 오류 처리로 전달. `ExecuteAsync` 호출은 호출자에게 예외 전달. 명시적 취소만 오류 보고에서 제외. 중복 실행·실패·취소 후 상태 복구 테스트 추가. |
| L2 | 명령 오류는 명령 경계에서 처리. 그 밖의 처리되지 않은 UI 오류는 로그 후 종료. 알 수 없는 상태로 계속 실행하지 않는 정책을 코드에 명시. |
| 안전 사본 테스트 갭 | 반복 복원이 고유한 안전 사본을 생성함을 확인. 이전 사본의 존재와 SHA-256이 유지됨을 확인. 자동 정리 대상에서도 제외하는 기존 테스트 유지. |

남은 항목:

- H3: 일반 DB 작업의 단일 게이트 유지. 내보내기는 별도 스냅샷 경로 사용. 전체 읽기·쓰기 동시성 확대는 추가 측정 후 판단.
- M7: 기존 성능 보고서의 런타임 구간 통합.
- L3: 히트맵 클릭이 날짜와 기간을 각각 설정하며 갱신을 두 번 요청. 디바운스로 합쳐지지만 요청 자체는 남아 있음.
- L4: VM 전체의 디스패처 추상화와 헤드리스 테스트.
- 로드맵에 남긴 실제 UI 사용자 확인.

검증: Release 솔루션 빌드 경고 0/오류 0. 전체 스모크 테스트 통과. 릴리즈 생성은 보류.

## 11. 2026-10-09 히트맵 날짜 선택 안정화

- L3 해소: 날짜와 기간을 먼저 확정한 뒤 속성 알림을 전달. 필터와 히트맵 선택 표시는 한 번만 갱신. 검색 예약도 한 번 호출.
- 기존 구현의 검색 요청은 대부분 이미 한 번 발생했음. 중복은 날짜와 기간 setter의 필터·선택 표시 갱신에서 발생. 이번 수정은 중간 선택 상태가 알림으로 노출되는 문제도 해소.
- 같은 날짜 재클릭은 검색·필터·선택 표시를 갱신하지 않음. 선택 불가 날짜는 적용하지 않음.
- `HeatmapViewModelTests` 추가. 별도 STA 스레드의 WPF Application과 실제 디스패처에서 `MainViewModel`을 실행. 임시 DB·설정·감시 폴더만 사용. 자동 실행 설정은 읽기만 수행.
- 테스트: 전체 기간에서 날짜 선택, 지정 날짜 사이 이동, 필터 유지, 저장 검색 선택 해제, 일관된 상태 알림, 같은 날짜 재클릭, 선택 불가 날짜, 빠른 연속 클릭의 최신 결과 적용.
- L4의 디스패처 추상화는 아직 미구현. 이번 테스트는 실제 디스패처를 이용하는 기반이며 VM 전체의 헤드리스 테스트를 대신하지 않음.

검증: Release 솔루션 빌드 경고 0/오류 0. 히트맵 화면 모델을 포함한 전체 스모크 테스트 통과. 실제 화면 조작의 사용자 확인은 별도로 유지.
