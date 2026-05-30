using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CampusNetTraffic.Models;
using CampusNetTraffic.Services;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CampusNetTraffic;

public partial class MainWindow : Window
{
    private readonly NetworkTrafficMonitor _trafficMonitor = new();
    private readonly TrafficRepository _trafficRepository = new();
    private readonly DrcomCampusClient _campusClient = new();
    private readonly CampusSessionStore _sessionStore = new();
    private readonly StartupService _startupService = new();
    private readonly AppLogger _logger = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _campusTimer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private TrafficSample? _firstSample;
    private TrafficSample? _lastSavedSample;
    private bool _isCampusSyncing;
    private bool _loginPromptShown;
    private bool _sessionRestored;
    private bool _allowExit;
    private bool _availableTrafficNotified;
    private bool _balanceNotified;
    private bool _sessionTrafficNotified;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;

        _campusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _campusTimer.Tick += async (_, _) => await SyncCampusAsync(false);

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;

        _notifyIcon = CreateNotifyIcon();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartWithWindowsCheckBox.IsChecked = _startupService.IsEnabled();
        LoadNetworkAdapters();
        await _trafficRepository.InitializeAsync();
        await InitializeLoginWebViewAsync();
        _timer.Start();
        _campusTimer.Start();
        await SyncCampusAsync(false);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        var sample = _trafficMonitor.Capture();
        _firstSample ??= sample;

        DownloadSpeedText.Text = FormatRate(sample.DownloadBytesPerSecond);
        UploadSpeedText.Text = FormatRate(sample.UploadBytesPerSecond);

        var localBytes = Math.Max(0, sample.TotalReceivedBytes - _firstSample.TotalReceivedBytes)
            + Math.Max(0, sample.TotalSentBytes - _firstSample.TotalSentBytes);
        var localTotal = FormatBytes(localBytes);
        LocalTotalText.Text = $"\u672c\u6b21\u8fd0\u884c\uff1a{localTotal}";
        HeroUsageText.Text = localTotal;
        HeroRateText.Text = $"{FormatRate(sample.DownloadBytesPerSecond)}  /  {FormatRate(sample.UploadBytesPerSecond)}";
        _notifyIcon.Text = $"CampusNet Traffic\n下载 {FormatRate(sample.DownloadBytesPerSecond)}\n上传 {FormatRate(sample.UploadBytesPerSecond)}";
        CheckSessionTrafficThreshold(localBytes);
        LastSampleText.Text = $"\u6700\u8fd1\u91c7\u6837\uff1a{sample.CapturedAt:HH:mm:ss}";

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
        StatusText.Text = "\u8bf7\u5728\u7f51\u9875\u767b\u5f55\u6846\u4e2d\u767b\u5f55\uff0c\u5b8c\u6210\u540e\u70b9\u51fb\u201c\u540c\u6b65\u6821\u56ed\u7f51\u201d\u3002";
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
            if (success)
            {
                StatusText.Text = $"已注销设备：{device.Ip}";
                await SyncCampusAsync(true);
            }
            else
            {
                StatusText.Text = $"注销失败：{device.Ip}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"注销设备失败：{ex.Message}";
        }
    }

    private async Task InitializeLoginWebViewAsync()
    {
        if (LoginWebView.CoreWebView2 is not null)
        {
            return;
        }

        var userDataFolder = Path.Combine(
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
                    StatusText.Text = "\u6b63\u5728\u540c\u6b65\u6821\u56ed\u7f51\u6570\u636e\uff0c\u8bf7\u7a0d\u7b49\u51e0\u79d2\u3002";
                }
                return;
            }

            _isCampusSyncing = true;
            if (showLoginHint)
            {
                StatusText.Text = "\u6b63\u5728\u540c\u6b65\u6821\u56ed\u7f51\u6570\u636e...";
            }

            await InitializeLoginWebViewAsync();
            await EnsureDashboardPageAsync();

            var currentUrl = LoginWebView.Source?.ToString() ?? string.Empty;
            if (currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                if (showLoginHint)
                {
                    StatusText.Text = "\u5f53\u524d\u8fd8\u5728\u767b\u5f55\u9875\uff0c\u8bf7\u767b\u5f55\u6210\u529f\u8fdb\u5165 dashboard \u540e\u518d\u540c\u6b65\u3002";
                }
                return;
            }

            var account = await GetAccountSnapshotFromWebViewAsync();
            var devices = await GetOnlineDevicesFromWebViewAsync();
            await SaveCurrentSessionAsync();
            await _logger.InfoAsync($"Campus sync ok. Devices={devices.Count}, Used={account.UsedTrafficMb:N0}M, Available={account.AvailableTrafficMb:N0}M");

            ApplyAccount(account);
            DeviceGrid.ItemsSource = devices;
            StatusText.Text = $"\u81ea\u52a8\u540c\u6b65\u4e2d\uff1a{account.CapturedAt:HH:mm:ss}\uff0c\u5728\u7ebf\u8bbe\u5907 {devices.Count} \u53f0";
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "\u540c\u6b65\u8d85\u65f6\uff1a\u6821\u56ed\u7f51\u540e\u53f0\u8bf7\u6c42\u8d85\u8fc7 8 \u79d2\u672a\u54cd\u5e94\uff0c\u8bf7\u786e\u8ba4\u5185\u5d4c\u7f51\u9875\u5df2\u8fdb\u5165\u9996\u9875\u540e\u518d\u8bd5\u3002";
            await _logger.ErrorAsync("Campus sync timeout.");
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("Campus sync failed.", ex);
            if (ex.Message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("redirected to login", StringComparison.OrdinalIgnoreCase))
            {
                _sessionStore.Clear();
                PromptLoginIfNeeded(showLoginHint);
            }
            else if (showLoginHint)
            {
                StatusText.Text = $"\u540c\u6b65\u5931\u8d25\uff1a{ex.Message}";
            }
            else if (!ex.Message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                && !ex.Message.Contains("redirected to login", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = $"\u81ea\u52a8\u540c\u6b65\u6682\u65f6\u5931\u8d25\uff1a{ex.Message}";
            }
        }
        finally
        {
            _isCampusSyncing = false;
        }
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

        var cookie = coreWebView.CookieManager.CreateCookie(
            "JSESSIONID",
            sessionId,
            "www.cauc.edu.cn",
            "/Self");
        cookie.IsHttpOnly = true;
        cookie.IsSecure = true;
        coreWebView.CookieManager.AddOrUpdateCookie(cookie);
        StatusText.Text = "\u5df2\u8f7d\u5165\u4e0a\u6b21\u6821\u56ed\u7f51\u4f1a\u8bdd\uff0c\u6b63\u5728\u81ea\u52a8\u68c0\u6d4b\u662f\u5426\u8fc7\u671f\u3002";
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
                    const currentUrl = location.href;
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
                    const loginForm = parsed.querySelector('form[action*=\"login/verify\"]');
                    const accountInput = parsed.querySelector('[name=\"account\"]');
                    const hasTraffic = html.includes('已用流量') || html.includes('可用流量');
                    const isLogin = !!(loginForm && accountInput && !hasTraffic);
                    parsed.querySelectorAll('script, style').forEach(node => node.remove());
                    const text = parsed.body ? parsed.body.innerText : '';
                    return { ok: request.status >= 200 && request.status < 300, status: request.status, isLogin, text, currentUrl, htmlStart: html.slice(0, 180) };
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
            throw new InvalidOperationException($"Unexpected dashboard script result: {json[..Math.Min(json.Length, 220)]}");
        }

        if (isLogin)
        {
            throw new InvalidOperationException("Campus session is not logged in. Please log in inside the app first.");
        }

        var text = TryGetString(root, "text", out var parsedText) ? parsedText : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Dashboard response did not contain text: {json[..Math.Min(json.Length, 220)]}");
        }

        return new AccountSnapshot(
            UsedTrafficMb: ReadDoubleBefore(text, "\u5df2\u7528\u6d41\u91cf"),
            AvailableTrafficMb: ReadDoubleBefore(text, "\u53ef\u7528\u6d41\u91cf"),
            Balance: ReadDecimalBefore(text, "\u8d26\u6237\u4f59\u989d"),
            Status: ReadAfter(text, "\u72b6\u3000\u3000\u6001\uff1a"),
            Plan: ReadAfter(text, "\u5957\u3000\u3000\u9910\uff1a"),
            BillingMode: ReadAfter(text, "\u8ba1\u8d39\u65b9\u5f0f\uff1a"),
            BillingPeriod: ReadAfter(text, "\u8ba1\u8d39\u5468\u671f\uff1a"),
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
            throw new InvalidOperationException($"Online list response did not contain text: {json[..Math.Min(json.Length, 220)]}");
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
        if (completed != waitForNavigation.Task && handler is not null)
        {
            LoginWebView.CoreWebView2.NavigationCompleted -= handler;
        }
    }

    private async Task<(string CookieHeader, string DebugText)> BuildCampusCookieHeaderAsync()
    {
        var cookieManager = LoginWebView.CoreWebView2.CookieManager;
        var cookies = await cookieManager.GetCookiesAsync("https://www.cauc.edu.cn/Self/dashboard");
        var debugText = $"\u5f53\u524d Cookie \u6570\uff1a{cookies.Count}\uff0c\u5f53\u524d\u9875\uff1a{LoginWebView.Source}";
        var sessionCookie = cookies
            .Where(cookie => cookie.Name.Equals("JSESSIONID", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(cookie => cookie.Path.StartsWith("/Self", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(cookie => cookie.Path.Length)
            .FirstOrDefault();

        if (sessionCookie is null)
        {
            return (string.Empty, debugText);
        }

        debugText += $"\uff0cJSESSIONID Path\uff1a{sessionCookie.Path}";
        return ($"{sessionCookie.Name}={sessionCookie.Value}", debugText);
    }

    private void PromptLoginIfNeeded(bool forceShow)
    {
        if (forceShow || !_loginPromptShown)
        {
            _loginPromptShown = true;
            ShowPage(LoginPanel, "校园网登录", "会话过期后需要重新登录");
            LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
        }

        StatusText.Text = "\u5c1a\u672a\u8fde\u63a5\u6821\u56ed\u7f51\u540e\u53f0\uff1a\u8bf7\u5728\u53f3\u4fa7\u7f51\u9875\u767b\u5f55\u5230\u9996\u9875\uff0cAPP \u4f1a\u81ea\u52a8\u5f00\u59cb\u5b9e\u65f6\u540c\u6b65\u3002";
    }

    private void ApplyAccount(AccountSnapshot account)
    {
        UsedTrafficText.Text = $"{account.UsedTrafficMb:N0} M";
        AvailableTrafficText.Text = $"{account.AvailableTrafficMb:N0} M";
        AccountStatusText.Text = account.Status;
        BalanceText.Text = $"{account.Balance:N2} \u5143";
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

    private static double ReadThreshold(string text, double fallback)
    {
        return double.TryParse(text, out var value) && value > 0 ? value : fallback;
    }

    private async Task RefreshUsageStatsAsync()
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        TodayUsageText.Text = FormatBytes(await _trafficRepository.GetTransferredBytesSinceAsync(todayStart));
        MonthUsageText.Text = FormatBytes(await _trafficRepository.GetTransferredBytesSinceAsync(monthStart));
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
        NetworkAdapterComboBox.SelectedValue = "all";
    }

    private void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        _sessionStore.Clear();
        LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
        StatusText.Text = "已清除校园网登录状态，下次同步需要重新登录。";
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

    private Forms.NotifyIcon CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("同步校园网", null, (_, _) => Dispatcher.Invoke(async () => await SyncCampusAsync(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "CampusNet Traffic",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit || MinimizeToTrayCheckBox.IsChecked != true)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
        _notifyIcon.ShowBalloonTip(
            2500,
            "CampusNet Traffic",
            "已最小化到托盘，仍在后台监测流量。",
            Forms.ToolTipIcon.Info);
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Close();
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
        var match = System.Text.RegularExpressions.Regex.Match(text, $@"(?<value>\d+(?:\.\d+)?)\s*M\s*\n\s*{System.Text.RegularExpressions.Regex.Escape(label)}");
        return match.Success
            ? double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0d;
    }

    private static decimal ReadDecimalBefore(string text, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, $@"(?<value>\d+(?:\.\d+)?)\s*\u5143\s*\n\s*{System.Text.RegularExpressions.Regex.Escape(label)}");
        return match.Success
            ? decimal.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0m;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) ? property.ToString() : string.Empty;
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        return double.TryParse(ReadString(element, name), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value)
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
}
