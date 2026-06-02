using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    public string ExportDiagnosticsToDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var exportPath = Path.Combine(desktop, $"CAUCNetTraffic-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var appData = Path.GetDirectoryName(_logPath)!;
        using var archive = ZipFile.Open(exportPath, ZipArchiveMode.Create);

        AddTextFileIfExists(archive, _logPath, "app.log");
        AddTextFileIfExists(archive, Path.Combine(appData, "crash.log"), "crash.log");
        AddSettingsSummary(archive, Path.Combine(appData, "settings.json"));
        AddSystemInfo(archive);

        return exportPath;
    }

    private async Task WriteAsync(string level, string message)
    {
        var safeMessage = RedactSensitiveText(message);
        await File.AppendAllTextAsync(_logPath, $"[{DateTimeOffset.Now:O}] [{level}] {safeMessage}{Environment.NewLine}");
    }

    private static void AddTextFileIfExists(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(RedactSensitiveText(File.ReadAllText(path)));
    }

    private static void AddSettingsSummary(ZipArchive archive, string settingsPath)
    {
        var entry = archive.CreateEntry("settings-summary.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        if (!File.Exists(settingsPath))
        {
            writer.Write("{}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            using var stream = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                jsonWriter.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name.Contains("license", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonWriter.WriteString(property.Name, "***");
                    }
                    else
                    {
                        property.WriteTo(jsonWriter);
                    }
                }

                jsonWriter.WriteEndObject();
            }

            writer.Write(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch
        {
            writer.Write("{}");
        }
    }

    private static void AddSystemInfo(ZipArchive archive)
    {
        var entry = archive.CreateEntry("system-info.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.WriteLine($"Time: {DateTimeOffset.Now:O}");
        writer.WriteLine($"OS: {Environment.OSVersion}");
        writer.WriteLine($".NET: {Environment.Version}");
        writer.WriteLine($"Machine: {Environment.MachineName}");
        writer.WriteLine($"ProcessorCount: {Environment.ProcessorCount}");
        writer.WriteLine();
        writer.WriteLine("Network adapters:");

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            writer.WriteLine($"- {adapter.Name} | {adapter.NetworkInterfaceType} | {adapter.OperationalStatus}");
        }
    }

    private static string RedactSensitiveText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;
        redacted = Regex.Replace(redacted, @"(?i)(JSESSIONID|Cookie|csrftoken|session|token)\s*[:=]\s*[^;\s,""']+", "$1=***");
        redacted = Regex.Replace(redacted, @"(?i)(account|username|user|loginName)\s*[:=]\s*[^;\s,""']+", "$1=***");
        redacted = Regex.Replace(redacted, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "***.***.***.***");
        redacted = Regex.Replace(redacted, @"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b", "**:**:**:**:**:**");
        redacted = Regex.Replace(redacted, @"\b[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}\b", "**-**-**-**-**-**");
        redacted = Regex.Replace(redacted, @"(?<!\d)\d{8,14}(?!\d)", "***");
        return redacted;
    }
}
