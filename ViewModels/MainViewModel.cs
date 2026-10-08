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
    private long _storageLoadSequence;
    private DatabaseStorageStatistics? _storageStatistics;
    private bool _storageStatisticsFailed;
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
    private ICollectionView? _resultsView;
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

    public string AppBuildVersion { get; } = typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    public string AppVersionText => "v" + AppBuildVersion.Split('+', 2)[0];

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
            if (value == _selectedSavedSearch) return;
            ApplySearchState(value.SearchText, value.Extension,
                ExistenceOptions.First(option => option.Exists == value.Exists), value.Folder,
                value.IsSpecificDate ? value.SpecificDate ?? DateTime.Today : SpecificDate,
                SavedSearchDateRangeResolver.Resolve(value, DateRanges), value);
        }
    }

    private void ApplySearchState(string? searchText, string? extension, ExistenceOption existence,
        string? folder, DateTime? specificDate, DateRangeOption range, SavedSearch? preset)
    {
        searchText ??= string.Empty;
        extension ??= string.Empty;
        folder ??= string.Empty;
        specificDate = (specificDate ?? DateTime.Today).Date;
        var searchChanged = _searchText != searchText;
        var extensionChanged = _extensionFilter != extension;
        var existenceChanged = _selectedExistence != existence;
        var folderChanged = _folderFilter != folder;
        var dateChanged = _specificDate != specificDate;
        var rangeChanged = _selectedDateRange != range;
        var presetChanged = _selectedSavedSearch != preset;

        // Assign all conditions before notifying bindings. No observer should see a partial preset.
        _searchText = searchText;
        _extensionFilter = extension;
        _selectedExistence = existence;
        _folderFilter = folder;
        _specificDate = specificDate;
        _selectedDateRange = range;
        _selectedSavedSearch = preset;

        if (presetChanged)
        {
            OnPropertyChanged(nameof(SelectedSavedSearch));
            RemoveSavedSearchCommand.RaiseCanExecuteChanged();
        }
        if (searchChanged)
        {
            OnPropertyChanged(nameof(SearchText));
            ClearSearchCommand.RaiseCanExecuteChanged();
        }
        if (extensionChanged) OnPropertyChanged(nameof(ExtensionFilter));
        if (existenceChanged) OnPropertyChanged(nameof(SelectedExistence));
        if (folderChanged) OnPropertyChanged(nameof(FolderFilter));
        if (dateChanged) OnPropertyChanged(nameof(SpecificDate));
        if (rangeChanged)
        {
            OnPropertyChanged(nameof(SelectedDateRange));
            OnPropertyChanged(nameof(IsSpecificDateSelected));
        }
        if (extensionChanged || existenceChanged || folderChanged || rangeChanged || (dateChanged && range.IsSpecificDate))
            OnPropertyChanged(nameof(FilterChips));
        if (dateChanged || rangeChanged) UpdateActivitySelection();
        if (searchChanged || extensionChanged || existenceChanged || folderChanged || rangeChanged ||
            (dateChanged && range.IsSpecificDate)) ScheduleReload();
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
        if (SelectedSavedSearch is not null)
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
        private set
        {
            if (SetField(ref _items, value)) RebuildResultsView();
        }
    }

    public ICollectionView ResultsView => _resultsView ??= CreateResultsView();

    private ICollectionView CreateResultsView() => ProjectGroupingEnabled
        ? ProjectGrouping.CreateView(Items, Projects)
        : FolderGrouping.CreateView(Items, FolderGroupingEnabled);

    public IReadOnlyList<ProjectDefinition> Projects => _settings.Projects;

    public bool ProjectGroupingEnabled
    {
        get => _settings.ProjectGroupingEnabled;
        set
        {
            if (value == ProjectGroupingEnabled) return;
            try
            {
                _settings.SetProjectGrouping(value);
                RebuildResultsView();
            }
            catch (Exception ex)
            {
                _log.Error("Saving project grouping failed.", ex);
                StatusText = LocalizationManager.Instance.Format(
                    "프로젝트 보기 저장 실패: {0}", "Saving project view failed: {0}", ex.Message);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderGroupingEnabled));
        }
    }

    public bool SaveProject(ProjectDefinition project) => ChangeProjects(() => _settings.SaveProject(project));
    public bool RemoveProject(Guid id) => ChangeProjects(() => _settings.RemoveProject(id));

    private bool ChangeProjects(Action save)
    {
        try
        {
            save();
            OnPropertyChanged(nameof(Projects));
            RebuildResultsView();
            StatusText = LocalizationManager.Instance.Translate("프로젝트 설정을 저장했습니다.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Saving projects failed.", ex);
            StatusText = LocalizationManager.Instance.Format(
                "프로젝트 저장 실패: {0}", "Saving projects failed: {0}",
                LocalizationManager.Instance.Translate(ex.Message));
            return false;
        }
    }

    public bool FolderGroupingEnabled
    {
        get => _settings.FolderGroupingEnabled;
        set
        {
            if (value == _settings.FolderGroupingEnabled) return;
            try
            {
                _settings.SetFolderGrouping(value);
                RebuildResultsView();
            }
            catch (Exception ex)
            {
                _log.Error("Saving folder grouping failed.", ex);
                StatusText = LocalizationManager.Instance.Format(
                    "폴더 그룹화 설정 저장 실패: {0}", "Saving folder grouping failed: {0}", ex.Message);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProjectGroupingEnabled));
        }
    }

    private void RebuildResultsView()
    {
        var selectedId = SelectedItem?.Id;
        _resultsView = CreateResultsView();
        OnPropertyChanged(nameof(ResultsView));
        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(SelectedItem));
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
        ClearFiltersCommand = new RelayCommand(() => ApplySearchState(
            SearchText, string.Empty, ExistenceOptions[0], string.Empty, SpecificDate, DateRanges[0], null));
        SaveSearchCommand = new RelayCommand(SaveCurrentSearch);
        RemoveSavedSearchCommand = new RelayCommand(RemoveSelectedSavedSearch,
            () => SelectedSavedSearch is not null);
        _autoStartEnabled = TryGetAutoStart(autoStart);

        RefreshCommand = new AsyncCommand(RefreshAsync, OnCommandError, () => !IsBusy);
        ToggleRecordingCommand = new AsyncCommand(ToggleRecordingAsync, OnCommandError, () => !IsBusy);
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
        OnPropertyChanged(nameof(RecordingToggleText));
        OnPropertyChanged(nameof(StorageStatisticsText));
        if (_initialized)
        {
            ScheduleReload();
            ScheduleActivityReload();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ToggleRecordingCommand { get; }

    private void OnCommandError(Exception exception)
    {
        _log.Error("An asynchronous UI command failed.", exception);
        StatusText = LocalizationManager.Instance.Format(
            "작업 실패: {0}", "Operation failed: {0}", exception.Message);
    }
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

    public bool IsRecordingPaused => _monitor.IsRecordingPaused;
    public string RecordingToggleText => LocalizationManager.Instance.Translate(
        IsRecordingPaused ? "기록 다시 시작" : "기록 일시정지");
    public IReadOnlyList<string> ExcludedFolders => _settings.ExcludedFolders;

    private async Task ToggleRecordingAsync()
    {
        IsBusy = true;
        try
        {
            _settings.SetRecordingPaused(!IsRecordingPaused);
            await _monitor.RefreshConfigurationAsync();
            StatusText = LocalizationManager.Instance.Translate(IsRecordingPaused
                ? "기록을 일시정지했습니다. 재시작 후에도 유지됩니다."
                : "기록을 다시 시작했습니다. 지금부터 열린 항목을 수집합니다.");
        }
        catch (Exception ex)
        {
            _log.Error("Changing recording pause failed.", ex);
            StatusText = LocalizationManager.Instance.Format(
                "기록 설정 변경 실패: {0}", "Changing recording settings failed: {0}", ex.Message);
        }
        finally
        {
            UpdateMonitorStatus();
            OnPropertyChanged(nameof(IsRecordingPaused));
            OnPropertyChanged(nameof(RecordingToggleText));
            IsBusy = false;
        }
    }

    public Task<bool> AddExcludedFolderAsync(string path) =>
        SaveExcludedFoldersAsync(_settings.ExcludedFolders.Append(path));

    public Task<bool> RemoveExcludedFolderAsync(string path) =>
        SaveExcludedFoldersAsync(_settings.ExcludedFolders.Where(folder =>
            !string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)));

    private async Task<bool> SaveExcludedFoldersAsync(IEnumerable<string> folders)
    {
        IsBusy = true;
        try
        {
            _settings.SetExcludedFolders(folders);
            await _monitor.RefreshConfigurationAsync();
            StatusText = LocalizationManager.Instance.Translate("제외 폴더 설정을 저장했습니다.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Saving excluded folders failed.", ex);
            StatusText = LocalizationManager.Instance.Format(
                "제외 폴더 설정 저장 실패: {0}", "Saving excluded folders failed: {0}", ex.Message);
            return false;
        }
        finally
        {
            OnPropertyChanged(nameof(ExcludedFolders));
            IsBusy = false;
        }
    }

    public string StorageStatisticsText
    {
        get
        {
            if (_storageStatistics is not { } stats)
                return LocalizationManager.Instance.Translate(_storageStatisticsFailed
                    ? "저장 공간을 확인하지 못했습니다. 다시 계산해 주세요."
                    : "저장 공간을 확인하려면 다시 계산을 누르세요.");
            return LocalizationManager.Instance.Format(
                "전체 파일: {0:N2} MB\nDB: {1:N2} MB · WAL: {2:N2} MB · SHM: {3:N2} MB\nDB 내부 할당: {4:N2} MB · 회수 가능한 빈 페이지: {5:N2} MB",
                "Total files: {0:N2} MB\nDB: {1:N2} MB · WAL: {2:N2} MB · SHM: {3:N2} MB\nAllocated in DB: {4:N2} MB · Reclaimable free pages: {5:N2} MB",
                stats.TotalFileBytes / 1048576d, stats.DatabaseBytes / 1048576d,
                stats.WalBytes / 1048576d, stats.SharedMemoryBytes / 1048576d,
                stats.AllocatedBytes / 1048576d, stats.FreeBytes / 1048576d);
        }
    }

    public async Task RefreshStorageStatisticsAsync()
    {
        var sequence = Interlocked.Increment(ref _storageLoadSequence);
        try
        {
            var stats = await _database.GetStorageStatisticsAsync();
            if (_disposed || sequence != Interlocked.Read(ref _storageLoadSequence) ||
                !string.Equals(stats.DatabasePath, DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
            _storageStatistics = stats;
            _storageStatisticsFailed = false;
        }
        catch (Exception ex)
        {
            _log.Error("Reading database storage statistics failed.", ex);
            if (_disposed || sequence != Interlocked.Read(ref _storageLoadSequence)) return;
            _storageStatistics = null;
            _storageStatisticsFailed = true;
        }
        OnPropertyChanged(nameof(StorageStatisticsText));
        OnPropertyChanged(nameof(DatabaseSizeText));
    }

    public async Task<bool> CompactDatabaseAsync(string expectedDatabasePath)
    {
        IsBusy = true;
        StatusText = LocalizationManager.Instance.Format(
            "기록을 유지하며 DB 공간을 정리하는 중…", "Compacting database without deleting history…");
        try
        {
            await _database.CompactAsync(expectedDatabasePath);
            await RefreshStorageStatisticsAsync();
            StatusText = LocalizationManager.Instance.Format(
                "DB 공간 정리를 완료했습니다. 기록은 유지되었습니다.",
                "Database compaction completed. History was preserved.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Database compaction failed.", ex);
            StatusText = LocalizationManager.Instance.Format(
                "DB 공간 정리 실패: {0}", "Database compaction failed: {0}", ex.Message);
            return false;
        }
        finally { IsBusy = false; }
    }

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
                ToggleRecordingCommand.RaiseCanExecuteChanged();
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
            StatusText = IsRecordingPaused
                ? "기록을 일시정지했습니다. 재시작 후에도 유지됩니다."
                : "최근 항목 폴더를 실시간으로 감시하고 있습니다.";
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
            await RefreshStorageStatisticsAsync();
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
        var searchText = SearchText;
        var dateRange = BuildSelectedDateRange();
        var filters = new HistoryFilters(ExtensionFilter, SelectedExistence.Exists, FolderFilter);
        IsBusy = true;
        StatusText = "현재 검색 결과를 내보내는 중…";
        try
        {
            await _database.ExportSearchToAsync(destinationPath, format, searchText, dateRange, filters);
            StatusText = LocalizationManager.Instance.Format(
                "현재 검색 조건의 전체 기록을 내보냈습니다: {0}",
                "Exported all history matching current filters to: {0}", destinationPath);
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
        var databaseBytes = TryGetFileSize(DatabasePath);
        var isEnglish = LocalizationManager.Instance.Language == "en";
        string Label(string korean) => LocalizationManager.Instance.Translate(korean);
        var builder = new StringBuilder();
        builder.AppendLine(isEnglish ? "FindHistory Diagnostics" : "FindHistory 진단 정보")
            .AppendLine($"{Label("생성 시각")}: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"{Label("앱 버전")}: {AppBuildVersion}")
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
        Interlocked.Increment(ref _storageLoadSequence);
        _storageStatistics = null;
        _storageStatisticsFailed = false;
        OnPropertyChanged(nameof(StorageStatisticsText));
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
            StatusText = IsRecordingPaused
                ? "기록을 일시정지했습니다. 저장된 기록을 표시합니다."
                : $"최근 항목 {captured:N0}개를 확인했습니다.";
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

        var date = day.Date.Date;
        var range = DateRanges.First(option => option.IsSpecificDate);
        var dateChanged = _specificDate != date;
        var rangeChanged = _selectedDateRange != range;
        if (dateChanged || rangeChanged)
        {
            // Publish a complete date selection. Binding observers must not see an intermediate range.
            _specificDate = date;
            _selectedDateRange = range;
            if (dateChanged) OnPropertyChanged(nameof(SpecificDate));
            if (rangeChanged)
            {
                OnPropertyChanged(nameof(SelectedDateRange));
                OnPropertyChanged(nameof(IsSpecificDateSelected));
            }
            ClearSavedSearchSelection();
            OnPropertyChanged(nameof(FilterChips));
            UpdateActivitySelection();
            ScheduleReload();
        }
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

    public void NotifyExistenceChanged() =>
        PostToUi(ScheduleReload, "file existence refresh");

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
                           !IsRecordingPaused &&
                           diagnostics.IsWatcherActive;
        MonitorStatusText = diagnostics.IsStopping
            ? "기록 종료 중"
            : IsRecordingPaused
                ? "기록 일시정지 중"
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
        if (monitor.IsRecordingPaused)
            return LocalizationManager.Instance.Translate("기록 일시정지 중");
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
