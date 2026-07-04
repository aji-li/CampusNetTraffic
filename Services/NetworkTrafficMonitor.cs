using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
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
        return NativeNetworkInterfaces.GetRows()
            .Where(IsUsableInterface)
            .Select(row => new NetworkAdapterOption(
                FormatInterfaceId(row.InterfaceGuid),
                $"{row.Name} ({FormatInterfaceType(row.Type)})"))
            .OrderBy(adapter => adapter.Name)
            .ToList();
    }

    public TrafficSample Capture()
    {
        var now = DateTimeOffset.Now;
        var (received, sent) = TryReadServiceBytes(_selectedAdapterId) ?? ReadActiveInterfaceBytes(_selectedAdapterId);

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

        foreach (var row in NativeNetworkInterfaces.GetRows())
        {
            if (!IsUsableInterface(row))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selectedAdapterId)
                && !string.Equals(FormatInterfaceId(row.InterfaceGuid), selectedAdapterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            received += unchecked((long)Math.Min(row.InOctets, long.MaxValue));
            sent += unchecked((long)Math.Min(row.OutOctets, long.MaxValue));
        }

        return (received, sent);
    }

    private static (long received, long sent)? TryReadServiceBytes(string? selectedAdapterId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                TrafficPipeProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            pipe.Connect(TrafficPipeProtocol.ConnectTimeoutMilliseconds);
            using var writer = new StreamWriter(pipe, TrafficPipeProtocol.Encoding, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, TrafficPipeProtocol.Encoding, leaveOpen: true);

            writer.WriteLine(TrafficPipeProtocol.CreateGetCountersCommand(selectedAdapterId));
            var response = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(response))
            {
                return null;
            }

            var counters = JsonSerializer.Deserialize<TrafficCounterResponse>(response);
            return counters is null ? null : (counters.TotalReceivedBytes, counters.TotalSentBytes);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsUsableInterface(NativeNetworkInterfaces.InterfaceRow row)
    {
        return row.OperStatus == NativeNetworkInterfaces.IfOperStatus.Up
            && !row.IsFilterInterface
            && row.Type is not NativeNetworkInterfaces.IfType.SoftwareLoopback
            && row.Type is not NativeNetworkInterfaces.IfType.Tunnel;
    }

    private static string FormatInterfaceId(Guid interfaceGuid) => interfaceGuid.ToString("B");

    private static string FormatInterfaceType(NativeNetworkInterfaces.IfType type)
    {
        return type switch
        {
            NativeNetworkInterfaces.IfType.EthernetCsmacd => "Ethernet",
            NativeNetworkInterfaces.IfType.Ieee80211 => "Wi-Fi",
            NativeNetworkInterfaces.IfType.Ppp => "PPP",
            NativeNetworkInterfaces.IfType.Tunnel => "Tunnel",
            NativeNetworkInterfaces.IfType.SoftwareLoopback => "Loopback",
            _ => type.ToString()
        };
    }
}

public sealed record NetworkAdapterOption(string Id, string Name);
