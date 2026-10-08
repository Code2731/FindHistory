using System.Text.Json;
using FindHistory.Models;

namespace FindHistory.Services;

public sealed class AppSettingsService
{
    public const int MaxSavedSearches = 30;
    public const int MaxSavedSearchNameLength = 64;

    private readonly string _settingsPath;
    private volatile AppSettings _settings;

    public AppSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
        _settings = Load();
    }

    public string DatabasePath => string.IsNullOrWhiteSpace(_settings.DatabasePath)
        ? AppPaths.DefaultDatabasePath
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_settings.DatabasePath));

    public string Language => _settings.Language;

    public bool FolderGroupingEnabled => _settings.FolderGroupingEnabled;
    public bool RecordingPaused => _settings.RecordingPaused;
    public IReadOnlyList<string> ExcludedFolders => _settings.ExcludedFolders.ToArray();

    public RecordingConfiguration GetRecordingConfiguration()
    {
        var settings = _settings;
        return new RecordingConfiguration(settings.RecordingPaused, settings.ExcludedFolders.ToArray(),
            settings.RecordingResumeAfterUtc);
    }

    public void SetRecordingPaused(bool paused)
    {
        var updatedSettings = _settings with
        {
            RecordingPaused = paused,
            RecordingResumeAfterUtc = _settings.RecordingPaused && !paused
                ? DateTimeOffset.UtcNow : _settings.RecordingResumeAfterUtc
        };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public void SetExcludedFolders(IEnumerable<string> folders)
    {
        var updatedSettings = _settings with { ExcludedFolders = ExcludedFolderPathRules.Normalize(folders) };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public void SetFolderGrouping(bool enabled)
    {
        var updatedSettings = _settings with { FolderGroupingEnabled = enabled };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public bool AutoBackupEnabled => _settings.AutoBackupEnabled;
    public int AutoBackupRetention => Math.Clamp(_settings.AutoBackupRetention, 1, 30);

    public (bool Enabled, int Retention) GetAutoBackupSettings()
    {
        var settings = _settings;
        return (settings.AutoBackupEnabled, Math.Clamp(settings.AutoBackupRetention, 1, 30));
    }

    public void SetAutoBackup(bool enabled, int retention)
    {
        if (retention is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(retention));
        var updatedSettings = _settings with
        {
            AutoBackupEnabled = enabled,
            AutoBackupRetention = retention
        };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public void SetLanguage(string language)
    {
        var normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";
        var updatedSettings = _settings with { Language = normalized };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public void SetDatabasePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var updatedSettings = _settings with { DatabasePath = fullPath };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    public IReadOnlyList<SavedSearch> SavedSearches => _settings.SavedSearches;

    public bool AddSavedSearch(SavedSearch savedSearch)
    {
        ArgumentNullException.ThrowIfNull(savedSearch);
        var name = savedSearch.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("저장 검색 이름을 입력해야 합니다.", nameof(savedSearch));
        }
        if (name.Length > MaxSavedSearchNameLength)
        {
            throw new ArgumentException(
                $"저장 검색 이름은 {MaxSavedSearchNameLength}자 이하여야 합니다.", nameof(savedSearch));
        }

        var searches = _settings.SavedSearches.ToList();
        if (searches.Any(existing => string.Equals(existing.Name?.Trim(), name,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("같은 이름의 저장 검색이 이미 있습니다.", nameof(savedSearch));
        }
        if (searches.Count >= MaxSavedSearches)
        {
            return false;
        }

        searches.Add(savedSearch with { Name = name });
        var updatedSettings = _settings with { SavedSearches = searches };
        Save(updatedSettings);
        _settings = updatedSettings;
        return true;
    }

    public void RemoveSavedSearch(Guid id)
    {
        var searches = _settings.SavedSearches.Where(search => search.Id != id).ToList();
        if (searches.Count == _settings.SavedSearches.Count)
        {
            return;
        }

        var updatedSettings = _settings with { SavedSearches = searches };
        Save(updatedSettings);
        _settings = updatedSettings;
    }

    private AppSettings Load()
    {
        try
        {
            using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream)
                           ?? throw new InvalidDataException("설정 파일이 비어 있거나 유효하지 않습니다.");
            return settings with
            {
                SavedSearches = settings.SavedSearches ?? [],
                ExcludedFolders = ExcludedFolderPathRules.Normalize(settings.ExcludedFolders ?? [])
            };
        }
        catch (FileNotFoundException)
        {
            return new AppSettings(null);
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings(null);
        }
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private sealed record AppSettings(string? DatabasePath)
    {
        public List<SavedSearch> SavedSearches { get; init; } = [];
        public string Language { get; init; } = "ko";
        public bool AutoBackupEnabled { get; init; }
        public int AutoBackupRetention { get; init; } = 7;
        public bool FolderGroupingEnabled { get; init; }
        public bool RecordingPaused { get; init; }
        public DateTimeOffset? RecordingResumeAfterUtc { get; init; }
        public string[] ExcludedFolders { get; init; } = [];
    }
}
