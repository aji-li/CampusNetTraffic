using System.IO;
using System.Text.Json;

namespace CampusNetTraffic.Services;

public sealed class AppSettingsStore
{
    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");
        Directory.CreateDirectory(appData);
        _settingsPath = Path.Combine(appData, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (!json.Contains(nameof(AppSettings.ShowFloatingMeter), StringComparison.Ordinal))
            {
                settings.ShowFloatingMeter = false;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}

public sealed class AppSettings
{
    public string SelectedAdapterId { get; set; } = "all";
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowFloatingMeter { get; set; }
    public double AvailableThresholdGb { get; set; } = 2;
    public double BalanceThresholdYuan { get; set; } = 5;
    public double SessionThresholdGb { get; set; } = 5;
}
