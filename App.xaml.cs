using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using Syncfusion.Licensing;

namespace CampusNetTraffic;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\CAUCNetTraffic.SingleInstance";
    private const string SingleInstancePipeName = "CAUCNetTraffic.SingleInstancePipe";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private CancellationTokenSource? _pipeCancellation;

    public App()
    {
        RegisterSyncfusionLicenseIfPresent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!AcquireSingleInstance())
        {
            NotifyExistingInstance();
            Shutdown();
            return;
        }

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
        MainWindow = new MainWindow();
        MainWindow.Show();
        StartSingleInstancePipeServer();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancellation?.Cancel();
        _pipeCancellation?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        return createdNew;
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
            client.Connect(700);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch
        {
            // If the first instance is starting up and the pipe is not ready yet, simply avoid launching a second instance.
        }
    }

    private void StartSingleInstancePipeServer()
    {
        _pipeCancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_pipeCancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_pipeCancellation.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var command = await reader.ReadLineAsync();
                    if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.Invoke(ShowExistingMainWindow);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(300);
                }
            }
        });
    }

    private void ShowExistingMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
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
            45, 6, 26, 90, 33, 8, 18, 33, 44, 6, 18, 9, 43, 53, 36, 27, 34, 51, 77, 76, 53,
            80, 63, 43, 2, 39, 64, 0, 52, 54, 17, 32, 5, 80, 51, 19, 49, 12, 63, 36, 7, 13,
            17, 86, 5, 52, 35, 43, 58, 55, 47, 54, 55, 57, 13, 2, 48, 81, 69, 39, 45, 41, 35,
            49, 7, 10, 17, 15, 52, 57, 25, 6, 0, 57, 47, 59, 50, 12, 55, 58, 53, 10, 35, 26,
            59, 36, 1, 52, 58, 36, 26, 94
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
