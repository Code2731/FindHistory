using Microsoft.Win32;
using System.Reflection;

namespace FindHistory.Services;

public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FindHistory";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 찾지 못했습니다.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var command = isDotnetHost && !string.IsNullOrWhiteSpace(entryAssembly)
            ? $"\"{processPath}\" \"{entryAssembly}\" --background"
            : $"\"{processPath}\" --background";

        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
