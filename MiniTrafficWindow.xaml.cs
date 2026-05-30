using System.Windows;
using System.Windows.Input;

namespace CampusNetTraffic;

public partial class MiniTrafficWindow : Window
{
    public event EventHandler? UserClosedMiniWindow;

    public MiniTrafficWindow()
    {
        InitializeComponent();
    }

    public void UpdateTraffic(string usage, string downloadRate, string uploadRate)
    {
        UsageText.Text = usage;
        DownloadText.Text = $"↓ {downloadRate}";
        UploadText.Text = $"↑ {uploadRate}";
    }

    public void PlaceNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;
    }

    private void MiniWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        UserClosedMiniWindow?.Invoke(this, EventArgs.Empty);
    }
}
