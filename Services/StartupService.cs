using Microsoft.Win32;

namespace CampusNetTraffic.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CampusNetTraffic";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(AppName)?.ToString());
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
