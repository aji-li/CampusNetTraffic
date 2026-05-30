namespace CampusNetTraffic.Models;

public sealed record OnlineDevice(
    string LoginTime,
    string Ip,
    string Mac,
    string HostName,
    string TerminalType,
    double DownloadMb,
    double UploadMb,
    TimeSpan UseTime,
    string SessionId);
