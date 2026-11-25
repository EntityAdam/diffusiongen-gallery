using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Manages tag definitions and CSV transfer.</summary>
public interface IGalleryTags
{
    public const long MaxCsvBytes = 10 * 1024 * 1024;

    Task<IReadOnlyList<TagDefinition>> LoadTagCatalogAsync();
    Task<IReadOnlyList<string>> LoadTagNamesAsync();
    Task AddTagAsync(string name);
    Task RenameTagAsync(string name, string replacement);
    Task DeleteTagAsync(string name);
    Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input);
    /// <summary>Exports tags as CSV, rejecting output larger than <see cref="MaxCsvBytes"/>.</summary>
    Task<ExportFile> ExportTagsCsvAsync();
}
