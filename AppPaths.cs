namespace FindHistory;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FindHistory");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "findhistory.db");
}
