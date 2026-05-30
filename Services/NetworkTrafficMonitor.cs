using System.Net.NetworkInformation;
using CampusNetTraffic.Models;

namespace CampusNetTraffic.Services;

public sealed class NetworkTrafficMonitor
{
    private DateTimeOffset? _lastCapturedAt;
    private long _lastReceived;
    private long _lastSent;
    private string? _selectedAdapterId;

    public void SelectAdapter(string? adapterId)
    {
        _selectedAdapterId = string.IsNullOrWhiteSpace(adapterId) ? null : adapterId;
        _lastCapturedAt = null;
        _lastReceived = 0;
        _lastSent = 0;
    }

    public IReadOnlyList<NetworkAdapterOption> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsableAdapter)
            .Select(adapter => new NetworkAdapterOption(
                adapter.Id,
                $"{adapter.Name} ({adapter.NetworkInterfaceType})"))
            .OrderBy(adapter => adapter.Name)
            .ToList();
    }

    public TrafficSample Capture()
    {
        var now = DateTimeOffset.Now;
        var (received, sent) = ReadActiveInterfaceBytes(_selectedAdapterId);

        var downloadRate = 0d;
        var uploadRate = 0d;

        if (_lastCapturedAt is not null)
        {
            var seconds = Math.Max(0.1, (now - _lastCapturedAt.Value).TotalSeconds);
            downloadRate = Math.Max(0, received - _lastReceived) / seconds;
            uploadRate = Math.Max(0, sent - _lastSent) / seconds;
        }

        _lastCapturedAt = now;
        _lastReceived = received;
        _lastSent = sent;

        return new TrafficSample(now, received, sent, downloadRate, uploadRate);
    }

    private static (long received, long sent) ReadActiveInterfaceBytes(string? selectedAdapterId)
    {
        long received = 0;
        long sent = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsableAdapter(adapter))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selectedAdapterId) && adapter.Id != selectedAdapterId)
            {
                continue;
            }

            var stats = adapter.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        return (received, sent);
    }

    private static bool IsUsableAdapter(NetworkInterface adapter)
    {
        return adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && adapter.NetworkInterfaceType is not NetworkInterfaceType.Tunnel;
    }
}

public sealed record NetworkAdapterOption(string Id, string Name);
