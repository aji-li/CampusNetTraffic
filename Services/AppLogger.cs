using System.IO;

namespace CampusNetTraffic.Services;

public sealed class AppLogger
{
    private readonly string _logPath;

    public AppLogger()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");
        Directory.CreateDirectory(appData);
        _logPath = Path.Combine(appData, "app.log");
    }

    public string LogPath => _logPath;

    public async Task InfoAsync(string message)
    {
        await WriteAsync("INFO", message);
    }

    public async Task ErrorAsync(string message, Exception? exception = null)
    {
        await WriteAsync("ERROR", exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");
    }

    public string ExportToDesktop()
    {
        if (!File.Exists(_logPath))
        {
            File.WriteAllText(_logPath, string.Empty);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var exportPath = Path.Combine(desktop, $"CampusNetTraffic-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.Copy(_logPath, exportPath, overwrite: true);
        return exportPath;
    }

    private async Task WriteAsync(string level, string message)
    {
        var safeMessage = message
            .Replace("JSESSIONID", "SESSION", StringComparison.OrdinalIgnoreCase)
            .Replace("Cookie", "COOKIE", StringComparison.OrdinalIgnoreCase);
        await File.AppendAllTextAsync(_logPath, $"[{DateTimeOffset.Now:O}] [{level}] {safeMessage}{Environment.NewLine}");
    }
}
