using System.Net;
using System.Text;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativeSasRestoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData("nightly/gallery")]
    public async Task SasRestore_WhenBackupsShareAPrefix_ThenListsAndRestoresTheWholeIncrementalChain(string prefix)
    {
        using var source = new TestVault();
        await source.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var firstBytes = GalleryTests.MakePng(32, 24);
        var secondBytes = GalleryTests.MakePng(40, 30);
        var first = await source.Service.ImportAsync(new MemoryStream(firstBytes), "first.png", "folder");
        var backups = source.Root + "-backups";
        var destination = source.Root + "-restored";
        var clientRoot = source.Root + "-client";
        GalleryService? restored = null;
        try
        {
            await source.Service.SaveBackupOptionsAsync("local", backups, "", "", "");
            var full = await source.Service.CreateFullBackupAsync();
            await source.Service.UpdateAsync(first.Id, edit => edit with { Description = "latest metadata" });
            var second = await source.Service.ImportAsync(new MemoryStream(secondBytes), "second.png", "folder");
            var incremental = await source.Service.CreateIncrementalBackupAsync();
            var archives = new Dictionary<string, byte[]>
            {
                [full.BackupName] = await File.ReadAllBytesAsync(Path.Combine(backups, full.BackupName)),
                [incremental.BackupName] = await File.ReadAllBytesAsync(Path.Combine(backups, incremental.BackupName))
            };
            var mediator = new TestMediatorAzureBackups(prefix, archives);
            restored = GalleryService.Create(clientRoot,
                new GalleryRuntimeOptions { StateDirectory = clientRoot + "-state" },
                new HttpClient(mediator.CreateHttpMessageHandler()));
            const string sas = "https://account.blob.core.windows.net/backups?sr=c&sp=rl&sig=test-only";
            var enteredPrefix = prefix.Length == 0 ? "" : " /" + prefix + "/ ";

            var names = await restored.ListRestoreBackupsFromSasAsync(sas, enteredPrefix);
            Assert.Equal(archives.Keys.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
            await restored.RestoreGalleryFromSasAsync(sas, incremental.BackupName, destination,
                TestVault.Passphrase, azurePrefix: enteredPrefix);
            await restored.UnlockWithPassphraseAsync(TestVault.Passphrase);

            var records = await restored.ListAsync();
            Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
                records.Select(record => record.Id).Order(StringComparer.Ordinal));
            Assert.Equal("latest metadata", records.Single(record => record.Id == first.Id).Description);
            Assert.Equal(firstBytes, await ReadMediaAsync(restored, first.Id));
            Assert.Equal(secondBytes, await ReadMediaAsync(restored, second.Id));
            Assert.Equal([prefix.Length == 0 ? "" : prefix + "/"], mediator.CapturedListingPrefixes);
            var blobPrefix = "/backups/" + (prefix.Length == 0 ? "" : prefix + "/");
            Assert.Contains(blobPrefix + full.BackupName, mediator.CapturedDownloadPaths);
            Assert.Contains(blobPrefix + incremental.BackupName, mediator.CapturedDownloadPaths);
            Assert.All(mediator.CapturedDownloadPaths, path => Assert.StartsWith(blobPrefix, path));
        }
        finally
        {
            if (restored is not null)
            {
                await restored.CloseVaultAsync();
                restored.Dispose();
            }
            foreach (var directory in new[] { backups, destination, clientRoot, clientRoot + "-state" })
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../nightly")]
    [InlineData("nightly/./gallery")]
    public async Task SasRestore_WhenPrefixHasTraversalSegments_ThenRejectsBeforeNetworkAccess(string prefix)
    {
        using var source = new TestVault();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.Service.ListRestoreBackupsFromSasAsync(
                "https://account.blob.core.windows.net/backups?sr=c&sp=rl&sig=test-only", prefix));
    }

    private static async Task<byte[]> ReadMediaAsync(GalleryService service, string id)
    {
        var media = await service.OpenMediaAsync(id);
        await using var stream = media.Content;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private sealed class TestMediatorAzureBackups(string prefix, IReadOnlyDictionary<string, byte[]> archives)
    {
        private readonly string prefix = prefix;
        private readonly IReadOnlyDictionary<string, byte[]> archives = archives;
        private readonly List<string> listingPrefixes = [];
        private readonly List<string> downloadPaths = [];
        internal IReadOnlyList<string> CapturedListingPrefixes => listingPrefixes;
        internal IReadOnlyList<string> CapturedDownloadPaths => downloadPaths;
        internal HttpMessageHandler CreateHttpMessageHandler() => new SpyingHandler(this);

        private sealed class SpyingHandler(TestMediatorAzureBackups mediator) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = request.RequestUri!;
                byte[] body;
                var listing = uri.AbsolutePath == "/backups" && uri.Query.Contains("comp=list", StringComparison.Ordinal);
                if (listing)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    mediator.listingPrefixes.Add(query["prefix"] ?? "");
                    var blobPrefix = mediator.prefix.Length == 0 ? "" : mediator.prefix + "/";
                    var blobs = string.Join("", mediator.archives.Select(archive => $"""
                        <Blob><Name>{blobPrefix}{archive.Key}</Name><Properties>
                        <Last-Modified>Mon, 12 Oct 2026 12:00:00 GMT</Last-Modified><Etag>"test"</Etag>
                        <Content-Length>{archive.Value.Length}</Content-Length><Content-Type>application/octet-stream</Content-Type>
                        <BlobType>BlockBlob</BlobType></Properties></Blob>
                        """));
                    body = Encoding.UTF8.GetBytes($"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <EnumerationResults ServiceEndpoint="https://account.blob.core.windows.net/" ContainerName="backups">
                        <Prefix>{blobPrefix}</Prefix><Blobs>{blobs}</Blobs><NextMarker /></EnumerationResults>
                        """);
                }
                else
                {
                    var blobPrefix = "/backups/" + (mediator.prefix.Length == 0 ? "" : mediator.prefix + "/");
                    var path = Uri.UnescapeDataString(uri.AbsolutePath);
                    mediator.downloadPaths.Add(path);
                    if (!path.StartsWith(blobPrefix, StringComparison.Ordinal)
                        || !mediator.archives.TryGetValue(path[blobPrefix.Length..], out body!))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body),
                    RequestMessage = request
                };
                response.Content.Headers.ContentLength = body.Length;
                response.Content.Headers.ContentType = new(listing ? "application/xml" : "application/octet-stream");
                response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
                response.Headers.ETag = new("\"test\"");
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
                response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString("N"));
                return Task.FromResult(response);
            }
        }
    }
}
