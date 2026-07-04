namespace CampusNetTraffic.Services;

internal static class TrafficPipeProtocol
{
    public const string PipeName = "CampusNetTraffic.Traffic";
    public const string GetCountersCommand = "GET";
    public const int ConnectTimeoutMilliseconds = 35;

    public static readonly System.Text.Encoding Encoding = new System.Text.UTF8Encoding(false);

    public static string CreateGetCountersCommand(string? adapterId)
    {
        return string.IsNullOrWhiteSpace(adapterId)
            ? GetCountersCommand
            : $"{GetCountersCommand} {adapterId}";
    }

    public static string? ParseGetCountersAdapterId(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (string.Equals(trimmed, GetCountersCommand, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!trimmed.StartsWith(GetCountersCommand + " ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var adapterId = trimmed[GetCountersCommand.Length..].Trim();
        return string.IsNullOrWhiteSpace(adapterId) ? string.Empty : adapterId;
    }
}

internal sealed record TrafficCounterResponse(
    DateTimeOffset CapturedAt,
    long TotalReceivedBytes,
    long TotalSentBytes);
