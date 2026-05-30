using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampusNetTraffic.Services;

public sealed class CampusSessionStore
{
    private readonly string _sessionPath;

    public CampusSessionStore()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CampusNetTraffic");
        Directory.CreateDirectory(appData);
        _sessionPath = Path.Combine(appData, "campus-session.json");
    }

    public async Task SaveAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(sessionId),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        var payload = new StoredSession(
            Convert.ToBase64String(protectedBytes),
            DateTimeOffset.Now);

        await File.WriteAllTextAsync(_sessionPath, JsonSerializer.Serialize(payload));
    }

    public async Task<string?> LoadAsync()
    {
        if (!File.Exists(_sessionPath))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<StoredSession>(
                await File.ReadAllTextAsync(_sessionPath));
            if (payload is null || string.IsNullOrWhiteSpace(payload.ProtectedSessionId))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(payload.ProtectedSessionId);
            var bytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }
    }

    private sealed record StoredSession(string ProtectedSessionId, DateTimeOffset SavedAt);
}
