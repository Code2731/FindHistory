using System.Text.Json;
using FindHistory.Models;

namespace FindHistory.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
        _settings = Load();
    }

    public string DatabasePath => string.IsNullOrWhiteSpace(_settings.DatabasePath)
        ? AppPaths.DefaultDatabasePath
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_settings.DatabasePath));

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
        var searches = _settings.SavedSearches.ToList();
        if (searches.Count >= 30)
        {
            return false;
        }

        searches.Add(savedSearch);
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
            return settings.SavedSearches is null
                ? settings with { SavedSearches = [] }
                : settings;
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
    }
}
