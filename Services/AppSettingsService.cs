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
        _settings = _settings with { DatabasePath = fullPath };
        Save(_settings);
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
        _settings = _settings with { SavedSearches = searches };
        Save(_settings);
        return true;
    }

    public void RemoveSavedSearch(Guid id)
    {
        var searches = _settings.SavedSearches.Where(search => search.Id != id).ToList();
        if (searches.Count == _settings.SavedSearches.Count)
        {
            return;
        }

        _settings = _settings with { SavedSearches = searches };
        Save(_settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings(null);
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                   ?? new AppSettings(null);
        }
        catch (JsonException)
        {
            return new AppSettings(null);
        }
        catch (IOException)
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
