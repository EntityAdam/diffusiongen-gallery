using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    public const int DuplicateThreshold = 6;
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
                        try { results[index] = new(record.PngSha256, DifferenceHash(plain)); }
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
        var groups = await Task.Run(() => GroupDuplicates(hashed, record => hashes[record.Id].Hash, DuplicateThreshold));
        var scan = new DuplicateScan(groups, hashed.Count, DateTimeOffset.UtcNow);
        await WriteEncryptedSettingAsync(scan, DuplicateScanId, DuplicateScanPurpose);
        return scan;
    }

    /// <summary>64-bit difference hash: grayscale 9x8, one bit per horizontal gradient.</summary>
    public static ulong DifferenceHash(byte[] image)
    {
        using var pixels = Image.Load<L8>(image);
        pixels.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(9, 8), Mode = ResizeMode.Stretch }));
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (pixels[x, y].PackedValue > pixels[x + 1, y].PackedValue) hash |= 1UL << bit;
        return hash;
    }

    public static int HammingDistance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    public static List<DuplicateGroup> GroupDuplicates(IReadOnlyList<ImageRecord> records, Func<ImageRecord, ulong> hashOf, int threshold)
    {
        var hashes = records.Select(hashOf).ToArray();
        var parent = Enumerable.Range(0, records.Count).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }

        for (var left = 0; left < records.Count; left++)
            for (var right = left + 1; right < records.Count; right++)
            {
                var exact = records[left].PngSha256.Length > 0 && records[left].PngSha256 == records[right].PngSha256;
                if (exact || (HammingDistance(hashes[left], hashes[right]) <= threshold && SimilarShape(records[left], records[right])))
                    parent[Root(right)] = Root(left);
            }

        var groups = new List<DuplicateGroup>();
        foreach (var members in Enumerable.Range(0, records.Count).GroupBy(Root).Where(group => group.Count() > 1))
        {
            var indexes = members.ToList();
            var maxDistance = 0;
            foreach (var left in indexes)
                foreach (var right in indexes)
                    maxDistance = Math.Max(maxDistance, HammingDistance(hashes[left], hashes[right]));
            var items = indexes.Select(index => records[index]).ToList();
            var keeper = PickKeeper(items);
            var ordered = items.OrderByDescending(item => item.Id == keeper.Id).ThenBy(item => item.ImportedAt).ToList();
            var exact = items.All(item => item.PngSha256.Length > 0 && item.PngSha256 == keeper.PngSha256);
            var reclaimable = ordered.Skip(1).Sum(item => FileLength(item.StoredPath) ?? 0);
            groups.Add(new(keeper.Id, ordered.Select(item => item.Id).ToList(), maxDistance, exact, reclaimable));
        }
        return groups.OrderByDescending(group => group.ReclaimableBytes).ThenByDescending(group => group.Ids.Count).ToList();
    }

    /// <summary>Keep the most curated, highest-resolution copy; ties go to the oldest import.</summary>
    public static ImageRecord PickKeeper(IEnumerable<ImageRecord> records) => records
        .OrderBy(record => record.MarkedForDeletion)
        .ThenByDescending(record => record.Rating)
        .ThenByDescending(record => record.Favorite)
        .ThenByDescending(record => (long)record.Width * record.Height)
        .ThenByDescending(record => record.OriginalBytes)
        .ThenBy(record => record.ImportedAt)
        .First();

    private static bool SimilarShape(ImageRecord left, ImageRecord right)
    {
        if (left.Width <= 0 || left.Height <= 0 || right.Width <= 0 || right.Height <= 0) return true;
        var a = (double)left.Width / left.Height;
        var b = (double)right.Width / right.Height;
        return Math.Abs(a - b) / Math.Max(a, b) <= 0.05;
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
