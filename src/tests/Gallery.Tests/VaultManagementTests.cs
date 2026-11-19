using System.Collections.Immutable;
using System.IO.Compression;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Services;
using Gallery.Web.Composition;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class VaultManagementTests
{
    [Fact]
    public async Task Selection_RemembersLastUsedGallery_AndLockReleasesCheckout()
    {
        using var fixture = new TestVault();
        var services = new ServiceCollection();
        GalleryServiceRegistration.Register(services, fixture.Configuration, fixture.Root);
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IGalleryVault>();
        var second = secondScope.ServiceProvider.GetRequiredService<IGalleryVault>();
        await first.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "First vault");
        Assert.Equal("First vault", first.VaultName);
        await Assert.ThrowsAsync<IOException>(() => second.OpenExistingVaultAsync(fixture.Root));
        first.Panic();
        await second.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await first.CloseVaultAsync();
        await second.OpenExistingVaultAsync(fixture.Root);
        await second.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await second.RenameVaultAsync("Renamed vault");
        var otherRoot = Path.Combine(fixture.Root, "second-vault");
        await second.SelectNewVaultAsync(otherRoot);
        Assert.False(second.IsUnlocked);
        Assert.False(second.VaultExists);
        await second.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Second vault");
        await first.OpenExistingVaultAsync(fixture.Root);
        await first.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal("Renamed vault", first.VaultName);
        await first.CloseVaultAsync();
        await second.UnlockWithPassphraseAsync(TestVault.Passphrase);
        using var nextScope = provider.CreateScope();
        var next = nextScope.ServiceProvider.GetRequiredService<IGalleryVault>();
        Assert.Equal(otherRoot, next.VaultRoot);
        Assert.Equal("Second vault", next.VaultName);
    }

    [Fact]
    public async Task Close_WaitsForOutstandingKeyLeases_BeforeReleasingCheckout()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        using var lease = fixture.Session.AcquireUnlockedSession().Borrow();
        var closing = fixture.Scope.Vault.CloseVaultAsync();
        Assert.False(closing.IsCompleted);
        Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));
        lease.Dispose();
        await closing;
        using var checkout = VaultCheckout.Acquire(fixture.Root);
        Assert.False(fixture.Scope.Vault.IsUnlocked);
    }

    [Fact]
    public async Task Lock_WaitsForOutstandingKeyLeases_BeforeReleasingCheckout()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        using var lease = fixture.Session.AcquireUnlockedSession().Borrow();
        fixture.Scope.Vault.LockVault();
        Assert.Throws<IOException>(() => VaultCheckout.Acquire(fixture.Root));
        lease.Dispose();
        using var checkout = await WaitForCheckoutReleaseAsync(fixture.Root);
        Assert.False(fixture.Scope.Vault.IsUnlocked);
    }

    [Theory]
    [InlineData("http://account.blob.core.windows.net/container?sr=c&sp=rl&sig=x")]
    [InlineData("https://account.blob.core.windows.net/container/blob.zip?sr=c&sp=rl&sig=x")]
    [InlineData("https://account.blob.core.windows.net/container?sr=b&sp=rl&sig=x")]
    [InlineData("https://account.blob.core.windows.net/container?sr=c&sp=r&sig=x")]
    [InlineData("https://account.blob.core.windows.net/container?sr=c&sp=l&sig=x")]
    public async Task RestoreFromSas_RejectsNonContainerOrInsufficientSas(string sasUrl)
    {
        using var fixture = new TestVault();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Scope.Restore.ListRestoreBackupsFromSasAsync(sasUrl));
        Assert.Contains("SAS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_TransfersCatalogAndExternalMedia_ThenPurgeDestroysOnlyVaultData()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Travel vault");
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(24, 16)), "second.png", "folder");
        var destination = NewTemporaryDirectory();
        var external = NewTemporaryDirectory();
        Directory.CreateDirectory(external);
        try
        {
            await fixture.Scope.MediaMaintenance.MoveAsync(second.Id, external, "external");
            var externalPath = (await fixture.Scope.Catalog.ListAsync()).Single(record => record.Id == second.Id).StoredPath;
            var originalThumbnail = await fixture.Scope.Media.ThumbnailAsync(first.Id);
            await fixture.Scope.Vault.MoveVaultAsync(destination);
            Assert.False(fixture.Scope.Vault.IsUnlocked);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
            Assert.False(File.Exists(first.StoredPath));
            Assert.False(File.Exists(externalPath));
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal("Travel vault", fixture.Scope.Vault.VaultName);
            var moved = await fixture.Scope.Catalog.ListAsync();
            Assert.Equal(2, moved.Count);
            Assert.All(moved, record => Assert.StartsWith(destination, record.StoredPath));
            Assert.Equal(originalThumbnail, await fixture.Scope.Media.ThumbnailAsync(first.Id));
            await File.WriteAllTextAsync(Path.Combine(destination, "unrelated.txt"), "keep");
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Vault.PurgeVaultAsync("yes"));
            Assert.True(fixture.Scope.Vault.IsUnlocked);
            await fixture.Scope.Vault.PurgeVaultAsync("PURGE");
            Assert.False(fixture.Scope.Vault.VaultExists);
            Assert.False(File.Exists(Path.Combine(destination, "gallery.db")));
            Assert.All(moved, record => Assert.False(File.Exists(record.StoredPath)));
            Assert.True(File.Exists(Path.Combine(destination, "unrelated.txt")));
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            DeleteTemporaryDirectory(destination);
            DeleteTemporaryDirectory(external);
        }
    }

    [Fact]
    public async Task Move_RejectsNonemptyAndOverlappingDestinations_WithoutChangingSource()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var destination = NewTemporaryDirectory();
        Directory.CreateDirectory(destination);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Vault.MoveVaultAsync(destination));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Scope.Vault.MoveVaultAsync(Path.Combine(fixture.Root, "nested")));
            Assert.True(fixture.Scope.Vault.IsUnlocked);
            Assert.Equal(fixture.Root, fixture.Scope.Vault.VaultRoot);
        }
        finally { DeleteTemporaryDirectory(destination); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Restore_RebuildsFullOrIncrementalChain_InANewDirectory(bool incremental, bool recovery)
    {
        using var fixture = new TestVault();
        var words = await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Original vault");
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var backups = NewTemporaryDirectory();
        var destination = NewTemporaryDirectory();
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", backups, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            var latest = full;
            if (incremental)
            {
                await fixture.Scope.Records.UpdateAsync(first.Id, edit => edit with { Description = "Latest metadata" });
                latest = await fixture.Scope.Backup.CreateIncrementalBackupAsync();
                await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(24, 16)), "new.png", "folder");
                latest = await fixture.Scope.Backup.CreateIncrementalBackupAsync();
            }
            Assert.Contains(latest.BackupName, await fixture.Scope.Restore.ListRestoreBackupsAsync("local", backups, "", "", ""));
            await fixture.Scope.Restore.RestoreVaultAsync("local", backups, "", "", "", latest.BackupName, destination,
                recovery ? words : TestVault.Passphrase, recovery, "Restored vault");
            Assert.False(fixture.Scope.Vault.IsUnlocked);
            Assert.True(File.Exists(first.StoredPath));
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal("Restored vault", fixture.Scope.Vault.VaultName);
            var restored = await fixture.Scope.Catalog.ListAsync();
            Assert.Equal(incremental ? 2 : 1, restored.Count);
            Assert.All(restored, record => Assert.StartsWith(destination, record.StoredPath));
            Assert.Equal(incremental ? "Latest metadata" : "", restored.Single(record => record.Id == first.Id).Description);
            Assert.StartsWith("data:image/png;base64,", await fixture.Scope.Media.ThumbnailAsync(first.Id));
            Assert.StartsWith("data:image/", await fixture.Scope.Media.PreviewAsync(first.Id));
            Assert.Null((await fixture.Scope.Backup.LoadBackupOptionsAsync()).LastBackupName);
        }
        finally
        {
            await fixture.Scope.Vault.CloseVaultAsync();
            DeleteTemporaryDirectory(destination);
            DeleteTemporaryDirectory(backups);
        }
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("missing-parent")]
    [InlineData("media")]
    [InlineData("database")]
    public async Task Restore_RejectsInvalidBackups_WithoutPublishingOrReplacingTheOpenVault(string failure)
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var backups = NewTemporaryDirectory();
        var destination = NewTemporaryDirectory();
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", backups, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            var latest = full;
            if (failure == "missing-parent")
            {
                await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(24, 16)), "new.png", "folder");
                latest = await fixture.Scope.Backup.CreateIncrementalBackupAsync();
                File.Delete(Path.Combine(backups, full.BackupName));
            }

            if (failure is "media" or "database")
            {
                using var archive = ZipFile.Open(Path.Combine(backups, full.BackupName), ZipArchiveMode.Update);
                var entry = failure == "media" ? archive.Entries.First(e => e.FullName.StartsWith("media/")) : archive.GetEntry("gallery.db")!;
                var name = entry.FullName;
                entry.Delete();
                using var stream = archive.CreateEntry(name).Open();
                stream.Write([1, 2, 3]);
            }
            Func<Task> restore = () => fixture.Scope.Restore.RestoreVaultAsync("local", backups, "", "", "",
                latest.BackupName, destination, failure == "credentials" ? "incorrect credentials" : TestVault.Passphrase);
            switch (failure)
            {
                case "credentials":
                    await Assert.ThrowsAsync<AuthenticationTagMismatchException>(restore);
                    break;
                case "missing-parent":
                    await Assert.ThrowsAsync<FileNotFoundException>(restore);
                    break;
                case "media":
                    var corruptMedia = await Assert.ThrowsAsync<CryptographicException>(restore);
                    AsserterExceptionContract.AssertMessageContains(["media", "integrity"], corruptMedia.Message);
                    break;
                case "database":
                    var corruptDatabase = await Assert.ThrowsAsync<SqliteException>(restore);
                    Assert.Equal(26, corruptDatabase.SqliteErrorCode);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }
            Assert.Equal(fixture.Root, fixture.Scope.Vault.VaultRoot);
            Assert.True(fixture.Scope.Vault.IsUnlocked);
            Assert.False(File.Exists(Path.Combine(destination, "vault.json")));
            Assert.False(File.Exists(Path.Combine(destination, "gallery.db")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(destination), path => path.Contains(".vault-stage-"));
        }
        finally
        {
            DeleteTemporaryDirectory(destination);
            DeleteTemporaryDirectory(backups);
        }
    }

    [Fact]
    public async Task Restore_FromAzure_ListsAndDownloadsArchivesUsingTheSpecifiedStorageAccount()
    {
        using var fixture = new TestVault();
        await fixture.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var image = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "azure.png", "folder");
        var backups = NewTemporaryDirectory();
        var destination = NewTemporaryDirectory();
        using var listener = StartLoopbackListener(out var endpoint);
        Task? server = null;
        var requests = new System.Collections.Concurrent.ConcurrentQueue<AzureRequest>();
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", backups, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(backups, full.BackupName));
            server = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception exception) when (!listener.IsListening && exception is HttpListenerException or ObjectDisposedException) { break; }
                    var response = context.Response;
                    try
                    {
                        requests.Enqueue(new AzureRequest(context.Request.HttpMethod, context.Request.Url!.AbsolutePath,
                            context.Request.QueryString.AllKeys.Where(key => key is not null)
                                .ToImmutableDictionary(key => key!, key => context.Request.QueryString[key] ?? "", StringComparer.Ordinal),
                            context.Request.Headers["x-ms-version"]));
                        var listing = context.Request.QueryString["comp"] == "list";
                        var body = listing ? Encoding.UTF8.GetBytes($"""
                            <?xml version="1.0" encoding="utf-8"?>
                            <EnumerationResults ServiceEndpoint="{endpoint}" ContainerName="gallery-backups">
                            <Prefix>nightly/</Prefix><Blobs><Blob><Name>nightly/{full.BackupName}</Name>
                            <Properties><Last-Modified>Mon, 12 Oct 2026 12:00:00 GMT</Last-Modified>
                            <Etag>"test"</Etag><Content-Length>{bytes.Length}</Content-Length>
                            <Content-Type>application/octet-stream</Content-Type><BlobType>BlockBlob</BlobType>
                            </Properties></Blob></Blobs><NextMarker /></EnumerationResults>
                            """) : bytes;
                        if (!listing && Uri.UnescapeDataString(context.Request.Url!.AbsolutePath) != $"/testaccount/gallery-backups/nightly/{full.BackupName}")
                            response.StatusCode = 404;
                        response.ContentType = listing ? "application/xml" : "application/octet-stream";
                        response.ContentLength64 = body.Length;
                        response.Headers["ETag"] = "\"test\"";
                        response.Headers["Last-Modified"] = "Mon, 12 Oct 2026 12:00:00 GMT";
                        response.Headers["x-ms-blob-type"] = "BlockBlob";
                        response.Headers["x-ms-request-id"] = Guid.NewGuid().ToString("N");
                        if (context.Request.HttpMethod != "HEAD") await response.OutputStream.WriteAsync(body);
                    }
                    finally { response.Close(); }
                }
            });
            var connection = $"DefaultEndpointsProtocol=http;AccountName=testaccount;AccountKey={Convert.ToBase64String(new byte[32])};BlobEndpoint={endpoint}testaccount";
            Assert.Contains(full.BackupName, await fixture.Scope.Restore.ListRestoreBackupsAsync("azure", "", "gallery-backups", "nightly", connection));
            var error = await Record.ExceptionAsync(() => fixture.Scope.Restore.RestoreVaultAsync("azure", "", "gallery-backups", "nightly", connection,
                full.BackupName, destination, TestVault.Passphrase));
            var observedRequests = requests.ToArray().ToImmutableArray();
            Assert.True(error is null, $"Expected no restore exception; Actual: {error?.ToString() ?? "<none>"}. Requests: {JsonSerializer.Serialize(observedRequests)}.");
            Assert.True(observedRequests.Any(request => request.Method == "GET"
                && request.Path == "/testaccount/gallery-backups"
                && request.Query.GetValueOrDefault("comp") == "list"
                && request.Query.GetValueOrDefault("prefix") == "nightly/"),
                $"Expected a GET listing for testaccount/gallery-backups with prefix nightly/; Actual: {JsonSerializer.Serialize(observedRequests)}.");
            Assert.True(observedRequests.Any(request => request.Method == "GET"
                && Uri.UnescapeDataString(request.Path) == $"/testaccount/gallery-backups/nightly/{full.BackupName}"
                && !string.IsNullOrEmpty(request.Version)),
                $"Expected a versioned GET download for backup '{full.BackupName}'; Actual: {JsonSerializer.Serialize(observedRequests)}.");
            await fixture.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.Equal(image.Id, Assert.Single(await fixture.Scope.Catalog.ListAsync()).Id);
            Assert.StartsWith("data:image/", await fixture.Scope.Media.PreviewAsync(image.Id));
        }
        finally
        {
            listener.Stop();
            if (server is not null) await server.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Scope.Vault.CloseVaultAsync();
            DeleteTemporaryDirectory(destination);
            DeleteTemporaryDirectory(backups);
        }
    }

    [Fact]
    public async Task Checkout_IsExclusiveAcrossProcesses_AndReleasedOnExit()
    {
        var root = NewTemporaryDirectory();
        Directory.CreateDirectory(root);
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { Path.Combine(AppContext.BaseDirectory, "ProcessMediator", "Gallery.ProcessMediator.dll"), "checkout", root },
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            })!;
            try
            {
                Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.False(process.HasExited);
                Assert.Throws<IOException>(() => { using var conflicting = VaultCheckout.Acquire(root); });
                process.Kill();
                await process.WaitForExitAsync();
                using var released = await WaitForCheckoutReleaseAsync(root);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        finally { DeleteTemporaryDirectory(root); }
    }

    private static string NewTemporaryDirectory() => Path.Combine(Path.GetTempPath(), "gallery-vault-tests-" + Guid.NewGuid().ToString("N"));
    private static HttpListener StartLoopbackListener(out string endpoint)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();

            var listener = new HttpListener();
            var candidate = $"http://127.0.0.1:{port}/";
            try
            {
                listener.Prefixes.Add(candidate);
                listener.Start();
                endpoint = candidate;
                return listener;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }

        throw new InvalidOperationException("Could not bind the Azure test listener.");
    }

    private static async Task<VaultCheckout> WaitForCheckoutReleaseAsync(string root)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return VaultCheckout.Acquire(root); }
            catch (IOException) when (DateTime.UtcNow < timeout) { await Task.Delay(25); }
        }
    }
    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
    private sealed record AzureRequest(string Method, string Path, ImmutableDictionary<string, string> Query, string? Version);

}
