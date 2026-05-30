using System.IO;
using CampusNetTraffic.Models;
using Microsoft.Data.Sqlite;

namespace CampusNetTraffic.Services;

public sealed class TrafficRepository
{
    private readonly string _connectionString;

    public TrafficRepository()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");
        Directory.CreateDirectory(appData);
        _connectionString = $"Data Source={Path.Combine(appData, "traffic.db")}";
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS traffic_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                total_received_bytes INTEGER NOT NULL,
                total_sent_bytes INTEGER NOT NULL,
                download_bps REAL NOT NULL,
                upload_bps REAL NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveAsync(TrafficSample sample)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO traffic_samples
                (captured_at, total_received_bytes, total_sent_bytes, download_bps, upload_bps)
            VALUES
                ($captured_at, $total_received_bytes, $total_sent_bytes, $download_bps, $upload_bps);
            """;
        command.Parameters.AddWithValue("$captured_at", sample.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$total_received_bytes", sample.TotalReceivedBytes);
        command.Parameters.AddWithValue("$total_sent_bytes", sample.TotalSentBytes);
        command.Parameters.AddWithValue("$download_bps", sample.DownloadBytesPerSecond);
        command.Parameters.AddWithValue("$upload_bps", sample.UploadBytesPerSecond);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> GetTransferredBytesSinceAsync(DateTimeOffset since)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                MIN(total_received_bytes),
                MAX(total_received_bytes),
                MIN(total_sent_bytes),
                MAX(total_sent_bytes)
            FROM traffic_samples
            WHERE captured_at >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0))
        {
            return 0;
        }

        var minReceived = reader.GetInt64(0);
        var maxReceived = reader.GetInt64(1);
        var minSent = reader.GetInt64(2);
        var maxSent = reader.GetInt64(3);
        return Math.Max(0, maxReceived - minReceived) + Math.Max(0, maxSent - minSent);
    }
}
