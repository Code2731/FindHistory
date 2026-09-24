# FindHistory

Windows의 **최근 항목**을 로컬 SQLite 데이터베이스에 계속 누적하고, 파일명·경로·기간으로 검색하는 WPF 앱입니다.

## 주요 기능

- 앱 시작 시 기존 Windows 최근 항목 가져오기
- 최근 항목 폴더 실시간 감시 (`.lnk`, `.url`)
- 동일 파일의 재등장을 열림 횟수로 누적
- 파일명과 전체 경로를 여러 검색어로 필터링
- 오늘 / 7일 / 30일 / 1년 기간 필터
- 파일 열기와 탐색기에서 위치 열기
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

## 수집 범위

Windows가 `%AppData%\Microsoft\Windows\Recent`에 만드는 바로가기만 수집합니다. 앱 실행 이전에 Windows가 이미 삭제한 오래된 항목은 복구할 수 없지만, FindHistory 실행 이후 감지한 항목은 Windows 최근 목록에서 사라져도 DB에 유지됩니다.
