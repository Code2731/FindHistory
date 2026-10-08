namespace FindHistory.Services;

public static class ExcludedFolderPathRules
{
    public static string[] Normalize(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var result = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("제외 폴더 경로가 비어 있습니다.", nameof(folders));
            var expanded = Environment.ExpandEnvironmentVariables(folder.Trim());
            if (!Path.IsPathFullyQualified(expanded))
                throw new ArgumentException("제외 폴더는 전체 경로로 지정해야 합니다.", nameof(folders));
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            if (result.Count > 50)
                throw new ArgumentOutOfRangeException(nameof(folders), "제외 폴더는 최대 50개까지 설정할 수 있습니다.");
        }
        return result.ToArray();
    }

    public static bool IsExcluded(string targetPath, IReadOnlyList<string> excludedFolders)
    {
        if (!Path.IsPathFullyQualified(targetPath)) return false;
        string fullPath;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        foreach (var folder in excludedFolders)
        {
            if (string.Equals(fullPath, folder, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
