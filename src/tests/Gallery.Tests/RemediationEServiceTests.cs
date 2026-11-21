using System.IO.Compression;
using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Ingestion.Managers.Scanners;
using Gallery.Core.Features.Media.Removal;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Storage;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class RemediationEServiceTests
{
    [Fact]
    public async Task DuplicateScan_WhenGalleryIsSwitchedDuringScan_ThenResultsStayInTheScannedGallery()
    {
        using var fixture = new TestVault();
        var other = fixture.Root + "-other";
        try
        {
            await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "First");
            await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
            await fixture.Scope.Duplicates.ScanDuplicatesAsync();
            await fixture.Scope.Vault.SelectNewVaultAsync(other);
            await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Second");
            await fixture.Scope.Vault.OpenExistingVaultAsync(fixture.Root);
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Task? switchGallery = null;

            var scan = await fixture.Scope.Duplicates.ScanDuplicatesAsync(
                new SynchronousProgress(_ => switchGallery ??= fixture.Scope.Vault.OpenExistingVaultAsync(other)));
            await switchGallery!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, scan.ScannedCount);
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal("Second", fixture.Scope.Vault.VaultName);
            Assert.Null(await fixture.Scope.Duplicates.CachedDuplicateScanAsync());
            await fixture.Scope.Vault.OpenExistingVaultAsync(fixture.Root);
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal(scan.ScannedAt, (await fixture.Scope.Duplicates.CachedDuplicateScanAsync())!.ScannedAt);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void MediaIndex_WhenExternalDirectoriesDifferOnlyByCase_ThenUsesPlatformPathComparison()
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-media-index-" + Guid.NewGuid().ToString("N"));
        var imageRoot = Path.Combine(root, "images");
        var upper = Path.Combine(root, "external", "A");
        var lower = Path.Combine(root, "external", "a");

        var directories = MediaStorageIndex.ExternalMediaDirectories(
            [Path.Combine(upper, "1.blob"), Path.Combine(lower, "2.blob"), Path.Combine(imageRoot, "3.blob")],
            imageRoot).ToArray();

        Assert.Equal(OperatingSystem.IsWindows() ? [upper] : new[] { upper, lower }, directories);
    }

    [Fact]
    public void IngestionDrive_WhenAMountNamePrefixesASiblingDirectory_ThenSelectsOnlyAtADirectoryBoundary()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var mount = Path.Combine(root, "mnt", "media");
        string[] mounts = [root, mount];

        Assert.Equal(root, IngestionSourceScanner.SelectIngestionMount(Path.Combine(root, "mnt", "media-old", "inbox"), mounts));
        Assert.Equal(mount, IngestionSourceScanner.SelectIngestionMount(Path.Combine(mount, "inbox"), mounts));
        Assert.Equal(mount, IngestionSourceScanner.SelectIngestionMount(mount, mounts));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Relocation_WhenItFailsAfterLocking_ThenRestoresTheSessionOnlyWithoutANewerExplicitLock(
        bool catalogOnly, bool lockDuringMove)
    {
        using var fixture = new TestVault();
        var destination = fixture.Root + "-moved";
        var header = Path.Combine(fixture.Root, "vault.json");
        try
        {
            await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
            Task move;
            using var lease = fixture.Session.AcquireUnlockedSession().Borrow();
            try
            {
                move = catalogOnly
                    ? fixture.Scope.Vault.RelocateCatalogAsync(destination)
                    : fixture.Scope.Vault.MoveVaultAsync(destination);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (fixture.Scope.Vault.IsUnlocked && DateTime.UtcNow < deadline) await Task.Delay(5);
                Assert.False(fixture.Scope.Vault.IsUnlocked);
                if (lockDuringMove) fixture.Scope.Vault.Panic();
                File.Move(header, header + ".hidden");
            }
            finally { lease.Dispose(); }

            await Assert.ThrowsAsync<InvalidOperationException>(() => move);
            File.Move(header + ".hidden", header);

            Assert.Equal(!lockDuringMove, fixture.Scope.Vault.IsUnlocked);
            Assert.Equal(fixture.Root, fixture.Scope.Vault.VaultRoot);
            if (lockDuringMove)
                await Assert.ThrowsAnyAsync<Exception>(() => fixture.Scope.Catalog.ListAsync());
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.True(fixture.Scope.Vault.IsUnlocked);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Removal_WhenAPriorDriveQueueFaultIsUnobserved_ThenDeliversEveryReceiptAndSurfacesTheFault()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");
        await fixture.Scope.Records.UpdateManyAsync([first.Id, second.Id], image => image with { IsMarkedForDeletion = true });
        var plan = await fixture.Scope.MediaMaintenance.PrepareRemovalAsync([first.Id, second.Id]);
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var queueFailure = new IOException("Cannot read queue counts");
        var reads = 0;
        using var faultedPlayer = new DriveOperationPlayer(() => null, () => Interlocked.Increment(ref reads) == 1
            ? Task.FromException<DriveQueueCounts>(queueFailure)
            : Task.FromResult(new DriveQueueCounts(0, 0, null)));
        faultedPlayer.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (faultedPlayer.Status.State != DriveQueueState.Faulted && DateTime.UtcNow < deadline) await Task.Delay(1);
        var lifetime = new GalleryLifetime(fixture.Store, new VaultStore(fixture.Root));
        var removalManager = new RemovalManager(lifetime, fixture.Session, faultedPlayer.Start);
        var observed = new List<RemovalResult>();

        var thrown = await Assert.ThrowsAsync<IOException>(() => removalManager.RemoveAsync(
            plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", observed.Add));

        Assert.Same(queueFailure, thrown);
        Assert.Equal([first.Id, second.Id], observed.Select(receipt => receipt.Id));
        Assert.All(observed, receipt => Assert.True(receipt.WasRemoved));
        Assert.Empty(await fixture.Scope.Catalog.ListAsync());
        await Assert.ThrowsAsync<IOException>(faultedPlayer.WhenIdleAsync);
    }

    [Fact]
    public void ArchiveBounds_WhenEntriesDeclareMoreThanTheArchiveHolds_ThenRejectsBeforeExtraction()
    {
        using var compressed = CreateArchive("gallery.db", new byte[4 * 1024 * 1024], CompressionLevel.Optimal);
        using var stored = CreateArchive("gallery.db", new byte[64 * 1024], CompressionLevel.NoCompression);

        using (var archive = new ZipArchive(compressed, ZipArchiveMode.Read))
            Assert.Throws<InvalidDataException>(() => BackupArchive.RequireEntriesWithinArchive(archive, compressed.Length));
        using (var archive = new ZipArchive(stored, ZipArchiveMode.Read))
            BackupArchive.RequireEntriesWithinArchive(archive, stored.Length);
    }

    [Fact]
    public async Task Restore_WhenHeaderEntryExpandsBeyondItsLimit_ThenRejectsBeforeCredentialChecksAndKeepsCurrentGallery()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var backups = fixture.Root + "-backups";
        var destination = fixture.Root + "-restored";
        Directory.CreateDirectory(backups);
        try
        {
            using (var archive = CreateArchive("vault.json", new byte[8 * 1024 * 1024], CompressionLevel.Optimal))
                await File.WriteAllBytesAsync(Path.Combine(backups, "expanding.zip"), archive.ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Scope.Restore.RestoreVaultAsync(
                "local", backups, "", "", "", "expanding.zip", destination, "wrong credential for this backup"));

            Assert.True(fixture.Scope.Vault.IsUnlocked);
            Assert.Equal(fixture.Root, fixture.Scope.Vault.VaultRoot);
            Assert.False(File.Exists(Path.Combine(destination, "vault.json")));
            Assert.Empty(Directory.Exists(destination)
                ? Directory.EnumerateDirectories(destination, ".vault-stage-*") : []);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    private static MemoryStream CreateArchive(string entryName, byte[] content, CompressionLevel compressionLevel)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entryStream = archive.CreateEntry(entryName, compressionLevel).Open();
            entryStream.Write(content);
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class SynchronousProgress(Action<(int Completed, int Total)> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => report(value);
    }
}
