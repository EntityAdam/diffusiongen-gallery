using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string StorageOverviewId = "storage-overview";
    private const string StorageOverviewPurpose = "storage-overview:v1";

    public async Task<StorageOverview?> CachedStorageOverviewAsync()
    {
        using var key = session.Borrow();
        var encrypted = await store.ReadSettingsAsync(StorageOverviewId);
        if (encrypted is null) return null;
        var plain = VaultCrypto.Decrypt(encrypted, key.Bytes, StorageOverviewPurpose);
        try
        {
            return JsonSerializer.Deserialize<StorageOverview>(plain)
                ?? throw new InvalidOperationException("Invalid storage overview.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<StorageOverview> RefreshStorageOverviewAsync()
    {
        var records = await ListAsync();
        var overview = await Task.Run(() => MeasureStorage(records));
        using var key = session.Borrow();
        var plain = JsonSerializer.SerializeToUtf8Bytes(overview);
        try { await store.SaveSettingsAsync(VaultCrypto.Encrypt(plain, key.Bytes, StorageOverviewPurpose), StorageOverviewId); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return overview;
    }

    private StorageOverview MeasureStorage(IEnumerable<ImageRecord> records)
    {
        var database = Path.Combine(vault.Root, "gallery.db");
        string[] databaseFiles = [database, database + "-wal", database + "-shm"];
        long databaseBytes = 0, vaultBytes = 0;
        var vaultFiles = 0;
        foreach (var path in databaseFiles.Append(Path.Combine(vault.Root, "vault.json")))
        {
            var length = FileLength(path);
            if (length is null) continue;
            vaultFiles++;
            vaultBytes += length.Value;
            if (databaseFiles.Contains(path)) databaseBytes += length.Value;
        }

        long imageBytes = 0;
        int imageFiles = 0, missing = 0;
        foreach (var path in records.Select(record => record.StoredPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var length = FileLength(path);
            if (length is null) { missing++; continue; }
            imageFiles++;
            imageBytes += length.Value;
        }
        return new(databaseBytes, imageFiles, imageBytes, vaultFiles, vaultBytes, missing, DateTimeOffset.UtcNow);
    }

    private static long? FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
