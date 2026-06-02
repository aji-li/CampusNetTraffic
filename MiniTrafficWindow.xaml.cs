using System.Windows;
using System.Windows.Input;

namespace CampusNetTraffic;

public partial class MiniTrafficWindow : Window
{
    public event EventHandler? UserClosedMiniWindow;
    public event EventHandler? UserMovedMiniWindow;

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

    public void SetSavedPosition(double left, double top)
    {
        Left = left;
        Top = top;
    }

    private void MiniWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            UserMovedMiniWindow?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        UserClosedMiniWindow?.Invoke(this, EventArgs.Empty);
    }
}
