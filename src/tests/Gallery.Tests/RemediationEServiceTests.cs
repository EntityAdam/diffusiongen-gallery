using System.IO.Compression;
using System.Reflection;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;

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
            await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "First");
            await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
            await fixture.Service.ScanDuplicatesAsync();
            await fixture.Service.SelectNewVaultAsync(other);
            await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Second");
            await fixture.Service.OpenExistingVaultAsync(fixture.Root);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Task? switchGallery = null;

            var scan = await fixture.Service.ScanDuplicatesAsync(
                new SynchronousProgress(_ => switchGallery ??= fixture.Service.OpenExistingVaultAsync(other)));
            await switchGallery!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, scan.ScannedCount);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal("Second", fixture.Service.VaultName);
            Assert.Null(await fixture.Service.CachedDuplicateScanAsync());
            await fixture.Service.OpenExistingVaultAsync(fixture.Root);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal(scan.ScannedAt, (await fixture.Service.CachedDuplicateScanAsync())!.ScannedAt);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
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

        var directories = GalleryService.ExternalMediaDirectories(
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

        Assert.Equal(root, GalleryService.SelectIngestionMount(Path.Combine(root, "mnt", "media-old", "inbox"), mounts));
        Assert.Equal(mount, GalleryService.SelectIngestionMount(Path.Combine(mount, "inbox"), mounts));
        Assert.Equal(mount, GalleryService.SelectIngestionMount(mount, mounts));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Move_WhenItFailsAfterLocking_ThenRestoresTheSessionOnlyWithoutANewerExplicitLock(bool lockDuringMove)
    {
        using var fixture = new TestVault();
        var destination = fixture.Root + "-moved";
        var header = Path.Combine(fixture.Root, "vault.json");
        try
        {
            await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await fixture.Service.WaitForDriveOperationsAsync();
            Task move;
            await fixture.Store.Gate.WaitAsync();
            try
            {
                move = fixture.Service.MoveVaultAsync(destination);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (fixture.Service.IsUnlocked && DateTime.UtcNow < deadline) await Task.Delay(5);
                Assert.False(fixture.Service.IsUnlocked);
                if (lockDuringMove) fixture.Service.Panic();
                File.Move(header, header + ".hidden");
            }
            finally { fixture.Store.Gate.Release(); }

            await Assert.ThrowsAsync<InvalidOperationException>(() => move);
            File.Move(header + ".hidden", header);

            Assert.Equal(!lockDuringMove, fixture.Service.IsUnlocked);
            Assert.Equal(fixture.Root, fixture.Service.VaultRoot);
            if (lockDuringMove)
                await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ListAsync());
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.True(fixture.Service.IsUnlocked);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Removal_WhenAPriorDriveQueueFaultIsUnobserved_ThenDeliversEveryReceiptAndSurfacesTheFault()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 10)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(21, 10)), "second.png", "folder");
        await fixture.Service.UpdateManyAsync([first.Id, second.Id], image => image with { IsMarkedForDeletion = true });
        var plan = await fixture.Service.PrepareRemovalAsync([first.Id, second.Id]);
        await fixture.Service.WaitForDriveOperationsAsync();
        var queueFailure = new IOException("Cannot read queue counts");
        var reads = 0;
        using var faultedPlayer = new DriveOperationPlayer(() => null, () => Interlocked.Increment(ref reads) == 1
            ? Task.FromException<DriveQueueCounts>(queueFailure)
            : Task.FromResult(new DriveQueueCounts(0, 0, null)));
        faultedPlayer.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (faultedPlayer.Status.State != DriveQueueState.Faulted && DateTime.UtcNow < deadline) await Task.Delay(1);
        var playerField = typeof(GalleryService).GetField("driveOperationPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalPlayer = playerField.GetValue(fixture.Service);
        playerField.SetValue(fixture.Service, faultedPlayer);
        var observed = new List<RemovalResult>();
        try
        {
            var thrown = await Assert.ThrowsAsync<IOException>(() => fixture.Service.RemoveAsync(
                plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, "", observed.Add));

            Assert.Same(queueFailure, thrown);
            Assert.Equal([first.Id, second.Id], observed.Select(receipt => receipt.Id));
            Assert.All(observed, receipt => Assert.True(receipt.WasRemoved));
            Assert.Empty(await fixture.Service.ListAsync());
            await Assert.ThrowsAsync<IOException>(faultedPlayer.WhenIdleAsync);
        }
        finally { playerField.SetValue(fixture.Service, originalPlayer); }
    }

    [Fact]
    public void ArchiveBounds_WhenEntriesDeclareMoreThanTheArchiveHolds_ThenRejectsBeforeExtraction()
    {
        using var compressed = CreateArchive("gallery.db", new byte[4 * 1024 * 1024], CompressionLevel.Optimal);
        using var stored = CreateArchive("gallery.db", new byte[64 * 1024], CompressionLevel.NoCompression);

        using (var archive = new ZipArchive(compressed, ZipArchiveMode.Read))
            Assert.Throws<InvalidDataException>(() => GalleryService.RequireEntriesWithinArchive(archive, compressed.Length));
        using (var archive = new ZipArchive(stored, ZipArchiveMode.Read))
            GalleryService.RequireEntriesWithinArchive(archive, stored.Length);
    }

    [Fact]
    public async Task Restore_WhenHeaderEntryExpandsBeyondItsLimit_ThenRejectsBeforeCredentialChecksAndKeepsCurrentGallery()
    {
        using var fixture = new TestVault();
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var backups = fixture.Root + "-backups";
        var destination = fixture.Root + "-restored";
        Directory.CreateDirectory(backups);
        try
        {
            using (var archive = CreateArchive("vault.json", new byte[8 * 1024 * 1024], CompressionLevel.Optimal))
                await File.WriteAllBytesAsync(Path.Combine(backups, "expanding.zip"), archive.ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RestoreVaultAsync(
                "local", backups, "", "", "", "expanding.zip", destination, "wrong credential for this backup"));

            Assert.True(fixture.Service.IsUnlocked);
            Assert.Equal(fixture.Root, fixture.Service.VaultRoot);
            Assert.False(File.Exists(Path.Combine(destination, "vault.json")));
            Assert.Empty(Directory.Exists(destination)
                ? Directory.EnumerateDirectories(destination, ".vault-stage-*") : []);
        }
        finally
        {
            await fixture.Service.CloseVaultAsync();
            if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Creation_WhenInterruptedAfterPublishingOnlyTheCatalog_ThenRetryRollsBackItsOwnCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-interrupted-create-" + Guid.NewGuid().ToString("N"));
        var state = root + "-state";
        Directory.CreateDirectory(root);
        try
        {
            await ArrangeInterruptedPublicationAsync(root, markerMatches: true);
            using var service = GalleryService.Create(root, new GalleryRuntimeOptions { StateDirectory = state }, new HttpClient());
            try
            {
                var recovery = await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Retried");
                service.LockVault();
                await service.UnlockWithRecoveryPhraseAsync(recovery);

                Assert.Equal("Retried", service.VaultName);
                Assert.Empty(await service.ListAsync());
                Assert.Empty(Directory.EnumerateDirectories(root, ".vault-create-*"));
            }
            finally { await service.CloseVaultAsync(); }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public async Task Creation_WhenOrphanCatalogDoesNotMatchAStagedCreation_ThenPreservesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-unrelated-create-" + Guid.NewGuid().ToString("N"));
        var state = root + "-state";
        Directory.CreateDirectory(root);
        try
        {
            await ArrangeInterruptedPublicationAsync(root, markerMatches: false);
            var catalog = Path.Combine(root, "gallery.db");
            var original = await File.ReadAllBytesAsync(catalog);
            using var service = GalleryService.Create(root, new GalleryRuntimeOptions { StateDirectory = state }, new HttpClient());
            try
            {
                await Assert.ThrowsAsync<IOException>(() =>
                    service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Retried"));

                Assert.Equal(original, await File.ReadAllBytesAsync(catalog));
                Assert.False(service.VaultExists);
            }
            finally { await service.CloseVaultAsync(); }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
        }
    }

    private static async Task ArrangeInterruptedPublicationAsync(string root, bool markerMatches)
    {
        var staging = Path.Combine(root, VaultManager.CreationStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        await new VaultStore(staging).CreateAsync(TestVault.Passphrase);
        await new GalleryStore(Path.Combine(staging, "gallery.db")).InitializeAsync();
        await VaultManager.WritePublicationMarkerAsync(staging);
        if (!markerMatches)
            await File.WriteAllTextAsync(Path.Combine(staging, VaultManager.PublicationMarkerFileName), new string('0', 64));
        File.Move(Path.Combine(staging, "gallery.db"), Path.Combine(root, "gallery.db"));
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
