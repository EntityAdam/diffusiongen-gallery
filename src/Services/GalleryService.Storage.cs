using Gallery.Models;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    private const string StorageOverviewId = "storage-overview";
    private const string StorageOverviewPurpose = "storage-overview:v1";

    public async Task<StorageOverview?> CachedStorageOverviewAsync()
        => await ReadEncryptedSettingAsync<StorageOverview>(StorageOverviewId, StorageOverviewPurpose);

    public async Task<StorageOverview> RefreshStorageOverviewAsync()
    {
        var records = await ListAsync();
        var storageOverview = await Task.Run(() => StorageOverviewCalculator.Measure(vaultStore.Root, records));
        await WriteEncryptedSettingAsync(storageOverview, StorageOverviewId, StorageOverviewPurpose);
        return storageOverview;
    }

    private static class StorageOverviewCalculator
    {
        public static StorageOverview Measure(string vaultRoot, IEnumerable<ImageRecord> records)
        {
            var vaultMeasurement = MeasureVaultFiles(vaultRoot);
            var imageMeasurement = MeasureImageFiles(records);
            return new(vaultMeasurement.DatabaseBytes, imageMeasurement.FileCount, imageMeasurement.Bytes,
                vaultMeasurement.FileCount, vaultMeasurement.Bytes, imageMeasurement.Missing, DateTimeOffset.UtcNow);
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

        private static (int FileCount, long Bytes, int Missing) MeasureImageFiles(IEnumerable<ImageRecord> records)
        {
            long imageBytes = 0;
            int imageFileCount = 0, missing = 0;
            foreach (var path in records.Select(record => record.StoredPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var length = FileLength(path);
                if (length is null) { missing++; continue; }
                imageFileCount++;
                imageBytes += length.Value;
            }
            return (imageFileCount, imageBytes, missing);
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
