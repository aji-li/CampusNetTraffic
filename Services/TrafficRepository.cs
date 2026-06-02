using System.IO;
using System.Globalization;
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

            CREATE TABLE IF NOT EXISTS traffic_usage_minutes (
                minute_start TEXT PRIMARY KEY,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                sent_bytes INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_traffic_samples_captured_at
                ON traffic_samples(captured_at);
            CREATE INDEX IF NOT EXISTS idx_traffic_usage_minutes_minute_start
                ON traffic_usage_minutes(minute_start);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveAsync(TrafficSample sample)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var previous = await GetPreviousSampleAsync(connection, sample);
        var receivedDelta = 0L;
        var sentDelta = 0L;
        if (previous is not null)
        {
            var gap = sample.CapturedAt - previous.Value.CapturedAt;
            if (gap > TimeSpan.Zero && gap <= TimeSpan.FromMinutes(5))
            {
                receivedDelta = Math.Max(0, sample.TotalReceivedBytes - previous.Value.Received);
                sentDelta = Math.Max(0, sample.TotalSentBytes - previous.Value.Sent);
            }
        }

        var insertSample = connection.CreateCommand();
        insertSample.Transaction = (SqliteTransaction)transaction;
        insertSample.CommandText = """
            INSERT INTO traffic_samples
                (captured_at, total_received_bytes, total_sent_bytes, download_bps, upload_bps)
            VALUES
                ($captured_at, $total_received_bytes, $total_sent_bytes, $download_bps, $upload_bps);
            """;
        insertSample.Parameters.AddWithValue("$captured_at", sample.CapturedAt.ToString("O"));
        insertSample.Parameters.AddWithValue("$total_received_bytes", sample.TotalReceivedBytes);
        insertSample.Parameters.AddWithValue("$total_sent_bytes", sample.TotalSentBytes);
        insertSample.Parameters.AddWithValue("$download_bps", sample.DownloadBytesPerSecond);
        insertSample.Parameters.AddWithValue("$upload_bps", sample.UploadBytesPerSecond);
        await insertSample.ExecuteNonQueryAsync();

        if (receivedDelta > 0 || sentDelta > 0)
        {
            var minuteStart = new DateTimeOffset(
                sample.CapturedAt.Year,
                sample.CapturedAt.Month,
                sample.CapturedAt.Day,
                sample.CapturedAt.Hour,
                sample.CapturedAt.Minute,
                0,
                sample.CapturedAt.Offset);

            var upsertMinute = connection.CreateCommand();
            upsertMinute.Transaction = (SqliteTransaction)transaction;
            upsertMinute.CommandText = """
                INSERT INTO traffic_usage_minutes (minute_start, received_bytes, sent_bytes)
                VALUES ($minute_start, $received_bytes, $sent_bytes)
                ON CONFLICT(minute_start) DO UPDATE SET
                    received_bytes = received_bytes + excluded.received_bytes,
                    sent_bytes = sent_bytes + excluded.sent_bytes;
                """;
            upsertMinute.Parameters.AddWithValue("$minute_start", minuteStart.ToString("O"));
            upsertMinute.Parameters.AddWithValue("$received_bytes", receivedDelta);
            upsertMinute.Parameters.AddWithValue("$sent_bytes", sentDelta);
            await upsertMinute.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<long> GetTransferredBytesSinceAsync(DateTimeOffset since)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var aggregated = await SumAggregatedBytesSinceAsync(connection, since);
        if (aggregated > 0)
        {
            return aggregated;
        }

        return await SumLegacySampleRangeAsync(connection, since);
    }

    public async Task<IReadOnlyList<TrafficUsagePoint>> GetRecentMinuteUsageAsync(int minuteCount)
    {
        var now = DateTimeOffset.Now;
        var endMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var startMinute = endMinute.AddMinutes(-(minuteCount - 1));
        var values = await LoadUsageBucketsAsync(startMinute, endMinute, "minute");

        return Enumerable.Range(0, minuteCount)
            .Select(i =>
            {
                var minute = startMinute.AddMinutes(i);
                values.TryGetValue(minute.ToString("O"), out var bytes);
                return new TrafficUsagePoint(minute, bytes);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<TrafficUsagePoint>> GetRecentDailyUsageAsync(int dayCount)
    {
        var today = DateTimeOffset.Now.Date;
        var start = new DateTimeOffset(today.AddDays(-(dayCount - 1)), DateTimeOffset.Now.Offset);
        var end = new DateTimeOffset(today.AddDays(1), DateTimeOffset.Now.Offset);
        var values = await LoadUsageBucketsAsync(start, end, "day");

        return Enumerable.Range(0, dayCount)
            .Select(i =>
            {
                var day = start.AddDays(i);
                values.TryGetValue(day.ToString("yyyy-MM-dd"), out var bytes);
                return new TrafficUsagePoint(day, bytes);
            })
            .ToList();
    }

    public async Task CleanupAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var sampleCutoff = DateTimeOffset.Now.AddDays(-14).ToString("O");
        var minuteCutoff = DateTimeOffset.Now.AddDays(-400).ToString("O");
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM traffic_samples WHERE captured_at < $sample_cutoff;
            DELETE FROM traffic_usage_minutes WHERE minute_start < $minute_cutoff;
            VACUUM;
            """;
        command.Parameters.AddWithValue("$sample_cutoff", sampleCutoff);
        command.Parameters.AddWithValue("$minute_cutoff", minuteCutoff);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearAllAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM traffic_samples;
            DELETE FROM traffic_usage_minutes;
            VACUUM;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(DateTimeOffset CapturedAt, long Received, long Sent)?> GetPreviousSampleAsync(SqliteConnection connection, TrafficSample sample)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, total_received_bytes, total_sent_bytes
            FROM traffic_samples
            WHERE captured_at < $captured_at
            ORDER BY captured_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$captured_at", sample.CapturedAt.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private static async Task<long> SumAggregatedBytesSinceAsync(SqliteConnection connection, DateTimeOffset since)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(received_bytes + sent_bytes), 0)
            FROM traffic_usage_minutes
            WHERE minute_start >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<long> SumLegacySampleRangeAsync(SqliteConnection connection, DateTimeOffset since)
    {
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

    private async Task<Dictionary<string, long>> LoadUsageBucketsAsync(DateTimeOffset start, DateTimeOffset end, string bucket)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = bucket == "day"
            ? """
              SELECT substr(minute_start, 1, 10) AS bucket_key,
                     COALESCE(SUM(received_bytes + sent_bytes), 0) AS bytes
              FROM traffic_usage_minutes
              WHERE minute_start >= $start AND minute_start < $end
              GROUP BY bucket_key;
              """
            : """
              SELECT minute_start AS bucket_key,
                     COALESCE(SUM(received_bytes + sent_bytes), 0) AS bytes
              FROM traffic_usage_minutes
              WHERE minute_start >= $start AND minute_start <= $end
              GROUP BY bucket_key;
              """;
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));

        var values = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values[reader.GetString(0)] = reader.GetInt64(1);
        }

        return values;
    }
}

public sealed record TrafficUsagePoint(DateTimeOffset CapturedAt, long Bytes);
