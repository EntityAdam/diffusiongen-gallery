using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

internal static class BrowsingWorkspaceFixture
{
    internal static List<ImageRecord> CreateImages(int count) =>
        Enumerable.Range(0, count).Select(index => new ImageRecord
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Name = index.ToString("D2")
        }).ToList();

    internal static Task<WorkspaceSession> OpenSessionAsync(TestVault testVault, List<ImageRecord> imageRecords) =>
        OpenSessionAsync(testVault, new BrowsingCatalogSource(testVault.Scope.Catalog, imageRecords));

    internal static async Task<WorkspaceSession> OpenSessionAsync(TestVault testVault, BrowsingCatalogSource catalog)
    {
        if (!testVault.Scope.Vault.IsUnlocked) await testVault.InitializeAsync();
        var services = GalleryCapabilityTestSetup.Workspace(testVault.Scope) with
        {
            Catalog = catalog,
            Media = new ThumbnailMedia()
        };
        var workspace = new WorkspaceSession(services, new(), new(services.Collections),
            new ComponentRenderFixture.NoOpJsRuntime(), NullLogger.Instance, action => action(), () => { });
        try
        {
            await workspace.Catalog.OpenAsync(workspace.Browser.PageSize, trash: false);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    internal sealed class BrowsingCatalogSource(IGalleryCatalog projections, List<ImageRecord> records, Task? releaseFullLoad = null)
        : IGalleryCatalog
    {
        internal List<ImageRecord> Records { get; set; } = records;

        public Task<GalleryCatalog> LoadInitialPageAsync(int pageSize, bool markedOnly, CancellationToken cancellationToken)
        {
            var available = markedOnly ? Records.Where(record => record.IsMarkedForDeletion).ToList() : Records;
            var complete = releaseFullLoad is null || available.Count <= pageSize;
            var page = complete ? available : available.Take(pageSize).ToList();
            return Task.FromResult(new GalleryCatalog(page, 0, available.Count, complete, IsCurrent: true, CacheHit: false));
        }

        public async Task<GalleryCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            if (releaseFullLoad is not null) await releaseFullLoad.WaitAsync(cancellationToken);
            return new GalleryCatalog(Records, 0, Records.Count, IsComplete: true, IsCurrent: true, CacheHit: false);
        }

        public Task<List<ImageRecord>> ReloadRecordsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
        {
            var selectedIds = ids.ToHashSet();
            return Task.FromResult(Records.Where(record => selectedIds.Contains(record.Id)).ToList());
        }

        public Task<long> CountRecordsAsync(CancellationToken cancellationToken) => Task.FromResult((long)Records.Count);
        public Task<List<ImageRecord>> ListAsync() => Task.FromResult(Records);
        public Task<List<ImageRecord>> LoadAsync() => ListAsync();

        public List<ImageRecord> Filter(IEnumerable<ImageRecord> records, GalleryFilter filter, IReadOnlySet<string>? awaitingApprovalIds) =>
            projections.Filter(records, filter, awaitingApprovalIds);

        public List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> records) => projections.GroupByPrompt(records);
        public List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> records) => projections.GroupByDiffusionModel(records);
    }

    internal sealed class ThumbnailMedia : IGalleryMedia
    {
        public Task<string> ThumbnailAsync(string id) => Task.FromResult("");
        public Task<Stream> OpenThumbnailAsync(string id) => Task.FromResult<Stream>(new MemoryStream());
        public Task<string> PreviewAsync(string id, ImageResolution resolution) => throw new NotSupportedException();
        public Task<string> ViewerImageAsync(string id, ImageResolution resolution) => throw new NotSupportedException();
        public Task<(Stream Content, string ContentType)> OpenMediaAsync(string id) => throw new NotSupportedException();
        public Task<ExportFile> ExportAsync(string id) => throw new NotSupportedException();
        public Task<ExportFile> ExportZipAsync(IEnumerable<string> ids) => throw new NotSupportedException();
        public Task<ExportFile?> WorkflowAsync(string id) => throw new NotSupportedException();
    }
}
