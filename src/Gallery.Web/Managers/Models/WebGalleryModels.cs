using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Gallery.Web.Models;

public sealed record WebImageRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string StoredPath { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Width { get; init; }
    public int Height { get; init; }
    public long OriginalBytes { get; init; }
    public string Sha256 { get; init; } = "";
    /// <summary>SHA-256 of the stored plaintext bytes. Equals <see cref="Sha256"/> for originals stored unchanged;
    /// older image records hold the hash of the PNG they were converted to.</summary>
    public string PngSha256 { get; init; } = "";
    public string Metadata { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tags { get; init; } = "";
    public string AnalysisModel { get; init; } = "";
    public bool Favorite { get; init; }
    public bool MarkedForDeletion { get; init; }
    public ImmutableArray<string> Collections { get; init; } = [];
    public int Rating { get; init; }
    public bool Reviewed { get; init; }
    /// <summary>"image" (original PNG/JPEG/WebP bytes stored encrypted as .dpng) or "video" (original MP4 stored encrypted).</summary>
    public string MediaType { get; init; } = "image";
    /// <summary>Format of the stored plaintext; defaults to PNG for older records, which were converted on import.</summary>
    public string ContentType { get; init; } = "image/png";
    public double DurationSeconds { get; init; }
    public WebGenerationInfo? Generation { get; init; }
    /// <summary>Version of the generation-metadata parser applied to this record; older records are re-parsed once.</summary>
    public int MetadataVersion { get; init; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsVideo => MediaType == "video";
    [System.Text.Json.Serialization.JsonIgnore] public string Prompt => Generation?.Prompt ?? "";

}

/// <summary>Generation parameters extracted from an embedded ComfyUI API prompt.</summary>
public sealed record WebGenerationInfo
{
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public double? Cfg { get; init; }
    public int? Steps { get; init; }
    public string Model { get; init; } = "";
    /// <summary>Checkpoint, Diffusion model or Model, describing which loader supplied <see cref="Model"/>.</summary>
    public string ModelKind { get; init; } = "";
    public bool HasWorkflow { get; init; }
    public bool HasApiPrompt { get; init; }
}

public sealed record WebExportFile
{
    private readonly byte[] content;

    public WebExportFile(string fileName, string contentType, ReadOnlySpan<byte> contentBytes)
    {
        FileName = fileName;
        ContentType = contentType;
        content = contentBytes.ToArray();
    }

    public string FileName { get; init; }
    public string ContentType { get; init; }
    [JsonPropertyName("Content")]
    public ImmutableArray<byte> ContentBytes => ImmutableArray.CreateRange(content);
    internal byte[] CopyContentBytes() => content.ToArray();
    internal ReadOnlyMemory<byte> OwnedContent => content;
}

public sealed record WebPromptGroup(string Kind, string Prompt, ImmutableArray<WebImageRecord> Images)
{
    public const string Shared = "shared", Unique = "unique", None = "none";
}

public enum WebExistingOriginalAction { Retain, Delete }
public enum WebMissingOriginalAction { RestorePng, Discard }
public sealed record WebRemovalItem(string Id, string Name, string OriginalPath, bool OriginalExists);
public sealed record WebRemovalFailure(string ImageId, string Message, Exception Cause);
public sealed record WebRemovalResult(string Id, bool Removed, string? RestoredPath, WebRemovalFailure? Failure)
{
    public string? Error => Failure?.Message;
}

public sealed record WebImportProgress(int? Total, int Completed, int Imported, int Failed, string Status);

public sealed record WebUiPreferences
{
    public const string ComfortableDensity = "comfortable";
    public static readonly ImmutableArray<string> Densities = ["compact", ComfortableDensity, "large"];
    public const int DefaultAutoLockMinutes = 15;
    public string GridDensity { get; init; } = ComfortableDensity;
    public int AutoLockMinutes { get; init; } = DefaultAutoLockMinutes;
}

public sealed record WebDuplicateGroup(
    string KeepId,
    [property: JsonPropertyName("Ids")] ImmutableArray<string> ImageIds,
    int MaxDistance,
    bool Exact,
    long ReclaimableBytes);

public sealed record WebDuplicateScan(ImmutableArray<WebDuplicateGroup> Groups, int ScannedCount, DateTimeOffset ScannedAt)
{
    public int DuplicateCount => Groups.Sum(group => group.ImageIds.Length - 1);
    public long ReclaimableBytes => Groups.Sum(group => group.ReclaimableBytes);
}

public sealed record WebStorageOverview(
    long DatabaseBytes,
    int ImageFileCount,
    long ImageBytes,
    int VaultFileCount,
    long VaultFileBytes,
    int MissingImageFiles,
    DateTimeOffset UpdatedAt)
{
    public int TotalFileCount => ImageFileCount + VaultFileCount;
    public long TotalBytes => ImageBytes + VaultFileBytes;
    /// <summary>True while the background file index is still being built; unindexed files were measured directly.</summary>
    public bool IndexPending { get; init; }
    public bool IsStale { get; init; }
    public long? CatalogRevision { get; init; }
}

public enum WebDriveQueueState { Idle, Running, Waiting, Paused, Faulted }

/// <summary>Progress of queued drive operations (file deletes, index scans and recovery) played in the background.</summary>
public sealed record WebDriveQueueStatus(WebDriveQueueState State, int Pending, int Failed, int Completed, string Activity)
{
    public static readonly WebDriveQueueStatus Empty = new(WebDriveQueueState.Idle, 0, 0, 0, "");
    public bool HasWork => Pending > 0 || Failed > 0 || State is WebDriveQueueState.Running or WebDriveQueueState.Waiting;
}

public sealed record WebBackupOptions(
    string DestinationType,
    string LocalDirectory,
    string AzureContainer,
    string AzurePrefix,
    bool HasAzureCredentials,
    string? LastBackupName,
    string? LastBackupType);

public sealed record WebBackupRunResult(
    string BackupType,
    string BackupName,
    int MediaFilesCopied,
    int TotalMediaFiles,
    long BytesWritten,
    DateTimeOffset CreatedAt,
    bool Skipped = false);

public sealed record WebGalleryFilter
{
    public const string AnyValue = "all";
    public string Search { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Model { get; init; } = "";
    public bool FavoritesOnly { get; init; }
    public string Deletion { get; init; } = "active";
    public string Analysis { get; init; } = AnyValue;
    public string Orientation { get; init; } = AnyValue;
    public int MinWidth { get; init; }
    public int MinHeight { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string Sort { get; init; } = "newest";
    public string Collection { get; init; } = "";
    public int MinRating { get; init; }
    public string Review { get; init; } = AnyValue;
    public bool UnorganizedOnly { get; init; }
    public string Media { get; init; } = AnyValue;
    /// <summary>Exact positive prompt match; empty means any prompt.</summary>
    public string Prompt { get; init; } = "";
}

public sealed record WebVisionSettings
{
    public const int MinRequestTimeoutSeconds = 10;
    public const int MaxRequestTimeoutSeconds = 3600;
    public string Endpoint { get; init; } = "http://127.0.0.1:1234/v1/";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public int RequestTimeoutSeconds { get; init; } = 180;
}
