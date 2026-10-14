using System.Security.Cryptography;
using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers;

namespace Gallery.Web.Services;

public sealed partial class GalleryService
{
    private readonly SemaphoreSlim lifecycleGate = new(1);
    private VaultCheckout? checkout;
    private VaultHistory? history;
    private int disposalStarted;

    public static string DefaultApplicationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery02");
    public string VaultName => vaultStore.Name;

    public static string GenerateVaultName()
    {
        string[] adjectives = ["bright", "quiet", "curious", "gentle", "bold", "silver", "cosmic", "merry"];
        string[] nouns = ["otter", "nebula", "willow", "fox", "comet", "orchid", "raven", "badger"];
        return $"{adjectives[RandomNumberGenerator.GetInt32(adjectives.Length)]}-{nouns[RandomNumberGenerator.GetInt32(nouns.Length)]}-{RandomNumberGenerator.GetInt32(10000):D4}";
    }

    public static string DefaultVaultDirectory(string name) =>
        Path.Combine(DefaultApplicationDirectory, "galleries", name);

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
    private void EnsureCheckout()
    {
        EnsureActive();
        checkout ??= VaultCheckout.Acquire(VaultRoot);
    }

    public Task OpenExistingVaultAsync(string directory) => SelectVaultAsync(directory, existing: true);
    public Task SelectNewVaultAsync(string directory) => SelectVaultAsync(directory, existing: false);

    private async Task SelectVaultAsync(string directory, bool existing)
    {
        var root = NormalizeVaultDirectory(directory);
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
            var nextCheckout = VaultCheckout.Acquire(root);
            try
            {
                if (!existing) RequireEmptyDestination(root);
                await StopDrivePlayerAsync();
                await vaultSession.LockAndDrainAsync();
                EnsureActive();
                checkout?.Dispose();
                checkout = nextCheckout;
                vaultStore = store;
                galleryStore = new GalleryStore(Path.Combine(root, "gallery.db"));
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
            await StopDrivePlayerAsync();
            await vaultSession.LockAndDrainAsync();
            checkout?.Dispose();
            checkout = null;
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task RenameVaultAsync(string name)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            using var key = vaultSession.AcquireUnlockedSession().Borrow();
            EnsureCheckout();
            await vaultStore.RenameAsync(name);
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task MoveVaultAsync(string directory)
    {
        var destination = NormalizeVaultDirectory(directory);
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
            destinationCheckout = VaultCheckout.Acquire(destination);
            RequireEmptyDestination(destination);
            key = vaultSession.Key;
            await StopDrivePlayerAsync();
            await vaultSession.LockAndDrainAsync();
            var sourceRoot = VaultRoot;
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
            var sourceCheckout = checkout;
            checkout = destinationCheckout;
            destinationCheckout = null;
            vaultStore = new VaultStore(destination);
            galleryStore = new GalleryStore(Path.Combine(destination, "gallery.db"));
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
            if (!published && key is not null && Volatile.Read(ref disposalStarted) == 0) vaultSession.Unlock(key);
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
            throw new InvalidOperationException("The destination must be separate from the current gallery folder.");
    }

    private static void RequireEmptyDestination(string directory)
    {
        if (File.Exists(directory) || Directory.Exists(directory) &&
            Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != ".vault-checkout"))
            throw new InvalidOperationException("Choose a new or empty destination folder. Existing files will not be overwritten.");
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
        var images = Path.Combine(root, "images");
        if (Directory.Exists(images))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(images, "*", options))
                if (Path.GetExtension(path) is ".dpng" or ".dmp4" || path.Contains(".removing-", StringComparison.Ordinal))
                    media.Add(path);
        }
        foreach (var path in media) File.Delete(path);
        if (Directory.Exists(images))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(images, "*", options).OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(images).Any()) Directory.Delete(images);
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
        await store.DeleteSettingsAsync(StorageOverviewId, MediaIndexId);
        await store.ClearDriveStateAsync();
        var settings = await store.ReadSettingsAsync<StoredBackupSettings>(BackupSettingsId, BackupSettingsPurpose, key);
        if (settings is not null)
            await store.SaveSettingsAsync(settings with { LastBackupName = null, LastBackupType = null },
                BackupSettingsId, BackupSettingsPurpose, key);
    }
}
