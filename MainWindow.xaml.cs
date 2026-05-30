using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CampusNetTraffic.Models;
using CampusNetTraffic.Services;
using Microsoft.Web.WebView2.Core;
using Syncfusion.UI.Xaml.Charts;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CampusNetTraffic;

public partial class MainWindow : Window
{
    private readonly NetworkTrafficMonitor _trafficMonitor = new();
    private readonly TrafficRepository _trafficRepository = new();
    private readonly CampusSessionStore _sessionStore = new();
    private readonly StartupService _startupService = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly AppLogger _logger = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _campusTimer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _trayStatusItem;
    private readonly Forms.ToolStripMenuItem _trayUsageItem;
    private readonly Forms.ToolStripMenuItem _traySpeedItem;
    private readonly Forms.ToolStripMenuItem _trayMiniWindowItem;

    private TrafficSample? _firstSample;
    private TrafficSample? _lastSavedSample;
    private bool _isCampusSyncing;
    private bool _loginPromptShown;
    private bool _sessionRestored;
    private bool _allowExit;
    private bool _availableTrafficNotified;
    private bool _balanceNotified;
    private bool _sessionTrafficNotified;
    private bool _isLoadingSettings = true;
    private AppSettings _settings = new();
    private IReadOnlyList<TrafficUsagePoint> _recentTrend = Array.Empty<TrafficUsagePoint>();
    private IReadOnlyList<TrafficUsagePoint> _dailyTrend = Array.Empty<TrafficUsagePoint>();
    private MiniTrafficWindow? _miniTrafficWindow;
    private string _currentLocalUsage = "0 MB";
    private string _currentDownloadRate = "0 KB/s";
    private string _currentUploadRate = "0 KB/s";
    private string _campusStatus = "等待同步";

    private enum CloseChoice
    {
        Cancel,
        MinimizeToTray,
        Exit
    }

    private enum AppConnectionState
    {
        Monitoring,
        Syncing,
        Connected,
        LoginRequired,
        CampusUnavailable,
        Error
    }

    private sealed record ChartPoint(string Label, double ValueMb);

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;

        _campusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _campusTimer.Tick += async (_, _) => await SyncCampusAsync(false);

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;

        (_notifyIcon, _trayStatusItem, _trayUsageItem, _traySpeedItem, _trayMiniWindowItem) = CreateNotifyIcon();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingSettings = true;
        _settings = _settingsStore.Load();
        StartWithWindowsCheckBox.IsChecked = _startupService.IsEnabled();
        LoadNetworkAdapters();
        ApplySettingsToControls();
        _isLoadingSettings = false;
        ApplyFloatingMeterSetting();

        await _trafficRepository.InitializeAsync();
        _ = _trafficRepository.CleanupAsync();
        await InitializeLoginWebViewAsync();
        _timer.Start();
        _campusTimer.Start();
        await SyncCampusAsync(false);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        var sample = _trafficMonitor.Capture();
        _firstSample ??= sample;

        _currentDownloadRate = FormatRate(sample.DownloadBytesPerSecond);
        _currentUploadRate = FormatRate(sample.UploadBytesPerSecond);
        DownloadSpeedText.Text = _currentDownloadRate;
        UploadSpeedText.Text = _currentUploadRate;

        var localBytes = Math.Max(0, sample.TotalReceivedBytes - _firstSample.TotalReceivedBytes)
            + Math.Max(0, sample.TotalSentBytes - _firstSample.TotalSentBytes);
        var localTotal = FormatBytes(localBytes);
        _currentLocalUsage = localTotal;

        LocalTotalText.Text = $"本次运行：{localTotal}";
        HeroUsageText.Text = localTotal;
        HeroRateText.Text = $"{_currentDownloadRate}  /  {_currentUploadRate}";
        LastSampleText.Text = $"最近采样：{sample.CapturedAt:HH:mm:ss}";
        UpdateTrayLiveInfo();
        UpdateMiniTrafficWindow();

        CheckSessionTrafficThreshold(localBytes);

        if (_lastSavedSample is null || (sample.CapturedAt - _lastSavedSample.CapturedAt).TotalSeconds >= 60)
        {
            _lastSavedSample = sample;
            await _trafficRepository.SaveAsync(sample);
            await RefreshUsageStatsAsync();
        }
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LoginPanel, "校园网登录", "登录一次后，会话过期前会自动复用");
        await InitializeLoginWebViewAsync();
        LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
        SetStatus(AppConnectionState.LoginRequired, "请在网页登录框中登录，完成后 APP 会自动同步。");
    }

    private void HideBrowser_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPanel, "流量总览", "本机实时监控 + CAUC Dr.COM 后台官方数据");
    }

    private async void SyncCampus_Click(object sender, RoutedEventArgs e)
    {
        await SyncCampusAsync(true);
    }

    private async void OfflineDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OnlineDevice device })
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定要注销这台在线设备吗？\n\nIP：{device.Ip}\nMAC：{device.Mac}\n主机名：{device.HostName}",
            "注销在线设备",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await EnsureDashboardPageAsync();
            var success = await OfflineDeviceAsync(device.SessionId);
            StatusText.Text = success ? $"已注销设备：{device.Ip}" : $"注销失败：{device.Ip}";
            if (success)
            {
                await SyncCampusAsync(true);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"注销设备失败：{ex.Message}";
            await _logger.ErrorAsync("Offline device failed.", ex);
        }
    }

    private async Task InitializeLoginWebViewAsync()
    {
        if (LoginWebView.CoreWebView2 is not null)
        {
            return;
        }

        var userDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await LoginWebView.EnsureCoreWebView2Async(environment);

        var coreWebView = LoginWebView.CoreWebView2;
        if (coreWebView is null)
        {
            return;
        }

        await RestoreStoredSessionAsync(coreWebView);

        coreWebView.NavigationCompleted += async (_, _) =>
        {
            var currentUrl = LoginWebView.Source?.ToString() ?? string.Empty;
            if (currentUrl.Contains("/Self/dashboard", StringComparison.OrdinalIgnoreCase))
            {
                await SyncCampusAsync(false);
            }
        };
    }

    private async Task SyncCampusAsync(bool showLoginHint)
    {
        try
        {
            if (_isCampusSyncing)
            {
                if (showLoginHint)
                {
                    SetStatus(AppConnectionState.Syncing, "正在同步校园网数据，请稍等几秒。");
                }

                return;
            }

            _isCampusSyncing = true;
            if (showLoginHint)
            {
                SetStatus(AppConnectionState.Syncing, "正在同步校园网数据...");
            }

            await InitializeLoginWebViewAsync();
            await EnsureDashboardPageAsync();

            var currentUrl = LoginWebView.Source?.ToString() ?? string.Empty;
            if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                if (showLoginHint)
                {
                    SetStatus(AppConnectionState.LoginRequired, "当前还在登录页，请登录成功进入首页后再同步。");
                }

                return;
            }

            var account = await GetAccountSnapshotFromWebViewAsync();
            var devices = await GetOnlineDevicesFromWebViewAsync();
            await SaveCurrentSessionAsync();
            await _logger.InfoAsync($"Campus sync ok. Devices={devices.Count}, Used={account.UsedTrafficMb:N0}M, Available={account.AvailableTrafficMb:N0}M");

            ApplyAccount(account);
            DeviceGrid.ItemsSource = devices;
            SetStatus(AppConnectionState.Connected, $"校园网正常，在线设备 {devices.Count} 台，{account.CapturedAt:HH:mm:ss} 已同步。");
        }
        catch (TaskCanceledException)
        {
            SetStatus(AppConnectionState.CampusUnavailable, "同步超时：校园网后台超过 8 秒未响应。");
            await _logger.ErrorAsync("Campus sync timeout.");
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("Campus sync failed.", ex);
            if (IsLoginExpiredError(ex))
            {
                _sessionStore.Clear();
                LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
                PromptLoginIfNeeded(showLoginHint);
            }
            else if (showLoginHint)
            {
                SetStatus(AppConnectionState.Error, $"同步失败：{ex.Message}");
            }
            else
            {
                SetStatus(AppConnectionState.CampusUnavailable, $"自动同步暂时失败：{ex.Message}");
            }
        }
        finally
        {
            _isCampusSyncing = false;
        }
    }

    private static bool IsLoginExpiredError(Exception ex)
    {
        return ex.Message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("redirected to login", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("login page", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RestoreStoredSessionAsync(CoreWebView2 coreWebView)
    {
        if (_sessionRestored)
        {
            return;
        }

        _sessionRestored = true;
        var sessionId = await _sessionStore.LoadAsync();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var cookie = coreWebView.CookieManager.CreateCookie("JSESSIONID", sessionId, "www.cauc.edu.cn", "/Self");
        cookie.IsHttpOnly = true;
        cookie.IsSecure = true;
        coreWebView.CookieManager.AddOrUpdateCookie(cookie);
        SetStatus(AppConnectionState.Syncing, "已载入上次校园网会话，正在检测是否过期。");
    }

    private async Task SaveCurrentSessionAsync()
    {
        var cookieManager = LoginWebView.CoreWebView2.CookieManager;
        var cookies = await cookieManager.GetCookiesAsync("https://www.cauc.edu.cn/Self/dashboard");
        var sessionCookie = cookies
            .Where(cookie => cookie.Name.Equals("JSESSIONID", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(cookie => cookie.Path.StartsWith("/Self", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(cookie => cookie.Path.Length)
            .FirstOrDefault();

        if (sessionCookie is not null)
        {
            await _sessionStore.SaveAsync(sessionCookie.Value);
        }
    }

    private async Task<AccountSnapshot> GetAccountSnapshotFromWebViewAsync()
    {
        var script = """
            (() => {
                try {
                    const pageHtml = document.documentElement ? document.documentElement.innerHTML : '';
                    const refreshMatch = pageHtml.match(/csrftoken:\s*'([^']+)'/);
                    if (refreshMatch) {
                        try {
                            const refresh = new XMLHttpRequest();
                            refresh.open('GET', '/Self/dashboard/refreshaccount?csrftoken=' + encodeURIComponent(refreshMatch[1]) + '&t=' + Math.random(), false);
                            refresh.send();
                        } catch (_) {}
                    }

                    const request = new XMLHttpRequest();
                    request.open('GET', '/Self/dashboard?t=' + Math.random(), false);
                    request.setRequestHeader('Cache-Control', 'no-store');
                    request.send();
                    const html = request.responseText || '';
                    const parsed = new DOMParser().parseFromString(html, 'text/html');
                    const loginForm = parsed.querySelector('form[action*="login/verify"]');
                    const accountInput = parsed.querySelector('[name="account"]');
                    const hasTraffic = html.includes('已用流量') || html.includes('可用流量') || html.includes('宸茬敤娴侀噺') || html.includes('鍙敤娴侀噺');
                    const isLogin = !!(loginForm && accountInput && !hasTraffic);
                    parsed.querySelectorAll('script, style').forEach(node => node.remove());
                    const text = parsed.body ? parsed.body.innerText : '';
                    return { ok: request.status >= 200 && request.status < 300, status: request.status, isLogin, text, currentUrl: location.href, htmlStart: html.slice(0, 180) };
                } catch (error) {
                    return { ok: false, status: 0, isLogin: false, text: '', error: String(error), currentUrl: location.href };
                }
            })()
            """;

        var raw = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
        var json = NormalizeScriptJson(raw);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (TryGetString(root, "error", out var scriptError) && !string.IsNullOrWhiteSpace(scriptError))
        {
            throw new InvalidOperationException($"Dashboard script error: {scriptError}");
        }

        if (!TryGetBoolean(root, "isLogin", out var isLogin))
        {
            throw new InvalidOperationException($"Unexpected dashboard script result: {Shorten(json)}");
        }

        if (isLogin)
        {
            throw new InvalidOperationException("Campus session is not logged in. Please log in inside the app first.");
        }

        var text = TryGetString(root, "text", out var parsedText) ? parsedText : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Dashboard response did not contain text: {Shorten(json)}");
        }

        return new AccountSnapshot(
            UsedTrafficMb: ReadDoubleBefore(text, "已用流量"),
            AvailableTrafficMb: ReadDoubleBefore(text, "可用流量"),
            Balance: ReadDecimalBefore(text, "账户余额"),
            Status: ReadAfter(text, "状　　态："),
            Plan: ReadAfter(text, "套　　餐："),
            BillingMode: ReadAfter(text, "计费方式："),
            BillingPeriod: ReadAfter(text, "计费周期："),
            CapturedAt: DateTimeOffset.Now);
    }

    private async Task<IReadOnlyList<OnlineDevice>> GetOnlineDevicesFromWebViewAsync()
    {
        var script = """
            (() => {
                try {
                    const request = new XMLHttpRequest();
                    request.open('GET', '/Self/dashboard/getOnlineList?t=' + Math.random() + '&order=asc', false);
                    request.setRequestHeader('Cache-Control', 'no-store');
                    request.send();
                    const text = request.responseText || '';
                    return { ok: request.status >= 200 && request.status < 300, status: request.status, text, currentUrl: location.href, bodyStart: text.slice(0, 180) };
                } catch (error) {
                    return { ok: false, status: 0, text: '', error: String(error), currentUrl: location.href };
                }
            })()
            """;

        var raw = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
        var json = NormalizeScriptJson(raw);
        using var responseDocument = JsonDocument.Parse(json);
        var root = responseDocument.RootElement;

        if (TryGetString(root, "error", out var scriptError) && !string.IsNullOrWhiteSpace(scriptError))
        {
            throw new InvalidOperationException($"Online list script error: {scriptError}");
        }

        if (!TryGetString(root, "text", out var body))
        {
            throw new InvalidOperationException($"Online list response did not contain text: {Shorten(json)}");
        }

        using var document = JsonDocument.Parse(body);
        var devices = new List<OnlineDevice>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var downKb = ReadDouble(item, "downFlow");
            var upKb = ReadDouble(item, "upFlow");
            var seconds = ReadDouble(item, "useTime");

            devices.Add(new OnlineDevice(
                LoginTime: ReadString(item, "loginTime"),
                Ip: ReadString(item, "ip"),
                Mac: FormatMac(ReadString(item, "mac")),
                HostName: ReadString(item, "hostName"),
                TerminalType: ReadString(item, "terminalType").TrimStart('#'),
                DownloadMb: downKb / 1024d,
                UploadMb: upKb / 1024d,
                UseTime: TimeSpan.FromSeconds(seconds),
                SessionId: ReadString(item, "sessionId")));
        }

        return devices;
    }

    private async Task<bool> OfflineDeviceAsync(string sessionId)
    {
        var escapedSessionId = JsonSerializer.Serialize(sessionId);
        var script = $$"""
            (() => {
                try {
                    const sessionid = {{escapedSessionId}};
                    const request = new XMLHttpRequest();
                    request.open('GET', '/Self/dashboard/tooffline?sessionid=' + encodeURIComponent(sessionid) + '&t=' + Math.random(), false);
                    request.setRequestHeader('Cache-Control', 'no-store');
                    request.send();
                    let data = {};
                    try { data = JSON.parse(request.responseText || '{}'); } catch (_) {}
                    return { ok: request.status >= 200 && request.status < 300, success: !!data.success, status: request.status, body: (request.responseText || '').slice(0, 160) };
                } catch (error) {
                    return { ok: false, success: false, status: 0, error: String(error) };
                }
            })()
            """;

        var raw = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
        var json = NormalizeScriptJson(raw);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (TryGetString(root, "error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(error);
        }

        return TryGetBoolean(root, "success", out var success) && success;
    }

    private async Task EnsureDashboardPageAsync()
    {
        var currentUrl = LoginWebView.Source?.ToString() ?? string.Empty;
        if (currentUrl.Contains("/Self/dashboard", StringComparison.OrdinalIgnoreCase)
            && !currentUrl.Contains("getOnlineList", StringComparison.OrdinalIgnoreCase)
            && !currentUrl.Contains("getLoginHistory", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var waitForNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            LoginWebView.CoreWebView2.NavigationCompleted -= handler;
            waitForNavigation.TrySetResult();
        };

        LoginWebView.CoreWebView2.NavigationCompleted += handler;
        LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
        var completed = await Task.WhenAny(waitForNavigation.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        if (completed != waitForNavigation.Task)
        {
            LoginWebView.CoreWebView2.NavigationCompleted -= handler;
        }
    }

    private void PromptLoginIfNeeded(bool forceShow)
    {
        if (forceShow || !_loginPromptShown)
        {
            _loginPromptShown = true;
            ShowPage(LoginPanel, "校园网登录", "会话过期后需要重新登录");
            LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
        }

        SetStatus(AppConnectionState.LoginRequired, "校园网会话不可用：请登录到首页，APP 会自动保存会话。");
    }

    private void ApplyAccount(AccountSnapshot account)
    {
        UsedTrafficText.Text = $"{account.UsedTrafficMb:N0} M";
        AvailableTrafficText.Text = $"{account.AvailableTrafficMb:N0} M";
        AccountStatusText.Text = account.Status;
        BalanceText.Text = $"{account.Balance:N2} 元";
        PlanText.Text = account.Plan;
        BillingModeText.Text = account.BillingMode;
        BillingPeriodText.Text = account.BillingPeriod;
        CheckAccountThresholds(account);
    }

    private void CheckAccountThresholds(AccountSnapshot account)
    {
        var availableThresholdMb = ReadThreshold(AvailableThresholdGbTextBox.Text, 2) * 1024;
        if (!_availableTrafficNotified && account.AvailableTrafficMb > 0 && account.AvailableTrafficMb <= availableThresholdMb)
        {
            _availableTrafficNotified = true;
            ShowTrayNotice("剩余流量提醒", $"校园网剩余流量约 {account.AvailableTrafficMb:N0} MB。");
        }

        var balanceThreshold = (decimal)ReadThreshold(BalanceThresholdTextBox.Text, 5);
        if (!_balanceNotified && account.Balance <= balanceThreshold)
        {
            _balanceNotified = true;
            ShowTrayNotice("余额提醒", $"校园网余额 {account.Balance:N2} 元。");
        }
    }

    private void CheckSessionTrafficThreshold(long localBytes)
    {
        var thresholdBytes = ReadThreshold(SessionThresholdGbTextBox.Text, 5) * 1024d * 1024 * 1024;
        if (!_sessionTrafficNotified && localBytes >= thresholdBytes)
        {
            _sessionTrafficNotified = true;
            ShowTrayNotice("本次流量提醒", $"本次使用已达到 {FormatBytes(localBytes)}。");
        }
    }

    private void ShowTrayNotice(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);
        _ = _logger.InfoAsync($"{title}: {message}");
    }

    private void SetStatus(AppConnectionState state, string detail)
    {
        _campusStatus = state switch
        {
            AppConnectionState.Monitoring => "本机监控中",
            AppConnectionState.Syncing => "正在同步",
            AppConnectionState.Connected => "校园网已连接",
            AppConnectionState.LoginRequired => "需要登录",
            AppConnectionState.CampusUnavailable => "后台不可达",
            AppConnectionState.Error => "同步异常",
            _ => "运行中"
        };

        StatusText.Text = $"{_campusStatus}\n{detail}";
        UpdateTrayLiveInfo();
    }

    private async Task RefreshUsageStatsAsync()
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        TodayUsageText.Text = FormatBytes(await _trafficRepository.GetTransferredBytesSinceAsync(todayStart));
        MonthUsageText.Text = FormatBytes(await _trafficRepository.GetTransferredBytesSinceAsync(monthStart));
        _recentTrend = await _trafficRepository.GetRecentMinuteUsageAsync(10);
        _dailyTrend = await _trafficRepository.GetRecentDailyUsageAsync(7);
        RenderTrendCharts();
    }

    private void RenderTrendCharts()
    {
        if (RecentTrendHost is not null)
        {
            RecentTrendHost.Content = CreateLineChart(_recentTrend);
            var maxRecent = _recentTrend.Count == 0 ? 0 : _recentTrend.Max(point => point.Bytes);
            RecentTrendHintText.Text = maxRecent > 0
                ? $"峰值每分钟 {FormatBytes(maxRecent)}"
                : "等待更多采样数据";
        }

        if (DailyTrendHost is not null)
        {
            DailyTrendHost.Content = CreateColumnChart(_dailyTrend);
            var maxDaily = _dailyTrend.Count == 0 ? 0 : _dailyTrend.Max(point => point.Bytes);
            DailyTrendHintText.Text = maxDaily > 0
                ? $"最近 7 天峰值 {FormatBytes(maxDaily)}"
                : "每天本机上传 + 下载合计";
        }
    }

    private static SfChart CreateLineChart(IReadOnlyList<TrafficUsagePoint> points)
    {
        var chart = CreateBaseChart();
        var data = points
            .Select(point => new ChartPoint(point.CapturedAt.ToString("HH:mm"), BytesToMb(point.Bytes)))
            .ToList();

        chart.Series.Add(new FastLineSeries
        {
            ItemsSource = data,
            XBindingPath = nameof(ChartPoint.Label),
            YBindingPath = nameof(ChartPoint.ValueMb),
            Label = "每分钟流量",
            Interior = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            StrokeThickness = 3,
            ShowTooltip = true
        });
        return chart;
    }

    private static SfChart CreateColumnChart(IReadOnlyList<TrafficUsagePoint> points)
    {
        var chart = CreateBaseChart();
        var data = points
            .Select(point => new ChartPoint(point.CapturedAt.ToString("MM-dd"), BytesToMb(point.Bytes)))
            .ToList();

        chart.Series.Add(new ColumnSeries
        {
            ItemsSource = data,
            XBindingPath = nameof(ChartPoint.Label),
            YBindingPath = nameof(ChartPoint.ValueMb),
            Label = "每日流量",
            Interior = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            ShowTooltip = true
        });
        return chart;
    }

    private static SfChart CreateBaseChart()
    {
        var chart = new SfChart
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0),
            Padding = new Thickness(0)
        };

        chart.PrimaryAxis = new CategoryAxis
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            LabelPlacement = LabelPlacement.BetweenTicks,
            MajorGridLineStyle = CreateGridLineStyle(0)
        };
        chart.SecondaryAxis = new NumericalAxis
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            LabelFormat = "0.## MB",
            Minimum = 0,
            MajorGridLineStyle = CreateGridLineStyle(1)
        };
        return chart;
    }

    private static Style CreateGridLineStyle(double opacity)
    {
        var style = new Style(typeof(Line));
        style.Setters.Add(new Setter(Shape.StrokeProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240))));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, opacity));
        style.Setters.Add(new Setter(Shape.StrokeThicknessProperty, 1d));
        return style;
    }

    private static double BytesToMb(long bytes)
    {
        return Math.Round(bytes / 1024d / 1024d, 2);
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPanel, "流量总览", "本机实时监控 + CAUC Dr.COM 后台官方数据");
    }

    private async void LoginNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LoginPanel, "校园网登录", "登录一次后，会话过期前会自动复用");
        await InitializeLoginWebViewAsync();
        LoginWebView.Source ??= new Uri("https://www.cauc.edu.cn/Self/dashboard");
    }

    private void DevicesNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(DevicesPanel, "在线设备", "查看当前账号在线终端");
    }

    private async void StatsNav_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUsageStatsAsync();
        ShowPage(StatsPanel, "历史统计", "基于本机采样数据库聚合");
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPanel, "设置", "管理登录状态和本地配置");
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || AvailableThresholdGbTextBox is null || BalanceThresholdTextBox is null || SessionThresholdGbTextBox is null || FloatingMeterCheckBox is null)
        {
            return;
        }

        try
        {
            _startupService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            StatusText.Text = StartWithWindowsCheckBox.IsChecked == true
                ? "已开启开机自启。"
                : "已关闭开机自启。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"开机自启设置失败：{ex.Message}";
            StartWithWindowsCheckBox.IsChecked = _startupService.IsEnabled();
        }
    }

    private void NetworkAdapterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NetworkAdapterComboBox.SelectedItem is not NetworkAdapterOption option)
        {
            return;
        }

        _trafficMonitor.SelectAdapter(option.Id == "all" ? null : option.Id);
        _firstSample = null;
        if (!_isLoadingSettings)
        {
            SaveSettingsFromControls();
        }

        StatusText.Text = option.Id == "all"
            ? "已切换为统计全部活动网卡。"
            : $"已切换统计网卡：{option.Name}";
    }

    private void LoadNetworkAdapters()
    {
        var options = new List<NetworkAdapterOption>
        {
            new("all", "全部活动网卡")
        };
        options.AddRange(_trafficMonitor.GetAdapters());
        NetworkAdapterComboBox.ItemsSource = options;
        NetworkAdapterComboBox.SelectedValue = options.Any(option => option.Id == _settings.SelectedAdapterId)
            ? _settings.SelectedAdapterId
            : "all";
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromControls();
    }

    private void ApplySettingsToControls()
    {
        FloatingMeterCheckBox.IsChecked = _settings.ShowFloatingMeter;
        AvailableThresholdGbTextBox.Text = _settings.AvailableThresholdGb.ToString("0.###", CultureInfo.InvariantCulture);
        BalanceThresholdTextBox.Text = _settings.BalanceThresholdYuan.ToString("0.###", CultureInfo.InvariantCulture);
        SessionThresholdGbTextBox.Text = _settings.SessionThresholdGb.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SaveSettingsFromControls()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _settings.AvailableThresholdGb = ReadThreshold(AvailableThresholdGbTextBox.Text, 2);
        _settings.BalanceThresholdYuan = ReadThreshold(BalanceThresholdTextBox.Text, 5);
        _settings.SessionThresholdGb = ReadThreshold(SessionThresholdGbTextBox.Text, 5);
        _settings.ShowFloatingMeter = FloatingMeterCheckBox.IsChecked == true;

        if (NetworkAdapterComboBox.SelectedItem is NetworkAdapterOption option)
        {
            _settings.SelectedAdapterId = option.Id;
        }

        _settingsStore.Save(_settings);
        ApplyFloatingMeterSetting();
    }

    private void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        _sessionStore.Clear();
        LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
        SetStatus(AppConnectionState.LoginRequired, "已清除校园网登录状态，下次同步需要重新登录。");
        ShowPage(LoginPanel, "校园网登录", "登录状态已清除");
        LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _logger.ExportToDesktop();
            StatusText.Text = $"诊断日志已导出：{path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出日志失败：{ex.Message}";
        }
    }

    private void ShowPage(UIElement activePanel, string title, string subtitle)
    {
        OverviewPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;

        activePanel.Visibility = Visibility.Visible;
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
    }

    private (Forms.NotifyIcon Icon, Forms.ToolStripMenuItem StatusItem, Forms.ToolStripMenuItem UsageItem, Forms.ToolStripMenuItem SpeedItem, Forms.ToolStripMenuItem MiniWindowItem) CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var statusItem = new Forms.ToolStripMenuItem("状态：等待同步") { Enabled = false };
        var usageItem = new Forms.ToolStripMenuItem("本次用量：0 MB") { Enabled = false };
        var speedItem = new Forms.ToolStripMenuItem("网速：↓ 0 KB/s  ↑ 0 KB/s") { Enabled = false };
        var miniWindowItem = new Forms.ToolStripMenuItem("显示迷你流量窗", null, (_, _) => Dispatcher.Invoke(ToggleMiniTrafficWindow))
        {
            CheckOnClick = false
        };

        menu.Items.Add(statusItem);
        menu.Items.Add(usageItem);
        menu.Items.Add(speedItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开总览", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            OverviewNav_Click(this, new RoutedEventArgs());
        }));
        menu.Items.Add("打开历史统计", null, (_, _) => Dispatcher.Invoke(async () =>
        {
            ShowFromTray();
            await RefreshUsageStatsAsync();
            ShowPage(StatsPanel, "历史统计", "基于本机采样数据库聚合");
        }));
        menu.Items.Add("同步校园网", null, (_, _) => Dispatcher.Invoke(async () => await SyncCampusAsync(true)));
        menu.Items.Add(miniWindowItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "CAUCNet Traffic",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return (icon, statusItem, usageItem, speedItem, miniWindowItem);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void ToggleMiniTrafficWindow()
    {
        _settings.ShowFloatingMeter = !_settings.ShowFloatingMeter;
        if (FloatingMeterCheckBox is not null)
        {
            FloatingMeterCheckBox.IsChecked = _settings.ShowFloatingMeter;
        }

        _settingsStore.Save(_settings);
        ApplyFloatingMeterSetting();
    }

    private void ApplyFloatingMeterSetting()
    {
        if (_settings.ShowFloatingMeter)
        {
            EnsureMiniTrafficWindow();
            _miniTrafficWindow?.Show();
            UpdateMiniTrafficWindow();
        }
        else
        {
            _miniTrafficWindow?.Hide();
        }

        _trayMiniWindowItem.Checked = _settings.ShowFloatingMeter;
        _trayMiniWindowItem.Text = _settings.ShowFloatingMeter ? "隐藏迷你流量窗" : "显示迷你流量窗";
    }

    private void EnsureMiniTrafficWindow()
    {
        if (_miniTrafficWindow is not null)
        {
            return;
        }

        _miniTrafficWindow = new MiniTrafficWindow();
        _miniTrafficWindow.UserClosedMiniWindow += (_, _) =>
        {
            _settings.ShowFloatingMeter = false;
            if (FloatingMeterCheckBox is not null)
            {
                FloatingMeterCheckBox.IsChecked = false;
            }

            _settingsStore.Save(_settings);
            ApplyFloatingMeterSetting();
        };
        _miniTrafficWindow.PlaceNearTaskbar();
    }

    private void UpdateMiniTrafficWindow()
    {
        _miniTrafficWindow?.UpdateTraffic(_currentLocalUsage, _currentDownloadRate, _currentUploadRate);
    }

    private void UpdateTrayLiveInfo()
    {
        _trayStatusItem.Text = $"状态：{_campusStatus}";
        _trayUsageItem.Text = $"本次用量：{_currentLocalUsage}";
        _traySpeedItem.Text = $"网速：↓ {_currentDownloadRate}  ↑ {_currentUploadRate}";

        var text = $"CAUCNet Traffic\n{_campusStatus}\n{_currentLocalUsage}  ↓{_currentDownloadRate} ↑{_currentUploadRate}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit)
        {
            _miniTrafficWindow?.Close();
            DisposeNotifyIcon();
            return;
        }

        var result = ShowCloseChoiceDialog();

        if (result == CloseChoice.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowTrayNotice("CAUCNet Traffic", "已最小化到托盘，仍在后台监测流量。");
            return;
        }

        if (result == CloseChoice.Exit)
        {
            _allowExit = true;
            _miniTrafficWindow?.Close();
            DisposeNotifyIcon();
            return;
        }

        e.Cancel = true;
    }

    private CloseChoice ShowCloseChoiceDialog()
    {
        var dialog = new Window
        {
            Title = "退出 CAUCNet Traffic",
            Owner = this,
            Width = 420,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "要退出程序，还是最小化到托盘继续监测？",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.Black,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var minimizeButton = new System.Windows.Controls.Button
        {
            Content = "最小化到托盘",
            Padding = new Thickness(14, 10, 14, 10),
            FontWeight = FontWeights.SemiBold
        };
        var exitButton = new System.Windows.Controls.Button
        {
            Content = "直接退出",
            Padding = new Thickness(14, 10, 14, 10),
            FontWeight = FontWeights.SemiBold
        };

        var choice = CloseChoice.Cancel;
        minimizeButton.Click += (_, _) =>
        {
            choice = CloseChoice.MinimizeToTray;
            dialog.DialogResult = true;
        };
        exitButton.Click += (_, _) =>
        {
            choice = CloseChoice.Exit;
            dialog.DialogResult = true;
        };

        Grid.SetColumn(minimizeButton, 0);
        Grid.SetColumn(exitButton, 2);
        buttons.Children.Add(minimizeButton);
        buttons.Children.Add(exitButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();
        return choice;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _miniTrafficWindow?.Close();
        DisposeNotifyIcon();
        Close();
    }

    private void DisposeNotifyIcon()
    {
        if (_notifyIcon.Visible)
        {
            _notifyIcon.Visible = false;
        }

        _notifyIcon.Dispose();
    }

    private static string NormalizeScriptJson(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || raw == "null")
        {
            return "{}";
        }

        return raw[0] == '"'
            ? JsonSerializer.Deserialize<string>(raw) ?? "{}"
            : raw;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return bool.TryParse(property.ToString(), out value);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ToString();
        return true;
    }

    private static string ReadAfter(string text, string label)
    {
        var index = text.IndexOf(label, StringComparison.Ordinal);
        if (index < 0)
        {
            return "-";
        }

        var rest = text[(index + label.Length)..]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return rest.FirstOrDefault() ?? "-";
    }

    private static double ReadDoubleBefore(string text, string label)
    {
        var match = Regex.Match(text, $@"(?<value>\d+(?:\.\d+)?)\s*M\s*\n\s*{Regex.Escape(label)}");
        return match.Success
            ? double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            : 0d;
    }

    private static decimal ReadDecimalBefore(string text, string label)
    {
        var match = Regex.Match(text, $@"(?<value>\d+(?:\.\d+)?)\s*元\s*\n\s*{Regex.Escape(label)}");
        return match.Success
            ? decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            : 0m;
    }

    private static double ReadThreshold(string text, double fallback)
    {
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) ? property.ToString() : string.Empty;
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        return double.TryParse(ReadString(element, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
    }

    private static string FormatMac(string mac)
    {
        if (mac.Length != 12)
        {
            return mac;
        }

        return string.Join("-", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / 1024 / 1024:N2} MB/s"
            : $"{bytesPerSecond / 1024:N0} KB/s";
    }

    private static string FormatBytes(long bytes)
    {
        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024 / 1024:N2} GB"
            : $"{bytes / 1024d / 1024:N1} MB";
    }

    private static string Shorten(string text)
    {
        return text[..Math.Min(text.Length, 220)];
    }
}
