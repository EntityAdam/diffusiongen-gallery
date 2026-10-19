namespace Gallery.Core.Models;

public sealed record IngestionOptions
{
    public int Workers { get; init; } = 4;
    public int CatalogBatchSize { get; init; } = 16;

    public static IngestionOptions Hdd => new() { Workers = 4, CatalogBatchSize = 16 };
    public static IngestionOptions Ssd => new() { Workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 16), CatalogBatchSize = 16 };


}

public sealed record MediaStorageOptions
{
    /// <summary>Empty uses the local gallery directory; existing files are never moved by this setting.</summary>
    public string MediaDirectory { get; init; } = "";
}
