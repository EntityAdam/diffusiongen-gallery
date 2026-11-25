namespace Gallery.Core.Models;

public sealed record StorageOverview(
    long DatabaseBytes,
    int ImageFileCount,
    long ImageBytes,
    int VaultFileCount,
    long VaultFileBytes,
    int MissingImageFiles,
    DateTimeOffset UpdatedAt)
{
    internal const string SettingId = "storage-overview";
    internal const string SettingPurpose = "storage-overview:v1";

    public int TotalFileCount => ImageFileCount + VaultFileCount;
    public long TotalBytes => ImageBytes + VaultFileBytes;
    /// <summary>True while the background file index is still being built; unindexed files were measured directly.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("IndexPending")]
    public bool IsIndexPending { get; init; }
    public bool IsStale { get; init; }
    public long? CatalogRevision { get; init; }
}
