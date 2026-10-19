using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class IngestionOptionsValidator
{
    public static void Validate(IngestionOptions ingestionOptions)
    {
        if (ingestionOptions.Workers is < 1 or > 32)
            throw new InvalidOperationException($"Ingestion workers '{ingestionOptions.Workers}' must be between 1 and 32.");
        if (ingestionOptions.CatalogBatchSize is < 1 or > 64)
            throw new InvalidOperationException($"Catalog batch size '{ingestionOptions.CatalogBatchSize}' must be between 1 and 64.");
    }
}
