namespace CampusNetTraffic.Models;

public sealed record ApplicationTrafficSnapshot(
    int ProcessId,
    string ProcessName,
    long DownloadBytes,
    long UploadBytes,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
}
