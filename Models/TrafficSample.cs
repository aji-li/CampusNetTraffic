namespace CampusNetTraffic.Models;

public sealed record TrafficSample(
    DateTimeOffset CapturedAt,
    long TotalReceivedBytes,
    long TotalSentBytes,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond);
