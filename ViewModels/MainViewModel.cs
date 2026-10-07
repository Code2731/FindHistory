using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using FindHistory.Models;
using FindHistory.Services;
using FindHistory.Localization;

namespace FindHistory.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RecentDatabase _database;
    private readonly RecentItemsMonitor _monitor;
    private readonly AutoStartService _autoStart;
    private readonly AppSettingsService _settings;
    private readonly AppLogService _log;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _activityCancellation;
    private long _loadSequence;
    private long _activityLoadSequence;
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
    private IReadOnlyList<ActivityWeek> _activityWeeks = [];
    private string _activityPeriodText = "최근 활동 준비 중";
    private string _activitySummaryText = string.Empty;
    private double _lastSearchLatencyMilliseconds;
    private string _extensionFilter = string.Empty;
    private string _folderFilter = string.Empty;
    private ExistenceOption _selectedExistence;
    private IReadOnlyList<ExistenceOption> _existenceOptions = BuildExistenceOptions();
    private IReadOnlyList<DateRangeOption> _dateRanges = BuildDateRanges();
    private IReadOnlyList<SavedSearch> _savedSearches;
    private SavedSearch? _selectedSavedSearch;
    private bool _isApplyingSavedSearch;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        [new("ko", "한국어"), new("en", "English")];

    public string SelectedLanguage
    {
        get => _settings.Language;
        set
        {
            if (string.Equals(value, _settings.Language, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                _settings.SetLanguage(value);
                LocalizationManager.Instance.SetLanguage(_settings.Language);
                StatusText = "언어를 변경했습니다.";
            }
            catch (Exception ex)
            {
                _log.Error("Saving the language setting failed.", ex);
                StatusText = $"언어 설정 저장 실패: {ex.Message}";
                OnPropertyChanged();
            }
        }
    }

    public record LanguageOption(string Code, string Label);

    public IReadOnlyList<ExistenceOption> ExistenceOptions
    {
        get => _existenceOptions;
        private set => SetField(ref _existenceOptions, value);
    }

    public string ExtensionFilter
    {
        get => _extensionFilter;
        set { if (SetField(ref _extensionFilter, value)) { ClearSavedSearchSelection(); OnPropertyChanged(nameof(FilterChips)); ScheduleReload(); } }
    }

    public string FolderFilter
    {
        get => _folderFilter;
        private set { if (SetField(ref _folderFilter, value)) { ClearSavedSearchSelection(); OnPropertyChanged(nameof(FilterChips)); ScheduleReload(); } }
    }

    public ExistenceOption SelectedExistence
    {
        get => _selectedExistence;
        set { if (value is not null && SetField(ref _selectedExistence, value)) { ClearSavedSearchSelection(); OnPropertyChanged(nameof(FilterChips)); ScheduleReload(); } }
    }

    public IReadOnlyList<SavedSearch> SavedSearches
    {
        get => _savedSearches;
        private set => SetField(ref _savedSearches, value);
    }

    public SavedSearch? SelectedSavedSearch
    {
        get => _selectedSavedSearch;
        set
        {
            if (value is null)
            {
                if (SetField(ref _selectedSavedSearch, null)) RemoveSavedSearchCommand.RaiseCanExecuteChanged();
                return;
            }
            if (!SetField(ref _selectedSavedSearch, value)) return;
            RemoveSavedSearchCommand.RaiseCanExecuteChanged();
            _isApplyingSavedSearch = true;
            try
            {
                SearchText = value.SearchText;
                ExtensionFilter = value.Extension;
                SelectedExistence = ExistenceOptions.First(option => option.Exists == value.Exists);
                FolderFilter = value.Folder;
                if (value.IsSpecificDate)
                {
                    SpecificDate = value.SpecificDate ?? DateTime.Today;
                }
                SelectedDateRange = SavedSearchDateRangeResolver.Resolve(value, DateRanges);
            }
            finally
            {
                _isApplyingSavedSearch = false;
            }
        }
    }

    public IReadOnlyList<FilterChip> FilterChips
    {
        get
        {
            var chips = new List<FilterChip>();
            if (!string.IsNullOrWhiteSpace(ExtensionFilter)) chips.Add(new("extension", $"확장자: {ExtensionFilter.Trim()} ×"));
            if (SelectedExistence.Exists is not null) chips.Add(new("exists", $"상태: {SelectedExistence.Label} ×"));
            if (FolderFilter.Length > 0) chips.Add(new("folder", $"폴더: {FolderFilter} ×"));
            if (SelectedDateRange != DateRanges[0]) chips.Add(new("date", $"기간: {BuildSelectedDateRange()?.Label} ×"));
            return chips;
        }
    }

    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand<FilterChip> RemoveFilterCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand SaveSearchCommand { get; }
    public RelayCommand RemoveSavedSearchCommand { get; }

    private void ChooseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "이 폴더와 하위 폴더의 기록을 검색합니다",
            UseDescriptionForTitle = true,
            SelectedPath = FolderFilter
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) FolderFilter = dialog.SelectedPath;
    }

    private void SaveCurrentSearch()
    {
        var dialog = new SaveSearchDialog(SavedSearches.Select(search => search.Name).ToArray())
            { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var savedSearch = new SavedSearch(
            Guid.NewGuid(), dialog.SearchName, SearchText, ExtensionFilter, SelectedExistence.Exists,
            FolderFilter, SelectedDateRange.IsSpecificDate ? null : SelectedDateRange.CalendarDayCount,
            SelectedDateRange.IsSpecificDate ? SpecificDate : null, SelectedDateRange.IsSpecificDate);
        try
        {
            if (!_settings.AddSavedSearch(savedSearch))
            {
                StatusText = $"저장 검색은 최대 {AppSettingsService.MaxSavedSearches}개까지 보관할 수 있습니다.";
                return;
            }

            SavedSearches = _settings.SavedSearches.ToArray();
            SelectedSavedSearch = savedSearch;
            StatusText = $"'{savedSearch.Name}' 검색을 저장했습니다.";
        }
        catch (Exception ex)
        {
            _log.Error("Saving a search preset failed.", ex);
            StatusText = $"검색 저장 실패: {ex.Message}";
        }
    }

    private void RemoveSelectedSavedSearch()
    {
        if (SelectedSavedSearch is not { } selected) return;
        try
        {
            _settings.RemoveSavedSearch(selected.Id);
            SavedSearches = _settings.SavedSearches.ToArray();
            SelectedSavedSearch = null;
            StatusText = $"'{selected.Name}' 저장 검색을 삭제했습니다.";
        }
        catch (Exception ex)
        {
            _log.Error("Removing a saved search failed.", ex);
            StatusText = $"저장 검색 삭제 실패: {ex.Message}";
        }
    }

    private void ClearSavedSearchSelection()
    {
        if (!_isApplyingSavedSearch && SelectedSavedSearch is not null)
        {
            SelectedSavedSearch = null;
        }
    }

    private void RemoveFilter(FilterChip chip)
    {
        switch (chip.Key)
        {
            case "extension": ExtensionFilter = string.Empty; break;
            case "exists": SelectedExistence = ExistenceOptions[0]; break;
            case "folder": FolderFilter = string.Empty; break;
            case "date": SelectedDateRange = DateRanges[0]; break;
        }
    }

    public IReadOnlyList<RecentItem> Items
    {
        get => _items;
        private set => SetField(ref _items, value);
    }

    public IReadOnlyList<ActivityWeek> ActivityWeeks
    {
        get => _activityWeeks;
        private set => SetField(ref _activityWeeks, value);
    }

    public string ActivityPeriodText
    {
        get => _activityPeriodText;
        private set => SetField(ref _activityPeriodText, value);
    }

    public string ActivitySummaryText
    {
        get => _activitySummaryText;
        private set => SetField(ref _activitySummaryText, value);
    }

    public IReadOnlyList<DateRangeOption> DateRanges
    {
        get => _dateRanges;
        private set => SetField(ref _dateRanges, value);
    }

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
        LocalizationManager.Instance.SetLanguage(_settings.Language);
        DateRanges = BuildDateRanges();
        ExistenceOptions = BuildExistenceOptions();
        LocalizationManager.Instance.PropertyChanged += OnLanguageChanged;
        _selectedDateRange = DateRanges[0];
        _selectedExistence = ExistenceOptions[0];
        _savedSearches = _settings.SavedSearches.ToArray();
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        RemoveFilterCommand = new RelayCommand<FilterChip>(RemoveFilter);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            ExtensionFilter = string.Empty;
            FolderFilter = string.Empty;
            SelectedExistence = ExistenceOptions[0];
            SelectedDateRange = DateRanges[0];
        });
        SaveSearchCommand = new RelayCommand(SaveCurrentSearch);
        RemoveSavedSearchCommand = new RelayCommand(RemoveSelectedSavedSearch,
            () => SelectedSavedSearch is not null);
        _autoStartEnabled = TryGetAutoStart(autoStart);

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        OpenCommand = new RelayCommand(OpenSelected, () => SelectedItem is not null);
        RevealCommand = new RelayCommand(RevealSelected, () => SelectedItem is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => SearchText.Length > 0);
        SelectActivityDateCommand = new RelayCommand<ActivityDay>(SelectActivityDate,
            day => day.CanSelect);
    }

    private static IReadOnlyList<ExistenceOption> BuildExistenceOptions() =>
    [
        new(LocalizationManager.Instance.Translate("전체 상태"), null),
        new(LocalizationManager.Instance.Translate("존재함 (저장된 상태)"), true),
        new(LocalizationManager.Instance.Translate("찾을 수 없음"), false)
    ];

    private static IReadOnlyList<DateRangeOption> BuildDateRanges() =>
    [
        new(LocalizationManager.Instance.Translate("전체 기간")),
        new(LocalizationManager.Instance.Translate("오늘"), 1),
        new(LocalizationManager.Instance.Translate("최근 7일"), 7),
        new(LocalizationManager.Instance.Translate("최근 30일"), 30),
        new(LocalizationManager.Instance.Translate("최근 1년"), 365),
        new(LocalizationManager.Instance.Translate("날짜 지정"), IsSpecificDate: true)
    ];

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.Language)) return;

        var selectedDays = SelectedDateRange.CalendarDayCount;
        var selectedSpecificDate = SelectedDateRange.IsSpecificDate;
        var selectedExists = SelectedExistence.Exists;
        DateRanges = BuildDateRanges();
        ExistenceOptions = BuildExistenceOptions();
        _selectedDateRange = DateRanges.First(option =>
            option.CalendarDayCount == selectedDays && option.IsSpecificDate == selectedSpecificDate);
        _selectedExistence = ExistenceOptions.First(option => option.Exists == selectedExists);
        OnPropertyChanged(nameof(SelectedDateRange));
        OnPropertyChanged(nameof(SelectedExistence));
        OnPropertyChanged(nameof(IsSpecificDateSelected));
        OnPropertyChanged(nameof(FilterChips));
        Items = Items.ToArray();
        ActivityWeeks = ActivityWeeks.ToArray();
        OnPropertyChanged(nameof(ActivityPeriodText));
        OnPropertyChanged(nameof(ActivitySummaryText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(MonitorStatusText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SelectedLanguage));
        if (_initialized)
        {
            ScheduleReload();
            ScheduleActivityReload();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand<ActivityDay> SelectActivityDateCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ClearSearchCommand.RaiseCanExecuteChanged();
            ClearSavedSearchSelection();
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
                OnPropertyChanged(nameof(FilterChips));
                ClearSavedSearchSelection();
                UpdateActivitySelection();
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
            if (SetField(ref _specificDate, normalizedDate))
            {
                ClearSavedSearchSelection();
                OnPropertyChanged(nameof(FilterChips));
                UpdateActivitySelection();
                if (IsSpecificDateSelected)
                {
                    ScheduleReload();
                }
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
        get => LocalizationManager.Instance.Translate(_statusText);
        private set => SetField(ref _statusText, value);
    }

    public string SummaryText
    {
        get => LocalizationManager.Instance.Translate(_summaryText);
        private set => SetField(ref _summaryText, value);
    }

    public string MonitorStatusText
    {
        get => LocalizationManager.Instance.Translate(_monitorStatusText);
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

    public string AutoBackupDirectory => AppPaths.AutoBackupDirectory;
    public IReadOnlyList<int> AutoBackupRetentionOptions { get; } = Enumerable.Range(1, 30).ToArray();

    public bool AutoBackupEnabled
    {
        get => _settings.AutoBackupEnabled;
        set => SaveAutoBackupSettings(value, AutoBackupRetention);
    }

    public int AutoBackupRetention
    {
        get => _settings.AutoBackupRetention;
        set => SaveAutoBackupSettings(AutoBackupEnabled, value);
    }

    private void SaveAutoBackupSettings(bool enabled, int retention)
    {
        try
        {
            _settings.SetAutoBackup(enabled, retention);
            StatusText = LocalizationManager.Instance.Format(
                "자동 백업 설정을 저장했습니다.", "Automatic backup settings saved.");
        }
        catch (Exception ex)
        {
            _log.Error("Saving automatic backup settings failed.", ex);
            StatusText = LocalizationManager.Instance.Format(
                "자동 백업 설정 저장 실패: {0}", "Saving automatic backup settings failed: {0}", ex.Message);
            System.Windows.MessageBox.Show(StatusText, LocalizationManager.Instance.Translate("설정"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        OnPropertyChanged(nameof(AutoBackupEnabled));
        OnPropertyChanged(nameof(AutoBackupRetention));
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
            await LoadActivityAsync(CancellationToken.None);
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
        try
        {
            var destinationPath = Path.Combine(destinationDirectory, "findhistory.db");
            var previousDatabaseRemoved = await _database.MoveToAsync(destinationPath, _settings.SetDatabasePath);
            NotifyDatabaseLocationChanged();
            try
            {
                await LoadAsync(CancellationToken.None);
                await LoadActivityAsync(CancellationToken.None);
            }
            catch (Exception refreshException)
            {
                _log.Error("Database moved, but the UI refresh failed.", refreshException);
                StatusText = $"DB를 새 위치로 옮겼지만 화면 갱신에 실패했습니다: {refreshException.Message}";
                return true;
            }

            StatusText = previousDatabaseRemoved
                ? "데이터베이스와 기존 기록을 새 위치로 이동했습니다."
                : "데이터베이스 이동은 완료했지만 이전 위치의 파일을 정리하지 못했습니다. 이전 DB 사본은 보존되어 있습니다.";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database move failed.", ex);
            var actualPath = _database.DatabasePath;
            var settingsRestored = TryRestoreSettingsPath(actualPath);
            NotifyDatabaseLocationChanged();
            var recoveryDetails = new List<string>();
            if (!settingsRestored)
            {
                recoveryDetails.Add("DB 경로 설정 저장 실패");
            }
            StatusText = $"데이터베이스 이동 실패: {ex.Message}" +
                         (recoveryDetails.Count == 0
                             ? string.Empty
                             : $" ({string.Join("; ", recoveryDetails)}). 현재 DB 경로: {actualPath}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> BackupDatabaseAsync(string destinationPath)
    {
        IsBusy = true;
        StatusText = "데이터베이스를 백업하는 중…";
        try
        {
            await _database.BackupToAsync(destinationPath);
            StatusText = $"백업을 저장했습니다: {destinationPath}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database backup failed.", ex);
            StatusText = $"백업에 실패했습니다: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> RestoreDatabaseAsync(string backupPath)
    {
        IsBusy = true;
        StatusText = "백업을 확인하고 복원하는 중…";
        try
        {
            var safetyPath = await _database.RestoreFromAsync(backupPath);
            await LoadAsync(CancellationToken.None);
            await LoadActivityAsync(CancellationToken.None);
            OnPropertyChanged(nameof(DatabaseSizeText));
            StatusText = $"백업을 복원했습니다. 복원 전 DB 사본: {safetyPath}";
            return safetyPath;
        }
        catch (Exception ex)
        {
            _log.Error("Database restore failed.", ex);
            StatusText = $"복원에 실패했습니다: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ExportDatabaseAsync(string destinationPath, string format)
    {
        IsBusy = true;
        StatusText = $"전체 기록을 {format.ToUpperInvariant()} 파일로 내보내는 중…";
        try
        {
            await _database.ExportToAsync(destinationPath, format);
            StatusText = $"전체 기록을 내보냈습니다: {destinationPath}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Database {format} export failed.", ex);
            StatusText = $"내보내기에 실패했습니다: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ExportCurrentResultsAsync(string destinationPath, string format)
    {
        var itemIds = Items.Select(item => item.Id).Distinct().ToArray();
        if (itemIds.Length == 0)
        {
            StatusText = "현재 표시된 검색 결과가 없습니다.";
            return false;
        }

        IsBusy = true;
        StatusText = "현재 검색 결과를 내보내는 중…";
        try
        {
            await _database.ExportItemsToAsync(destinationPath, format, itemIds);
            StatusText = LocalizationManager.Instance.Format(
                "{0:N0}개 검색 결과를 내보냈습니다: {1}",
                "Exported {0:N0} search results to: {1}", itemIds.Length, destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Search result {format} export failed.", ex);
            StatusText = $"검색 결과 내보내기 실패: {ex.Message}";
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
        try
        {
            await _database.UseAsync(databasePath);
            _settings.SetDatabasePath(_database.DatabasePath);
            NotifyDatabaseLocationChanged();
            await LoadAsync(CancellationToken.None);
            await LoadActivityAsync(CancellationToken.None);
            StatusText = "선택한 데이터베이스를 사용합니다.";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database switch failed.", ex);
            string? rollbackError = null;
            if (!string.Equals(_database.DatabasePath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _database.UseAsync(previousPath);
                }
                catch (Exception rollbackException)
                {
                    rollbackError = rollbackException.Message;
                    _log.Error("Database switch rollback failed.", rollbackException);
                }
            }

            var actualPath = _database.DatabasePath;
            var settingsRestored = TryRestoreSettingsPath(actualPath);
            NotifyDatabaseLocationChanged();
            var recoveryDetails = new List<string>();
            if (rollbackError is not null)
            {
                recoveryDetails.Add($"DB 원복 실패: {rollbackError}");
            }
            if (!settingsRestored)
            {
                recoveryDetails.Add("DB 경로 설정 저장 실패");
            }
            StatusText = $"데이터베이스를 열 수 없습니다: {ex.Message}" +
                         (recoveryDetails.Count == 0
                             ? string.Empty
                             : $" ({string.Join("; ", recoveryDetails)}). 현재 DB 경로: {actualPath}");
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
        var isEnglish = LocalizationManager.Instance.Language == "en";
        string Label(string korean) => LocalizationManager.Instance.Translate(korean);
        var builder = new StringBuilder();
        builder.AppendLine(isEnglish ? "FindHistory Diagnostics" : "FindHistory 진단 정보")
            .AppendLine($"{Label("생성 시각")}: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"{Label("앱 버전")}: {version}")
            .AppendLine($"{Label("운영체제")}: {RuntimeInformation.OSDescription}")
            .AppendLine($"{Label("프로세스")}: {RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}")
            .AppendLine()
            .AppendLine(isEnglish ? "[Recent Items Monitor]" : "[최근 항목 감시]")
            .AppendLine($"{Label("상태")}: {FormatMonitorState(monitor)}")
            .AppendLine($"{Label("감시 폴더")}: {monitor.RecentFolder}")
            .AppendLine($"{Label("시작 시각")}: {FormatLocalTime(monitor.StartedUtc)}")
            .AppendLine($"{Label("마지막 전체 스캔")}: {FormatLocalTime(monitor.LastScanCompletedUtc)}")
            .AppendLine($"{Label("마지막 스캔 항목")}: {monitor.LastScanItemCount:N0} {(isEnglish ? "items" : "개")}")
            .AppendLine($"{Label("마지막 스캔 시간")}: {FormatDuration(monitor.LastScanDuration)}")
            .AppendLine($"{Label("마지막 실시간 기록")}: {FormatLocalTime(monitor.LastCaptureUtc)}")
            .AppendLine($"{Label("세션 실시간 기록")}: {monitor.SessionCaptureCount:N0} {(isEnglish ? "captures" : "개")}")
            .AppendLine($"{Label("감시 복구 횟수")}: {monitor.WatcherRecoveryCount:N0} {(isEnglish ? "times" : "회")}")
            .AppendLine($"{Label("진행 중 작업")}: {monitor.PendingTaskCount:N0}")
            .AppendLine($"{Label("마지막 오류")}: {FormatError(monitor)}")
            .AppendLine()
            .AppendLine(isEnglish ? "[Database]" : "[데이터베이스]")
            .AppendLine($"{Label("경로")}: {DatabasePath}")
            .AppendLine($"{Label("파일 크기")}: {FormatBytes(databaseBytes)}")
            .AppendLine($"{Label("고유 항목")}: {database.UniqueItems:N0} {(isEnglish ? "items" : "개")}")
            .AppendLine($"{Label("누적 열기 횟수")}: {database.TotalOpenCount:N0} {(isEnglish ? "opens" : "회")}")
            .AppendLine($"{Label("저장된 날짜 이벤트")}: {database.StoredEvents:N0} {(isEnglish ? "events" : "개")}")
            .AppendLine($"{Label("추정 날짜 이벤트")}: {database.EstimatedEvents:N0} {(isEnglish ? "events" : "개")}")
            .AppendLine()
            .AppendLine(isEnglish ? "[Settings]" : "[설정]")
            .AppendLine($"{Label("로그인 시 자동 실행")}: {Label(AutoStartEnabled ? "사용" : "사용 안 함")}")
            .AppendLine($"{Label("로그 폴더")}: {_log.LogDirectory}");
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
            await LoadActivityAsync(CancellationToken.None);
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
            catch (Exception ex)
            {
                _log.Error("Scheduled search refresh failed.", ex);
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    return;
                }

                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed && !token.IsCancellationRequested)
                        {
                            StatusText = $"검색 실패: {ex.Message}";
                        }
                    }).Task;
                }
                catch (Exception dispatchException)
                {
                    _log.Error("Could not report the search refresh failure to the UI.", dispatchException);
                }
            }
        }, token);
    }

    private void ScheduleActivityReload()
    {
        if (_disposed)
        {
            return;
        }

        _activityCancellation?.Cancel();
        _activityCancellation?.Dispose();
        _activityCancellation = new CancellationTokenSource();
        var token = _activityCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                await System.Windows.Application.Current.Dispatcher
                    .InvokeAsync(() => LoadActivityAsync(token)).Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
                // 여러 파일 감시 이벤트를 하나의 활동 집계 갱신으로 합친다.
            }
            catch (Exception ex)
            {
                _log.Error("Activity heatmap refresh failed.", ex);
            }
        }, token);
    }

    private async Task LoadActivityAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _activityLoadSequence);
        const int weekCount = 16;
        var today = DateTime.Today;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var firstMonday = today.AddDays(-daysSinceMonday - ((weekCount - 1) * 7));
        var range = HistoryDateRangeFactory.CreateLocalCalendarRange(
            firstMonday, today.AddDays(1), "최근 16주");
        var activity = await Task.Run(() => _database.GetDailyActivityAsync(
            range, cancellationToken: cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (sequence != Volatile.Read(ref _activityLoadSequence) || _disposed)
        {
            return;
        }

        var byDate = activity.ToDictionary(day => day.Date.Date);
        var maximumCount = activity.Count == 0 ? 0 : activity.Max(day => day.OpenCount);
        var selectedDate = IsSpecificDateSelected ? SpecificDate?.Date : null;
        var weeks = new List<ActivityWeek>(weekCount);
        for (var weekIndex = 0; weekIndex < weekCount; weekIndex++)
        {
            var days = new List<ActivityDay>(7);
            for (var dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                var date = firstMonday.AddDays((weekIndex * 7) + dayIndex);
                byDate.TryGetValue(date, out var daily);
                var openCount = daily?.OpenCount ?? 0;
                days.Add(new ActivityDay(
                    date,
                    openCount,
                    ActivityLevelCalculator.Calculate(openCount, maximumCount),
                    daily?.ContainsEstimated ?? false,
                    date == today,
                    selectedDate == date,
                    date <= today));
            }

            weeks.Add(new ActivityWeek(days));
        }

        ActivityWeeks = weeks;
        ActivityPeriodText = $"{firstMonday:yyyy.MM.dd} – {today:yyyy.MM.dd}";
        ActivitySummaryText = LocalizationManager.Instance.Format(
            "{0:N0}회 · 활동 {1:N0}일", "{0:N0} opens · {1:N0} active days",
            activity.Sum(day => day.OpenCount), activity.Count);
    }

    private void SelectActivityDate(ActivityDay day)
    {
        if (!day.CanSelect)
        {
            return;
        }

        SpecificDate = day.Date;
        SelectedDateRange = DateRanges.First(option => option.IsSpecificDate);
        StatusText = $"{day.Date:yyyy-MM-dd}에 열었던 파일을 표시합니다.";
    }

    private void UpdateActivitySelection()
    {
        if (ActivityWeeks.Count == 0)
        {
            return;
        }

        var selectedDate = IsSpecificDateSelected ? SpecificDate?.Date : null;
        ActivityWeeks = ActivityWeeks
            .Select(week => new ActivityWeek(week.Days
                .Select(day => day with { IsSelected = day.Date == selectedDate })
                .ToArray()))
            .ToArray();
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
        var filters = new HistoryFilters(ExtensionFilter, SelectedExistence.Exists, FolderFilter);
        var snapshot = await Task.Run(
            () => _database.SearchWithStatsAsync(searchText, dateRange, cancellationToken: cancellationToken, filters: filters),
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
            SummaryText = LocalizationManager.Instance.Format(
                "{0:N0}개 결과  ·  {1:N0}개 항목  ·  누적 {2:N0}회",
                "{0:N0} results  ·  {1:N0} items  ·  {2:N0} total opens",
                snapshot.Items.Count, snapshot.Stats.UniqueItems, snapshot.Stats.TotalOpenCount);
        }
        else
        {
            var rangeOpenCount = snapshot.Items.Sum(item => item.OpenCount);
            var estimatedText = snapshot.Items.Any(item => item.IsEstimatedHistory)
                ? "  ·  " + LocalizationManager.Instance.Translate("이전 기록 일부 추정")
                : string.Empty;
            SummaryText = LocalizationManager.Instance.Format(
                              "{0:N0}개 결과  ·  {1} {2:N0}회",
                              "{0:N0} results  ·  {1} {2:N0} opens",
                              snapshot.Items.Count, dateRange.Label, rangeOpenCount) + estimatedText;
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
        PostToUi(() =>
        {
            ScheduleReload();
            ScheduleActivityReload();
        }, "recent history refresh");

    private void OnMonitorError(object? sender, string message)
    {
        PostToUi(() => StatusText = $"감시 오류: {message}", "monitor error notification");
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e) =>
        PostToUi(UpdateMonitorStatus, "monitor diagnostics update");

    private void PostToUi(Action action, string operationName)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (_disposed || dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (_disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not complete the {operationName} on the UI thread.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error($"Could not dispatch the {operationName} to the UI thread.", ex);
        }
    }

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
            return LocalizationManager.Instance.Translate("종료 중");
        }
        if (!monitor.IsStarted)
        {
            return LocalizationManager.Instance.Translate("시작 전");
        }
        return LocalizationManager.Instance.Translate(monitor.IsWatcherActive ? "정상 감시 중" : "감시기 비활성");
    }

    private static string FormatLocalTime(DateTimeOffset? utc) =>
        utc is null
            ? LocalizationManager.Instance.Translate("기록 없음")
            : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null ? "기록 없음" : $"{duration.Value.TotalMilliseconds:N0} ms";

    private static string FormatError(MonitorDiagnostics monitor) =>
        monitor.LastErrorUtc is null
            ? LocalizationManager.Instance.Translate("없음")
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
        Interlocked.Increment(ref _activityLoadSequence);
        LocalizationManager.Instance.PropertyChanged -= OnLanguageChanged;
        _monitor.HistoryChanged -= OnHistoryChanged;
        _monitor.MonitorError -= OnMonitorError;
        _monitor.DiagnosticsChanged -= OnDiagnosticsChanged;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _activityCancellation?.Cancel();
        _activityCancellation?.Dispose();
        _searchCancellation = null;
        _activityCancellation = null;
    }
}
