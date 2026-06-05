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

            if (!json.Contains(nameof(AppSettings.CampusSyncIntervalSeconds), StringComparison.Ordinal))
            {
                settings.CampusSyncIntervalSeconds = 120;
            }

            if (!json.Contains(nameof(AppSettings.CloseWithoutPrompt), StringComparison.Ordinal)
                && settings.CloseToTrayWithoutPrompt)
            {
                settings.CloseWithoutPrompt = true;
                settings.CloseDefaultAction = AppSettings.CloseActionMinimizeToTray;
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
    public const string DefaultUpdateSourceUrl = "https://raw.githubusercontent.com/aji-li/CampusNetTraffic/main/latest.json";

    public string SelectedAdapterId { get; set; } = "all";
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTrayWithoutPrompt { get; set; }
    public bool CloseWithoutPrompt { get; set; }
    public string CloseDefaultAction { get; set; } = CloseActionMinimizeToTray;
    public bool ShowFloatingMeter { get; set; }
    public double? FloatingMeterLeft { get; set; }
    public double? FloatingMeterTop { get; set; }
    public int CampusSyncIntervalSeconds { get; set; } = 120;
    public bool EnableAvailableTrafficAlert { get; set; } = true;
    public bool EnableBalanceAlert { get; set; } = true;
    public bool EnableSessionTrafficAlert { get; set; } = true;
    public double AvailableThresholdGb { get; set; } = 2;
    public double BalanceThresholdYuan { get; set; } = 5;
    public double SessionThresholdGb { get; set; } = 5;
    public bool HasCompletedFirstRunGuide { get; set; }
    public string UpdateSourceUrl { get; set; } = DefaultUpdateSourceUrl;

    public const string CloseActionMinimizeToTray = "MinimizeToTray";
    public const string CloseActionExit = "Exit";
}
