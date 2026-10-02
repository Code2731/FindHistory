using FindHistory.Models;
using FindHistory.Services;

var testRoot = Path.Combine(Path.GetTempPath(), $"FindHistory-Smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    var logDirectory = Path.Combine(testRoot, "logs");
    Directory.CreateDirectory(logDirectory);
    var expiredLog = Path.Combine(logDirectory, "findhistory-2000-01-01.log");
    await File.WriteAllTextAsync(expiredLog, "expired");
    File.SetLastWriteTimeUtc(expiredLog, DateTime.UtcNow.AddDays(-30));
    var logger = new AppLogService(logDirectory, retentionDays: 7, maximumFileBytes: 256);
    for (var index = 0; index < 8; index++)
    {
        logger.Information($"rotation-test-{index}: {new string('x', 120)}");
    }
    var currentLogs = Directory.GetFiles(logDirectory, "findhistory-*.log");
    Assert(!File.Exists(expiredLog), "보관 기간이 지난 로그 파일을 정리하지 못했습니다.");
    Assert(currentLogs.Length >= 2, "로그 파일 크기 제한에 따른 회전이 동작하지 않습니다.");
    Assert(currentLogs.Any(path => File.ReadAllText(path).Contains("rotation-test-7")),
        "회전된 로그에 최신 메시지가 기록되지 않았습니다.");

    var originalPath = Path.Combine(testRoot, "original", "findhistory.db");
    using var database = new RecentDatabase(originalPath);
    await database.InitializeAsync();
    await database.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "sample.txt"),
        "sample.txt",
        "TXT",
        "파일",
        Path.Combine(testRoot, "sample.lnk"),
        DateTimeOffset.UtcNow,
        false));

    var originalStats = await database.GetStatsAsync();
    Assert(originalStats.UniqueItems == 1, "첫 항목이 저장되지 않았습니다.");
    Assert((await database.SearchAsync("mple", null)).Count == 1,
        "FTS 부분 문자열 검색이 동작하지 않습니다.");
    Assert((await database.SearchAsync("sa", null)).Count == 1,
        "3자 미만 LIKE 검색이 동작하지 않습니다.");
    Assert((await database.SearchAsync("sample TXT", null)).Count == 1,
        "여러 검색어 AND 검색이 동작하지 않습니다.");

    await database.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "wildcard_aXb.txt"),
        "wildcard_aXb.txt",
        "TXT",
        "파일",
        Path.Combine(testRoot, "wildcard.lnk"),
        DateTimeOffset.UtcNow,
        false));
    Assert((await database.SearchAsync("_b", null)).Count == 0,
        "LIKE 와일드카드 문자가 이스케이프되지 않았습니다.");

    await database.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "videos", "clip_2026.mp4"),
        "clip_2026.mp4",
        "MP4",
        "파일",
        Path.Combine(testRoot, "video.lnk"),
        DateTimeOffset.UtcNow,
        false));
    Assert((await database.SearchAsync("*.mp4", null)).Count == 1,
        "별표 확장자 와일드카드 검색이 동작하지 않습니다.");
    Assert((await database.SearchAsync("*.MP4", null)).Count == 1,
        "확장자 와일드카드 검색이 대소문자를 구분하고 있습니다.");
    Assert((await database.SearchAsync("clip_20??.mp4", null)).Count == 1,
        "물음표 한 글자 와일드카드 검색이 동작하지 않습니다.");
    Assert((await database.SearchAsync("*.avi", null)).Count == 0,
        "와일드카드 확장자 검색이 다른 확장자를 반환했습니다.");

    Assert((await database.SearchAsync(string.Empty, DateTimeOffset.UtcNow.AddDays(1))).Count == 0,
        "미래 기간 필터가 결과를 반환했습니다.");

    var snapshot = await database.SearchWithStatsAsync("sample", null);
    Assert(snapshot.Items.Count == 1 && snapshot.Stats.UniqueItems == 3,
        "검색 결과와 통계를 한 번에 읽지 못했습니다.");

    var batch = Enumerable.Range(0, 25)
        .Select(index => new RecentItemCandidate(
            Path.Combine(testRoot, "batch", $"batch_{index:D3}.dat"),
            $"batch_{index:D3}.dat",
            "DAT",
            "파일",
            Path.Combine(testRoot, "batch-links", $"batch_{index:D3}.lnk"),
            DateTimeOffset.UtcNow.AddMilliseconds(index),
            false))
        .ToArray();
    await database.UpsertManyAsync(batch);
    Assert((await database.SearchAsync("batch_012", null)).Count == 1,
        "배치 저장 항목을 FTS에서 찾지 못했습니다.");

    var stableTarget = Path.Combine(testRoot, "stable-target.bin");
    await database.UpsertAsync(new RecentItemCandidate(stableTarget, "initial_label.bin", "BIN", "파일",
        Path.Combine(testRoot, "stable.lnk"), DateTimeOffset.UtcNow, false));
    await database.UpsertAsync(new RecentItemCandidate(stableTarget, "updated_label.bin", "BIN", "파일",
        Path.Combine(testRoot, "stable.lnk"), DateTimeOffset.UtcNow.AddSeconds(1), false));
    Assert((await database.SearchAsync("updated_label", null)).Count == 1,
        "수정된 항목이 FTS 인덱스에 반영되지 않았습니다.");
    var updatedStats = await database.GetStatsAsync();
    Assert(updatedStats.UniqueItems == 29 && updatedStats.TotalOpenCount == 30,
        "누적 통계 트리거가 추가/갱신 횟수를 정확히 반영하지 못했습니다.");

    var eventTarget = Path.Combine(testRoot, "timeline", "daily-notes.md");
    var firstDay = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
    var secondDayMorning = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    var secondDayEvening = new DateTimeOffset(2026, 9, 21, 18, 30, 0, TimeSpan.Zero);
    foreach (var openedAt in new[] { firstDay, secondDayMorning, secondDayEvening, secondDayEvening })
    {
        await database.UpsertAsync(new RecentItemCandidate(
            eventTarget,
            "daily-notes.md",
            "MD",
            "파일",
            Path.Combine(testRoot, "daily-notes.lnk"),
            openedAt,
            false));
    }

    var secondDayRange = new HistoryDateRange(
        new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero),
        "2026-09-21");
    var secondDaySnapshot = await database.SearchWithStatsAsync("daily-notes", secondDayRange);
    Assert(secondDaySnapshot.Items.Count == 1,
        "특정 날짜에 연 파일을 찾지 못했습니다.");
    Assert(secondDaySnapshot.Items[0].OpenCount == 2,
        "같은 날짜의 중복 감시 이벤트를 제거하거나 열기 횟수를 집계하지 못했습니다.");
    Assert(secondDaySnapshot.Items[0].LastSeen == secondDayEvening,
        "특정 날짜 안의 마지막 열기 시각이 정확하지 않습니다.");
    Assert(!secondDaySnapshot.Items[0].IsEstimatedHistory,
        "새로 수집한 날짜 기록이 추정 기록으로 표시되었습니다.");

    var activityRange = new HistoryDateRange(
        new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
        "활동 집계 테스트");
    var dailyActivity = await database.GetDailyActivityAsync(activityRange, TimeZoneInfo.Utc);
    Assert(dailyActivity.Count == 2,
        "열기 이벤트가 있는 날짜만 활동 집계에 포함되지 않았습니다.");
    Assert(dailyActivity.Single(day => day.Date == new DateTime(2026, 9, 20)).OpenCount == 1,
        "첫 번째 날짜의 활동 횟수가 정확하지 않습니다.");
    Assert(dailyActivity.Single(day => day.Date == new DateTime(2026, 9, 21)).OpenCount == 2,
        "중복 이벤트 제거 후 두 번째 날짜의 활동 횟수가 정확하지 않습니다.");
    Assert(dailyActivity.All(day => !day.ContainsEstimated),
        "새로 수집한 활동이 추정 기록으로 잘못 표시되었습니다.");

    var emptyDayRange = new HistoryDateRange(
        new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
        "2026-09-22");
    Assert((await database.SearchWithStatsAsync("daily-notes", emptyDayRange)).Items.Count == 0,
        "열지 않은 날짜에 파일이 반환되었습니다.");

    var boundaryTarget = Path.Combine(testRoot, "timeline", "boundary.txt");
    var boundaryStart = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
    var boundaryEnd = boundaryStart.AddDays(1);
    foreach (var openedAt in new[]
             {
                 boundaryStart,
                 boundaryStart,
                 boundaryEnd.AddTicks(-1),
                 boundaryEnd
             })
    {
        await database.UpsertAsync(new RecentItemCandidate(
            boundaryTarget,
            "boundary.txt",
            "TXT",
            "파일",
            Path.Combine(testRoot, "boundary.lnk"),
            openedAt,
            false));
    }

    var boundaryRange = new HistoryDateRange(boundaryStart, boundaryEnd, "경계 테스트");
    var boundarySnapshot = await database.SearchWithStatsAsync("boundary", boundaryRange);
    Assert(boundarySnapshot.Items.Count == 1 && boundarySnapshot.Items[0].OpenCount == 2,
        "날짜 범위가 시작 시각 포함·종료 시각 제외 또는 중복 이벤트 제거 규칙을 지키지 않았습니다.");
    Assert(boundarySnapshot.Items[0].LastSeen == boundaryEnd.AddTicks(-1),
        "날짜 범위 종료 직전 이벤트를 찾지 못했거나 종료 경계 이벤트를 포함했습니다.");

    var filtered = await database.SearchWithStatsAsync("boundary", boundaryRange,
        filters: new HistoryFilters("*.txt", false, Path.Combine(testRoot, "timeline")));
    Assert(filtered.Items.Count == 1 && filtered.Items[0].OpenCount == 2,
        "검색어·날짜·확장자·존재 여부·폴더 AND 필터가 동작하지 않습니다.");
    Assert((await database.SearchWithStatsAsync("boundary", boundaryRange,
        filters: new HistoryFilters("TXT", true))).Items.Count == 0,
        "존재 여부 필터가 다른 상태의 기록을 반환했습니다.");
    Assert((await database.SearchWithStatsAsync("boundary", boundaryRange,
        filters: new HistoryFilters("PDF"))).Items.Count == 0,
        "확장자 필터가 다른 확장자를 반환했습니다.");
    Assert((await database.SearchWithStatsAsync("boundary", boundaryRange,
        filters: new HistoryFilters(Folder: Path.Combine(testRoot, "time")))).Items.Count == 0,
        "폴더 필터가 이름의 일부만 같은 인접 폴더를 반환했습니다.");
    var literalFolder = Path.Combine(testRoot, "work_%");
    await database.UpsertAsync(new RecentItemCandidate(Path.Combine(literalFolder, "nested", "literal.txt"),
        "literal.txt", "TXT", "파일", Path.Combine(testRoot, "literal.lnk"), boundaryStart, true));
    await database.UpsertAsync(new RecentItemCandidate(Path.Combine(testRoot, "work_X", "literal.txt"),
        "literal.txt", "TXT", "파일", Path.Combine(testRoot, "other-literal.lnk"), boundaryStart, true));
    Assert((await database.SearchWithStatsAsync("literal", boundaryRange,
        filters: new HistoryFilters(".txt", true, literalFolder))).Items.Count == 1,
        "폴더의 LIKE 특수문자를 리터럴로 처리하거나 하위 폴더를 포함하지 못했습니다.");

    var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    var springDstRange = HistoryDateRangeFactory.CreateLocalCalendarRange(
        new DateTime(2026, 3, 8), new DateTime(2026, 3, 9), "DST 시작", pacific);
    var fallDstRange = HistoryDateRangeFactory.CreateLocalCalendarRange(
        new DateTime(2026, 11, 1), new DateTime(2026, 11, 2), "DST 종료", pacific);
    Assert(springDstRange.EndUtc - springDstRange.StartUtc == TimeSpan.FromHours(23),
        "DST 시작일의 로컬 날짜 범위를 23시간으로 변환하지 못했습니다.");
    Assert(fallDstRange.EndUtc - fallDstRange.StartUtc == TimeSpan.FromHours(25),
        "DST 종료일의 로컬 날짜 범위를 25시간으로 변환하지 못했습니다.");

    var databaseDiagnostics = await database.GetDiagnosticsAsync();
    Assert(databaseDiagnostics.UniqueItems > 0 && databaseDiagnostics.StoredEvents > 0,
        "데이터베이스 진단 통계를 읽지 못했습니다.");
    Assert(databaseDiagnostics.EstimatedEvents == 0,
        "새 데이터베이스 이벤트가 추정 기록으로 집계되었습니다.");

    var movedPath = Path.Combine(testRoot, "moved", "findhistory.db");
    await database.MoveToAsync(movedPath);
    Assert(File.Exists(movedPath), "DB 파일이 새 위치로 이동되지 않았습니다.");
    Assert(!File.Exists(originalPath), "이전 위치에 DB 파일이 남아 있습니다.");

    var movedResults = await database.SearchAsync("sample", null);
    Assert(movedResults.Count == 1, "이동한 DB에서 기존 기록을 읽지 못했습니다.");

    var otherPath = Path.Combine(testRoot, "other", "findhistory.db");
    using var otherDatabase = new RecentDatabase(otherPath);
    await otherDatabase.InitializeAsync();
    await otherDatabase.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "other.pdf"),
        "other.pdf",
        "PDF",
        "파일",
        Path.Combine(testRoot, "other.lnk"),
        DateTimeOffset.UtcNow,
        false));

    await database.UseAsync(otherPath);
    var switchedResults = await database.SearchAsync("other", null);
    Assert(switchedResults.Count == 1, "기존 DB로 전환하지 못했습니다.");

    var invalidPath = Path.Combine(testRoot, "invalid.db");
    await File.WriteAllTextAsync(invalidPath, "not a sqlite database");
    await AssertThrowsAsync(() => database.UseAsync(invalidPath),
        "잘못된 DB 선택이 실패하지 않았습니다.");
    Assert(string.Equals(database.DatabasePath, otherPath, StringComparison.OrdinalIgnoreCase),
        "잘못된 DB 선택 뒤 현재 DB 경로가 바뀌었습니다.");
    Assert((await database.SearchAsync("other", null)).Count == 1,
        "잘못된 DB 선택 뒤 기존 DB 연결이 손상되었습니다.");

    var malformedSchemaPath = Path.Combine(testRoot, "malformed-schema.db");
    await using (var malformedConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                     $"Data Source={malformedSchemaPath};Pooling=False"))
    {
        await malformedConnection.OpenAsync();
        await using var malformedCommand = malformedConnection.CreateCommand();
        malformedCommand.CommandText = "CREATE TABLE recent_items (id INTEGER PRIMARY KEY);";
        await malformedCommand.ExecuteNonQueryAsync();
    }
    await AssertThrowsAsync(() => database.UseAsync(malformedSchemaPath),
        "필수 컬럼이 빠진 FindHistory 유사 DB를 허용했습니다.");
    Assert(string.Equals(database.DatabasePath, otherPath, StringComparison.OrdinalIgnoreCase) &&
           (await database.SearchAsync("other", null)).Count == 1,
        "스키마가 잘못된 DB 거부 후 기존 DB 연결이 유지되지 않았습니다.");

    var futureSchemaPath = Path.Combine(testRoot, "future-schema.db");
    await using (var futureConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                     $"Data Source={futureSchemaPath};Pooling=False"))
    {
        await futureConnection.OpenAsync();
        await using var futureCommand = futureConnection.CreateCommand();
        futureCommand.CommandText = "PRAGMA user_version = 99;";
        await futureCommand.ExecuteNonQueryAsync();
    }
    using (var futureDatabase = new RecentDatabase(futureSchemaPath))
    {
        await AssertThrowsAsync(() => futureDatabase.InitializeAsync(),
            "지원하지 않는 미래 DB 버전을 초기화해 버렸습니다.");
    }
    await using (var futureCheckConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                     $"Data Source={futureSchemaPath};Pooling=False"))
    {
        await futureCheckConnection.OpenAsync();
        await using var futureCheckCommand = futureCheckConnection.CreateCommand();
        futureCheckCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recent_items';";
        Assert(Convert.ToInt32(await futureCheckCommand.ExecuteScalarAsync()) == 0,
            "미래 버전을 거부하기 전에 DB 스키마를 수정했습니다.");
    }

    var legacyPath = Path.Combine(testRoot, "legacy", "findhistory.db");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    var legacyOpenedAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    await using (var legacyConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                     $"Data Source={legacyPath};Pooling=False"))
    {
        await legacyConnection.OpenAsync();
        await using var legacyCommand = legacyConnection.CreateCommand();
        legacyCommand.CommandText = """
            CREATE TABLE recent_items (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                target_path         TEXT NOT NULL COLLATE NOCASE UNIQUE,
                display_name        TEXT NOT NULL,
                extension           TEXT NOT NULL,
                item_kind           TEXT NOT NULL,
                source_link_path    TEXT NOT NULL,
                first_seen_utc      TEXT NOT NULL,
                last_seen_utc       TEXT NOT NULL,
                last_link_write_utc TEXT NOT NULL,
                open_count          INTEGER NOT NULL DEFAULT 1,
                exists_flag         INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO recent_items (
                target_path, display_name, extension, item_kind, source_link_path,
                first_seen_utc, last_seen_utc, last_link_write_utc, open_count, exists_flag)
            VALUES ($path, 'legacy.txt', 'TXT', '파일', $link, $opened, $opened, $opened, 4, 0);
            """;
        legacyCommand.Parameters.AddWithValue("$path", Path.Combine(testRoot, "legacy.txt"));
        legacyCommand.Parameters.AddWithValue("$link", Path.Combine(testRoot, "legacy.lnk"));
        legacyCommand.Parameters.AddWithValue("$opened", legacyOpenedAt.UtcDateTime.ToString("O"));
        await legacyCommand.ExecuteNonQueryAsync();
    }

    using (var legacyDatabase = new RecentDatabase(legacyPath))
    {
        await legacyDatabase.InitializeAsync();
        await legacyDatabase.InitializeAsync();
        var legacyRange = new HistoryDateRange(
            legacyOpenedAt.AddHours(-1), legacyOpenedAt.AddHours(1), "기존 기록");
        var legacySnapshot = await legacyDatabase.SearchWithStatsAsync("legacy", legacyRange);
        Assert(legacySnapshot.Items.Count == 1 && legacySnapshot.Items[0].OpenCount == 1,
            "기존 DB의 마지막 기록을 날짜 이벤트로 이관하지 못했거나 중복 이관했습니다.");
        Assert(legacySnapshot.Items[0].IsEstimatedHistory,
            "기존 DB에서 복원한 날짜가 추정 기록으로 표시되지 않았습니다.");
        var legacyDiagnostics = await legacyDatabase.GetDiagnosticsAsync();
        Assert(legacyDiagnostics.EstimatedEvents == 1,
            "기존 DB에서 이관한 추정 이벤트가 진단 통계에 반영되지 않았습니다.");
        var legacyActivity = await legacyDatabase.GetDailyActivityAsync(
            HistoryDateRangeFactory.CreateLocalCalendarRange(
                new DateTime(2026, 8, 15), new DateTime(2026, 8, 16), "기존 활동",
                TimeZoneInfo.Utc),
            TimeZoneInfo.Utc);
        Assert(legacyActivity.Count == 1 && legacyActivity[0].ContainsEstimated,
            "이관된 추정 이벤트가 날짜별 활동 집계에 표시되지 않았습니다.");
        Assert((await legacyDatabase.GetStatsAsync()).TotalOpenCount == 4,
            "이관 시 저장된 전체 열기 횟수가 보존되지 않았습니다.");
        Assert((await legacyDatabase.GetDiagnosticsAsync()).EstimatedEvents == 1,
            "DB를 다시 초기화할 때 추정 이벤트가 중복 생성되었습니다.");
        await using var schemaConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={legacyPath};Pooling=False");
        await schemaConnection.OpenAsync();
        await using var schemaCommand = schemaConnection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await schemaCommand.ExecuteScalarAsync()) == 1,
            "완료된 스키마 이관 버전이 기록되지 않았습니다.");
        schemaCommand.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='recent_items_fts_au';";
        await using var schemaReader = await schemaCommand.ExecuteReaderAsync();
        Assert(await schemaReader.ReadAsync(), "FTS 갱신 트리거를 찾을 수 없습니다.");
        var ftsUpdateTrigger = schemaReader.IsDBNull(0) ? string.Empty : schemaReader.GetString(0);
        Assert(ftsUpdateTrigger.Contains("UPDATE OF display_name, target_path", StringComparison.OrdinalIgnoreCase) &&
               ftsUpdateTrigger.Contains("WHEN old.display_name <> new.display_name", StringComparison.OrdinalIgnoreCase),
            "기존 FTS 갱신 트리거가 변경 필드 조건으로 교체되지 않았습니다.");
    }

    var recentFolder = Path.Combine(testRoot, "recent-folder");
    Directory.CreateDirectory(recentFolder);
    await File.WriteAllTextAsync(Path.Combine(recentFolder, "initial-shortcut.url"),
        "[InternetShortcut]\nURL=https://example.com/initial\n");
    using var monitorDatabase = new RecentDatabase(Path.Combine(testRoot, "monitor", "findhistory.db"));
    await monitorDatabase.InitializeAsync();
    await using (var monitor = new RecentItemsMonitor(monitorDatabase, new ShortcutResolver(), recentFolder))
    {
        await monitor.StartAsync();
        Assert((await monitorDatabase.SearchAsync("initial-shortcut", null)).Count == 1,
            "감시 시작 시 기존 바로가기를 수집하지 못했습니다.");
        var startupDiagnostics = monitor.GetDiagnosticsSnapshot();
        Assert(startupDiagnostics.IsStarted && startupDiagnostics.IsWatcherActive,
            "감시기 진단 상태가 실행 중으로 표시되지 않습니다.");
        Assert(startupDiagnostics.LastScanCompletedUtc is not null &&
               startupDiagnostics.LastScanItemCount == 1 &&
               startupDiagnostics.LastScanDuration is not null,
            "초기 전체 스캔 진단 정보가 기록되지 않았습니다.");

        await File.WriteAllTextAsync(Path.Combine(recentFolder, "live-shortcut.url"),
            "[InternetShortcut]\nURL=https://example.com/live\n");
        await WaitUntilAsync(
            async () => (await monitorDatabase.SearchAsync("live-shortcut", null)).Count == 1,
            TimeSpan.FromSeconds(5));
        var liveDiagnostics = monitor.GetDiagnosticsSnapshot();
        Assert(liveDiagnostics.SessionCaptureCount >= 1 && liveDiagnostics.LastCaptureUtc is not null,
            "실시간 기록이 감시기 진단 통계에 반영되지 않았습니다.");

        await monitor.StopAsync();
        var stoppedDiagnostics = monitor.GetDiagnosticsSnapshot();
        Assert(stoppedDiagnostics.IsStopping && !stoppedDiagnostics.IsWatcherActive,
            "감시기 종료 상태가 진단 정보에 반영되지 않았습니다.");
    }

    var settingsPath = Path.Combine(testRoot, "settings.json");
    var settings = new AppSettingsService(settingsPath);
    settings.SetDatabasePath(movedPath);
    var savedSearch = new SavedSearch(Guid.NewGuid(), "MP4 영상", "*.mp4", "mp4", null,
        Path.Combine(testRoot, "videos"), 7, null, false);
    Assert(settings.AddSavedSearch(savedSearch), "저장 검색을 추가하지 못했습니다.");
    var reloadedSettings = new AppSettingsService(settingsPath);
    Assert(string.Equals(reloadedSettings.DatabasePath, movedPath, StringComparison.OrdinalIgnoreCase),
        "DB 위치 설정이 저장되지 않았습니다.");
    Assert(reloadedSettings.SavedSearches.Count == 1 &&
           reloadedSettings.SavedSearches[0] == savedSearch,
        "저장 검색이 설정 파일에서 원래 조건대로 복원되지 않았습니다.");
    reloadedSettings.RemoveSavedSearch(savedSearch.Id);
    Assert(new AppSettingsService(settingsPath).SavedSearches.Count == 0,
        "저장 검색 삭제가 설정 파일에 반영되지 않았습니다.");

    Console.WriteLine("PASS: 로그, 저장, 검색, 날짜 경계/DST, 활동 집계, 진단, 기존 DB 이관, DB 이동/전환 복구, 설정, 실시간 감시");
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (Exception)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    while (!await condition())
    {
        try
        {
            await Task.Delay(50, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("실시간 최근 항목 감시 결과를 제한 시간 안에 확인하지 못했습니다.");
        }
    }
}
