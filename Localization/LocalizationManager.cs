using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FindHistory.Localization;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["최근에 열었던 파일을 로컬 기록에서 바로 찾으세요"] = "Find files you opened recently in your local history",
            ["진단"] = "Diagnostics",
            ["감시 상태와 로그 정보를 확인합니다"] = "View monitoring status and log information",
            ["데이터 저장소"] = "Storage",
            ["자동 백업 사용"] = "Enable automatic backups",
            ["앱 실행 중 하루에 한 번 백업합니다. 설정 변경은 1분 이내에 적용됩니다."] = "Back up once a day while the app is running. Settings apply within one minute.",
            ["DB별 보관 개수"] = "Backups per database",
            ["자동 백업만 정리합니다. 수동 백업과 복원 전 안전 사본은 유지합니다."] = "Only automatic backups are pruned. Manual backups and pre-restore safety copies are kept.",
            ["데이터베이스 위치를 이동하거나 기존 DB를 선택합니다"] = "Move the database or select an existing one",
            ["파일명이나 경로 검색 (예: *.mp4)"] = "Search file names or paths (for example, *.mp4)",
            ["조회할 날짜를 선택합니다"] = "Choose a date to view",
            ["새로고침"] = "Refresh",
            ["확장자"] = "Extension",
            ["예: mp4, .pdf 또는 *.txt · 단일 확장자를 입력하세요"] = "Example: mp4, .pdf, or *.txt · Enter one extension",
            ["폴더 선택"] = "Choose folder",
            ["선택한 폴더와 하위 폴더의 기록을 검색합니다"] = "Search history in this folder and its subfolders",
            ["필터 초기화"] = "Clear filters",
            ["저장한 검색 조건을 불러옵니다"] = "Load a saved search",
            ["검색 저장"] = "Save search",
            ["선택 삭제"] = "Remove selected",
            ["최근 활동"] = "Recent activity",
            ["날짜를 누르면 해당 기록만 표시합니다"] = "Select a date to show its history",
            ["월"] = "M", ["화"] = "T", ["수"] = "W", ["목"] = "T",
            ["금"] = "F", ["토"] = "S", ["일"] = "S",
            ["적음"] = "Less", ["많음"] = "More",
            ["최근 기록"] = "Recent history",
            ["언어"] = "Language",
            ["생성 시각"] = "Generated",
            ["앱 버전"] = "App version",
            ["운영체제"] = "Operating system",
            ["프로세스"] = "Process",
            ["최근 항목 감시"] = "Recent Items Monitor",
            ["감시 폴더"] = "Monitored folder",
            ["시작 시각"] = "Started",
            ["마지막 전체 스캔"] = "Last full scan",
            ["마지막 스캔 항목"] = "Items in last scan",
            ["마지막 스캔 시간"] = "Last scan duration",
            ["마지막 실시간 기록"] = "Last live capture",
            ["세션 실시간 기록"] = "Live captures this session",
            ["감시 복구 횟수"] = "Watcher recoveries",
            ["진행 중 작업"] = "Pending tasks",
            ["마지막 오류"] = "Last error",
            ["데이터베이스"] = "Database",
            ["경로"] = "Path",
            ["파일 크기"] = "File size",
            ["고유 항목"] = "Unique items",
            ["누적 열기 횟수"] = "Total opens",
            ["저장된 날짜 이벤트"] = "Stored date events",
            ["추정 날짜 이벤트"] = "Estimated date events",
            ["설정"] = "Settings",
            ["로그인 시 자동 실행"] = "Run at sign-in",
            ["로그 폴더"] = "Log folder",
            ["사용"] = "Enabled",
            ["사용 안 함"] = "Disabled",
            ["없음"] = "None",
            ["기록 없음"] = "Not recorded",
            ["확인 불가"] = "Unavailable",
            ["시작 전"] = "Not started",
            ["종료 중"] = "Stopping",
            ["정상 감시 중"] = "Monitoring normally",
            ["감시기 비활성"] = "Watcher inactive",
            ["파일"] = "File", ["종류"] = "Type", ["마지막 기록"] = "Last opened",
            ["횟수"] = "Opens", ["상태"] = "Status",
            ["검색 결과가 없습니다"] = "No results found",
            ["다른 파일명이나 경로로 검색해 보세요"] = "Try another file name or path",
            ["위치 열기"] = "Open location", ["파일 열기"] = "Open file",
            ["전체 상태"] = "Any status", ["존재함 (저장된 상태)"] = "Available (last checked)",
            ["찾을 수 없음"] = "Not found", ["존재함"] = "Available",
            ["전체 기간"] = "All time", ["오늘"] = "Today", ["최근 7일"] = "Last 7 days",
            ["최근 30일"] = "Last 30 days", ["최근 1년"] = "Last year", ["날짜 지정"] = "Choose date",
            ["기록 준비 중"] = "Preparing history", ["최근 활동 준비 중"] = "Preparing recent activity",
            ["진단 정보"] = "Diagnostics",
            ["최근 항목 감시 상태, 데이터베이스 통계와 마지막 오류를 확인합니다."] = "View recent-item monitoring, database statistics, and the last error.",
            ["정보 복사"] = "Copy report", ["로그 폴더 열기"] = "Open log folder", ["닫기"] = "Close",
            ["기록을 보관할 위치를 바꾸거나 이전 DB를 다시 연결합니다."] = "Change the history location or reconnect an existing database.",
            ["현재 데이터베이스"] = "Current database", ["폴더 열기"] = "Open folder",
            ["현재 DB 이동"] = "Move current database",
            ["기존 기록을 유지한 채 새 폴더로 파일을 옮깁니다."] = "Move the database to a new folder and keep existing history.",
            ["새 위치 선택"] = "Choose new location", ["기존 DB 사용"] = "Use existing database",
            ["다른 위치에 있는 FindHistory DB로 전환합니다."] = "Switch to a FindHistory database in another location.",
            ["DB 파일 선택"] = "Choose database file",
            ["로컬 또는 외장 드라이브를 권장합니다. 실시간 동기화 폴더는 SQLite 파일 충돌을 만들 수 있습니다."] = "Use a local or external drive. Live-sync folders can cause SQLite file conflicts.",
            ["백업 및 복원"] = "Backup and restore",
            ["복원 전 현재 DB도 안전 사본으로 보관합니다."] = "A safety copy of the current database is made before restore.",
            ["백업 저장"] = "Save backup", ["백업 복원"] = "Restore backup",
            ["기록 내보내기"] = "Export history",
            ["전체 기록과 개별 열기 이력을 파일로 저장합니다."] = "Save all history and individual open events to a file.",
            ["현재 표시된 검색 결과만 내보냅니다. 선택된 항목의 전체 열기 이력도 포함합니다."] = "Export only the search results currently shown. Include the full open history for each item.",
            ["CSV 내보내기"] = "Export CSV", ["JSON 내보내기"] = "Export JSON",
            ["현재 결과 CSV"] = "Current results CSV", ["현재 결과 JSON"] = "Current results JSON",
            ["현재 표시된 검색 결과가 없습니다."] = "There are no search results to export.",
            ["현재 검색 결과를 내보내는 중…"] = "Exporting current search results…",
            ["현재 표시된 검색 결과를 CSV로 내보내기"] = "Export current search results as CSV",
            ["현재 표시된 검색 결과를 JSON으로 내보내기"] = "Export current search results as JSON",
            ["현재 표시된 검색 결과를 내보냈습니다.\n\n{0}"] = "Current search results exported.\n\n{0}",
            ["현재 결과 CSV 파일 (*.csv)|*.csv"] = "Current results CSV files (*.csv)|*.csv",
            ["현재 결과 JSON 파일 (*.json)|*.json"] = "Current results JSON files (*.json)|*.json",
            ["Windows 시작 시 백그라운드 실행"] = "Run in the background when Windows starts",
            ["현재 검색 조건에 이름을 붙여 저장합니다. 이름은 최대 64자이며 중복할 수 없습니다."] = "Name and save the current search. Names can have up to 64 characters and must be unique.",
            ["이름을 입력하세요."] = "Enter a name.",
            ["같은 이름의 검색이 이미 있습니다."] = "A search with this name already exists.",
            ["취소"] = "Cancel", ["저장"] = "Save",
            ["FindHistory 열기"] = "Open FindHistory", ["종료"] = "Exit",
            ["최근 항목 기록 중"] = "Recording recent items",
            ["확인"] = "Confirm", ["이동 완료"] = "Move complete",
            ["DB 열기 실패"] = "Could not open database", ["복사 실패"] = "Copy failed",
            ["데이터베이스가 이미 있음"] = "Database already exists",
            ["데이터베이스 이동"] = "Move database",
            ["DB 이동 실패"] = "Database move failed",
            ["백업 완료"] = "Backup complete", ["복원 완료"] = "Restore complete",
            ["내보내기 완료"] = "Export complete", ["데이터베이스 복원"] = "Restore database",
            ["기존 기록을 새 위치로 이동했습니다."] = "History moved to the new location.",
            ["선택한 파일을 FindHistory DB로 열 수 없습니다."] = "The selected file is not a valid FindHistory database.",
            ["사용할 FindHistory 데이터베이스 선택"] = "Choose a FindHistory database",
            ["FindHistory 데이터베이스를 옮길 폴더를 선택하세요."] = "Choose a folder for the FindHistory database.",
            ["FindHistory 데이터베이스 백업 저장"] = "Save a FindHistory database backup",
            ["복원할 FindHistory 백업 선택"] = "Choose a FindHistory backup to restore",
            ["전체 기록을 CSV로 내보내기"] = "Export all history to CSV",
            ["전체 기록을 JSON으로 내보내기"] = "Export all history to JSON",
            ["FindHistory 백업 (*.fhbackup)|*.fhbackup|SQLite 데이터베이스 (*.db)|*.db"] = "FindHistory backup (*.fhbackup)|*.fhbackup|SQLite database (*.db)|*.db",
            ["FindHistory 및 SQLite 백업 (*.fhbackup;*.db)|*.fhbackup;*.db|모든 파일 (*.*)|*.*"] = "FindHistory and SQLite backups (*.fhbackup;*.db)|*.fhbackup;*.db|All files (*.*)|*.*",
            ["CSV 파일 (*.csv)|*.csv"] = "CSV files (*.csv)|*.csv",
            ["JSON 파일 (*.json)|*.json"] = "JSON files (*.json)|*.json",
            ["선택한 백업의 기록으로 현재 데이터베이스를 교체합니다.\n현재 DB는 복원 전 안전 사본으로 자동 보관됩니다.\n\n계속할까요?"] = "Replace the current database with the selected backup.\nA safety copy of the current database will be kept.\n\nContinue?",
            ["SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*"] = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            ["모든 파일 (*.*)|*.*"] = "All files (*.*)|*.*",
            ["백업 실패"] = "Backup failed", ["복원 실패"] = "Restore failed",
            ["내보내기 실패"] = "Export failed", ["이동 실패"] = "Move failed",
            ["FindHistory를 시작하지 못했습니다."] = "FindHistory could not start.",
            ["이 폴더와 하위 폴더의 기록을 검색합니다"] = "Search history in this folder and its subfolders",
            ["설정 파일이 비어 있거나 유효하지 않습니다."] = "The settings file is empty or invalid.",
            ["언어를 변경했습니다."] = "Language changed.",
            ["크기 확인 불가"] = "Size unavailable",
            ["기록 종료 중"] = "Stopping",
            ["백그라운드 기록 중"] = "Recording in background",
            ["감시 복구 필요"] = "Monitoring needs attention",
            ["최근 항목을 불러오는 중…"] = "Loading recent items…",
            ["최근 항목 폴더를 실시간으로 감시하고 있습니다."] = "Monitoring the Recent Items folder.",
            ["데이터베이스를 새 위치로 이동하는 중…"] = "Moving the database…",
            ["데이터베이스와 기존 기록을 새 위치로 이동했습니다."] = "The database and history moved to the new location.",
            ["데이터베이스 이동은 완료했지만 이전 위치의 파일을 정리하지 못했습니다. 이전 DB 사본은 보존되어 있습니다."] = "The database moved, but the old files could not be removed. The old copy is preserved.",
            ["데이터베이스를 백업하는 중…"] = "Backing up the database…",
            ["백업을 확인하고 복원하는 중…"] = "Validating and restoring the backup…",
            ["선택한 데이터베이스를 확인하는 중…"] = "Validating the selected database…",
            ["선택한 데이터베이스를 사용합니다."] = "Using the selected database.",
            ["최근 항목을 다시 확인하는 중…"] = "Refreshing recent items…",
            ["파일 위치를 찾을 수 없습니다."] = "File location not found.",
            ["저장 검색은 최대 30개까지 보관할 수 있습니다."] = "You can save up to 30 searches.",
            ["활동"] = "active days",
            ["Windows 시작 시 백그라운드 실행이 켜졌습니다."] = "Run at Windows startup is enabled.",
            ["Windows 시작 시 실행이 꺼졌습니다."] = "Run at Windows startup is disabled.",
            ["일부 추정"] = "partly estimated",
            ["폴더"] = "Folder",
            ["바로 가기"] = "Shortcut",
            ["웹"] = "Web",
            ["링크"] = "Link"
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishPrefixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["검색 저장 실패: "] = "Could not save search: ",
            ["검색 결과 내보내기 실패: "] = "Could not export search results: ",
            ["저장 검색 삭제 실패: "] = "Could not remove saved search: ",
            ["시작 프로그램 설정 실패: "] = "Could not update startup setting: ",
            ["DB를 새 위치로 옮겼지만 화면 갱신에 실패했습니다: "] = "The database moved, but the view could not refresh: ",
            ["데이터베이스 이동 실패: "] = "Database move failed: ",
            ["현재 기록을 다음 위치로 이동합니다.\n\n"] = "Move the current history to this location:\n\n",
            ["\n\n계속할까요?"] = "\n\nContinue?",
            ["선택한 폴더에 findhistory.db가 이미 있습니다.\n'기존 DB 사용'으로 해당 파일을 선택해 주세요."] = "findhistory.db already exists in this folder.\nChoose it with 'Use existing database'.",
            ["백업을 저장했습니다: "] = "Backup saved: ",
            ["백업에 실패했습니다: "] = "Backup failed: ",
            ["복원에 실패했습니다: "] = "Restore failed: ",
            ["전체 기록을 내보냈습니다: "] = "History exported: ",
            ["백업을 복원했습니다. 복원 전 DB 사본: "] = "Backup restored. Previous database copy: ",
            ["내보내기에 실패했습니다: "] = "Export failed: ",
            ["데이터베이스를 열 수 없습니다: "] = "Could not open database: ",
            ["데이터베이스 이동 중 오류가 발생했습니다.\n\n"] = "An error occurred while moving the database.\n\n",
            ["데이터베이스 전환 중 오류가 발생했습니다.\n\n"] = "An error occurred while switching databases.\n\n",
            ["진단 정보를 불러오지 못했습니다.\n\n"] = "Could not load diagnostics.\n\n",
            ["진단 정보를 클립보드에 복사하지 못했습니다.\n\n"] = "Could not copy diagnostics to the clipboard.\n\n",
            ["FindHistory를 시작하지 못했습니다.\n\n"] = "FindHistory could not start.\n\n",
            ["데이터 폴더를 열 수 없습니다: "] = "Could not open data folder: ",
            ["로그 폴더를 열 수 없습니다: "] = "Could not open log folder: ",
            ["새로고침 실패: "] = "Refresh failed: ",
            ["검색 실패: "] = "Search failed: ",
            ["감시 오류: "] = "Monitoring error: ",
            ["열 수 없습니다: "] = "Could not open: ",
            ["위치를 열 수 없습니다: "] = "Could not open location: ",
            ["언어 설정 저장 실패: "] = "Could not save language setting: ",
            ["확장자: "] = "Extension: ", ["상태: "] = "Status: ",
            ["폴더: "] = "Folder: ", ["기간: "] = "Period: "
        };

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = "ko";

    public string this[string koreanText] => Translate(koreanText);

    public void SetLanguage(string? language)
    {
        var normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";
        if (Language == normalized)
        {
            return;
        }

        Language = normalized;
        var culture = CultureInfo.GetCultureInfo(normalized == "en" ? "en-US" : "ko-KR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        OnPropertyChanged(nameof(Language));
    }

    public string Translate(string koreanText)
    {
        if (Language != "en") return koreanText;
        if (English.TryGetValue(koreanText, out var translation)) return translation;
        const string savedSuffix = "' 검색을 저장했습니다.";
        if (koreanText.StartsWith('\'') && koreanText.EndsWith(savedSuffix, StringComparison.Ordinal))
        {
            return $"Saved search '{koreanText[1..^savedSuffix.Length]}'.";
        }
        const string removedSuffix = "' 저장 검색을 삭제했습니다.";
        if (koreanText.StartsWith('\'') && koreanText.EndsWith(removedSuffix, StringComparison.Ordinal))
        {
            return $"Removed saved search '{koreanText[1..^removedSuffix.Length]}'.";
        }
        const string openedSuffix = "에 열었던 파일을 표시합니다.";
        if (koreanText.EndsWith(openedSuffix, StringComparison.Ordinal))
        {
            return $"Showing files opened on {koreanText[..^openedSuffix.Length]}.";
        }
        const string openItemSuffix = " 열기";
        if (koreanText.EndsWith(openItemSuffix, StringComparison.Ordinal))
        {
            return $"Open {koreanText[..^openItemSuffix.Length]}";
        }
        const string refreshedPrefix = "최근 항목 ";
        const string refreshedSuffix = "개를 확인했습니다.";
        if (koreanText.StartsWith(refreshedPrefix, StringComparison.Ordinal) &&
            koreanText.EndsWith(refreshedSuffix, StringComparison.Ordinal))
        {
            var countText = koreanText[refreshedPrefix.Length..^refreshedSuffix.Length];
            if (long.TryParse(countText, NumberStyles.Number, CultureInfo.CurrentCulture, out var count))
            {
                return $"Refreshed {count:N0} recent items.";
            }
        }
        const string exportProgressPrefix = "전체 기록을 ";
        const string exportProgressSuffix = " 파일로 내보내는 중…";
        if (koreanText.StartsWith(exportProgressPrefix, StringComparison.Ordinal) &&
            koreanText.EndsWith(exportProgressSuffix, StringComparison.Ordinal))
        {
            var format = koreanText[exportProgressPrefix.Length..^exportProgressSuffix.Length];
            return $"Exporting all history to {format}…";
        }
        foreach (var prefix in EnglishPrefixes)
        {
            if (koreanText.StartsWith(prefix.Key, StringComparison.Ordinal))
            {
                return prefix.Value + koreanText[prefix.Key.Length..];
            }
        }

        return koreanText;
    }

    public string Format(string koreanFormat, string englishFormat, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Language == "en" ? englishFormat : koreanFormat, arguments);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
