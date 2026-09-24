using FindHistory.Models;
using FindHistory.Services;

var testRoot = Path.Combine(Path.GetTempPath(), $"FindHistory-Smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    var originalPath = Path.Combine(testRoot, "original", "findhistory.db");
    var database = new RecentDatabase(originalPath);
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

    var movedPath = Path.Combine(testRoot, "moved", "findhistory.db");
    await database.MoveToAsync(movedPath);
    Assert(File.Exists(movedPath), "DB 파일이 새 위치로 이동되지 않았습니다.");
    Assert(!File.Exists(originalPath), "이전 위치에 DB 파일이 남아 있습니다.");

    var movedResults = await database.SearchAsync("sample", null);
    Assert(movedResults.Count == 1, "이동한 DB에서 기존 기록을 읽지 못했습니다.");

    var otherPath = Path.Combine(testRoot, "other", "findhistory.db");
    var otherDatabase = new RecentDatabase(otherPath);
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

    var settingsPath = Path.Combine(testRoot, "settings.json");
    var settings = new AppSettingsService(settingsPath);
    settings.SetDatabasePath(movedPath);
    var reloadedSettings = new AppSettingsService(settingsPath);
    Assert(string.Equals(reloadedSettings.DatabasePath, movedPath, StringComparison.OrdinalIgnoreCase),
        "DB 위치 설정이 저장되지 않았습니다.");

    Console.WriteLine("PASS: 저장, 검색, DB 이동, 기존 DB 전환, 설정 유지");
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
