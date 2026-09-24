using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using FindHistory.Models;
using FindHistory.Services;

namespace FindHistory.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly RecentDatabase _database;
    private readonly RecentItemsMonitor _monitor;
    private readonly AutoStartService _autoStart;
    private CancellationTokenSource? _searchCancellation;
    private string _searchText = string.Empty;
    private DateRangeOption _selectedDateRange;
    private RecentItem? _selectedItem;
    private string _statusText = "최근 항목을 불러오는 중…";
    private string _summaryText = "기록 준비 중";
    private bool _autoStartEnabled;
    private bool _isBusy;
    private bool _initialized;

    public ObservableCollection<RecentItem> Items { get; } = [];

    public IReadOnlyList<DateRangeOption> DateRanges { get; } =
    [
        new("전체 기간", null),
        new("오늘", TimeSpan.FromDays(1)),
        new("최근 7일", TimeSpan.FromDays(7)),
        new("최근 30일", TimeSpan.FromDays(30)),
        new("최근 1년", TimeSpan.FromDays(365))
    ];

    public MainViewModel(RecentDatabase database, RecentItemsMonitor monitor, AutoStartService autoStart)
    {
        _database = database;
        _monitor = monitor;
        _autoStart = autoStart;
        _selectedDateRange = DateRanges[0];
        _autoStartEnabled = autoStart.IsEnabled;

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        OpenCommand = new RelayCommand(OpenSelected, () => SelectedItem is not null);
        RevealCommand = new RelayCommand(RevealSelected, () => SelectedItem is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => SearchText.Length > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ClearSearchCommand.RaiseCanExecuteChanged();
                ScheduleReload();
            }
        }
    }

    public DateRangeOption SelectedDateRange
    {
        get => _selectedDateRange;
        set
        {
            if (value is not null && SetField(ref _selectedDateRange, value))
            {
                ScheduleReload();
            }
        }
    }

    public RecentItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetField(ref _selectedItem, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
                RevealCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public string DatabasePath => AppPaths.DatabasePath;

    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set
        {
            if (_autoStartEnabled == value)
            {
                return;
            }

            try
            {
                _autoStart.SetEnabled(value);
                SetField(ref _autoStartEnabled, value);
                StatusText = value
                    ? "Windows 시작 시 백그라운드 실행이 켜졌습니다."
                    : "Windows 시작 시 실행이 꺼졌습니다.";
            }
            catch (Exception ex)
            {
                StatusText = $"시작 프로그램 설정 실패: {ex.Message}";
                OnPropertyChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        _monitor.HistoryChanged += OnHistoryChanged;
        _monitor.MonitorError += OnMonitorError;
        IsBusy = true;
        try
        {
            await _monitor.StartAsync();
            await LoadAsync(CancellationToken.None);
            StatusText = "최근 항목 폴더를 실시간으로 감시하고 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "최근 항목을 다시 확인하는 중…";
        try
        {
            var captured = await _monitor.ScanAsync();
            await LoadAsync(CancellationToken.None);
            StatusText = $"최근 항목 {captured:N0}개를 확인했습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"새로고침 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ScheduleReload()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => LoadAsync(token)).Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
                // A newer search superseded this one.
            }
        }, token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var since = SelectedDateRange.Duration is { } duration
            ? DateTimeOffset.Now.Subtract(duration)
            : (DateTimeOffset?)null;
        var items = await _database.SearchAsync(SearchText, since, cancellationToken: cancellationToken);
        var stats = await _database.GetStatsAsync(cancellationToken);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        SummaryText = $"{items.Count:N0}개 결과  ·  {stats.UniqueItems:N0}개 항목  ·  누적 {stats.TotalOpenCount:N0}회";
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => ScheduleReload();

    private void OnMonitorError(object? sender, string message)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => StatusText = $"감시 오류: {message}");
    }

    private void OpenSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(SelectedItem.TargetPath) { UseShellExecute = true });
            StatusText = $"{SelectedItem.DisplayName} 열기";
        }
        catch (Exception ex)
        {
            StatusText = $"열 수 없습니다: {ex.Message}";
        }
    }

    private void RevealSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(SelectedItem.TargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedItem.TargetPath}\"") { UseShellExecute = true });
            }
            else if (File.Exists(SelectedItem.TargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedItem.TargetPath}\"") { UseShellExecute = true });
            }
            else
            {
                StatusText = "파일 위치를 찾을 수 없습니다.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"위치를 열 수 없습니다: {ex.Message}";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
