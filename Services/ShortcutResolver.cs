using System.Runtime.InteropServices;
using System.Text;
using FindHistory.Models;

namespace FindHistory.Services;

public sealed class ShortcutResolver
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static Type? _shellLinkType;

    private static Type ShellLinkType => _shellLinkType ??=
        Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)!;

    public RecentItemCandidate? Resolve(string shortcutPath)
    {
        try
        {
            var extension = Path.GetExtension(shortcutPath);
            var targetPath = extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                ? ResolveInternetShortcut(shortcutPath)
                : ResolveShellLink(shortcutPath);

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            targetPath = Environment.ExpandEnvironmentVariables(targetPath.Trim());
            var isWeb = Uri.TryCreate(targetPath, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            var isDirectory = !isWeb && Directory.Exists(targetPath);
            var exists = isWeb || isDirectory || File.Exists(targetPath);
            var displayName = isWeb
                ? Path.GetFileNameWithoutExtension(shortcutPath)
                : Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = targetPath;
            }

            var targetExtension = isWeb || isDirectory ? string.Empty : Path.GetExtension(targetPath);
            var kind = isWeb ? "웹" : isDirectory ? "폴더" : "파일";
            var writeTime = new DateTimeOffset(File.GetLastWriteTimeUtc(shortcutPath), TimeSpan.Zero);

            return new RecentItemCandidate(
                targetPath,
                displayName,
                targetExtension.TrimStart('.').ToUpperInvariant(),
                kind,
                shortcutPath,
                writeTime,
                exists);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveInternetShortcut(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return line[4..].Trim();
            }
        }
        return null;
    }

    private static string? ResolveShellLink(string path)
    {
        var shellLink = (IShellLinkW)Activator.CreateInstance(ShellLinkType)!;
        try
        {
            ((IPersistFile)shellLink).Load(path, 0);
            var target = new StringBuilder(32768);
            shellLink.GetPath(target, target.Capacity, out _, 0x4);
            return target.Length == 0 ? null : target.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
            out Win32FindData findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength,
            out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
}
