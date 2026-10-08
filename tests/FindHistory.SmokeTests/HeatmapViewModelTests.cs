using System.Windows;
using FindHistory.Models;
using FindHistory.Services;
using FindHistory.ViewModels;

internal static class HeatmapViewModelTests
{
    public static Task RunAsync(string testRoot)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Exception? failure = null;
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Startup += async (_, _) =>
                {
                    try { await VerifyAsync(testRoot); }
                    catch (Exception ex) { failure = ex; }
                    finally { application.Shutdown(); }
                };
                application.Run();
                if (failure is null) completion.SetResult();
                else completion.SetException(failure);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task VerifyAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "heatmap-vm");
        var recent = Path.Combine(root, "recent");
        Directory.CreateDirectory(recent);
        var settings = new AppSettingsService(Path.Combine(root, "settings.json"));
        settings.SetRecordingPaused(true);
        var firstDate = DateTime.Today.AddDays(-2);
        var secondDate = firstDate.AddDays(1);
        var search = new SavedSearch(Guid.NewGuid(), "Heatmap", "heatmap", "txt", true,
            root, null, null, false);
        settings.AddSavedSearch(search);
        await using var database = new RecentDatabase(Path.Combine(root, "history.db"));
        await database.InitializeAsync();
        await database.UpsertManyAsync(new[]
        {
            Candidate(root, "heatmap-first.txt", firstDate),
            Candidate(root, "heatmap-second.txt", secondDate),
            Candidate(root, "heatmap-other.pdf", firstDate)
        });
        var log = new AppLogService(Path.Combine(root, "logs"));
        await using var monitor = new RecentItemsMonitor(database, new ShortcutResolver(), recent, log, settings);
        using var viewModel = new MainViewModel(database, monitor, new AutoStartService(), settings, log);
        await viewModel.InitializeAsync();
        viewModel.SelectedSavedSearch = viewModel.SavedSearches.Single();
        await WaitUntilAsync(() => viewModel.Items.Count == 2);

        var notifications = new List<string?>();
        var inconsistentSelection = false;
        DateTime? expectedDate = firstDate;
        viewModel.PropertyChanged += (_, e) =>
        {
            notifications.Add(e.PropertyName);
            if (e.PropertyName is nameof(MainViewModel.SpecificDate) or nameof(MainViewModel.SelectedDateRange))
                inconsistentSelection |= viewModel.SpecificDate != expectedDate || !viewModel.IsSpecificDateSelected;
        };
        ActivityDay Day(DateTime date) => viewModel.ActivityWeeks.SelectMany(week => week.Days)
            .Single(day => day.Date == date);

        viewModel.SelectActivityDateCommand.Execute(Day(firstDate));
        Check(!inconsistentSelection && viewModel.SpecificDate == firstDate && viewModel.IsSpecificDateSelected,
            "히트맵 클릭 알림이 중간 날짜·기간 상태를 노출했습니다.");
        Check(notifications.Count(name => name == nameof(MainViewModel.FilterChips)) == 1 &&
              notifications.Count(name => name == nameof(MainViewModel.ActivityWeeks)) == 1,
            "히트맵 클릭이 필터 또는 선택 표시를 중복 갱신했습니다.");
        Check(viewModel.SelectedSavedSearch is null && viewModel.SearchText == "heatmap" &&
              viewModel.ExtensionFilter == "txt" && viewModel.SelectedExistence.Exists == true && viewModel.FolderFilter == root,
            "히트맵 클릭이 검색·확장자·존재 상태·폴더 조건을 변경했습니다.");
        Check(Day(firstDate).IsSelected && !Day(secondDate).IsSelected,
            "히트맵 클릭 후 선택 테두리가 날짜와 일치하지 않습니다.");
        await WaitUntilAsync(() => viewModel.Items.Count == 1 && viewModel.Items[0].DisplayName == "heatmap-first.txt");
        Check(notifications.Count(name => name == nameof(MainViewModel.Items)) == 1,
            "히트맵 클릭이 결과를 중복 갱신했습니다.");

        expectedDate = secondDate;
        notifications.Clear();
        viewModel.SelectActivityDateCommand.Execute(Day(secondDate));
        Check(!inconsistentSelection && Day(secondDate).IsSelected && !Day(firstDate).IsSelected,
            "지정 날짜 사이의 히트맵 이동이 선택 상태를 잘못 적용했습니다.");
        Check(notifications.Count(name => name == nameof(MainViewModel.FilterChips)) == 1 &&
              notifications.Count(name => name == nameof(MainViewModel.ActivityWeeks)) == 1,
            "지정 날짜 사이의 이동이 중복 알림을 발생시켰습니다.");
        await WaitUntilAsync(() => viewModel.Items.Count == 1 && viewModel.Items[0].DisplayName == "heatmap-second.txt");

        notifications.Clear();
        viewModel.SelectActivityDateCommand.Execute(Day(secondDate));
        await Task.Delay(300);
        Check(notifications.All(name => name is not nameof(MainViewModel.Items) and not nameof(MainViewModel.ActivityWeeks)
                  and not nameof(MainViewModel.FilterChips)),
            "같은 히트맵 날짜를 다시 클릭할 때 불필요한 갱신이 발생했습니다.");
        notifications.Clear();
        viewModel.SelectActivityDateCommand.Execute(Day(secondDate) with { CanSelect = false });
        Check(notifications.Count == 0 && viewModel.SpecificDate == secondDate,
            "선택할 수 없는 히트맵 날짜가 적용됐습니다.");

        // A rapid newer click must win over the older debounced search.
        notifications.Clear();
        expectedDate = firstDate;
        viewModel.SelectActivityDateCommand.Execute(Day(firstDate));
        expectedDate = secondDate;
        viewModel.SelectActivityDateCommand.Execute(Day(secondDate));
        await WaitUntilAsync(() => notifications.Any(name => name == nameof(MainViewModel.Items)));
        Check(!inconsistentSelection && viewModel.Items.Count == 1 && viewModel.Items[0].DisplayName == "heatmap-second.txt" &&
              viewModel.SpecificDate == secondDate && Day(secondDate).IsSelected &&
              notifications.Count(name => name == nameof(MainViewModel.Items)) == 1,
            "빠른 연속 클릭 뒤 이전 날짜가 최신 선택을 덮어썼습니다.");
    }

    private static RecentItemCandidate Candidate(string root, string name, DateTime date)
    {
        var range = HistoryDateRangeFactory.CreateLocalCalendarRange(date, date.AddDays(1), "test");
        return new RecentItemCandidate(Path.Combine(root, name), name, Path.GetExtension(name).TrimStart('.'),
            "파일", Path.Combine(root, name + ".lnk"), range.StartUtc.AddHours(12), true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("히트맵 화면 모델 갱신이 완료되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
