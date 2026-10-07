using System.ComponentModel;
using System.Windows.Data;
using FindHistory.Localization;

namespace FindHistory.Models;

public static class FolderGrouping
{
    public static string GetFolderName(string targetPath)
    {
        if (Uri.TryCreate(targetPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return LocalizationManager.Instance.Translate("웹 주소");
        try
        {
            if (!Path.IsPathFullyQualified(targetPath))
                return LocalizationManager.Instance.Translate("기타 경로");
            var fullPath = Path.GetFullPath(targetPath);
            var root = Path.GetPathRoot(fullPath)!;
            if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) return root;
            return Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? parent : root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LocalizationManager.Instance.Translate("기타 경로");
        }
    }

    public static ICollectionView CreateView(IReadOnlyList<RecentItem> items, bool enabled)
    {
        var view = new ListCollectionView(items.ToList());
        if (enabled)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecentItem.FolderGroupName))
            {
                StringComparison = StringComparison.OrdinalIgnoreCase
            });
        return view;
    }
}
