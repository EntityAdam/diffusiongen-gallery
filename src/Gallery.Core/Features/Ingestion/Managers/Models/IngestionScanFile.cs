namespace Gallery.Core.Features.Ingestion.Managers.Models;

internal sealed record IngestionScanFile(string Path, long Bytes, DateTime LastWriteTimeUtc, bool IsVideo);
