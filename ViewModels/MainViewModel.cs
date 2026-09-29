using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using FindHistory.Models;
using FindHistory.Services;

namespace FindHistory.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecentDatabase _database;
    private readonly RecentItemsMonitor _monitor;
    private readonly AutoStartService _autoStart;
    private readonly AppSettingsService _settings;
    private readonly AppLogService _log;
    private CancellationTokenSource? _searchCancellation;
    private long _loadSequence;
    private string _searchText = string.Empty;
    private DateRangeOption _selectedDateRange;
    private DateTime? _specificDate = DateTime.Today;
    private RecentItem? _selectedItem;
    private string _statusText = "최근 항목을 불러오는 중…";
    private string _summaryText = "기록 준비 중";
    private string _monitorStatusText = "기록 준비 중";
    private bool _isMonitorHealthy;
    private bool _autoStartEnabled;
    private bool _isBusy;
    private bool _initialized;
    private bool _disposed;
    private IReadOnlyList<RecentItem> _items = [];
    private double _lastSearchLatencyMilliseconds;

    public IReadOnlyList<RecentItem> Items
    {
        get => _items;
        private set => SetField(ref _items, value);
    }

    public IReadOnlyList<DateRangeOption> DateRanges { get; } =
    [
        new("전체 기간"),
        new("오늘", 1),
        new("최근 7일", 7),
        new("최근 30일", 30),
        new("최근 1년", 365),
        new("날짜 지정", IsSpecificDate: true)
    ];

    public MainViewModel(
        RecentDatabase database,
        RecentItemsMonitor monitor,
        AutoStartService autoStart,
        AppSettingsService settings,
        AppLogService log)
    {
        _database = database;
        _monitor = monitor;
        _autoStart = autoStart;
        _settings = settings;
        _log = log;
        _selectedDateRange = DateRanges[0];
        _autoStartEnabled = TryGetAutoStart(autoStart);

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
                OnPropertyChanged(nameof(IsSpecificDateSelected));
                ScheduleReload();
            }
        }
    }

    public bool IsSpecificDateSelected => SelectedDateRange.IsSpecificDate;

    public DateTime? SpecificDate
    {
        get => _specificDate;
        set
        {
            var normalizedDate = (value ?? DateTime.Today).Date;
            if (SetField(ref _specificDate, normalizedDate) && IsSpecificDateSelected)
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

    public string MonitorStatusText
    {
        get => _monitorStatusText;
        private set => SetField(ref _monitorStatusText, value);
    }

    public bool IsMonitorHealthy
    {
        get => _isMonitorHealthy;
        private set => SetField(ref _isMonitorHealthy, value);
    }

    public double LastSearchLatencyMilliseconds
    {
        get => _lastSearchLatencyMilliseconds;
        private set => SetField(ref _lastSearchLatencyMilliseconds, value);
    }

    public string DatabasePath => _database.DatabasePath;

    public string DatabaseDirectory => Path.GetDirectoryName(DatabasePath) ?? DatabasePath;

    public string DatabaseSizeText
    {
        get
        {
            try
            {
                var bytes = new FileInfo(DatabasePath).Length;
                return bytes >= 1024 * 1024
                    ? $"{bytes / 1024d / 1024d:N1} MB"
                    : $"{Math.Max(1, bytes / 1024d):N0} KB";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "크기 확인 불가";
            }
        }
    }

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
                _log.Error("Windows startup registration update failed.", ex);
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
        _monitor.DiagnosticsChanged += OnDiagnosticsChanged;
        IsBusy = true;
        try
        {
            await _monitor.StartAsync();
            UpdateMonitorStatus();
            await LoadAsync(CancellationToken.None);
            StatusText = "최근 항목 폴더를 실시간으로 감시하고 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> MoveDatabaseAsync(string destinationDirectory)
    {
        IsBusy = true;
        StatusText = "데이터베이스를 새 위치로 이동하는 중…";
        var previousPath = _database.DatabasePath;
        try
        {
            var destinationPath = Path.Combine(destinationDirectory, "findhistory.db");
            _settings.SetDatabasePath(destinationPath);
            await _database.MoveToAsync(destinationPath);
            NotifyDatabaseLocationChanged();
            await LoadAsync(CancellationToken.None);
            StatusText = "데이터베이스와 기존 기록을 새 위치로 이동했습니다.";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database move failed.", ex);
            if (!string.Equals(_database.DatabasePath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                await _database.MoveToAsync(previousPath);
            }

            var restored = TryRestoreSettingsPath(previousPath);
            StatusText = restored
                ? $"데이터베이스 이동 실패: {ex.Message}"
                : $"데이터베이스 이동 실패: {ex.Message} (설정 복원도 실패했습니다)";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryGetAutoStart(AutoStartService autoStart)
    {
        try
        {
            return autoStart.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryRestoreSettingsPath(string path)
    {
        try
        {
            _settings.SetDatabasePath(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> UseDatabaseAsync(string databasePath)
    {
        IsBusy = true;
        StatusText = "선택한 데이터베이스를 확인하는 중…";
        var previousPath = _database.DatabasePath;
        var settingsUpdateAttempted = false;
        try
        {
            await _database.UseAsync(databasePath);
            settingsUpdateAttempted = true;
            _settings.SetDatabasePath(_database.DatabasePath);
            NotifyDatabaseLocationChanged();
            await LoadAsync(CancellationToken.None);
            StatusText = "선택한 데이터베이스를 사용합니다.";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database switch failed.", ex);
            var restored = true;
            if (!string.Equals(_database.DatabasePath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _database.UseAsync(previousPath);
                }
                catch (Exception)
                {
                    restored = false;
                }
            }

            if (settingsUpdateAttempted)
            {
                restored &= TryRestoreSettingsPath(previousPath);
            }
            StatusText = restored
                ? $"데이터베이스를 열 수 없습니다: {ex.Message}"
                : $"데이터베이스를 열 수 없습니다: {ex.Message} (이전 설정 복원도 실패했습니다)";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenDatabaseFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{DatabasePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("Opening the database folder failed.", ex);
            StatusText = $"데이터 폴더를 열 수 없습니다: {ex.Message}";
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.LogDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("Opening the log folder failed.", ex);
            StatusText = $"로그 폴더를 열 수 없습니다: {ex.Message}";
        }
    }

    public async Task<string> BuildDiagnosticsReportAsync(
        CancellationToken cancellationToken = default)
    {
        var monitor = _monitor.GetDiagnosticsSnapshot();
        var database = await _database.GetDiagnosticsAsync(cancellationToken);
        var assembly = Assembly.GetEntryAssembly() ?? typeof(MainViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "알 수 없음";
        var databaseBytes = TryGetFileSize(DatabasePath);
        var builder = new StringBuilder();
        builder.AppendLine("FindHistory 진단 정보")
            .AppendLine($"생성 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"앱 버전: {version}")
            .AppendLine($"운영체제: {RuntimeInformation.OSDescription}")
            .AppendLine($"프로세스: {RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}")
            .AppendLine()
            .AppendLine("[최근 항목 감시]")
            .AppendLine($"상태: {FormatMonitorState(monitor)}")
            .AppendLine($"감시 폴더: {monitor.RecentFolder}")
            .AppendLine($"시작 시각: {FormatLocalTime(monitor.StartedUtc)}")
            .AppendLine($"마지막 전체 스캔: {FormatLocalTime(monitor.LastScanCompletedUtc)}")
            .AppendLine($"마지막 스캔 항목: {monitor.LastScanItemCount:N0}개")
            .AppendLine($"마지막 스캔 시간: {FormatDuration(monitor.LastScanDuration)}")
            .AppendLine($"마지막 실시간 기록: {FormatLocalTime(monitor.LastCaptureUtc)}")
            .AppendLine($"세션 실시간 기록: {monitor.SessionCaptureCount:N0}개")
            .AppendLine($"감시 복구 횟수: {monitor.WatcherRecoveryCount:N0}회")
            .AppendLine($"진행 중 작업: {monitor.PendingTaskCount:N0}개")
            .AppendLine($"마지막 오류: {FormatError(monitor)}")
            .AppendLine()
            .AppendLine("[데이터베이스]")
            .AppendLine($"경로: {DatabasePath}")
            .AppendLine($"파일 크기: {FormatBytes(databaseBytes)}")
            .AppendLine($"고유 항목: {database.UniqueItems:N0}개")
            .AppendLine($"누적 열기 횟수: {database.TotalOpenCount:N0}회")
            .AppendLine($"저장된 날짜 이벤트: {database.StoredEvents:N0}개")
            .AppendLine($"추정 날짜 이벤트: {database.EstimatedEvents:N0}개")
            .AppendLine()
            .AppendLine("[설정]")
            .AppendLine($"로그인 시 자동 실행: {(AutoStartEnabled ? "사용" : "사용 안 함")}")
            .AppendLine($"로그 폴더: {_log.LogDirectory}");
        return builder.ToString().TrimEnd();
    }

    private void NotifyDatabaseLocationChanged()
    {
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(DatabaseDirectory));
        OnPropertyChanged(nameof(DatabaseSizeText));
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "최근 항목을 다시 확인하는 중…";
        try
        {
            var captured = await _monitor.ScanAsync(notifyChanges: false);
            await LoadAsync(CancellationToken.None);
            StatusText = $"최근 항목 {captured:N0}개를 확인했습니다.";
        }
        catch (Exception ex)
        {
            _log.Error("Manual Recent Items refresh failed.", ex);
            StatusText = $"새로고침 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ScheduleReload()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _loadSequence);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var requestStartedTimestamp = Stopwatch.GetTimestamp();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140, token);
                await System.Windows.Application.Current.Dispatcher
                    .InvokeAsync(() => LoadAsync(token, requestStartedTimestamp)).Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
                // A newer search superseded this one.
            }
        }, token);
    }

    private async Task LoadAsync(
        CancellationToken cancellationToken,
        long? requestStartedTimestamp = null)
    {
        var loadStartedTimestamp = Stopwatch.GetTimestamp();
        var sequence = Interlocked.Increment(ref _loadSequence);
        var selectedId = SelectedItem?.Id;
        var searchText = SearchText;
        var dateRange = BuildSelectedDateRange();
        var snapshot = await Task.Run(
            () => _database.SearchWithStatsAsync(searchText, dateRange, cancellationToken: cancellationToken),
            cancellationToken);

        if (sequence != Volatile.Read(ref _loadSequence))
        {
            return;
        }

        // ItemsSource 자체를 한 번 교체해 1,000개 행에서 발생하던 개별 CollectionChanged를 제거한다.
        Items = snapshot.Items;

        if (selectedId is { } id)
        {
            SelectedItem = Items.FirstOrDefault(item => item.Id == id);
        }

        var latencyStartedTimestamp = requestStartedTimestamp ?? loadStartedTimestamp;
        LastSearchLatencyMilliseconds = Stopwatch.GetElapsedTime(latencyStartedTimestamp).TotalMilliseconds;
        if (dateRange is null)
        {
            SummaryText = $"{snapshot.Items.Count:N0}개 결과  ·  {snapshot.Stats.UniqueItems:N0}개 항목  ·  " +
                          $"누적 {snapshot.Stats.TotalOpenCount:N0}회";
        }
        else
        {
            var rangeOpenCount = snapshot.Items.Sum(item => item.OpenCount);
            var estimatedText = snapshot.Items.Any(item => item.IsEstimatedHistory)
                ? "  ·  이전 기록 일부 추정"
                : string.Empty;
            SummaryText = $"{snapshot.Items.Count:N0}개 결과  ·  {dateRange.Label} {rangeOpenCount:N0}회" +
                          estimatedText;
        }
    }

    private HistoryDateRange? BuildSelectedDateRange()
    {
        if (SelectedDateRange.IsSpecificDate)
        {
            if (SpecificDate is not { } selectedDate)
            {
                return null;
            }

            var start = selectedDate.Date;
            return HistoryDateRangeFactory.CreateLocalCalendarRange(
                start, start.AddDays(1), start.ToString("yyyy-MM-dd"));
        }

        if (SelectedDateRange.CalendarDayCount is not { } days)
        {
            return null;
        }

        var today = DateTime.Today;
        var startDate = today.AddDays(-(days - 1));
        var label = days == 1 ? "오늘" : SelectedDateRange.Label;
        return HistoryDateRangeFactory.CreateLocalCalendarRange(
            startDate, today.AddDays(1), label);
    }

    private void OnHistoryChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(ScheduleReload);

    private void OnMonitorError(object? sender, string message)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => StatusText = $"감시 오류: {message}");
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdateMonitorStatus);

    private void UpdateMonitorStatus()
    {
        var diagnostics = _monitor.GetDiagnosticsSnapshot();
        IsMonitorHealthy = diagnostics.IsStarted &&
                           !diagnostics.IsStopping &&
                           diagnostics.IsWatcherActive;
        MonitorStatusText = diagnostics.IsStopping
            ? "기록 종료 중"
            : IsMonitorHealthy
                ? "백그라운드 기록 중"
                : diagnostics.IsStarted
                    ? "감시 복구 필요"
                    : "기록 준비 중";
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
            _log.Error("Opening a recent item failed.", ex);
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
            _log.Error("Revealing a recent item failed.", ex);
            StatusText = $"위치를 열 수 없습니다: {ex.Message}";
        }
    }

    private static string FormatMonitorState(MonitorDiagnostics monitor)
    {
        if (monitor.IsStopping)
        {
            return "종료 중";
        }
        if (!monitor.IsStarted)
        {
            return "시작 전";
        }
        return monitor.IsWatcherActive ? "정상 감시 중" : "감시기 비활성";
    }

    private static string FormatLocalTime(DateTimeOffset? utc) =>
        utc is null ? "기록 없음" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null ? "기록 없음" : $"{duration.Value.TotalMilliseconds:N0} ms";

    private static string FormatError(MonitorDiagnostics monitor) =>
        monitor.LastErrorUtc is null
            ? "없음"
            : $"{FormatLocalTime(monitor.LastErrorUtc)} · {monitor.LastErrorMessage}";

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "확인 불가";
        }
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes.Value / 1024d / 1024d / 1024d:N2} GB";
        }
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes.Value / 1024d / 1024d:N1} MB";
        }
        return $"{Math.Max(1, bytes.Value / 1024d):N0} KB";
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.HistoryChanged -= OnHistoryChanged;
        _monitor.MonitorError -= OnMonitorError;
        _monitor.DiagnosticsChanged -= OnDiagnosticsChanged;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }
}
