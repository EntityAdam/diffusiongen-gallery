using Gallery.Core.Services.Sessions.Locations;
using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private SemaphoreSlim lifecycleGate => galleryLifetime.Gate;
    private VaultCheckout? checkout => galleryLifetime.Current.Checkout;
    private VaultHistory? history => galleryLifetime.History;

    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
    public string VaultName => vaultStore.Name;

    public static string GenerateVaultName()
    {
        string[] adjectives = ["bright", "quiet", "curious", "gentle", "bold", "silver", "cosmic", "merry"];
        string[] nouns = ["otter", "nebula", "willow", "fox", "comet", "orchid", "raven", "badger"];
        return $"{adjectives[RandomNumberGenerator.GetInt32(adjectives.Length)]}-{nouns[RandomNumberGenerator.GetInt32(nouns.Length)]}-{RandomNumberGenerator.GetInt32(10000):D4}";
    }

    public static string DefaultVaultDirectory(string name) =>
        Path.Combine(DefaultApplicationDirectory, "galleries", name);

    private void EnsureActive() => ObjectDisposedException.ThrowIf(galleryLifetime.IsDisposed, this);
    private void EnsureCheckout()
    {
        EnsureActive();
        galleryLifetime.EnsureCheckout();
    }

    public Task OpenExistingVaultAsync(string directory) => SelectVaultAsync(directory, existing: true);
    public Task SelectNewVaultAsync(string directory) => SelectVaultAsync(directory, existing: false);

    private async Task SelectVaultAsync(string directory, bool existing)
    {
        var root = NormalizeVaultDirectory(directory);
        if (!existing) RequireLocalCatalogDirectory(root);
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureActive();
            if (PathEquals(root, VaultRoot) && checkout is not null)
            {
                if (!existing) RequireEmptyDestination(root);
                await StopDrivePlayerAsync();
                await vaultSession.LockAndDrainAsync();
                return;
            }
            var store = new VaultStore(root);
            if (existing && (!store.Exists || !File.Exists(Path.Combine(root, "gallery.db"))))
                throw new InvalidOperationException("Select a gallery folder containing vault.json and gallery.db.");
            if (!existing) RequireEmptyDestination(root);
            var nextCheckout = galleryLifetime.AcquireLocationCheckout(root);
            try
            {
                if (!existing) RequireEmptyDestination(root);
                await StopDrivePlayerAsync();
                await vaultSession.LockAndDrainAsync();
                EnsureActive();
                galleryLifetime.PublishLocation(new GalleryStore(Path.Combine(root, "gallery.db")), store, nextCheckout)?.Dispose();
            }
            catch { nextCheckout.Dispose(); throw; }
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task CloseVaultAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            try { await StopDrivePlayerAsync(); }
            finally
            {
                await vaultSession.LockAndDrainAsync();
                galleryLifetime.ReleaseCheckout();
            }
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task RenameVaultAsync(string name)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
            EnsureCheckout();
            await vaultStore.RenameAsync(name);
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task MoveVaultAsync(string directory)
    {
        var destination = NormalizeVaultDirectory(directory);
        RequireLocalCatalogDirectory(destination);
        await lifecycleGate.WaitAsync();
        byte[]? key = null;
        VaultCheckout? destinationCheckout = null;
        string? staging = null;
        var published = false;
        try
        {
            EnsureCheckout();
            RejectOverlappingDirectories(destination, VaultRoot);
            RequireEmptyDestination(destination);
            destinationCheckout = galleryLifetime.AcquireLocationCheckout(destination);
            RequireEmptyDestination(destination);
            key = vaultSession.Key;
            await StopDrivePlayerAsync();
            await vaultSession.LockAndDrainAsync();
            var sourceRoot = VaultRoot;
            await ReconcileOutstandingDeletesAsync(key);
            var records = await galleryStore.ReadRecordsAsync(key);
            staging = Path.Combine(destination, ".vault-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await vaultStore.CopyHeaderAsync(Path.Combine(staging, "vault.json"));
            await galleryStore.CreateSnapshotAsync(Path.Combine(staging, "gallery.db"));
            var stagedStore = new GalleryStore(Path.Combine(staging, "gallery.db"));
            foreach (var record in records)
            {
                ValidateMediaPath(record);
                var relative = MediaRelativePath(record);
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(record.StoredPath, target, overwrite: false);
                if (await HashFileAsync(record.StoredPath) != await HashFileAsync(target))
                    throw new IOException($"Media verification failed for '{record.Name}'. The source gallery was not removed.");
                await stagedStore.UpdateAsync(record with { StoredPath = Path.Combine(destination, relative) }, key);
            }
            await ClearLocationSettingsAsync(stagedStore, key);
            EnsureActive();
            PublishStagedVault(staging, destination);
            published = true;
            var sourceCheckout = galleryLifetime.PublishLocation(
                new GalleryStore(Path.Combine(destination, "gallery.db")), new VaultStore(destination), destinationCheckout);
            destinationCheckout = null;
            try
            {
                history?.Save(destination);
                DeleteVaultData(sourceRoot, records);
            }
            finally { sourceCheckout?.Dispose(); }
        }
        catch (Exception exception)
        {
            // Until publication, the original vault remains intact and can be unlocked again.
            if (published)
                throw new IOException($"The gallery was moved to '{destination}', but old-location cleanup or history persistence failed. The verified destination is intact; inspect the original location before removing any remaining files.", exception);
            if (!published && key is not null && !galleryLifetime.IsDisposed) vaultSession.Unlock(key);
            throw;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            destinationCheckout?.Dispose();
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { lifecycleGate.Release(); }
        }
    }

    public async Task PurgeVaultAsync(string confirmation)
    {
        await lifecycleGate.WaitAsync();
        byte[]? key = null;
        try
        {
            if (confirmation != "PURGE")
                throw new InvalidOperationException("Type PURGE to confirm permanent destruction of this gallery.");
            EnsureCheckout();
            key = vaultSession.Key;
            await StopDrivePlayerAsync();
            await vaultSession.LockAndDrainAsync();
            await ReconcileOutstandingDeletesAsync(key);
            var records = await galleryStore.ReadRecordsAsync(key);
            foreach (var record in records) ValidateMediaPath(record);
            DeleteVaultData(VaultRoot, records);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            lifecycleGate.Release();
        }
    }

    private async Task ReconcileOutstandingDeletesAsync(byte[] key)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var ownedPaths = (await galleryStore.ReadRecordsAsync(key))
                .Select(record => Path.GetFullPath(record.StoredPath))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var operation in await galleryStore.ReadOutstandingDeletesAsync())
            {
                var payload = DriveOperationProcessor.Read(operation, key);
                var path = Path.GetFullPath(payload.Path);
                if (!DriveOperationProcessor.PathFingerprint(key, path).AsSpan().SequenceEqual(operation.Target.AsSpan()))
                    throw new InvalidOperationException("Queued deletion path does not match its authenticated target.");
                if (!ownedPaths.Contains(path))
                    await DeleteObsoleteCiphertextAsync(galleryStore, operation, path, key);
                await galleryStore.CompleteDriveOperationsAsync(
                    [new DriveOperationResult(operation.Sequence, operation.Revision, DriveOperationOutcome.Done)]);
            }
        }
        finally { galleryStore.Gate.Release(); }
    }

    private static async Task DeleteObsoleteCiphertextAsync(
        GalleryStore store, DriveOperation operation, string path, byte[] key)
    {
        if (Directory.Exists(path)) throw new IOException("Queued ciphertext deletion points to a directory.");
        if (File.Exists(path))
        {
            if (operation.ImageId is null || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Queued ciphertext ownership cannot be verified.");
            await VerifyCiphertextAsync(path, operation.ImageId, key);
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
        }
        await store.RemoveIndexedFileAsync(DriveOperationProcessor.PathFingerprint(key, path));
    }

    private static async Task VerifyCiphertextAsync(string path, string imageId, byte[] key)
    {
        var plain = await StoredMediaProcessor.ReadAsync(path, imageId, key);
        CryptographicOperations.ZeroMemory(plain);
    }

    private static string NormalizeVaultDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        if (PathEquals(root, Path.GetPathRoot(root)!))
            throw new InvalidOperationException("A filesystem or share root cannot be a gallery. Select a dedicated folder.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Gallery directories cannot use symbolic links or directory junctions.");
        return root;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RejectOverlappingDirectories(string destination, string source)
    {
        if (IsPathWithin(destination, source) || IsPathWithin(source, destination))
            throw new InvalidOperationException($"Destination '{destination}' overlaps the current gallery folder '{source}'; choose a separate destination.");
    }

    private static void RequireEmptyDestination(string directory)
    {
        if (File.Exists(directory) || Directory.Exists(directory) &&
            Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != ".vault-checkout"))
            throw new InvalidOperationException($"Destination '{directory}' is not empty; choose a new or empty destination folder. Existing files will not be overwritten.");
    }

    private static string MediaRelativePath(ImageRecord record)
    {
        if (record.Id.Length < 4 || record.Id.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException("The gallery contains an invalid media identifier.");
        return Path.Combine("images", record.Id[..2], record.Id[2..4],
            record.Id + (record.IsVideo ? ".dmp4" : ".dpng"));
    }

    private static void ValidateMediaPath(ImageRecord record)
    {
        var expected = record.IsVideo ? ".dmp4" : ".dpng";
        if (!string.Equals(Path.GetExtension(record.StoredPath), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected encrypted media path for '{record.Name}'.");
        if (File.Exists(record.StoredPath) && File.GetAttributes(record.StoredPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Encrypted media cannot be a symbolic link.");
    }

    private static void DeleteVaultData(string root, IEnumerable<ImageRecord> records)
    {
        var media = records.Select(record => record.StoredPath).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var imagesDirectory = Path.Combine(root, "images");
        if (Directory.Exists(imagesDirectory))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(imagesDirectory, "*", options))
                if (Path.GetExtension(path) is ".dpng" or ".dmp4" || path.Contains(".removing-", StringComparison.Ordinal))
                    media.Add(path);
        }
        foreach (var path in media) File.Delete(path);
        if (Directory.Exists(imagesDirectory))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(imagesDirectory, "*", options).OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(imagesDirectory).Any()) Directory.Delete(imagesDirectory);
        }
        // Delete the header last; a partial failure remains an identifiable, checked-out vault.
        foreach (var name in new[] { "gallery.db-wal", "gallery.db-shm", "gallery.db", "vault.json" })
            File.Delete(Path.Combine(root, name));
    }

    private static void PublishStagedVault(string staging, string destination)
    {
        var moved = new List<string>();
        try
        {
            foreach (var name in new[] { "images", "gallery.db", "vault.json" })
            {
                var source = Path.Combine(staging, name);
                var target = Path.Combine(destination, name);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else if (File.Exists(source)) File.Move(source, target);
                else continue;
                moved.Add(name);
            }
        }
        catch
        {
            foreach (var name in moved.AsEnumerable().Reverse())
            {
                var source = Path.Combine(destination, name);
                var target = Path.Combine(staging, name);
                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
            }
            throw;
        }
    }

    // Queued drive work and the file index describe paths at the old location, so they never travel with a copy.
    private static async Task ClearLocationSettingsAsync(GalleryStore store, byte[] key)
    {
        await store.DeleteSettingsAsync(StorageOverviewId, MediaIndexId, "media-storage");
        await store.ClearDriveStateAsync();
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(BackupSettingsId, BackupSettingsPurpose, key);
        if (settings is not null)
            await store.SaveSettingsAsync(settings with { LastBackupName = null, LastBackupType = null },
                BackupSettingsId, BackupSettingsPurpose, key);
    }
}
