using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampusNetTraffic.Models;

namespace CampusNetTraffic.Services;

public sealed partial class DrcomCampusClient
{
    private static readonly Uri BaseUri = new("https://www.cauc.edu.cn/Self/");
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private string _cookieHeader = string.Empty;

    public void SetCookieHeader(string cookieHeader)
    {
        _cookieHeader = cookieHeader;
    }

    public async Task<AccountSnapshot> GetAccountSnapshotAsync()
    {
        var html = await GetStringAsync("dashboard");

        if (html.Contains("/Self/login", StringComparison.OrdinalIgnoreCase)
            || html.Contains("login/verify", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Campus session is not logged in. Please log in inside the app first.");
        }

        var csrfToken = ReadCsrfToken(html);
        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            await RefreshAccountAsync(csrfToken);
            html = await GetStringAsync("dashboard");
        }

        var text = HtmlTagRegex().Replace(WebUtility.HtmlDecode(html), "\n");
        text = CompactLinesRegex().Replace(text, "\n").Trim();

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

    public async Task<IReadOnlyList<OnlineDevice>> GetOnlineDevicesAsync()
    {
        var json = await GetStringAsync("dashboard/getOnlineList?t=app&order=asc");
        using var document = JsonDocument.Parse(json);
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

    private async Task RefreshAccountAsync(string csrfToken)
    {
        try
        {
            var relativeUrl = $"dashboard/refreshaccount?csrftoken={Uri.EscapeDataString(csrfToken)}&t={Random.Shared.NextDouble().ToString(CultureInfo.InvariantCulture)}";
            _ = await GetStringAsync(relativeUrl);
        }
        catch
        {
            // Refresh is best-effort; the dashboard can still be parsed when this endpoint changes.
        }
    }

    private async Task<string> GetStringAsync(string relativeUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relativeUrl));
        request.Headers.UserAgent.ParseAdd("CampusNetTraffic/0.1");

        if (!string.IsNullOrWhiteSpace(_cookieHeader))
        {
            request.Headers.Add("Cookie", _cookieHeader);
        }

        var response = await _httpClient.SendAsync(request);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            throw new InvalidOperationException($"Campus session redirected to login: {location}");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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
        var match = Regex.Match(text, $@"(?<value>\d+(?:\.\d+)?)\s*\u5143\s*\n\s*{Regex.Escape(label)}");
        return match.Success
            ? decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            : 0m;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) ? property.ToString() : string.Empty;
    }

    private static string ReadCsrfToken(string html)
    {
        var match = Regex.Match(html, @"csrftoken:\s*'(?<token>[^']+)'");
        return match.Success ? match.Groups["token"].Value : string.Empty;
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

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[\t\r ]*\n[\t\r ]*")]
    private static partial Regex CompactLinesRegex();
}
