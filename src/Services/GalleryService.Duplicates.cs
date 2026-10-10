using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core;
using Gallery.Models;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string PerceptualHashesId = "perceptual-hashes";
    private const string PerceptualHashesPurpose = "perceptual-hashes:v1";
    private const string DuplicateScanId = "duplicate-scan";
    private const string DuplicateScanPurpose = "duplicate-scan:v1";
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";
    private const int HashBatchSize = 64;

    private sealed record PerceptualHash(string PngSha256, ulong Hash);

    public async Task<UiPreferences> LoadPreferencesAsync() =>
        (await ReadEncryptedSettingAsync<UiPreferences>(PreferencesId, PreferencesPurpose) ?? new()).Normalize();

    public Task SavePreferencesAsync(UiPreferences preferences) =>
        WriteEncryptedSettingAsync(preferences.Normalize(), PreferencesId, PreferencesPurpose);

    public Task<DuplicateScan?> CachedDuplicateScanAsync() =>
        ReadEncryptedSettingAsync<DuplicateScan>(DuplicateScanId, DuplicateScanPurpose);

    public async Task<DuplicateScan> ScanDuplicatesAsync(IProgress<(int Completed, int Total)>? progress = null)
    {
        var records = await ListAsync();
        var cached = await ReadEncryptedSettingAsync<Dictionary<string, PerceptualHash>>(PerceptualHashesId, PerceptualHashesPurpose) ?? [];
        var hashes = new Dictionary<string, PerceptualHash>();
        var pending = new List<ImageRecord>();
        foreach (var record in records)
        {
            if (cached.TryGetValue(record.Id, out var hash) && hash.PngSha256 == record.PngSha256) hashes[record.Id] = hash;
            else pending.Add(record);
        }

        var completed = records.Count - pending.Count;
        progress?.Report((completed, records.Count));
        foreach (var batch in pending.Chunk(HashBatchSize))
        {
            var thumbnails = new List<(ImageRecord Record, byte[] Encrypted)>(batch.Length);
            foreach (var record in batch)
            {
                try { thumbnails.Add((record, await store.ReadThumbnailAsync(record.Id))); }
                catch (InvalidOperationException) { }
            }
            var computed = await Task.Run(() =>
            {
                using var key = session.Borrow();
                var results = new PerceptualHash?[thumbnails.Count];
                Parallel.For(0, thumbnails.Count, index =>
                {
                    var (record, encrypted) = thumbnails[index];
                    try
                    {
                        var plain = VaultCrypto.Decrypt(encrypted, key.Bytes, $"thumbnail:{record.Id}");
                        try { results[index] = new(record.PngSha256, DuplicateRules.DifferenceHash(plain)); }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    catch (Exception exception) when (exception is CryptographicException or ImageFormatException or UnknownImageFormatException) { }
                });
                return results;
            });
            for (var index = 0; index < thumbnails.Count; index++)
                if (computed[index] is { } hash) hashes[thumbnails[index].Record.Id] = hash;
            completed += batch.Length;
            progress?.Report((completed, records.Count));
        }

        await WriteEncryptedSettingAsync(hashes, PerceptualHashesId, PerceptualHashesPurpose);
        var hashed = records.Where(record => hashes.ContainsKey(record.Id)).ToList();
        var groups = await Task.Run(() => DuplicateRules.GroupDuplicates(hashed,
            record => hashes[record.Id].Hash, FileLength));
        var scan = new DuplicateScan(groups, hashed.Count, DateTimeOffset.UtcNow);
        await WriteEncryptedSettingAsync(scan, DuplicateScanId, DuplicateScanPurpose);
        return scan;
    }

    /// <summary>Unmarks the kept copies and marks every other copy in the group for deletion. Returns the failure count.</summary>
    public async Task<int> ApplyDuplicateDecisionAsync(IReadOnlyCollection<string> groupIds, IReadOnlySet<string> keep)
    {
        if (keep.Count == 0) throw new InvalidOperationException("Keep at least one image in the group.");
        if (!keep.All(groupIds.Contains)) throw new InvalidOperationException("Kept images must belong to the group.");
        var failed = await UpdateManyAsync(keep, record => record.MarkedForDeletion = false);
        return failed + await UpdateManyAsync(groupIds.Where(id => !keep.Contains(id)).ToList(), record => record.MarkedForDeletion = true);
    }

    private async Task<T?> ReadEncryptedSettingAsync<T>(string id, string purpose) where T : class
    {
        var encrypted = await store.ReadSettingsAsync(id);
        if (encrypted is null) return null;
        using var key = session.Borrow();
        var plain = VaultCrypto.Decrypt(encrypted, key.Bytes, purpose);
        try { return JsonSerializer.Deserialize<T>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var key = session.Borrow();
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { await store.SaveSettingsAsync(VaultCrypto.Encrypt(plain, key.Bytes, purpose), id); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
