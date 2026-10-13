using Gallery.Models;
using Gallery.Services.DataManagers.Models;
using Gallery.Services.Processors;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string StorageOverviewId = "storage-overview";
    private const string StorageOverviewPurpose = "storage-overview:v1";

    public async Task<StorageOverview?> CachedStorageOverviewAsync()
        => await ReadEncryptedSettingAsync<StorageOverview>(StorageOverviewId, StorageOverviewPurpose);

    public async Task<StorageOverview> RefreshStorageOverviewAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        var records = await ListWithKeyAsync(key.Bytes);
        var indexedLengths = await galleryStore.ReadMediaIndexAsync();
        var indexPending = await IsMediaIndexPendingAsync(key.Bytes);
        var (overview, measured) = await Task.Run(() =>
            StorageOverviewCalculator.Measure(vaultStore.Root, records, indexedLengths, key.Bytes));
        await galleryStore.IndexFilesAsync(measured);
        var storageOverview = overview with { IndexPending = indexPending };
        await WriteEncryptedSettingAsync(storageOverview, StorageOverviewId, StorageOverviewPurpose);
        return storageOverview;
    }

    private static class StorageOverviewCalculator
    {
        public static (StorageOverview Overview, List<(byte[] Directory, IndexedFile File)> Measured) Measure(
            string vaultRoot, IEnumerable<ImageRecord> records, Dictionary<string, long> indexedLengths, byte[] key)
        {
            var vaultMeasurement = MeasureVaultFiles(vaultRoot);
            var imageMeasurement = MeasureImageFiles(records, indexedLengths, key);
            return (new(vaultMeasurement.DatabaseBytes, imageMeasurement.FileCount, imageMeasurement.Bytes,
                vaultMeasurement.FileCount, vaultMeasurement.Bytes, imageMeasurement.Missing, DateTimeOffset.UtcNow),
                imageMeasurement.Measured);
        }

        private static (long DatabaseBytes, int FileCount, long Bytes) MeasureVaultFiles(string vaultRoot)
        {
            var databasePath = Path.Combine(vaultRoot, "gallery.db");
            string[] databaseFiles = [databasePath, databasePath + "-wal", databasePath + "-shm"];
            long databaseBytes = 0, vaultBytes = 0;
            var vaultFileCount = 0;
            foreach (var path in databaseFiles.Append(Path.Combine(vaultRoot, "vault.json")))
            {
                var length = FileLength(path);
                if (length is null) continue;
                vaultFileCount++;
                vaultBytes += length.Value;
                if (databaseFiles.Contains(path)) databaseBytes += length.Value;
            }

            return (databaseBytes, vaultFileCount, vaultBytes);
        }

        // Indexed files are counted from the database; only files the index has not seen are measured on disk.
        private static (int FileCount, long Bytes, int Missing, List<(byte[] Directory, IndexedFile File)> Measured) MeasureImageFiles(
            IEnumerable<ImageRecord> records, Dictionary<string, long> indexedLengths, byte[] key)
        {
            long imageBytes = 0;
            int imageFileCount = 0, missing = 0;
            var measured = new List<(byte[] Directory, IndexedFile File)>();
            foreach (var path in records.Select(record => record.StoredPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = VaultCrypto.PathFingerprint(key, path);
                if (!indexedLengths.TryGetValue(Convert.ToHexString(target), out var length))
                {
                    var measuredLength = FileLength(path);
                    if (measuredLength is null) { missing++; continue; }
                    length = measuredLength.Value;
                    measured.Add((VaultCrypto.PathFingerprint(key, Path.GetDirectoryName(path)!), new IndexedFile(target, length)));
                }
                imageFileCount++;
                imageBytes += length;
            }
            return (imageFileCount, imageBytes, missing, measured);
        }

    }

    private static long? FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException($"Could not read file length for '{path}'.", exception);
        }
    }
}
