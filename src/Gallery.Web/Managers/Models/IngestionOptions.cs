namespace Gallery.Web.Models;

public sealed record IngestionOptions
{
    public int Workers { get; init; } = 4;
    public int CatalogBatchSize { get; init; } = 16;

    public static IngestionOptions Hdd => new() { Workers = 4, CatalogBatchSize = 16 };
    public static IngestionOptions Ssd => new() { Workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 16), CatalogBatchSize = 16 };

    public void Validate()
    {
        if (Workers is < 1 or > 32)
            throw new InvalidOperationException("Choose between 1 and 32 ingestion workers.");
        if (CatalogBatchSize is < 1 or > 64)
            throw new InvalidOperationException("Choose a catalog batch size between 1 and 64.");
    }
}

public sealed record MediaStorageOptions
{
    /// <summary>Empty uses the local gallery directory; existing files are never moved by this setting.</summary>
    public string MediaDirectory { get; init; } = "";
}
