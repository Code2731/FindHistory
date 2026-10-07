namespace FindHistory.Services;

public static class FileExistenceProbe
{
    // null means unknown. Do not convert access errors or offline drives into "missing".
    public static bool? Check(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return null;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal)) return null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network || !drive.IsReady) return null;
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0 ? null : true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or System.Security.SecurityException) { return null; }
    }
}
