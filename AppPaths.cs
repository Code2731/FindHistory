namespace FindHistory;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FindHistory");

    public static string DefaultDatabasePath { get; } = Path.Combine(DataDirectory, "findhistory.db");

    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "Logs");
}
