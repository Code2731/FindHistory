<p align="center">
  <img src="Assets/findhistory-icon.png" width="128" height="128" alt="FindHistory icon">
</p>

<h1 align="center">FindHistory</h1>

<p align="center">
  Windows의 짧은 최근 항목 기록을 로컬 데이터베이스에 계속 보존하고 검색합니다.<br>
  Keep and search Windows Recent Items in a private local database.
</p>

<p align="center">
  <a href="#korean">한국어</a> · <a href="#english">English</a>
</p>

---

<a id="korean"></a>

## 한국어

### 소개

Windows의 **최근 항목** 목록은 보관 기간과 개수가 제한적입니다. FindHistory는 Windows가 만드는 최근 항목 바로가기(`.lnk`, `.url`)를 감시해 로컬 SQLite 데이터베이스에 누적하고, 파일명·전체 경로·와일드카드·날짜로 다시 찾을 수 있게 해 주는 Windows 데스크톱 앱입니다.

기록은 외부 서버로 전송하지 않으며, 사용자가 선택한 PC의 DB 파일에만 저장합니다.

### 주요 기능

- 메인 화면에서 한국어/영어 UI를 선택하고 설정에 저장
- 앱 시작 시 기존 Windows 최근 항목 가져오기
- 최근 항목 폴더 실시간 감시 및 파일 감시 오류 시 자동 전체 재검사
- 동일 파일의 재등장 횟수와 마지막 기록 시각 누적
- 개별 열기 이벤트를 날짜별로 보존하고 특정 날짜만 조회
- 최근 16주의 날짜별 활동량을 히트맵으로 확인하고 날짜를 눌러 바로 필터링
- 파일명과 전체 경로 검색, 여러 검색어 조합, `*`/`?` 와일드카드 지원
- 전체 기간 / 오늘 / 최근 7일 / 최근 30일 / 최근 1년 / 날짜 지정 필터
- 확장자·저장된 존재 상태·폴더(하위 폴더 포함)를 날짜·검색어와 AND 조건으로 조합하고 필터 칩으로 개별 해제
- 로컬 파일·폴더의 존재 상태를 백그라운드에서 순차 갱신
- 검색어와 필터 조합을 최대 30개까지 이름을 붙여 로컬에 저장하고 다시 불러오기
- 파일 열기 및 파일 탐색기에서 위치 열기
- 현재 DB를 다른 폴더로 이동하거나 기존 `findhistory.db` 선택
- 창을 닫은 뒤에도 시스템 트레이에서 백그라운드 기록
- 선택적인 Windows 로그인 시 자동 실행
- 감시 상태·DB 통계·마지막 오류를 확인하는 진단 창
- 14일 보관 및 파일 크기 회전을 적용한 로컬 진단 로그
- SQLite FTS5 및 검색 인덱스를 사용한 대량 기록 검색
- 네트워크 계정이나 클라우드 연결 없이 로컬에서 동작

### 검색 방법

검색창은 파일명과 전체 경로를 함께 검색합니다. `Ctrl+K`를 누르면 어디서든 검색창으로 이동합니다.

| 입력 예시 | 의미 |
| --- | --- |
| `meeting` | 파일명이나 경로에 `meeting`이 포함된 항목 |
| `*.mp4` | 확장자가 `.mp4`인 항목 |
| `clip_20??.mp4` | `?` 위치에 각각 한 글자가 들어가는 MP4 파일 |
| `project *.pdf` | `project`와 `*.pdf` 조건을 모두 만족하는 항목 |
| `D:\Work report` | 경로/파일명에서 두 검색어를 모두 만족하는 항목 |

와일드카드에서 `*`는 0개 이상의 문자, `?`는 정확히 한 문자를 뜻합니다. 여러 검색어는 **AND 조건**으로 적용됩니다.

### 날짜별 기록

기간 선택에서 **날짜 지정**을 고르면 달력의 특정 날짜에 열었던 파일만 볼 수 있습니다. 메인 화면의 **최근 활동** 히트맵에서 날짜를 누르면 같은 필터가 즉시 적용되며, 색이 진할수록 그날 기록된 열기 횟수가 많다는 뜻입니다.

날짜 기록에는 다음과 같은 Windows 측 한계가 있습니다.

- FindHistory가 실행 중이면 최근 항목 바로가기의 변경을 감지해 개별 열기 이벤트를 기록합니다.
- 앱이 종료된 동안 같은 파일을 여러 번 열면, 다음 실행 시 Windows가 남긴 **가장 최근 바로가기 시각 한 건**만 복구할 수 있습니다.
- FindHistory를 설치하기 전에 Windows가 이미 삭제한 오래된 최근 항목은 복구할 수 없습니다.
- 이전 버전 DB를 처음 열 때 기존 `last_seen` 값은 날짜 이벤트로 이관되며 **추정 기록**으로 표시됩니다.

날짜 이력을 최대한 정확하게 남기려면 **Windows 로그인 시 백그라운드 실행**을 켜 두는 것을 권장합니다.

### 파일 존재 상태

앱은 시작 시 최대 200개 기록을 검사합니다. 이후 30초마다 최대 200개씩 순서대로 검사합니다. 목록 끝에 도달하면 처음부터 다시 검사합니다. 파일 삭제·복원 또는 폴더 존재 상태가 바뀌면 결과표와 존재 상태 필터를 갱신합니다. 열기 횟수와 날짜 기록은 변경하지 않습니다. 기록이 많으면 전체 상태 갱신에 시간이 걸립니다. 화면의 상태는 마지막으로 확인한 값이며 실시간 보장은 아닙니다.

웹 주소, UNC 경로, 네트워크 드라이브, 검사 대상 자체가 링크인 경로는 자동 검사에서 제외합니다. 연결되지 않은 드라이브와 접근 오류도 기존 상태를 유지합니다. 웹 주소는 수집 시의 상태를 유지하며 접속 가능 여부를 검사하지 않습니다. 자동 검사는 백그라운드에서 실행하며 파일 확인 중에는 DB 잠금을 잡지 않습니다.

### 다운로드

[GitHub Releases](https://github.com/Code2731/FindHistory/releases/latest)에서 최신 릴리즈에 첨부된 `win-x64` ZIP을 내려받아 압축을 풀고 `FindHistory.exe`를 실행하세요. 공식 ZIP은 .NET 런타임을 포함하므로 별도로 설치할 필요가 없습니다.

### 요구 사항

- Windows 10 또는 Windows 11
- 공식 `win-x64` ZIP: 별도 런타임 불필요
- 소스에서 실행/빌드: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 프레임워크 종속 게시본 실행: .NET 10 Desktop Runtime

### 소스에서 실행

```powershell
git clone https://github.com/Code2731/FindHistory.git
cd FindHistory
dotnet run
```

Release 게시 폴더를 만들려면 다음 명령을 사용합니다.

```powershell
dotnet publish -c Release --self-contained false -o publish
```

생성된 `publish/FindHistory.exe`를 실행합니다. 창의 닫기 버튼은 앱을 시스템 트레이로 보냅니다. 완전히 종료하려면 트레이의 FindHistory 아이콘을 우클릭하고 **종료**를 선택하세요.

### 데이터 저장소와 개인정보

기본 DB 위치:

```text
%LocalAppData%\FindHistory\findhistory.db
```

설정 파일 위치:

```text
%LocalAppData%\FindHistory\settings.json
```

진단 로그 위치:

```text
%LocalAppData%\FindHistory\Logs
```

진단 로그는 로컬에만 저장되며 14일이 지난 파일은 앱 시작 시 자동으로 정리됩니다. 오류 메시지에는 접근 중이던 로컬 경로가 포함될 수 있으므로 공유하기 전에 내용을 확인하세요.

메인 화면의 **데이터 저장소**에서 기록을 유지한 채 DB를 다른 폴더로 옮기거나, 다른 위치의 기존 `findhistory.db`를 선택할 수 있습니다. DB는 실행 파일과 별도로 유지되므로 앱을 업데이트하거나 게시 폴더를 교체해도 기록이 남습니다.

같은 화면에서 전체 기록을 CSV 또는 JSON으로 내보낼 수 있습니다. **현재 결과 CSV/JSON**은 현재 검색어·확장자·존재 상태·폴더·날짜 조건에 맞는 전체 기록을 내보냅니다. 화면의 1,000개 표시 제한은 적용하지 않습니다. 날짜를 선택하면 해당 기간의 열기 이벤트만 포함합니다. 파일별 요약의 최초·최종 기록 시각과 누적 횟수는 전체 기간 값입니다. 날짜를 선택하지 않으면 일치하는 항목의 전체 열기 이력을 포함합니다. 빈 결과도 유효한 빈 JSON 또는 헤더만 있는 CSV로 저장합니다. JSON에는 검색 조건과 이력 범위 설명을 포함합니다. CSV는 Excel에서 수식으로 실행될 수 있는 값에 안전 접두사를 붙입니다.

같은 화면의 **백업 저장**은 SQLite 온라인 백업으로 일관된 백업 파일을 만듭니다. **백업 복원**은 유효한 FindHistory DB인지 먼저 검사하고, 현재 데이터베이스를 고유한 안전 사본으로 보관한 뒤 선택한 백업으로 복원합니다. 백업은 앱 설정 파일과 별개이므로, 저장된 검색 조건을 함께 보존하려면 `%LocalAppData%\FindHistory\settings.json`도 별도로 복사하세요.

**자동 백업 사용**을 켜면 앱 실행 중 로컬 날짜 기준 하루에 한 번 백업합니다. 기본값은 꺼짐이며 보관 개수는 DB별 1~30개입니다. 설정 변경은 1분 이내에 적용됩니다. 백업은 `%LocalAppData%\FindHistory\Backups`에 저장됩니다. 보관 개수를 넘으면 해당 DB의 오래된 자동 백업만 삭제합니다. 수동 백업과 복원 전 안전 사본은 삭제하지 않습니다. DB 위치를 바꾸면 별도의 백업 목록으로 관리합니다. 자동 백업도 **백업 복원**으로 복원할 수 있습니다. 같은 드라이브의 백업은 드라이브 고장에 대비할 수 없으므로 필요한 백업은 외장 드라이브에도 복사하세요.

**저장 공간**은 DB·WAL·SHM 파일 크기와 DB 내부의 할당 공간·빈 페이지 크기를 표시합니다. 백업 파일은 합계에 포함하지 않습니다. 값은 조회 시점의 크기입니다. **다시 계산**으로 갱신할 수 있습니다. **공간 정리**는 SQLite `VACUUM`으로 기록을 유지한 채 빈 공간을 회수합니다. 빈 페이지 크기는 예상 회수량이며 실제 파일 감소량과 다를 수 있습니다. 작업 중 검색·수집·내보내기는 대기합니다. 작업에는 추가 디스크 공간이 필요할 수 있습니다. 다른 프로그램이 DB를 사용 중이면 작업이 실패할 수 있습니다. 날짜별 기록 삭제는 아직 제공하지 않습니다.

검색 조건을 설정한 뒤 **검색 저장**을 눌러 이름을 지정하면 로컬 설정에 저장됩니다. 저장 검색 목록에서 조건을 다시 적용하거나 선택 삭제할 수 있습니다. 최근 7일 같은 상대 기간은 불러온 시점을 기준으로 계산하고, 날짜 지정 검색은 저장 당시 선택 날짜를 유지합니다.

SQLite DB는 로컬 드라이브 또는 외장 드라이브에 두는 것을 권장합니다. OneDrive 같은 실시간 동기화 폴더는 여러 장치나 프로세스가 동시에 DB에 접근할 때 충돌할 수 있습니다.

FindHistory가 읽는 범위는 Windows의 다음 최근 항목 폴더입니다.

```text
%AppData%\Microsoft\Windows\Recent
```

수집된 파일명, 경로, 날짜와 횟수는 선택한 로컬 DB에만 저장됩니다.

### 테스트와 성능

DB 저장·검색·마이그레이션·DB 이동/전환·설정 유지·실시간 파일 감시는 스모크 테스트로 검증할 수 있습니다.

```powershell
dotnet run --project tests\FindHistory.SmokeTests -c Release
```

벤치마크를 실행하려면:

```powershell
dotnet run --project benchmarks\FindHistory.Benchmarks -c Release
```

초기 10,000건 배치 쓰기 중 검색 대기 시간을 따로 측정하려면:

```powershell
dotnet run --project benchmarks\FindHistory.Benchmarks -c Release -- --contention-only
```

10만 건 JSON 내보내기 중 검색 대기를 측정하려면:

```powershell
dotnet run --project benchmarks\FindHistory.Benchmarks -c Release -- --export-contention-only
```

현재 최적화된 기준 구현은 10만 건 DB에서 최신 1,000건 조회 약 **4.21 ms**, `*.pdf` 1,000건 조회 약 **6.10 ms**, 최근 7일 이벤트 1,000건 조회 약 **13.42 ms**를 기록했습니다. 수치는 개발 환경에 따라 달라지며, 측정 방법과 100만 건 결과는 [성능 보고서](docs/PERFORMANCE_2026-09-24.md)에 정리되어 있습니다.

다음 개발 단계와 완료 조건은 [개발 로드맵](docs/ROADMAP.md)에서 확인할 수 있습니다.

### 기술 구성

- C# / .NET 10
- WPF / MVVM
- SQLite / Microsoft.Data.Sqlite
- SQLite FTS5 trigram 검색 및 인덱스 기반 와일드카드 최적화

---

<a id="english"></a>

## English

### Overview

Windows keeps only a limited Recent Items history. FindHistory watches the recent-item shortcuts (`.lnk`, `.url`) created by Windows, stores them in a local SQLite database, and lets you find them later by file name, full path, wildcard, or date.

Your history is not sent to an external server. It remains in the database file you choose on your PC.

### Features

- Switch the interface between Korean and English from the main window. The choice is saved locally.
- Imports existing Windows Recent Items at startup
- Watches the Recent Items directory in real time and automatically rescans after watcher errors
- Tracks how often an item reappears and when it was last seen
- Preserves individual open events and filters them by an exact calendar date
- Shows the last 16 weeks in an activity heatmap and filters by a clicked date
- Searches file names and full paths with multiple terms and `*`/`?` wildcards
- All time / Today / Last 7 days / Last 30 days / Last year / Specific date filters
- Combines extension, last recorded existence, and folder (including descendants) with date and search terms; removable chips show active filters
- Refreshes local file and folder existence in background batches
- Saves up to 30 named search combinations locally and restores them later
- Opens a file or reveals its location in File Explorer
- Moves the current database or switches to an existing `findhistory.db`
- Exports the complete history and per-open events to CSV or JSON
- Keeps recording in the system tray after the window is closed
- Optional background startup at Windows sign-in
- Diagnostics window for watcher health, database statistics, and the latest error
- Local diagnostic logs with 14-day retention and size-based rotation
- Uses SQLite FTS5 and dedicated indexes for large histories
- Works locally without an account or cloud connection

### Searching

The search box matches both file names and full paths. Press `Ctrl+K` to focus it from anywhere in the main window.

| Example | Meaning |
| --- | --- |
| `meeting` | Items whose name or path contains `meeting` |
| `*.mp4` | Items with the `.mp4` extension |
| `clip_20??.mp4` | MP4 names with exactly one character at each `?` |
| `project *.pdf` | Items matching both `project` and `*.pdf` |
| `D:\Work report` | Items whose path/name matches both terms |

In wildcard expressions, `*` matches zero or more characters and `?` matches exactly one character. Multiple terms are combined with **AND semantics**.

### Date history

Choose **Specific date** in the period selector to show only files opened on a selected calendar date. You can also click a day in the **Recent activity** heatmap to apply the same filter immediately; darker cells represent more recorded opens.

Date history is subject to limitations in the Windows Recent Items source:

- While FindHistory is running, it watches shortcut changes and records individual open events.
- If the same file is opened several times while FindHistory is not running, only the **latest shortcut timestamp** left by Windows can be recovered at the next startup.
- Items already removed by Windows before FindHistory saw them cannot be recovered.
- When an older FindHistory database is upgraded, each existing `last_seen` value is migrated as an **estimated event**.

For the most accurate date history, enable **background startup at Windows sign-in**.

### Download

Download the `win-x64` ZIP attached to the latest release from [GitHub Releases](https://github.com/Code2731/FindHistory/releases/latest), extract it, and run `FindHistory.exe`. The official ZIP is self-contained, so no separate .NET runtime installation is required.

### Requirements

- Windows 10 or Windows 11
- Official `win-x64` ZIP: no separate runtime required
- To run or build from source: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- To run a framework-dependent publish: .NET 10 Desktop Runtime

### Run from source

```powershell
git clone https://github.com/Code2731/FindHistory.git
cd FindHistory
dotnet run
```

To create a Release publish directory:

```powershell
dotnet publish -c Release --self-contained false -o publish
```

Run `publish/FindHistory.exe`. Closing the main window sends the app to the system tray. To quit completely, right-click the FindHistory tray icon and choose **Exit**.

### Database and privacy

Default database location:

```text
%LocalAppData%\FindHistory\findhistory.db
```

Settings location:

```text
%LocalAppData%\FindHistory\settings.json
```

Diagnostic log location:

```text
%LocalAppData%\FindHistory\Logs
```

Diagnostic logs remain local and files older than 14 days are removed at application startup. Error messages may contain a local path that was being accessed, so review the content before sharing it.

Use **Data storage** on the main screen to move the database without losing existing history, or select an existing `findhistory.db` elsewhere. The database is kept separately from the executable, so replacing or updating the publish directory does not remove your history.

**Save Backup** creates a consistent SQLite online backup. **Restore Backup** validates that the selected file is a FindHistory database, preserves the current database as a uniquely named safety copy, and then restores the selected backup. Backups do not include app settings; copy `%LocalAppData%\FindHistory\settings.json` separately if you also want to preserve saved searches.

Enable **automatic backups** to back up once per local calendar day while the app runs. This option is off by default. Keep 1–30 backups per database. Settings apply within one minute. Backups are saved in `%LocalAppData%\FindHistory\Backups`. Only the oldest automatic backups for that database are pruned. Manual backups and pre-restore safety copies are kept. Changing the database path starts a separate backup set. Use **Restore Backup** to restore an automatic backup. A backup on the same drive does not protect against drive failure. Copy important backups to an external drive as well.

**Storage space** shows DB, WAL, and SHM file sizes, allocated database space, and free pages. Backup files are excluded. Values are a snapshot; use **Refresh sizes** to update them. **Compact database** uses SQLite `VACUUM` to reclaim space without deleting history. Free pages indicate estimated reclaimable space; the actual file reduction can differ. Search, capture, and export wait during compaction. Extra disk space may be required. Compaction can fail if another program is using the database. Deleting history by date is not supported yet.

Export the complete history as CSV or JSON from **Data storage**. **Current results CSV/JSON** exports all matches for the current search text, extension, existence status, folder, and date filters. The 1,000-item display limit does not apply. With a date filter, only events in that range are included. Item summary dates and open counts remain lifetime values. Without a date filter, all events for matching items are included. Empty results produce a valid empty JSON list or a header-only CSV. JSON includes the selection and event scope. CSV prefixes spreadsheet formula-like values for safer opening in Excel.

Set the desired conditions and choose **Save Search** to name and store them in local settings. Select a saved search to apply it again or delete the selected entry. Relative periods such as Last 7 days are recalculated when applied; a specific date keeps the date selected when it was saved.

A local or external drive is recommended. Real-time synchronization folders such as OneDrive may cause conflicts if multiple devices or processes access the SQLite database concurrently.

FindHistory reads shortcuts from the following Windows directory:

```text
%AppData%\Microsoft\Windows\Recent
```

Collected file names, paths, dates, and counts remain only in the selected local database.

### File existence status

The app checks up to 200 records at startup, then up to 200 every 30 seconds. It checks records in order and restarts at the beginning after reaching the end. File deletion, restoration, and folder status changes update the results and existence filter. Open counts and date events are preserved. Large histories take longer to refresh. The displayed status is the last checked value, not a real-time guarantee.

Web addresses, UNC paths, network drives, and targets that are themselves links are skipped. Offline drives and access errors preserve the stored status. Web addresses retain their capture-time status; no availability request is made. Filesystem checks run in the background without holding the database gate.

### Tests and performance

The smoke-test project covers storage, search, migration, database move/switch, settings persistence, and live file watching.

```powershell
dotnet run --project tests\FindHistory.SmokeTests -c Release
```

Run the benchmark suite with:

```powershell
dotnet run --project benchmarks\FindHistory.Benchmarks -c Release
```

Measure search latency during a 100,000-item JSON export with:

```powershell
dotnet run --project benchmarks\FindHistory.Benchmarks -c Release -- --export-contention-only
```

On the current development setup, the optimized implementation measured approximately **4.21 ms** for the newest 1,000 rows in a 100,000-row database, **6.10 ms** for 1,000 `*.pdf` results, and **13.42 ms** for 1,000 events from the last seven days. Results vary by machine. See the [performance report](docs/PERFORMANCE_2026-09-24.md) for the methodology and 1-million-row results.

See the [development roadmap](docs/ROADMAP.md) for the next milestones and their completion gates.

### Technology

- C# / .NET 10
- WPF / MVVM
- SQLite / Microsoft.Data.Sqlite
- SQLite FTS5 trigram search and index-assisted wildcard queries
