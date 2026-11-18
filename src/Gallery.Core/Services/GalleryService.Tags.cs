using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const long MaxTagCsvBytes = TagCatalogManager.MaxTagCsvBytes;

    public Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync() => tagCatalogManager.LoadTagCatalogAsync();
    public Task<IReadOnlyList<string>> LoadTagNamesAsync() => tagCatalogManager.LoadTagNamesAsync();
    public Task AddTagAsync(string name) => tagCatalogManager.AddTagAsync(name);
    public Task RenameTagAsync(string name, string replacement) => tagCatalogManager.RenameTagAsync(name, replacement);
    public Task DeleteTagAsync(string name) => tagCatalogManager.DeleteTagAsync(name);

    public Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input) => tagCatalogManager.ImportTagsCsvAsync(input);
    public Task<ExportFile> ExportTagsCsvAsync() => tagCatalogManager.ExportTagsCsvAsync();
}
