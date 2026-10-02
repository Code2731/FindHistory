# FindHistory 프로젝트 리뷰

검토일: 2026-10-01
검토 대상: `codex/activity-heatmap` 브랜치 작업 트리 (origin/main 동일 지점 + 미커밋 변경)
검토 방법: 전체 소스·테스트·벤치마크·문서 정독, Release 빌드 및 스모크 테스트 실제 실행

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
