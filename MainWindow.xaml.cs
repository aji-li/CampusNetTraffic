using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Reflection;
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
    private static readonly TimeSpan DeviceAutoRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly NetworkTrafficMonitor _trafficMonitor = new();
    private readonly TrafficRepository _trafficRepository = new();
    private readonly CampusSessionStore _sessionStore = new();
    private readonly StartupService _startupService = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly AppLogger _logger = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
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
    private bool _loginExpiredNotified;
    private bool _isLoadingSettings = true;
    private int _campusSyncFailureCount;
    private AppSettings _settings = new();
    private IReadOnlyList<TrafficUsagePoint> _recentTrend = Array.Empty<TrafficUsagePoint>();
    private IReadOnlyList<TrafficUsagePoint> _dailyTrend = Array.Empty<TrafficUsagePoint>();
    private MiniTrafficWindow? _miniTrafficWindow;
    private string _currentLocalUsage = "0 MB";
    private string _currentDownloadRate = "0 KB/s";
    private string _currentUploadRate = "0 KB/s";
    private string _campusStatus = "等待同步";
    private DateTimeOffset? _lastDeviceSyncAt;
    private int _lastOnlineDeviceCount;

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
    private sealed record UpdateInfo(string Version, string? InstallerUrl, string? PortableUrl, string[] Notes);

    public MainWindow()
    {
        InitializeComponent();
        SetActiveNav(OverviewNavButton);

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
        ApplyConfiguredSyncInterval();
        _isLoadingSettings = false;
        ApplyFloatingMeterSetting();

        await _trafficRepository.InitializeAsync();
        _ = _trafficRepository.CleanupAsync();
        await InitializeLoginWebViewAsync();
        _timer.Start();
        _campusTimer.Start();
        await SyncCampusAsync(false);

        if (!_settings.HasCompletedFirstRunGuide)
        {
            _ = Dispatcher.BeginInvoke(() => ShowFirstRunGuide(true), DispatcherPriority.ApplicationIdle);
        }
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
        await StartCampusLoginAsync();
    }

    private async Task StartCampusLoginAsync()
    {
        ShowPage(LoginPanel, "校园网登录", "登录一次后，会话过期前会自动复用");
        SetActiveNav(LoginNavButton);
        await InitializeLoginWebViewAsync();
        LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
        SetStatus(AppConnectionState.LoginRequired, "请在网页登录框中登录，完成后 APP 会自动同步。");
    }

    private void HideBrowser_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(OverviewPanel, "流量总览", "本机实时监控 + 校园网后台数据");
    }

    private async void SyncCampus_Click(object sender, RoutedEventArgs e)
    {
        await SyncCampusAsync(true, true);
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
                await SyncCampusAsync(true, true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(AppConnectionState.Error, $"注销设备失败：{GetFriendlyErrorMessage(ex)}");
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

    private async Task SyncCampusAsync(bool showLoginHint, bool forceDeviceRefresh = false)
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
            IReadOnlyList<OnlineDevice>? devices = null;
            var shouldRefreshDevices = forceDeviceRefresh || ShouldAutoRefreshDevices();
            if (shouldRefreshDevices)
            {
                try
                {
                    devices = await GetOnlineDevicesFromWebViewAsync();
                    ApplyOnlineDevices(devices, DateTimeOffset.Now);
                }
                catch (Exception ex) when (!forceDeviceRefresh)
                {
                    await _logger.ErrorAsync("Auto device refresh skipped after failure.", ex);
                }
            }

            await SaveCurrentSessionAsync();
            await _logger.InfoAsync($"Campus sync ok. Devices={_lastOnlineDeviceCount}, DeviceRefresh={devices is not null}, Used={account.UsedTrafficMb:N0}M, Available={account.AvailableTrafficMb:N0}M");

            ApplyAccount(account);
            var deviceText = _lastDeviceSyncAt is null
                ? "在线设备未刷新"
                : $"在线设备 {_lastOnlineDeviceCount} 台，设备 {_lastDeviceSyncAt:HH:mm:ss} 刷新";
            SetStatus(AppConnectionState.Connected, $"校园网正常，账号 {account.CapturedAt:HH:mm:ss} 已同步，{deviceText}。");
            ResetCampusSyncBackoff();
        }
        catch (TaskCanceledException)
        {
            SetStatus(AppConnectionState.CampusUnavailable, "同步超时：校园网后台超过 8 秒未响应。");
            await _logger.ErrorAsync("Campus sync timeout.");
            IncreaseCampusSyncBackoff();
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("Campus sync failed.", ex);
            if (IsLoginExpiredError(ex))
            {
                _sessionStore.Clear();
                LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
                PromptLoginIfNeeded(showLoginHint);
                IncreaseCampusSyncBackoff();
            }
            else if (showLoginHint)
            {
                SetStatus(AppConnectionState.Error, $"同步失败：{GetFriendlyErrorMessage(ex)}");
                IncreaseCampusSyncBackoff();
            }
            else
            {
                SetStatus(AppConnectionState.CampusUnavailable, $"自动同步暂时失败：{GetFriendlyErrorMessage(ex)}");
                IncreaseCampusSyncBackoff();
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

    private static string GetFriendlyErrorMessage(Exception ex)
    {
        var message = ex.Message;
        if (IsLoginExpiredError(ex))
        {
            return "校园网登录已过期，请重新登录。";
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || ex is TaskCanceledException)
        {
            return "校园网后台响应超时，请稍后再试。";
        }

        if (message.Contains("too many", StringComparison.OrdinalIgnoreCase)
            || message.Contains("frequent", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429", StringComparison.OrdinalIgnoreCase))
        {
            return "同步可能过于频繁，已自动降低同步频率。";
        }

        if (message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            return "校园网后台要求重新登录，请打开网页登录页。";
        }

        if (message.Contains("json", StringComparison.OrdinalIgnoreCase)
            || message.Contains("script", StringComparison.OrdinalIgnoreCase)
            || message.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return "校园网后台数据格式变化或暂时不可用，请稍后再试。";
        }

        if (message.Contains("network", StringComparison.OrdinalIgnoreCase)
            || message.Contains("name resolution", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "网络连接异常，无法访问校园网后台。";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "校园网后台暂时无法访问。"
            : message;
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

    private bool ShouldAutoRefreshDevices()
    {
        return _lastDeviceSyncAt is null || DateTimeOffset.Now - _lastDeviceSyncAt >= DeviceAutoRefreshInterval;
    }

    private void ApplyOnlineDevices(IReadOnlyList<OnlineDevice> devices, DateTimeOffset refreshedAt)
    {
        _lastOnlineDeviceCount = devices.Count;
        _lastDeviceSyncAt = refreshedAt;
        DeviceGrid.ItemsSource = devices;
        DeviceRefreshStatusText.Text = $"最近刷新：{refreshedAt:HH:mm:ss}，在线设备 {devices.Count} 台";
    }

    private async Task RefreshOnlineDevicesAsync(bool showStatus)
    {
        if (_isCampusSyncing)
        {
            if (showStatus)
            {
                SetStatus(AppConnectionState.Syncing, "正在同步校园网数据，请稍后再刷新设备。");
            }

            return;
        }

        try
        {
            if (showStatus)
            {
                SetStatus(AppConnectionState.Syncing, "正在刷新在线设备列表...");
            }

            await InitializeLoginWebViewAsync();
            await EnsureDashboardPageAsync();
            var devices = await GetOnlineDevicesFromWebViewAsync();
            ApplyOnlineDevices(devices, DateTimeOffset.Now);
            await SaveCurrentSessionAsync();
            SetStatus(AppConnectionState.Connected, $"在线设备已刷新：{devices.Count} 台。");
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync("Refresh online devices failed.", ex);
            if (IsLoginExpiredError(ex))
            {
                _sessionStore.Clear();
                LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
                PromptLoginIfNeeded(showStatus);
            }
            else if (showStatus)
            {
                SetStatus(AppConnectionState.Error, $"刷新在线设备失败：{GetFriendlyErrorMessage(ex)}");
            }
        }
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
        if (!_loginExpiredNotified)
        {
            _loginExpiredNotified = true;
            ShowTrayNotice("需要重新登录", "校园网登录状态已过期，请打开 APP 完成网页登录。");
        }
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
        _loginExpiredNotified = false;
    }

    private void CheckAccountThresholds(AccountSnapshot account)
    {
        if (!_settings.EnableAvailableTrafficAlert && !_settings.EnableBalanceAlert)
        {
            return;
        }

        var availableThresholdMb = ReadThreshold(AvailableThresholdGbTextBox.Text, 2) * 1024;
        if (_settings.EnableAvailableTrafficAlert && !_availableTrafficNotified && account.AvailableTrafficMb > 0 && account.AvailableTrafficMb <= availableThresholdMb)
        {
            _availableTrafficNotified = true;
            ShowTrayNotice("剩余流量提醒", $"校园网剩余流量约 {account.AvailableTrafficMb:N0} MB。");
        }

        var balanceThreshold = (decimal)ReadThreshold(BalanceThresholdTextBox.Text, 5);
        if (_settings.EnableBalanceAlert && !_balanceNotified && account.Balance <= balanceThreshold)
        {
            _balanceNotified = true;
            ShowTrayNotice("余额提醒", $"校园网余额 {account.Balance:N2} 元。");
        }
    }

    private void CheckSessionTrafficThreshold(long localBytes)
    {
        if (!_settings.EnableSessionTrafficAlert)
        {
            return;
        }

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
        UpdateTopStatusBar(state, detail);
        UpdateTrayLiveInfo();
    }

    private void UpdateTopStatusBar(AppConnectionState state, string detail)
    {
        var (background, border, dot, title) = state switch
        {
            AppConnectionState.Connected => ("#ECFDF5", "#BBF7D0", "#22C55E", "已登录"),
            AppConnectionState.LoginRequired => ("#FFFBEB", "#FDE68A", "#F59E0B", "需重新登录"),
            AppConnectionState.CampusUnavailable => ("#FEF2F2", "#FECACA", "#EF4444", "后台不可达"),
            AppConnectionState.Error => ("#FEF2F2", "#FECACA", "#EF4444", "同步异常"),
            AppConnectionState.Syncing => ("#EEF4FF", "#C8D8FF", "#2563EB", "正在同步"),
            _ => ("#F8FAFC", "#E2E8F0", "#64748B", "本机监控中")
        };

        TopStatusBar.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(background));
        TopStatusBar.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(border));
        TopStatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dot));
        TopStatusTitleText.Text = title;
        TopStatusDetailText.Text = detail;
    }

    private void ResetCampusSyncBackoff()
    {
        _campusSyncFailureCount = 0;
        ApplyConfiguredSyncInterval();
    }

    private void IncreaseCampusSyncBackoff()
    {
        _campusSyncFailureCount = Math.Min(_campusSyncFailureCount + 1, 3);
        var seconds = _campusSyncFailureCount switch
        {
            1 => 60,
            2 => 120,
            _ => 180
        };
        _campusTimer.Interval = TimeSpan.FromSeconds(seconds);
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
        ShowPage(OverviewPanel, "流量总览", "本机实时监控 + 校园网后台数据");
        SetActiveNav(OverviewNavButton);
    }

    private async void LoginNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LoginPanel, "校园网登录", "登录一次后，会话过期前会自动复用");
        SetActiveNav(LoginNavButton);
        await InitializeLoginWebViewAsync();
        LoginWebView.Source ??= new Uri("https://www.cauc.edu.cn/Self/dashboard");
    }

    private async void DevicesNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(DevicesPanel, "在线设备", "查看当前账号在线终端");
        SetActiveNav(DevicesNavButton);
        if (_lastDeviceSyncAt is null || DateTimeOffset.Now - _lastDeviceSyncAt > TimeSpan.FromMinutes(1))
        {
            await RefreshOnlineDevicesAsync(true);
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOnlineDevicesAsync(true);
    }

    private async void StatsNav_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUsageStatsAsync();
        ShowPage(StatsPanel, "历史统计", "基于本机采样数据库聚合");
        SetActiveNav(StatsNavButton);
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPanel, "设置", "管理登录状态和本地配置");
        SetActiveNav(SettingsNavButton);
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings
            || AvailableThresholdGbTextBox is null
            || BalanceThresholdTextBox is null
            || SessionThresholdGbTextBox is null
            || FloatingMeterCheckBox is null
            || CloseToTrayWithoutPromptCheckBox is null
            || SyncIntervalComboBox is null
            || AvailableTrafficAlertCheckBox is null
            || BalanceAlertCheckBox is null
            || SessionTrafficAlertCheckBox is null
            || UpdateSourceTextBox is null)
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

    private void SyncIntervalComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveSettingsFromControls();
    }

    private void UpdateSourceTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromControls();
    }

    private void ApplySettingsToControls()
    {
        FloatingMeterCheckBox.IsChecked = _settings.ShowFloatingMeter;
        CloseToTrayWithoutPromptCheckBox.IsChecked = _settings.CloseWithoutPrompt || _settings.CloseToTrayWithoutPrompt;
        SyncIntervalComboBox.SelectedValue = _settings.CampusSyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        AvailableTrafficAlertCheckBox.IsChecked = _settings.EnableAvailableTrafficAlert;
        BalanceAlertCheckBox.IsChecked = _settings.EnableBalanceAlert;
        SessionTrafficAlertCheckBox.IsChecked = _settings.EnableSessionTrafficAlert;
        AvailableThresholdGbTextBox.Text = _settings.AvailableThresholdGb.ToString("0.###", CultureInfo.InvariantCulture);
        BalanceThresholdTextBox.Text = _settings.BalanceThresholdYuan.ToString("0.###", CultureInfo.InvariantCulture);
        SessionThresholdGbTextBox.Text = _settings.SessionThresholdGb.ToString("0.###", CultureInfo.InvariantCulture);
        UpdateSourceTextBox.Text = string.IsNullOrWhiteSpace(_settings.UpdateSourceUrl)
            ? AppSettings.DefaultUpdateSourceUrl
            : _settings.UpdateSourceUrl;
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
        _settings.CloseWithoutPrompt = CloseToTrayWithoutPromptCheckBox.IsChecked == true;
        _settings.CloseToTrayWithoutPrompt = _settings.CloseWithoutPrompt
            && _settings.CloseDefaultAction == AppSettings.CloseActionMinimizeToTray;
        _settings.CampusSyncIntervalSeconds = ReadSyncIntervalSeconds();
        _settings.EnableAvailableTrafficAlert = AvailableTrafficAlertCheckBox.IsChecked == true;
        _settings.EnableBalanceAlert = BalanceAlertCheckBox.IsChecked == true;
        _settings.EnableSessionTrafficAlert = SessionTrafficAlertCheckBox.IsChecked == true;
        _settings.UpdateSourceUrl = string.IsNullOrWhiteSpace(UpdateSourceTextBox.Text)
            ? AppSettings.DefaultUpdateSourceUrl
            : UpdateSourceTextBox.Text.Trim();

        if (NetworkAdapterComboBox.SelectedItem is NetworkAdapterOption option)
        {
            _settings.SelectedAdapterId = option.Id;
        }

        _settingsStore.Save(_settings);
        ApplyFloatingMeterSetting();
        if (_campusSyncFailureCount == 0)
        {
            ApplyConfiguredSyncInterval();
        }
    }

    private int ReadSyncIntervalSeconds()
    {
        if (SyncIntervalComboBox.SelectedValue is string text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds is 15 or 30 or 60 or 120)
        {
            return seconds;
        }

        return 120;
    }

    private void ApplyConfiguredSyncInterval()
    {
        var seconds = _settings.CampusSyncIntervalSeconds is 15 or 30 or 60 or 120
            ? _settings.CampusSyncIntervalSeconds
            : 120;
        _campusTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        _sessionStore.Clear();
        LoginWebView.CoreWebView2?.CookieManager.DeleteAllCookies();
        SetStatus(AppConnectionState.LoginRequired, "已清除校园网登录状态，下次同步需要重新登录。");
        ShowPage(LoginPanel, "校园网登录", "登录状态已清除");
        SetActiveNav(LoginNavButton);
        LoginWebView.Source = new Uri("https://www.cauc.edu.cn/Self/dashboard");
    }

    private async void ClearTrafficData_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定要清空本机流量统计数据吗？\n\n这会删除今日/本月统计和历史趋势数据，但不会影响校园网账号登录状态。",
            "清空本机流量统计",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _trafficRepository.ClearAllAsync();
        _firstSample = null;
        _lastSavedSample = null;
        _recentTrend = Array.Empty<TrafficUsagePoint>();
        _dailyTrend = Array.Empty<TrafficUsagePoint>();
        TodayUsageText.Text = "0 MB";
        MonthUsageText.Text = "0 MB";
        RenderTrendCharts();
        SetStatus(AppConnectionState.Monitoring, "本机流量统计数据已清空，正在重新开始采样。");
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _logger.ExportDiagnosticsToDesktop();
            StatusText.Text = $"诊断包已导出：{path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出诊断包失败：{GetFriendlyErrorMessage(ex)}";
        }
    }

    private void FirstRunGuide_Click(object sender, RoutedEventArgs e)
    {
        ShowFirstRunGuide(false);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromControls();
        var currentVersion = GetCurrentVersion();
        var updateSource = string.IsNullOrWhiteSpace(_settings.UpdateSourceUrl)
            ? AppSettings.DefaultUpdateSourceUrl
            : _settings.UpdateSourceUrl.Trim();

        if (!Uri.TryCreate(updateSource, UriKind.Absolute, out var updateUri)
            || updateUri.Scheme is not ("http" or "https"))
        {
            System.Windows.MessageBox.Show(
                "更新源地址无效，请填写 http 或 https 开头的 latest.json 地址。",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            StatusText.Text = "正在检查更新...";
            using var response = await _httpClient.GetAsync(updateUri);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var update = ParseUpdateInfo(json);

            if (!Version.TryParse(update.Version, out var latestVersion))
            {
                throw new InvalidOperationException("更新源中的 version 字段格式不正确。");
            }

            if (latestVersion <= currentVersion)
            {
                System.Windows.MessageBox.Show(
                    $"当前已是最新版本。\n\n当前版本：{currentVersion}\n更新源版本：{latestVersion}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText.Text = $"已是最新版本：{currentVersion}";
                return;
            }

            var downloadUrl = !string.IsNullOrWhiteSpace(update.InstallerUrl)
                ? update.InstallerUrl
                : update.PortableUrl;
            var notes = update.Notes.Length == 0
                ? "暂无更新日志。"
                : string.Join(Environment.NewLine, update.Notes.Select(note => $"· {note}"));
            var message = $"发现新版本：{latestVersion}\n当前版本：{currentVersion}\n\n更新内容：\n{notes}\n\n是否打开下载链接？";
            var result = System.Windows.MessageBox.Show(
                message,
                "发现新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                OpenExternalUrl(downloadUrl);
            }

            StatusText.Text = $"发现新版本：{latestVersion}";
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = $"检查更新失败：{GetFriendlyErrorMessage(ex)}";
            System.Windows.MessageBox.Show(
                $"无法连接更新源。\n\n请检查网络或更新源地址是否可访问。\n\n{GetFriendlyErrorMessage(ex)}",
                "检查更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "检查更新超时。";
            System.Windows.MessageBox.Show(
                "检查更新超时，请稍后再试。",
                "检查更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检查更新失败：{GetFriendlyErrorMessage(ex)}";
            System.Windows.MessageBox.Show(
                $"更新源格式不正确或不可用。\n\n{GetFriendlyErrorMessage(ex)}",
                "检查更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    }

    private static UpdateInfo ParseUpdateInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = ReadRequiredUpdateString(root, "version");
        var installerUrl = ReadOptionalUpdateString(root, "installerUrl");
        var portableUrl = ReadOptionalUpdateString(root, "portableUrl");
        var notes = Array.Empty<string>();

        if (root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.Array)
        {
            notes = notesElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .ToArray();
        }

        return new UpdateInfo(version, installerUrl, portableUrl, notes);
    }

    private static string ReadRequiredUpdateString(JsonElement root, string propertyName)
    {
        var value = ReadOptionalUpdateString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"更新源缺少 {propertyName} 字段。");
        }

        return value;
    }

    private static string? ReadOptionalUpdateString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutDialog();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        ShowPrivacyDialog();
    }

    private void ShowFirstRunGuide(bool markCompletedOnClose)
    {
        var dialog = new Window
        {
            Title = "首次使用 CAUCNet Traffic",
            Owner = this,
            Width = 560,
            Height = 390,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "欢迎使用 CAUCNet Traffic",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
        });
        header.Children.Add(new TextBlock
        {
            Text = "第一次使用建议按下面 3 步完成配置。",
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139))
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 22, 0, 22) };
        AddGuideLine(body, "1", "网页登录", "在内嵌网页登录校园网。APP 只复用浏览器会话，不保存明文密码。");
        AddGuideLine(body, "2", "同步校园网", "登录成功后点击同步，首页会显示官方已用流量、可用流量、余额和在线设备。");
        AddGuideLine(body, "3", "后台常驻", "关闭窗口时可选择最小化到托盘，继续实时统计本机流量。");
        body.Children.Add(new TextBlock
        {
            Text = "诊断包导出会自动脱敏账号、Cookie、IP 和 MAC。迷你流量窗、开机自启、提醒阈值都可以稍后在设置页调整。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139))
        });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var loginButton = new System.Windows.Controls.Button
        {
            Content = "开始网页登录",
            Padding = new Thickness(14, 11, 14, 11),
            FontWeight = FontWeights.SemiBold
        };
        var laterButton = new System.Windows.Controls.Button
        {
            Content = "以后再说",
            Padding = new Thickness(14, 11, 14, 11),
            FontWeight = FontWeights.SemiBold
        };

        var shouldLogin = false;
        loginButton.Click += (_, _) =>
        {
            shouldLogin = true;
            dialog.DialogResult = true;
        };
        laterButton.Click += (_, _) => dialog.DialogResult = true;

        Grid.SetColumn(loginButton, 0);
        Grid.SetColumn(laterButton, 2);
        buttons.Children.Add(loginButton);
        buttons.Children.Add(laterButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();

        if (markCompletedOnClose)
        {
            _settings.HasCompletedFirstRunGuide = true;
            _settingsStore.Save(_settings);
        }

        if (shouldLogin)
        {
            _ = StartCampusLoginAsync();
        }
    }

    private static void AddGuideLine(System.Windows.Controls.Panel parent, string number, string title, string description)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            VerticalAlignment = VerticalAlignment.Top
        };
        badge.Child = new TextBlock
        {
            Text = number,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(badge);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139))
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        parent.Children.Add(row);
    }

    private void ShowAboutDialog()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var dataPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");

        var dialog = new Window
        {
            Title = "关于 CAUCNet Traffic",
            Owner = this,
            Width = 480,
            Height = 330,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "CAUCNet Traffic",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
        });
        header.Children.Add(new TextBlock
        {
            Text = $"版本 {version}",
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139))
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new TextBlock
        {
            Text = $"校园网流量助手，用于本机流量监测、校园网后台同步、在线设备查看和托盘常驻。\n\n本地数据目录：\n{dataPath}\n\n隐私说明：不保存明文密码；会话和本地配置保存在当前 Windows 用户目录；诊断包导出时会生成本机日志，发送前请自行确认内容。",
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 18),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85))
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "知道了",
            Width = 110,
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FontWeight = FontWeights.SemiBold
        };
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ShowPrivacyDialog()
    {
        var dataPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");

        var dialog = new Window
        {
            Title = "隐私说明",
            Owner = this,
            Width = 580,
            Height = 470,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "隐私说明",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var body = new TextBlock
        {
            Text = $"CAUCNet Traffic 只在本机保存运行所需数据，不保存明文密码。\n\n保存哪些本地数据：\n- WebView2 登录会话：用于在会话过期前免重复登录\n- settings.json：网卡、同步间隔、提醒阈值、窗口偏好等设置\n- traffic.db：本机流量采样和今日/月度/趋势统计\n- app.log / crash.log：用于排查同步失败和崩溃问题\n\n本地数据目录：\n{dataPath}\n\n诊断包会脱敏哪些内容：\n- 账号、Cookie、JSESSIONID、token\n- IP 地址、MAC 地址\n- 可能像账号的长数字\n\n如何清除数据：\n- 设置页点击“清除校园网登录状态”：清除本地会话和 WebView Cookie\n- 设置页点击“清空本机流量统计”：删除本机历史统计数据库\n- 如果需要彻底清理，可退出软件后手动删除上面的本地数据目录。",
            FontSize = 14,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 18),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85))
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "知道了",
            Width = 110,
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FontWeight = FontWeights.SemiBold
        };
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        dialog.Content = root;
        dialog.ShowDialog();
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

    private void SetActiveNav(System.Windows.Controls.Button activeButton)
    {
        var buttons = new[]
        {
            OverviewNavButton,
            LoginNavButton,
            DevicesNavButton,
            StatsNavButton,
            SettingsNavButton
        };

        foreach (var button in buttons)
        {
            button.Background = button == activeButton
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = button == activeButton
                ? System.Windows.Media.Brushes.White
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225));
            button.FontWeight = button == activeButton ? FontWeights.SemiBold : FontWeights.Normal;
        }
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
        menu.Items.Add("同步校园网", null, (_, _) => Dispatcher.Invoke(async () => await SyncCampusAsync(true, true)));
        menu.Items.Add(miniWindowItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "CAUCNet Traffic",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return (icon, statusItem, usageItem, speedItem, miniWindowItem);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            System.IO.Path.Combine(Environment.CurrentDirectory, "Assets", "app.ico")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return new Drawing.Icon(path);
            }
        }

        return Drawing.SystemIcons.Application;
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
        if (_settings.FloatingMeterLeft is double left && _settings.FloatingMeterTop is double top && IsUsableWindowPosition(left, top))
        {
            _miniTrafficWindow.SetSavedPosition(left, top);
        }
        else
        {
            _miniTrafficWindow.PlaceNearTaskbar();
        }

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
        _miniTrafficWindow.UserMovedMiniWindow += (_, _) =>
        {
            _settings.FloatingMeterLeft = _miniTrafficWindow.Left;
            _settings.FloatingMeterTop = _miniTrafficWindow.Top;
            _settingsStore.Save(_settings);
        };
    }

    private static bool IsUsableWindowPosition(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        return left >= virtualLeft - 20
            && top >= virtualTop - 20
            && left <= virtualRight - 80
            && top <= virtualBottom - 40;
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

        if (_settings.CloseWithoutPrompt || _settings.CloseToTrayWithoutPrompt)
        {
            if (_settings.CloseDefaultAction == AppSettings.CloseActionExit)
            {
                _allowExit = true;
                _miniTrafficWindow?.Close();
                DisposeNotifyIcon();
            }
            else
            {
                e.Cancel = true;
                Hide();
                ShowTrayNotice("CAUCNet Traffic", "已最小化到托盘，仍在后台监测流量。");
            }

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
            Height = 225,
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

        var rememberCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "下次不再提示，默认此选项",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            FontSize = 13,
            Margin = new Thickness(0, 18, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(rememberCheckBox, 1);
        root.Children.Add(rememberCheckBox);

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
            SaveCloseDefaultIfRequested(rememberCheckBox.IsChecked == true, AppSettings.CloseActionMinimizeToTray);
            choice = CloseChoice.MinimizeToTray;
            dialog.DialogResult = true;
        };
        exitButton.Click += (_, _) =>
        {
            SaveCloseDefaultIfRequested(rememberCheckBox.IsChecked == true, AppSettings.CloseActionExit);
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

    private void SaveCloseDefaultIfRequested(bool remember, string action)
    {
        if (!remember)
        {
            return;
        }

        _settings.CloseWithoutPrompt = true;
        _settings.CloseDefaultAction = action;
        _settings.CloseToTrayWithoutPrompt = action == AppSettings.CloseActionMinimizeToTray;
        if (CloseToTrayWithoutPromptCheckBox is not null)
        {
            CloseToTrayWithoutPromptCheckBox.IsChecked = true;
        }

        _settingsStore.Save(_settings);
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
