using Gallery.Core.Features.Backups;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativeOperationLeaseTests
{
    [Fact]
    public async Task FullBackup_WhenLockedDuringUpload_ThenPublishesAndUpdatesChainTracking()
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-backup-lease-" + Guid.NewGuid().ToString("N"));
        var mediator = new TestMediatorAzureUploads();
        using var service = GalleryScope.Create(root,
            new GalleryRuntimeOptions { StateDirectory = root + "-state" },
            new HttpClient(mediator.CreateHttpMessageHandler()));
        Task<BackupRunResult>? backup = null;
        try
        {
            await service.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await service.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "backup.png", "folder");
            await service.DriveOperations.WaitForDriveOperationsAsync();
            var connection = $"DefaultEndpointsProtocol=https;AccountName=testaccount;AccountKey={Convert.ToBase64String(new byte[32])}";
            await service.Backup.SaveBackupOptionsAsync("azure", "", "gallery-backups", "nightly", connection);
            backup = service.Backup.CreateFullBackupAsync();
            await mediator.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            service.Vault.LockVault();
            await Assert.ThrowsAsync<VaultLockedException>(() => service.Catalog.ListAsync());
            mediator.ReleaseUpload.TrySetResult();

            var result = await backup.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(service.Vault.IsUnlocked);
            var archivePath = "/gallery-backups/nightly/" + result.BackupName;
            Assert.True(mediator.UploadedBlobs.ContainsKey(archivePath + ".manifest"));
            using (var archive = new ZipArchive(new MemoryStream(mediator.UploadedBlobs[archivePath])))
            {
                Assert.NotNull(archive.GetEntry("gallery.db"));
                Assert.Single(archive.Entries, entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal));
            }
            await service.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var options = await service.Backup.LoadBackupOptionsAsync();
            Assert.Equal(result.BackupName, options.LastBackupName);
            Assert.Equal("full", options.LastBackupType);
        }
        finally
        {
            mediator.ReleaseUpload.TrySetResult();
            try { if (backup is not null) await backup.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                await service.Vault.CloseVaultAsync();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(root + "-state")) Directory.Delete(root + "-state", recursive: true);
            }
        }
    }

    [Fact]
    public async Task IncrementalBackup_WhenSessionExpiresDuringParentValidation_ThenFinishesWithItsAdmittedLease()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "backup.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var destination = fixture.Root + "-backups";
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            await fixture.Scope.Records.UpdateAsync(image.Id, edit => edit with { Description = "new backup metadata" });
            using var parent = new Activity("incremental-lease-test").Start();
            var expired = false;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == BackupArchive.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName != "backup.chain.validate" || activity.TraceId != parent.TraceId) return;
                    fixture.Scope.Vault.ConfigureInactivityTimeout(TimeSpan.FromTicks(1));
                    expired = !fixture.Scope.Vault.IsUnlocked;
                }
            };
            ActivitySource.AddActivityListener(listener);

            var result = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            Assert.True(expired);
            Assert.False(result.WasSkipped);
            Assert.NotEqual(full.BackupName, result.BackupName);
            Assert.True(File.Exists(Path.Combine(destination, result.BackupName + ".manifest")));
            await Assert.ThrowsAsync<VaultLockedException>(() => fixture.Scope.Catalog.ListAsync());
            fixture.Scope.Vault.ConfigureInactivityTimeout(null);
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var options = await fixture.Scope.Backup.LoadBackupOptionsAsync();
            Assert.Equal(result.BackupName, options.LastBackupName);
            Assert.Equal("incremental", options.LastBackupType);
        }
        finally
        {
            fixture.Scope.Vault.ConfigureInactivityTimeout(null);
            await fixture.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateScan_WhenLocationChangesDuringProgress_ThenRetainsItsOriginalStoreAndKey(bool warmCache)
    {
        using var source = new TestVault();
        using var destination = new TestVault();
        await source.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await source.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        await source.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(64, 48)), "second.png", "folder");
        if (warmCache) await source.Scope.Duplicates.ScanDuplicatesAsync();
        await source.Scope.DriveOperations.WaitForDriveOperationsAsync();
        await destination.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var destinationImage = await destination.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "destination.png", "folder");
        await destination.Scope.Duplicates.ScanDuplicatesAsync();
        var settings = new[] { "duplicate-scan", "perceptual-hashes" };
        var expectedCache = new Dictionary<string, byte[]>();
        foreach (var id in settings) expectedCache[id] = (await destination.RawStore.ReadSettingsAsync(id))!;
        await destination.Scope.Vault.CloseVaultAsync();
        var progress = new BlockingProgress();
        var scan = Task.Run(() => source.Scope.Duplicates.ScanDuplicatesAsync(progress));
        Task? selection = null;
        try
        {
            await progress.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Scope.Vault.LockVault();
            Assert.False(source.Session.WhenDrainedAsync().IsCompleted);
            selection = source.Scope.Vault.OpenExistingVaultAsync(destination.Root);
            Assert.False(selection.IsCompleted);
            Assert.Equal(source.Root, source.Scope.Vault.VaultRoot);
            progress.Release.TrySetResult();

            var result = await scan.WaitAsync(TimeSpan.FromSeconds(10));
            await selection.WaitAsync(TimeSpan.FromSeconds(10));
            await source.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);

            Assert.Equal(2, result.ScannedCount);
            Assert.Equal(destinationImage.Id, Assert.Single(await source.Scope.Catalog.ListAsync()).Id);
            Assert.Equal(1, (await source.Scope.Duplicates.CachedDuplicateScanAsync())!.ScannedCount);
            foreach (var id in settings)
                Assert.Equal(expectedCache[id], await destination.RawStore.ReadSettingsAsync(id));
        }
        finally
        {
            progress.Release.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            if (selection is not null) await selection.WaitAsync(TimeSpan.FromSeconds(10));
            await source.Scope.Vault.CloseVaultAsync();
        }
    }

    private sealed class BlockingProgress : IProgress<(int Completed, int Total)>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report((int Completed, int Total) value)
        {
            if (!Entered.TrySetResult()) return;
            Release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class TestMediatorAzureUploads
    {
        internal TaskCompletionSource UploadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseUpload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Dictionary<string, byte[]> UploadedBlobs { get; } = [];
        internal HttpMessageHandler CreateHttpMessageHandler() => new SpyingHandler(this);

        private sealed class SpyingHandler(TestMediatorAzureUploads mediator) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                mediator.UploadEntered.TrySetResult();
                await mediator.ReleaseUpload.Task.WaitAsync(cancellationToken);
                mediator.UploadedBlobs.Add(request.RequestUri!.AbsolutePath, bytes);
                var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
                response.Headers.ETag = new("\"test\"");
                response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
                response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString("N"));
                return response;
            }
        }
    }
}
