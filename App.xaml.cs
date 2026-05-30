using System.Configuration;
using System.Data;
using System.Windows;

namespace CampusNetTraffic;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                args.Exception.Message,
                "CampusNetTraffic error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}

