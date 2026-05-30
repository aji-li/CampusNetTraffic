using System.IO;
using System.Windows;
using Syncfusion.Licensing;

namespace CampusNetTraffic;

public partial class App : System.Windows.Application
{
    public App()
    {
        RegisterSyncfusionLicenseIfPresent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CampusNetTraffic");
            Directory.CreateDirectory(appData);
            File.AppendAllText(
                Path.Combine(appData, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {args.Exception}{Environment.NewLine}");

            System.Windows.MessageBox.Show(
                "程序遇到异常，已写入本地 crash.log。请在设置里导出诊断日志，或查看本地应用数据目录。",
                "CampusNetTraffic error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    private static void RegisterSyncfusionLicenseIfPresent()
    {
        var key = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var appDataKeyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CampusNetTraffic",
                "syncfusion-license.txt");
            var exeKeyPath = Path.Combine(AppContext.BaseDirectory, "syncfusion-license.txt");
            var keyPath = File.Exists(appDataKeyPath) ? appDataKeyPath : exeKeyPath;
            if (File.Exists(keyPath))
            {
                key = File.ReadAllText(keyPath).Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            SyncfusionLicenseProvider.RegisterLicense(key);
        }
    }
}
