using FindHistory.Models;
using FindHistory.Services;
using FindHistory.ViewModels;

internal static class SavedSearchViewModelTests
{
    public static async Task VerifyAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "preset-vm");
        var folder = Path.Combine(root, "data");
        var recent = Path.Combine(root, "recent");
        Directory.CreateDirectory(recent);
        var settings = new AppSettingsService(Path.Combine(root, "settings.json"));
        settings.SetRecordingPaused(true);
        var date = DateTime.Today.AddDays(-2);
        var preset = new SavedSearch(Guid.NewGuid(), "Specific", "preset", "txt", true, folder, null, date, true);
        settings.AddSavedSearch(preset);
        var restored = new AppSettingsService(Path.Combine(root, "settings.json"));
        await using var database = new RecentDatabase(Path.Combine(root, "history.db"));
        await database.InitializeAsync();
        RecentItemCandidate Item(string parent, string name, DateTime localDate)
        {
            var range = HistoryDateRangeFactory.CreateLocalCalendarRange(localDate, localDate.AddDays(1), "test");
            return new(Path.Combine(parent, name), name, Path.GetExtension(name).TrimStart('.'),
                "파일", Path.Combine(root, name + ".lnk"), range.StartUtc.AddHours(12), true);
        }
        await database.UpsertManyAsync(new[]
        {
            Item(folder, "preset-first.txt", date), Item(folder, "preset-second.txt", date.AddDays(1)),
            Item(folder, "preset-first.pdf", date), Item(root, "preset-outside.txt", date)
        });
        var log = new AppLogService(Path.Combine(root, "logs"));
        await using var monitor = new RecentItemsMonitor(database, new ShortcutResolver(), recent, log, restored);
        using var vm = new MainViewModel(database, monitor, new AutoStartService(), restored, log);
        await vm.InitializeAsync();
        var notifications = new List<string?>();
        Func<bool>? consistent = null;
        var intermediateState = false;
        vm.PropertyChanged += (_, e) =>
        {
            notifications.Add(e.PropertyName);
            if (consistent is not null) intermediateState |= !consistent();
        };

        consistent = () => vm.SearchText == "preset" && vm.ExtensionFilter == "txt" &&
            vm.SelectedExistence.Exists == true && vm.FolderFilter == folder && vm.SpecificDate == date && vm.IsSpecificDateSelected;
        vm.SelectedSavedSearch = vm.SavedSearches.Single();
        Check(!intermediateState && consistent() && vm.SelectedSavedSearch == preset,
            "저장 검색이 일부 조건만 적용된 상태를 알림으로 노출했습니다.");
        Check(notifications.Count(name => name == nameof(MainViewModel.FilterChips)) == 1 &&
              notifications.Count(name => name == nameof(MainViewModel.ActivityWeeks)) == 1 && vm.FilterChips.Count == 4,
            "저장 검색 적용이 필터·선택 알림을 중복 발생시켰습니다.");
        await WaitUntilAsync(() => vm.Items.Count == 1 && vm.Items[0].DisplayName == "preset-first.txt");
        Check(notifications.Count(name => name == nameof(MainViewModel.Items)) == 1,
            "저장 검색 적용이 결과를 중복 갱신했습니다.");
        consistent = null;

        vm.RemoveFilterCommand.Execute(vm.FilterChips.Single(chip => chip.Key == "extension"));
        await WaitUntilAsync(() => vm.Items.Count == 2);
        Check(vm.SearchText == "preset" && vm.FolderFilter == folder && vm.SelectedExistence.Exists == true &&
              vm.IsSpecificDateSelected && vm.SelectedSavedSearch is null && vm.FilterChips.Count == 3,
            "필터 칩 개별 해제가 다른 조건 또는 저장 검색 선택을 잘못 유지했습니다.");

        notifications.Clear();
        consistent = () => vm.SearchText == "preset" && vm.ExtensionFilter == string.Empty &&
            vm.SelectedExistence.Exists is null && vm.FolderFilter == string.Empty && !vm.IsSpecificDateSelected;
        vm.ClearFiltersCommand.Execute(null);
        Check(!intermediateState && consistent() && vm.FilterChips.Count == 0,
            "필터 초기화가 중간 상태를 노출하거나 검색어를 삭제했습니다.");
        Check(notifications.Count(name => name == nameof(MainViewModel.FilterChips)) == 1 &&
              notifications.Count(name => name == nameof(MainViewModel.ActivityWeeks)) == 1,
            "필터 초기화가 필터·선택 알림을 중복 발생시켰습니다.");
        await WaitUntilAsync(() => vm.Items.Count == 4);
        Check(notifications.Count(name => name == nameof(MainViewModel.Items)) == 1,
            "필터 초기화가 결과를 중복 갱신했습니다.");
        consistent = null;
        notifications.Clear();
        vm.ClearFiltersCommand.Execute(null);
        await Task.Delay(300);
        Check(notifications.Count == 0, "같은 필터 초기화가 불필요한 갱신을 발생시켰습니다.");

        var relative = preset with { Id = Guid.NewGuid(), Name = "Relative", Extension = "pdf", Exists = null,
            Folder = string.Empty, CalendarDayCount = 7, SpecificDate = null, IsSpecificDate = false };
        vm.SelectedSavedSearch = relative;
        await WaitUntilAsync(() => vm.Items.Count == 1 && vm.Items[0].DisplayName == "preset-first.pdf");
        Check(vm.SelectedDateRange.CalendarDayCount == 7 && !vm.IsSpecificDateSelected,
            "상대 기간 저장 검색이 실제 화면 모델에 적용되지 않았습니다.");
        vm.SelectedSavedSearch = relative with { Id = Guid.NewGuid(), CalendarDayCount = 14, Extension = string.Empty };
        await WaitUntilAsync(() => vm.Items.Count == 4);
        Check(vm.SelectedDateRange == vm.DateRanges[0], "지원하지 않는 저장 기간이 전체 기간으로 복구되지 않았습니다.");

        vm.SelectedSavedSearch = preset;
        await WaitUntilAsync(() => vm.Items.Count == 1 && vm.Items[0].DisplayName == "preset-first.txt");
        var missingDate = preset with { Id = Guid.NewGuid(), SpecificDate = null };
        vm.SelectedSavedSearch = missingDate;
        await WaitUntilAsync(() => vm.Items.Count == 0);
        Check(vm.SpecificDate == DateTime.Today && vm.IsSpecificDateSelected,
            "날짜가 없는 저장 검색이 오늘 날짜로 복구되지 않았습니다.");

        notifications.Clear();
        vm.SelectedSavedSearch = missingDate;
        await Task.Delay(300);
        Check(notifications.Count == 0, "같은 저장 검색을 다시 적용할 때 불필요한 갱신이 발생했습니다.");
        vm.SelectedSavedSearch = preset;
        vm.ClearFiltersCommand.Execute(null);
        await WaitUntilAsync(() => vm.Items.Count == 4);
        Check(vm.SelectedSavedSearch is null && vm.FilterChips.Count == 0 && vm.SearchText == "preset",
            "빠른 저장 검색·필터 초기화 뒤 이전 검색 조건이 복구됐습니다.");
        vm.SelectedSavedSearch = preset;
        await WaitUntilAsync(() => vm.Items.Count == 1 && vm.Items[0].DisplayName == "preset-first.txt");
        vm.SelectedSavedSearch = relative with
        {
            Id = Guid.NewGuid(), SearchText = null!, Extension = null!, Folder = null!, CalendarDayCount = null
        };
        await WaitUntilAsync(() => vm.Items.Count == 4);
        Check(vm.SearchText == string.Empty && vm.ExtensionFilter == string.Empty && vm.FolderFilter == string.Empty,
            "누락된 저장 검색 문자열이 빈 조건으로 복구되지 않았습니다.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("저장 검색 화면 모델 갱신 시간 초과");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
