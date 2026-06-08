using System.Collections.Concurrent;
using System.Diagnostics;
using CampusNetTraffic.Models;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace CampusNetTraffic.Services;

public sealed class ApplicationTrafficMonitor : IDisposable
{
    private static readonly TimeSpan InactiveProcessRetention = TimeSpan.FromMinutes(10);

    private readonly object _snapshotLock = new();
    private readonly ConcurrentDictionary<int, ProcessTrafficCounter> _counters = new();
    private readonly ConcurrentDictionary<int, ProcessIdentity> _processNameCache = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private DateTimeOffset _lastSnapshotAt = DateTimeOffset.Now;
    private bool _isDisposed;
    private bool _startAttempted;

    public bool IsRunning { get; private set; }
    public string StatusMessage { get; private set; } = "应用流量监控未启动";

    public void Start()
    {
        if (_isDisposed || IsRunning || _startAttempted)
        {
            return;
        }

        _startAttempted = true;
        try
        {
            var sessionName = $"CAUCNetTraffic-AppTraffic-{Environment.ProcessId}";
            _session = new TraceEventSession(sessionName)
            {
                StopOnDispose = true
            };

            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            _session.Source.Kernel.TcpIpSend += data => AddTraffic(data.ProcessID, data.size, isUpload: true);
            _session.Source.Kernel.TcpIpRecv += data => AddTraffic(data.ProcessID, data.size, isUpload: false);
            _session.Source.Kernel.TcpIpSendIPV6 += data => AddTraffic(data.ProcessID, data.size, isUpload: true);
            _session.Source.Kernel.TcpIpRecvIPV6 += data => AddTraffic(data.ProcessID, data.size, isUpload: false);
            _session.Source.Kernel.UdpIpSend += data => AddTraffic(data.ProcessID, data.size, isUpload: true);
            _session.Source.Kernel.UdpIpRecv += data => AddTraffic(data.ProcessID, data.size, isUpload: false);
            _session.Source.Kernel.UdpIpSendIPV6 += data => AddTraffic(data.ProcessID, data.size, isUpload: true);
            _session.Source.Kernel.UdpIpRecvIPV6 += data => AddTraffic(data.ProcessID, data.size, isUpload: false);

            IsRunning = true;
            StatusMessage = "正在统计应用流量";
            _processingTask = Task.Run(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    IsRunning = false;
                    StatusMessage = $"应用流量监控已停止：{GetFriendlyStartError(ex)}";
                }
            });
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusMessage = GetFriendlyStartError(ex);
            _session?.Dispose();
            _session = null;
        }
    }

    public IReadOnlyList<ApplicationTrafficSnapshot> CaptureTop(int maxCount)
    {
        lock (_snapshotLock)
        {
            var now = DateTimeOffset.Now;
            var seconds = Math.Max(0.1, (now - _lastSnapshotAt).TotalSeconds);
            _lastSnapshotAt = now;
            CleanupInactiveCounters(now);

            return _counters.Values
                .Select(counter =>
                {
                    var downloadBytes = Interlocked.Read(ref counter.DownloadBytes);
                    var uploadBytes = Interlocked.Read(ref counter.UploadBytes);
                    var downloadRate = Math.Max(0, downloadBytes - counter.LastSnapshotDownloadBytes) / seconds;
                    var uploadRate = Math.Max(0, uploadBytes - counter.LastSnapshotUploadBytes) / seconds;
                    counter.LastSnapshotDownloadBytes = downloadBytes;
                    counter.LastSnapshotUploadBytes = uploadBytes;

                    return new ApplicationTrafficSnapshot(
                        counter.ProcessId,
                        GetProcessName(counter.ProcessId),
                        downloadBytes,
                        uploadBytes,
                        downloadRate,
                        uploadRate);
                })
                .Where(item => item.TotalBytes > 0)
                .OrderByDescending(item => item.DownloadBytesPerSecond + item.UploadBytesPerSecond)
                .ThenByDescending(item => item.TotalBytes)
                .Take(maxCount)
                .ToList();
        }
    }

    public void Reset()
    {
        _counters.Clear();
        _processNameCache.Clear();
        lock (_snapshotLock)
        {
            _lastSnapshotAt = DateTimeOffset.Now;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        IsRunning = false;
        _session?.Dispose();
        _session = null;
        try
        {
            _processingTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }
    }

    private void AddTraffic(int processId, int bytes, bool isUpload)
    {
        if (processId <= 0 || bytes <= 0)
        {
            return;
        }

        var counter = _counters.GetOrAdd(processId, static pid => new ProcessTrafficCounter(pid));
        counter.LastActiveAt = DateTimeOffset.Now;
        if (isUpload)
        {
            Interlocked.Add(ref counter.UploadBytes, bytes);
        }
        else
        {
            Interlocked.Add(ref counter.DownloadBytes, bytes);
        }
    }

    private string GetProcessName(int processId)
    {
        var currentIdentity = ReadProcessIdentity(processId);
        if (currentIdentity is null)
        {
            _processNameCache.TryRemove(processId, out _);
            return $"已退出进程 ({processId})";
        }

        var cachedIdentity = _processNameCache.AddOrUpdate(
            processId,
            currentIdentity,
            (_, existing) => existing.StartTime == currentIdentity.StartTime ? existing : currentIdentity);

        return cachedIdentity.Name;
    }

    private void CleanupInactiveCounters(DateTimeOffset now)
    {
        foreach (var pair in _counters)
        {
            if (now - pair.Value.LastActiveAt < InactiveProcessRetention)
            {
                continue;
            }

            if (IsProcessRunning(pair.Key))
            {
                continue;
            }

            _counters.TryRemove(pair.Key, out _);
            _processNameCache.TryRemove(pair.Key, out _);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessIdentity? ReadProcessIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = string.IsNullOrWhiteSpace(process.ProcessName)
                ? $"PID {processId}"
                : process.ProcessName;
            return new ProcessIdentity(name, process.StartTime);
        }
        catch
        {
            return null;
        }
    }

    private static string GetFriendlyStartError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || message.Contains("0x5", StringComparison.OrdinalIgnoreCase))
        {
            return "应用流量监控需要管理员权限，请以管理员身份运行后再试。";
        }

        return $"应用流量监控暂时不可用：{message}";
    }

    private sealed class ProcessTrafficCounter(int processId)
    {
        public int ProcessId { get; } = processId;
        public long DownloadBytes;
        public long UploadBytes;
        public long LastSnapshotDownloadBytes;
        public long LastSnapshotUploadBytes;
        public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.Now;
    }

    private sealed record ProcessIdentity(string Name, DateTime StartTime);
}
