using FindHistory.Models;
using FindHistory.Services;
using FindHistory.Localization;
using System.Text.Json;

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
    Assert(ActivityLevelCalculator.Calculate(0, 0) == 0,
        "활동이 없는 날짜에 히트맵 단계가 표시되었습니다.");
    Assert(ActivityLevelCalculator.Calculate(1, 1) == 1,
        "기록이 한 건뿐일 때 최대 활동 색상으로 표시되었습니다.");
    Assert(ActivityLevelCalculator.Calculate(3, 3) == 1 &&
           ActivityLevelCalculator.Calculate(4, 4) == 2 &&
           ActivityLevelCalculator.Calculate(10, 10) == 3 &&
           ActivityLevelCalculator.Calculate(25, 25) == 4,
        "히트맵 절대 활동 임계값이 올바르게 적용되지 않았습니다.");
    Assert(ActivityLevelCalculator.Calculate(2, 100) == 1 &&
           ActivityLevelCalculator.Calculate(100, 1000) == 3,
        "절대 임계값과 기간 내 상대 비교가 함께 적용되지 않았습니다.");

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
    var invalidFolderFilter = new HistoryFilters(Folder: "invalid\0folder");
    await AssertThrowsAsync(
        () => database.SearchWithStatsAsync("boundary", boundaryRange, filters: invalidFolderFilter),
        "잘못된 저장 폴더 경로가 검색 오류로 명확하게 처리되지 않았습니다.");
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
    var springDstEvents = new[]
    {
        new DateTimeOffset(2026, 3, 8, 7, 59, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 8, 9, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 9, 7, 0, 0, TimeSpan.Zero)
    };
    for (var index = 0; index < springDstEvents.Length; index++)
    {
        await database.UpsertAsync(new RecentItemCandidate(
            Path.Combine(testRoot, "dst", $"spring-{index}.txt"), $"spring-{index}.txt", "TXT", "파일",
            Path.Combine(testRoot, "dst", $"spring-{index}.lnk"), springDstEvents[index], false));
    }
    var springDstActivity = await database.GetDailyActivityAsync(springDstRange, pacific);
    Assert(springDstActivity.Count == 1 &&
           springDstActivity[0].Date == new DateTime(2026, 3, 8) &&
           springDstActivity[0].OpenCount == 3,
        "SQL 활동 집계가 DST 시작일의 로컬 경계 또는 23시간 범위를 정확히 처리하지 못했습니다.");
    var fallDstEvents = new[]
    {
        new DateTimeOffset(2026, 11, 1, 6, 59, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)
    };
    for (var index = 0; index < fallDstEvents.Length; index++)
    {
        await database.UpsertAsync(new RecentItemCandidate(
            Path.Combine(testRoot, "dst", $"fall-{index}.txt"), $"fall-{index}.txt", "TXT", "파일",
            Path.Combine(testRoot, "dst", $"fall-{index}.lnk"), fallDstEvents[index], false));
    }
    var fallDstActivity = await database.GetDailyActivityAsync(fallDstRange, pacific);
    Assert(fallDstActivity.Count == 1 &&
           fallDstActivity[0].Date == new DateTime(2026, 11, 1) &&
           fallDstActivity[0].OpenCount == 3,
        "SQL 활동 집계가 DST 종료일의 중복 시각 또는 25시간 범위를 정확히 처리하지 못했습니다.");

    var databaseDiagnostics = await database.GetDiagnosticsAsync();
    var extendedActivity = await database.GetDailyActivityAsync(
        new HistoryDateRange(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "확장 활동 범위"),
        TimeZoneInfo.Utc);
    Assert(extendedActivity.Sum(day => day.OpenCount) == databaseDiagnostics.StoredEvents,
        "366일을 넘는 활동 범위의 대체 집계가 전체 이벤트를 보존하지 못했습니다.");
    Assert(databaseDiagnostics.UniqueItems > 0 && databaseDiagnostics.StoredEvents > 0,
        "데이터베이스 진단 통계를 읽지 못했습니다.");
    Assert(databaseDiagnostics.EstimatedEvents == 0,
        "새 데이터베이스 이벤트가 추정 기록으로 집계되었습니다.");

    var formulaLikeName = "=HYPERLINK(\"https://example.invalid\")";
    await database.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "formula-like.csv"), formulaLikeName, "CSV", "파일",
        Path.Combine(testRoot, "formula-like.lnk"), DateTimeOffset.UtcNow, false));
    var exportDiagnostics = await database.GetDiagnosticsAsync();
    var csvExportPath = Path.Combine(testRoot, "exports", "history.csv");
    var jsonExportPath = Path.Combine(testRoot, "exports", "history.json");
    await database.ExportToAsync(csvExportPath, "csv");
    await database.ExportToAsync(jsonExportPath, ".json");
    var csvText = await File.ReadAllTextAsync(csvExportPath);
    Assert(csvText.Contains("'=HYPERLINK", StringComparison.Ordinal),
        "CSV 내보내기에서 스프레드시트 수식 입력을 안전하게 처리하지 않았습니다.");
    Assert(csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length ==
           exportDiagnostics.StoredEvents + 1,
        "CSV 내보내기에 개별 열기 이벤트 전체가 들어가지 않았습니다.");
    using (var jsonDocument = JsonDocument.Parse(await File.ReadAllTextAsync(jsonExportPath)))
    {
        var root = jsonDocument.RootElement;
        Assert(root.GetProperty("schemaVersion").GetInt32() == 1,
            "JSON 내보내기에 스키마 버전이 없습니다.");
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        var formulaItem = items.Single(item => item.GetProperty("displayName").GetString() == formulaLikeName);
        Assert(formulaItem.GetProperty("events").GetArrayLength() == 1,
            "JSON 내보내기에 개별 열기 이벤트가 들어가지 않았습니다.");
        Assert(items.Sum(item => item.GetProperty("events").GetArrayLength()) ==
               exportDiagnostics.StoredEvents,
            "JSON 내보내기에 전체 열기 이벤트가 들어가지 않았습니다.");
    }
    var formulaSearchItem = (await database.SearchAsync("formula-like", null)).Single();
    var searchCsvPath = Path.Combine(testRoot, "exports", "search-results.csv");
    var searchJsonPath = Path.Combine(testRoot, "exports", "search-results.json");
    await database.ExportItemsToAsync(searchCsvPath, "csv", [formulaSearchItem.Id]);
    await database.ExportItemsToAsync(searchJsonPath, "json", [formulaSearchItem.Id]);
    Assert((await File.ReadAllTextAsync(searchCsvPath)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 2,
        "검색 결과 CSV에 선택한 항목만 포함되지 않았습니다.");
    using (var searchJsonDocument = JsonDocument.Parse(await File.ReadAllTextAsync(searchJsonPath)))
    {
        var exportedItems = searchJsonDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert(exportedItems.Length == 1 &&
               exportedItems[0].GetProperty("displayName").GetString() == formulaLikeName &&
               exportedItems[0].GetProperty("events").GetArrayLength() == 1,
            "검색 결과 JSON에 선택 항목과 전체 열기 이력을 내보내지 못했습니다.");
    }
    await AssertThrowsAsync(
        () => database.ExportItemsToAsync(Path.Combine(testRoot, "exports", "empty.json"), "json", []),
        "빈 검색 결과 내보내기를 거부하지 않았습니다.");

    var preBackupItemCount = (await database.GetStatsAsync()).UniqueItems;
    var backupPath = Path.Combine(testRoot, "backups", "findhistory.fhbackup");
    await database.BackupToAsync(backupPath);
    Assert(File.Exists(backupPath), "SQLite Backup API 백업 파일이 생성되지 않았습니다.");
    using (var backupDatabase = new RecentDatabase(backupPath))
    {
        await backupDatabase.InitializeAsync();
        Assert((await backupDatabase.GetStatsAsync()).UniqueItems == preBackupItemCount,
            "백업 파일의 항목 수가 원본과 일치하지 않습니다.");
    }
    await database.UpsertAsync(new RecentItemCandidate(
        Path.Combine(testRoot, "added-after-backup.txt"),
        "added-after-backup.txt",
        "TXT",
        "파일",
        Path.Combine(testRoot, "added-after-backup.lnk"),
        DateTimeOffset.UtcNow,
        false));
    var safetyCopyPath = await database.RestoreFromAsync(backupPath);
    Assert(File.Exists(safetyCopyPath), "복원 전 현재 DB의 안전 사본이 생성되지 않았습니다.");
    Assert((await database.GetStatsAsync()).UniqueItems == preBackupItemCount,
        "복원 후 DB가 백업 시점의 항목 수로 돌아오지 않았습니다.");
    Assert((await database.SearchAsync("added-after-backup", null)).Count == 0,
        "복원 전에 추가한 항목이 백업 복원 후에도 남아 있습니다.");
    var invalidBackupPath = Path.Combine(testRoot, "invalid-backup.db");
    await File.WriteAllTextAsync(invalidBackupPath, "not a database");
    await AssertThrowsAsync(() => database.RestoreFromAsync(invalidBackupPath),
        "잘못된 백업 복원이 실패하지 않았습니다.");
    Assert((await database.GetStatsAsync()).UniqueItems == preBackupItemCount,
        "잘못된 백업 시도가 현재 DB를 변경했습니다.");

    var failedMovePath = Path.Combine(testRoot, "failed-move", "findhistory.db");
    await AssertThrowsAsync(
        () => database.MoveToAsync(failedMovePath, _ => throw new IOException("설정 저장 실패")),
        "설정 저장 실패 시 DB 이동이 중단되지 않았습니다.");
    Assert(string.Equals(database.DatabasePath, originalPath, StringComparison.OrdinalIgnoreCase),
        "설정 저장 실패 뒤 현재 DB 경로가 바뀌었습니다.");
    Assert(File.Exists(originalPath), "설정 저장 실패 뒤 원본 DB가 보존되지 않았습니다.");
    Assert(!File.Exists(failedMovePath), "설정 저장 실패 뒤 이동 대상 DB가 정리되지 않았습니다.");
    Assert((await database.SearchAsync("sample", null)).Count == 1,
        "설정 저장 실패 뒤 원본 DB에서 기존 기록을 읽지 못했습니다.");

    var movedPath = Path.Combine(testRoot, "moved", "findhistory.db");
    var persistedMovePath = string.Empty;
    var previousDatabaseRemoved = await database.MoveToAsync(movedPath, path =>
    {
        Assert(File.Exists(originalPath), "새 경로를 저장하기 전에 원본 DB가 삭제되었습니다.");
        Assert(File.Exists(path), "새 경로를 저장하기 전에 대상 DB가 준비되지 않았습니다.");
        persistedMovePath = path;
    });
    Assert(string.Equals(persistedMovePath, movedPath, StringComparison.OrdinalIgnoreCase),
        "DB 이동 전에 새 경로 설정이 저장되지 않았습니다.");
    Assert(previousDatabaseRemoved, "정상 이동 뒤 이전 DB 파일이 정리되지 않았습니다.");
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

    var versionOnePath = Path.Combine(testRoot, "version-one", "findhistory.db");
    Directory.CreateDirectory(Path.GetDirectoryName(versionOnePath)!);
    await using (var versionOneConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                     $"Data Source={versionOnePath};Pooling=False"))
    {
        await versionOneConnection.OpenAsync();
        await using var versionOneCommand = versionOneConnection.CreateCommand();
        versionOneCommand.CommandText = """
            CREATE TABLE recent_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                target_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                display_name TEXT NOT NULL,
                extension TEXT NOT NULL,
                item_kind TEXT NOT NULL,
                source_link_path TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_link_write_utc TEXT NOT NULL,
                open_count INTEGER NOT NULL DEFAULT 1,
                exists_flag INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE open_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recent_item_id INTEGER NOT NULL,
                opened_utc TEXT NOT NULL,
                source_link_path TEXT NOT NULL,
                is_estimated INTEGER NOT NULL DEFAULT 0,
                UNIQUE(recent_item_id, opened_utc),
                FOREIGN KEY(recent_item_id) REFERENCES recent_items(id) ON DELETE CASCADE
            );
            CREATE TABLE history_stats (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                unique_items INTEGER NOT NULL,
                total_open_count INTEGER NOT NULL
            );
            INSERT INTO recent_items (
                id, target_path, display_name, extension, item_kind, source_link_path,
                first_seen_utc, last_seen_utc, last_link_write_utc, open_count, exists_flag)
            VALUES (1, 'v1-item.txt', 'v1-item.txt', 'TXT', '파일', 'v1-item.lnk',
                    '2026-07-01T12:00:00.0000000+00:00',
                    '2026-07-01T12:00:00.0000000+00:00',
                    '2026-07-01T12:00:00.0000000+00:00', 3, 1);
            INSERT INTO open_events (recent_item_id, opened_utc, source_link_path, is_estimated)
            VALUES (1, '2026-07-01T12:00:00.0000000+00:00', 'v1-item.lnk', 0),
                   (1, '2026-06-30T12:00:00.0000000+00:00', 'v1-item.lnk', 1);
            INSERT INTO history_stats (id, unique_items, total_open_count) VALUES (1, 1, 3);
            PRAGMA user_version = 1;
            """;
        await versionOneCommand.ExecuteNonQueryAsync();
    }
    using (var versionOneDatabase = new RecentDatabase(versionOnePath))
    {
        await versionOneDatabase.InitializeAsync();
        var migratedDiagnostics = await versionOneDatabase.GetDiagnosticsAsync();
        Assert(migratedDiagnostics.UniqueItems == 1 && migratedDiagnostics.TotalOpenCount == 3 &&
               migratedDiagnostics.StoredEvents == 2 && migratedDiagnostics.EstimatedEvents == 1,
            "버전 1 DB의 진단 카운터를 정확히 이관하지 못했습니다.");
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
        await legacyDatabase.UpsertAsync(new RecentItemCandidate(
            Path.Combine(testRoot, "legacy.txt"), "legacy.txt", "TXT", "파일",
            Path.Combine(testRoot, "legacy.lnk"), legacyOpenedAt, false));
        var confirmedLegacyDiagnostics = await legacyDatabase.GetDiagnosticsAsync();
        Assert(confirmedLegacyDiagnostics.StoredEvents == 1 &&
               confirmedLegacyDiagnostics.EstimatedEvents == 0,
            "추정 이벤트가 실제 감시 이벤트로 확인될 때 진단 카운터가 갱신되지 않았습니다.");
        await using var schemaConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={legacyPath};Pooling=False");
        await schemaConnection.OpenAsync();
        await using var schemaCommand = schemaConnection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt32(await schemaCommand.ExecuteScalarAsync()) == 2,
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
    Assert(settings.Language == "ko", "새 설정의 기본 언어가 한국어가 아닙니다.");
    Assert(!settings.AutoBackupEnabled && settings.AutoBackupRetention == 7,
        "자동 백업은 기본으로 꺼져 있어야 합니다.");
    settings.SetAutoBackup(true, 3);
    settings.SetDatabasePath(movedPath);
    settings.SetLanguage("en");
    var savedSearch = new SavedSearch(Guid.NewGuid(), "MP4 영상", "*.mp4", "mp4", null,
        Path.Combine(testRoot, "videos"), 7, null, false);
    Assert(settings.AddSavedSearch(savedSearch), "저장 검색을 추가하지 못했습니다.");
    var duplicateSearch = savedSearch with { Id = Guid.NewGuid(), Name = "  mp4 영상  " };
    await AssertThrowsAsync(() => Task.Run(() => settings.AddSavedSearch(duplicateSearch)),
        "대소문자와 앞뒤 공백을 무시한 저장 검색 중복을 거부하지 않았습니다.");
    var blankSearch = savedSearch with { Id = Guid.NewGuid(), Name = "   " };
    await AssertThrowsAsync(() => Task.Run(() => settings.AddSavedSearch(blankSearch)),
        "빈 저장 검색 이름을 거부하지 않았습니다.");
    var longNameSearch = savedSearch with
    {
        Id = Guid.NewGuid(),
        Name = new string('x', AppSettingsService.MaxSavedSearchNameLength + 1)
    };
    await AssertThrowsAsync(() => Task.Run(() => settings.AddSavedSearch(longNameSearch)),
        "최대 길이를 넘는 저장 검색 이름을 거부하지 않았습니다.");
    Assert(settings.SavedSearches.Count == 1,
        "잘못된 저장 검색 검증이 설정 목록을 변경했습니다.");
    var reloadedSettings = new AppSettingsService(settingsPath);
    Assert(string.Equals(reloadedSettings.DatabasePath, movedPath, StringComparison.OrdinalIgnoreCase),
        "DB 위치 설정이 저장되지 않았습니다.");
    Assert(reloadedSettings.Language == "en", "앱 언어 설정이 저장되지 않았습니다.");
    Assert(reloadedSettings.AutoBackupEnabled && reloadedSettings.AutoBackupRetention == 3,
        "자동 백업 설정이 저장되지 않았습니다.");
    await AssertThrowsAsync(() => Task.Run(() => settings.SetAutoBackup(true, 0)),
        "잘못된 자동 백업 보관 개수를 거부하지 않았습니다.");
    LocalizationManager.Instance.SetLanguage(reloadedSettings.Language);
    Assert(LocalizationManager.Instance.Translate("최근 기록") == "Recent history",
        "영어 번역을 불러오지 못했습니다.");
    Assert(LocalizationManager.Instance.Translate("검색 실패: test") == "Search failed: test",
        "동적 상태 메시지를 영어로 번역하지 못했습니다.");
    LocalizationManager.Instance.SetLanguage("ko");
    Assert(LocalizationManager.Instance.Translate("최근 기록") == "최근 기록",
        "한국어로 되돌리지 못했습니다.");
    Assert(reloadedSettings.SavedSearches.Count == 1 &&
           reloadedSettings.SavedSearches[0] == savedSearch,
        "저장 검색이 설정 파일에서 원래 조건대로 복원되지 않았습니다.");
    reloadedSettings.RemoveSavedSearch(savedSearch.Id);
    Assert(new AppSettingsService(settingsPath).SavedSearches.Count == 0,
        "저장 검색 삭제가 설정 파일에 반영되지 않았습니다.");

    var cappedSettings = new AppSettingsService(Path.Combine(testRoot, "saved-search-limit.json"));
    for (var index = 0; index < AppSettingsService.MaxSavedSearches; index++)
    {
        var search = savedSearch with { Id = Guid.NewGuid(), Name = $"Search {index:D2}" };
        Assert(cappedSettings.AddSavedSearch(search), "저장 검색이 30개 제한 전에 거부되었습니다.");
    }
    Assert(!cappedSettings.AddSavedSearch(savedSearch with { Id = Guid.NewGuid(), Name = "Search 30" }),
        "저장 검색 30개 제한을 초과해 항목을 추가했습니다.");
    cappedSettings.RemoveSavedSearch(cappedSettings.SavedSearches[0].Id);
    Assert(cappedSettings.AddSavedSearch(savedSearch with { Id = Guid.NewGuid(), Name = "Replacement" }),
        "저장 검색 삭제 뒤 빈 자리에 항목을 추가하지 못했습니다.");

    var dateRangeOptions = new DateRangeOption[]
    {
        new("전체 기간"), new("오늘", 1), new("최근 7일", 7), new("특정 날짜", IsSpecificDate: true)
    };
    var unsupportedSavedRange = savedSearch with { CalendarDayCount = 14 };
    Assert(SavedSearchDateRangeResolver.Resolve(unsupportedSavedRange, dateRangeOptions) == dateRangeOptions[0],
        "지원하지 않는 저장 검색 기간이 전체 기간으로 안전하게 대체되지 않았습니다.");
    var restoredSavedRange = SavedSearchDateRangeResolver.Resolve(savedSearch, dateRangeOptions);
    Assert(restoredSavedRange.CalendarDayCount == 7,
        "지원되는 저장 검색 기간을 다시 선택하지 못했습니다.");

    var blockedSettingsPath = Path.Combine(testRoot, "blocked-settings.json");
    var blockedSettings = new AppSettingsService(blockedSettingsPath);
    var originalConfiguredPath = blockedSettings.DatabasePath;
    Directory.CreateDirectory(blockedSettingsPath);
    await AssertThrowsAsync(
        () => Task.Run(() => blockedSettings.SetDatabasePath(movedPath)),
        "설정 저장 실패를 감지하지 못했습니다.");
    Assert(string.Equals(blockedSettings.DatabasePath, originalConfiguredPath, StringComparison.OrdinalIgnoreCase),
        "설정 저장 실패 후 메모리의 DB 경로가 디스크와 달라졌습니다.");
    await AssertThrowsAsync(
        () => Task.Run(() => blockedSettings.SetLanguage("en")),
        "언어 설정 저장 실패를 감지하지 못했습니다.");
    Assert(blockedSettings.Language == "ko", "언어 설정 저장 실패 뒤 메모리 값이 먼저 바뀌었습니다.");
    await AssertThrowsAsync(() => Task.Run(() => blockedSettings.SetAutoBackup(true, 3)),
        "자동 백업 설정 저장 실패를 감지하지 못했습니다.");
    Assert(!blockedSettings.AutoBackupEnabled && blockedSettings.AutoBackupRetention == 7,
        "자동 백업 설정 저장 실패 뒤 메모리 값이 먼저 바뀌었습니다.");

    var autoSettings = new AppSettingsService(Path.Combine(testRoot, "auto-settings.json"));
    var autoDirectory = Path.Combine(testRoot, "automatic-backups");
    var backupNow = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(9));
    await using (var autoBackup = new AutoBackupService(database, autoSettings, logger,
                     autoDirectory, () => backupNow))
    {
        await autoBackup.CheckAsync();
        Assert(!Directory.Exists(autoDirectory), "꺼진 자동 백업이 폴더를 생성했습니다.");
        autoSettings.SetAutoBackup(true, 3);
        await Task.WhenAll(autoBackup.CheckAsync(), autoBackup.CheckAsync());
        var automaticFiles = Directory.GetFiles(autoDirectory, "*.fhbackup");
        Assert(automaticFiles.Length == 1, "동시 검사에서 하루에 여러 백업을 만들었습니다.");
        await using (var automaticDatabase = new RecentDatabase(automaticFiles[0]))
        {
            await automaticDatabase.InitializeAsync();
            Assert((await automaticDatabase.GetStatsAsync()).UniqueItems ==
                   (await database.GetStatsAsync()).UniqueItems, "자동 백업에 현재 기록이 없습니다.");
        }
        var manualPath = Path.Combine(autoDirectory, "manual.fhbackup");
        var autoSafetyCopyPath = Path.Combine(autoDirectory, "findhistory.db.bak");
        var malformedPath = Path.Combine(autoDirectory,
            Path.GetFileName(automaticFiles[0]).Replace("20261001T030000000Z", "invalid"));
        await File.WriteAllTextAsync(manualPath, "keep");
        await File.WriteAllTextAsync(autoSafetyCopyPath, "keep");
        await File.WriteAllTextAsync(malformedPath, "keep");
        for (var day = 0; day < 4; day++)
        {
            backupNow = backupNow.AddDays(1);
            await autoBackup.CheckAsync();
        }
        Assert(Directory.GetFiles(autoDirectory, "FindHistory-auto-*.fhbackup").Length == 4,
            "보관 개수 정리 또는 잘못된 파일명 보호가 실패했습니다.");
        Assert(!File.Exists(automaticFiles[0]), "가장 오래된 자동 백업이 정리되지 않았습니다.");
        Assert(File.Exists(manualPath) && File.Exists(autoSafetyCopyPath) && File.Exists(malformedPath),
            "자동 백업 정리가 수동 백업 또는 안전 사본을 삭제했습니다.");
        autoSettings.SetAutoBackup(true, 1);
        await autoBackup.CheckAsync();
        Assert(Directory.GetFiles(autoDirectory, "FindHistory-auto-*.fhbackup").Length == 2,
            "오늘 백업 후 보관 개수 변경이 적용되지 않았습니다.");
        autoSettings.SetAutoBackup(false, 1);
        backupNow = backupNow.AddDays(1);
        await autoBackup.CheckAsync();
        Assert(Directory.GetFiles(autoDirectory, "FindHistory-auto-*.fhbackup").Length == 2,
            "꺼진 자동 백업이 파일을 추가했습니다.");
        var rejectedAutomaticPath = Path.Combine(autoDirectory, "rejected.fhbackup");
        await AssertThrowsAsync(() => database.BackupToAsync(rejectedAutomaticPath,
                expectedDatabasePath: Path.Combine(testRoot, "different.db")),
            "DB 전환 후 잘못된 식별자로 자동 백업을 만들었습니다.");
        Assert(!File.Exists(rejectedAutomaticPath), "거부한 자동 백업 파일이 생성되었습니다.");
        await autoBackup.DisposeAsync();
        await AssertThrowsAsync(() => autoBackup.CheckAsync(), "종료된 자동 백업이 검사를 허용했습니다.");
    }

    var switchAutoSettings = new AppSettingsService(Path.Combine(testRoot, "switch-auto-settings.json"));
    switchAutoSettings.SetAutoBackup(true, 1);
    var firstAutoDbPath = Path.Combine(testRoot, "auto-first.db");
    var secondAutoDbPath = Path.Combine(testRoot, "auto-second.db");
    await using (var secondAutoDb = new RecentDatabase(secondAutoDbPath))
        await secondAutoDb.InitializeAsync();
    await using (var firstAutoDb = new RecentDatabase(firstAutoDbPath))
    {
        await firstAutoDb.InitializeAsync();
        var switchBackupDirectory = Path.Combine(testRoot, "switch-auto-backups");
        await using var switchAutoBackup = new AutoBackupService(firstAutoDb, switchAutoSettings, logger,
            switchBackupDirectory, () => backupNow);
        await switchAutoBackup.CheckAsync();
        await firstAutoDb.UseAsync(secondAutoDbPath);
        await switchAutoBackup.CheckAsync();
        Assert(Directory.GetFiles(switchBackupDirectory, "*.fhbackup").Length == 2,
            "같은 날 DB 전환 후 백업을 건너뛰거나 이전 DB 백업을 삭제했습니다.");
        switchAutoBackup.Start();
        await switchAutoBackup.DisposeAsync();
    }

    var failureAutoSettings = new AppSettingsService(Path.Combine(testRoot, "failure-auto-settings.json"));
    failureAutoSettings.SetAutoBackup(true, 3);
    var failureAutoDirectory = Path.Combine(testRoot, "failure-auto-backups");
    await using (var failureAutoDb = new RecentDatabase(Path.Combine(testRoot, "failure-auto.db")))
    {
        await failureAutoDb.InitializeAsync();
        await using var failureAutoBackup = new AutoBackupService(failureAutoDb, failureAutoSettings, logger,
            failureAutoDirectory, () => backupNow);
        await failureAutoBackup.CheckAsync();
        backupNow = backupNow.AddDays(1);
        await failureAutoBackup.CheckAsync();
        failureAutoSettings.SetAutoBackup(true, 1);
        backupNow = backupNow.AddDays(1);
        await failureAutoDb.DisposeAsync();
        await AssertThrowsAsync(() => failureAutoBackup.CheckAsync(),
            "닫힌 DB의 자동 백업 실패를 감지하지 못했습니다.");
        Assert(Directory.GetFiles(failureAutoDirectory, "*.fhbackup").Length == 2,
            "자동 백업 실패 후 이전 백업을 정리했습니다.");
    }
    var blockedSearch = savedSearch with { Id = Guid.NewGuid() };
    await AssertThrowsAsync(
        () => Task.Run(() => blockedSettings.AddSavedSearch(blockedSearch)),
        "저장 검색 설정 저장 실패를 감지하지 못했습니다.");
    Assert(blockedSettings.SavedSearches.Count == 0,
        "저장 검색 설정 저장 실패 후 메모리 목록이 디스크와 달라졌습니다.");

    var settingsWithSearchPath = Path.Combine(testRoot, "settings-with-search.json");
    var settingsWithSearch = new AppSettingsService(settingsWithSearchPath);
    Assert(settingsWithSearch.AddSavedSearch(savedSearch), "설정 쓰기 실패 테스트의 준비에 실패했습니다.");
    File.Delete(settingsWithSearchPath);
    Directory.CreateDirectory(settingsWithSearchPath);
    await AssertThrowsAsync(
        () => Task.Run(() => settingsWithSearch.RemoveSavedSearch(savedSearch.Id)),
        "저장 검색 삭제의 설정 쓰기 실패를 감지하지 못했습니다.");
    Assert(settingsWithSearch.SavedSearches.Count == 1,
        "저장 검색 삭제 실패 후 메모리 목록만 먼저 바뀌었습니다.");

    var invalidSettingsPath = Path.Combine(testRoot, "invalid-settings.json");
    await File.WriteAllTextAsync(invalidSettingsPath, "{");
    await AssertThrowsAsync(
        () => Task.Run(() => new AppSettingsService(invalidSettingsPath)),
        "손상된 설정 파일을 조용히 기본 설정으로 대체했습니다.");
    var inaccessibleSettingsPath = Path.Combine(testRoot, "settings-is-a-directory.json");
    Directory.CreateDirectory(inaccessibleSettingsPath);
    await AssertThrowsAsync(
        () => Task.Run(() => new AppSettingsService(inaccessibleSettingsPath)),
        "읽을 수 없는 설정 경로를 기본 DB 경로로 조용히 대체했습니다.");

    var disposalDatabase = new RecentDatabase(Path.Combine(testRoot, "dispose-race", "findhistory.db"));
    await disposalDatabase.InitializeAsync();
    var pendingReads = Enumerable.Range(0, 32)
        .Select(async _ =>
        {
            try
            {
                await disposalDatabase.SearchAsync(string.Empty, null);
            }
            catch (ObjectDisposedException)
            {
                // 종료와 겹친 대기 작업은 명확한 disposed 결과를 반환할 수 있다.
            }
        })
        .ToArray();
    await disposalDatabase.DisposeAsync();
    await Task.WhenAll(pendingReads);
    await AssertThrowsAsync(() => disposalDatabase.SearchAsync(string.Empty, null),
        "종료된 DB에서 검색을 거부하지 않았습니다.");

    Console.WriteLine("PASS: 로그, 저장/복원/내보내기, 검색, 날짜 경계/DST, 활동 집계, 진단, 기존 DB 이관, DB 이동/전환 복구, 설정 장애 복구, 실시간 감시");
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
