using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CampusNetTraffic.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CampusNetTraffic.TrafficService;

public sealed class TrafficPipeWorker(ILogger<TrafficPipeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Traffic pipe service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipe = CreatePipeServer();

                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(pipe, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Traffic pipe accept failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
                    TrafficPipeProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    pipeSecurity);
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, TrafficPipeProtocol.Encoding, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, TrafficPipeProtocol.Encoding, leaveOpen: true) { AutoFlush = true })
        {
            var command = await reader.ReadLineAsync(stoppingToken);
            var adapterId = TrafficPipeProtocol.ParseGetCountersAdapterId(command);
            if (adapterId is null)
            {
                await writer.WriteLineAsync("{}");
                return;
            }

            var selectedAdapterId = adapterId.Length == 0 ? null : adapterId;
            var (received, sent) = selectedAdapterId is null && DriverTrafficSource.TryReadCounters(out var driverReceived, out var driverSent)
                ? (driverReceived, driverSent)
                : ReadActiveInterfaceBytes(selectedAdapterId);
            var response = new TrafficCounterResponse(DateTimeOffset.Now, received, sent);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
    }

    private static (long received, long sent) ReadActiveInterfaceBytes(string? selectedAdapterId)
    {
        long received = 0;
        long sent = 0;

        foreach (var row in NativeNetworkInterfaces.GetRows())
        {
            if (row.OperStatus != NativeNetworkInterfaces.IfOperStatus.Up
                || row.IsFilterInterface
                || row.Type is NativeNetworkInterfaces.IfType.SoftwareLoopback
                || row.Type is NativeNetworkInterfaces.IfType.Tunnel)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selectedAdapterId)
                && !string.Equals(row.InterfaceGuid.ToString("B"), selectedAdapterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            received += unchecked((long)Math.Min(row.InOctets, long.MaxValue));
            sent += unchecked((long)Math.Min(row.OutOctets, long.MaxValue));
        }

        return (received, sent);
    }
}
