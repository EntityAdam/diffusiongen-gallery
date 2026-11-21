namespace Gallery.Core.Models;

public sealed record IngestionOptions
{
    private const int DefaultWorkerCount = 4;
    private const int DefaultCatalogBatchSize = 16;

    internal const string SettingId = "ingestion-options";
    internal const string SettingPurpose = "ingestion-options:v1";

    public int Workers { get; init; } = DefaultWorkerCount;
    public int CatalogBatchSize { get; init; } = DefaultCatalogBatchSize;

    public static IngestionOptions Hdd => new() { Workers = DefaultWorkerCount, CatalogBatchSize = DefaultCatalogBatchSize };
    public static IngestionOptions Ssd => new() { Workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 16), CatalogBatchSize = DefaultCatalogBatchSize };
}
