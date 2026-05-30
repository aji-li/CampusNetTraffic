using System.IO;
using System.Text;
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

        if (string.IsNullOrWhiteSpace(key))
        {
            key = GetEmbeddedSyncfusionLicense();
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            SyncfusionLicenseProvider.RegisterLicense(key);
        }
    }

    private static string? GetEmbeddedSyncfusionLicense()
    {
        // Simple XOR obfuscation to avoid plaintext in the string table.
        byte[] keyBytes = { 99, 97, 117, 99 };
        byte[] data =
        {
            45, 6, 26, 90, 33, 8, 18, 33, 44, 6, 18, 9, 36, 24, 25, 76, 53, 10, 35, 72, 59,
            52, 76, 34, 0, 13, 39, 38, 50, 12, 51, 34, 58, 55, 51, 81, 49, 83, 35, 41, 6, 39,
            39, 20, 7, 55, 76, 36, 57, 10, 2, 4, 44, 57, 68, 7, 50, 13, 76, 15, 48, 57, 1,
            50, 7, 10, 35, 8, 52, 57, 68, 1, 0, 82, 59, 55, 50, 38, 55, 59, 54, 10, 22, 94
        };
        if (data.Length == 0)
        {
            return null;
        }

        var decoded = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            decoded[i] = (byte)(data[i] ^ keyBytes[i % keyBytes.Length]);
        }

        return Encoding.ASCII.GetString(decoded).Trim();
    }
}
