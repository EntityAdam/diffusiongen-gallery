namespace Gallery.Core.Features.Ingestion;

internal sealed record IngestionScanFile(string Path, long Bytes, DateTime LastWriteTimeUtc, bool IsVideo);
