# FindHistory

Windows의 **최근 항목**을 로컬 SQLite 데이터베이스에 계속 누적하고, 파일명·경로·기간으로 검색하는 WPF 앱입니다.

## 주요 기능

- 앱 시작 시 기존 Windows 최근 항목 가져오기
- 최근 항목 폴더 실시간 감시 (`.lnk`, `.url`)
- 동일 파일의 재등장을 열림 횟수로 누적
- 파일명과 전체 경로를 여러 검색어로 필터링
- 오늘 / 7일 / 30일 / 1년 기간 필터
- `Ctrl+K`로 검색창에 즉시 포커스
- 파일 열기와 탐색기에서 위치 열기
- 현재 기록을 유지한 데이터베이스 위치 이동
- 다른 위치의 기존 FindHistory 데이터베이스 선택
- 창을 닫아도 시스템 트레이에서 기록 지속
- 선택적인 Windows 로그인 시 백그라운드 실행
- 모든 데이터는 로컬 SQLite DB에만 저장

## 실행

```powershell
dotnet run
```

배포용 단일 폴더를 만들려면:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

`publish/FindHistory.exe`를 실행하면 됩니다. 종료하려면 시스템 트레이의 FindHistory 아이콘을 우클릭하고 **종료**를 선택하세요.

## 데이터 위치

```text
%LocalAppData%\FindHistory\findhistory.db
```

DB는 앱과 별도로 유지되므로 앱을 업데이트해도 기록은 남습니다.

메인 화면의 **데이터 저장소**에서 다른 폴더로 DB를 옮기거나 기존 `findhistory.db`를 선택할 수 있습니다. 선택한 위치는 아래 설정에 저장됩니다.

```text
%LocalAppData%\FindHistory\settings.json
```

SQLite DB는 로컬 또는 외장 드라이브에 두는 것을 권장합니다. OneDrive 같은 실시간 동기화 폴더는 여러 장치가 동시에 접근할 때 충돌할 수 있습니다.

## 검증

DB 저장·검색·이동·기존 DB 전환·설정 유지는 다음 스모크 테스트로 확인할 수 있습니다.

```powershell
dotnet run --project tests\FindHistory.SmokeTests -c Release
```

10만~100만 건 성능 측정과 최적화 결과는 [성능 보고서](docs/PERFORMANCE_2026-09-24.md)에 정리되어 있습니다.

## 수집 범위

Windows가 `%AppData%\Microsoft\Windows\Recent`에 만드는 바로가기만 수집합니다. 앱 실행 이전에 Windows가 이미 삭제한 오래된 항목은 복구할 수 없지만, FindHistory 실행 이후 감지한 항목은 Windows 최근 목록에서 사라져도 DB에 유지됩니다.
