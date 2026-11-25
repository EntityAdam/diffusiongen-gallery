namespace Gallery.Core.Models;

public sealed record BackupRunResult(
    string BackupType,
    string BackupName,
    int MediaFilesCopied,
    int TotalMediaFiles,
    long BytesWritten,
    DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("Skipped")] bool WasSkipped = false);
