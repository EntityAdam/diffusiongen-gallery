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
        using var service = GalleryService.Create(root,
            new GalleryRuntimeOptions { StateDirectory = root + "-state" },
            new HttpClient(mediator.CreateHttpMessageHandler()));
        Task<BackupRunResult>? backup = null;
        try
        {
            await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "backup.png", "folder");
            await service.WaitForDriveOperationsAsync();
            var connection = $"DefaultEndpointsProtocol=https;AccountName=testaccount;AccountKey={Convert.ToBase64String(new byte[32])}";
            await service.SaveBackupOptionsAsync("azure", "", "gallery-backups", "nightly", connection);
            backup = service.CreateFullBackupAsync();
            await mediator.UploadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            service.LockVault();
            await Assert.ThrowsAsync<VaultLockedException>(() => service.ListAsync());
            mediator.ReleaseUpload.TrySetResult();

            var result = await backup.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(service.IsUnlocked);
            var archivePath = "/gallery-backups/nightly/" + result.BackupName;
            Assert.True(mediator.UploadedBlobs.ContainsKey(archivePath + ".manifest"));
            using (var archive = new ZipArchive(new MemoryStream(mediator.UploadedBlobs[archivePath])))
            {
                Assert.NotNull(archive.GetEntry("gallery.db"));
                Assert.Single(archive.Entries, entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal));
            }
            await service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var options = await service.LoadBackupOptionsAsync();
            Assert.Equal(result.BackupName, options.LastBackupName);
            Assert.Equal("full", options.LastBackupType);
        }
        finally
        {
            mediator.ReleaseUpload.TrySetResult();
            try { if (backup is not null) await backup.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                await service.CloseVaultAsync();
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
        var image = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "backup.png", "folder");
        await fixture.Service.WaitForDriveOperationsAsync();
        var destination = fixture.Root + "-backups";
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Service.CreateFullBackupAsync();
            await fixture.Service.UpdateAsync(image.Id, edit => edit with { Description = "new backup metadata" });
            using var parent = new Activity("incremental-lease-test").Start();
            var expired = false;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GalleryService.BackupActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName != "backup.chain.validate" || activity.TraceId != parent.TraceId) return;
                    fixture.Service.ConfigureInactivityTimeout(TimeSpan.FromTicks(1));
                    expired = !fixture.Service.IsUnlocked;
                }
            };
            ActivitySource.AddActivityListener(listener);

            var result = await fixture.Service.CreateIncrementalBackupAsync();

            Assert.True(expired);
            Assert.False(result.WasSkipped);
            Assert.NotEqual(full.BackupName, result.BackupName);
            Assert.True(File.Exists(Path.Combine(destination, result.BackupName + ".manifest")));
            await Assert.ThrowsAsync<VaultLockedException>(() => fixture.Service.ListAsync());
            fixture.Service.ConfigureInactivityTimeout(null);
            await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            var options = await fixture.Service.LoadBackupOptionsAsync();
            Assert.Equal(result.BackupName, options.LastBackupName);
            Assert.Equal("incremental", options.LastBackupType);
        }
        finally
        {
            fixture.Service.ConfigureInactivityTimeout(null);
            await fixture.Service.CloseVaultAsync();
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
        await source.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await source.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        await source.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(64, 48)), "second.png", "folder");
        if (warmCache) await source.Service.ScanDuplicatesAsync();
        await source.Service.WaitForDriveOperationsAsync();
        await destination.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var destinationImage = await destination.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 30)), "destination.png", "folder");
        await destination.Service.ScanDuplicatesAsync();
        var settings = new[] { "duplicate-scan", "perceptual-hashes" };
        var expectedCache = new Dictionary<string, byte[]>();
        foreach (var id in settings) expectedCache[id] = (await destination.RawStore.ReadSettingsAsync(id))!;
        await destination.Service.CloseVaultAsync();
        var progress = new BlockingProgress();
        var scan = Task.Run(() => source.Service.ScanDuplicatesAsync(progress));
        Task? selection = null;
        try
        {
            await progress.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Service.LockVault();
            Assert.False(source.Session.WhenDrainedAsync().IsCompleted);
            selection = source.Service.OpenExistingVaultAsync(destination.Root);
            Assert.False(selection.IsCompleted);
            Assert.Equal(source.Root, source.Service.VaultRoot);
            progress.Release.TrySetResult();

            var result = await scan.WaitAsync(TimeSpan.FromSeconds(10));
            await selection.WaitAsync(TimeSpan.FromSeconds(10));
            await source.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);

            Assert.Equal(2, result.ScannedCount);
            Assert.Equal(destinationImage.Id, Assert.Single(await source.Service.ListAsync()).Id);
            Assert.Equal(1, (await source.Service.CachedDuplicateScanAsync())!.ScannedCount);
            foreach (var id in settings)
                Assert.Equal(expectedCache[id], await destination.RawStore.ReadSettingsAsync(id));
        }
        finally
        {
            progress.Release.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            if (selection is not null) await selection.WaitAsync(TimeSpan.FromSeconds(10));
            await source.Service.CloseVaultAsync();
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
