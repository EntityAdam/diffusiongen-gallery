namespace Gallery.Core.Models;

public sealed record IngestionOptions
{
    private const int DefaultWorkerCount = 4;

    internal const string SettingId = "ingestion-options";
    internal const string SettingPurpose = "ingestion-options:v1";

    public int Workers { get; init; } = DefaultWorkerCount;
    public int CatalogBatchSize { get; init; } = 16;

    public static IngestionOptions Hdd => new() { Workers = DefaultWorkerCount, CatalogBatchSize = 16 };
    public static IngestionOptions Ssd => new() { Workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 16), CatalogBatchSize = 16 };
}
