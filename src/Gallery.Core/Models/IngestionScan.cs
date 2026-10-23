using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed class IngestionScan
{
    internal IngestionScan(string folder, FolderSearchScope searchScope, string catalogRoot, string mediaRoot,
        ImmutableArray<IngestionScanFile> files)
    {
        Folder = folder;
        SearchScope = searchScope;
        CatalogRoot = catalogRoot;
        MediaRoot = mediaRoot;
        Files = files;
    }

    public string Folder { get; }
    public FolderSearchScope SearchScope { get; }
    public int ImageCount => Files.Count(file => !file.IsVideo);
    public int VideoCount => Files.Count(file => file.IsVideo);
    public long SourceBytes => Files.Sum(file => file.Bytes);
    internal string CatalogRoot { get; }
    internal string MediaRoot { get; }
    internal ImmutableArray<IngestionScanFile> Files { get; }

    internal ImmutableArray<IngestionScanFile> SelectFiles(bool includeImages, bool includeVideos) =>
        Files.Where(file => file.IsVideo ? includeVideos : includeImages).ToImmutableArray();
}

internal sealed record IngestionScanFile(string Path, long Bytes, DateTime LastWriteTimeUtc, bool IsVideo);

public sealed record IngestionSpaceEstimate(
    int FileCount, long SelectedSourceBytes, long EstimatedBytes, long RequiredBytes,
    long MediaRequiredBytes, long MediaAvailableBytes, long CatalogRequiredBytes, long CatalogAvailableBytes)
{
    public bool HasEnoughSpace => FileCount > 0 && MediaRequiredBytes <= MediaAvailableBytes
        && CatalogRequiredBytes <= CatalogAvailableBytes;
}
