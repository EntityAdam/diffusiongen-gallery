using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Gallery.Core.Models;

public sealed record ExportFile
{
    private readonly byte[] contentBytes;

    public ExportFile(string fileName, string contentType, ReadOnlySpan<byte> contentBytes)
    {
        FileName = fileName;
        ContentType = contentType;
        this.contentBytes = contentBytes.ToArray();
    }

    public string FileName { get; init; }
    public string ContentType { get; init; }
    [JsonPropertyName("Content")]
    public ImmutableArray<byte> ContentBytes => ImmutableArray.CreateRange(contentBytes);
    internal byte[] CopyContentBytes() => contentBytes.ToArray();
    internal ReadOnlyMemory<byte> OwnedContent => contentBytes;
}
public sealed record RemovalFailure(string ImageId, string Message, [property: System.Text.Json.Serialization.JsonPropertyName("Cause")] Exception Exception);
public sealed record RemovalResult(string Id, [property: System.Text.Json.Serialization.JsonPropertyName("Removed")] bool WasRemoved, string? RestoredPath, [property: System.Text.Json.Serialization.JsonPropertyName("Failure")] RemovalFailure? RemovalFailure)
{
    public string? Error => RemovalFailure?.Message;
}

public sealed record ImportProgress(int? Total, int Completed, int Imported, int Failed, string Status);

public sealed record UiPreferences
{
    public const string DetailDensity = "detail";
    public static readonly ImmutableArray<string> Densities = ["compact", DetailDensity];
    public const int DefaultAutoLockMinutes = 15;
    public string GridDensity { get; init; } = DetailDensity;
    public int AutoLockMinutes { get; init; } = DefaultAutoLockMinutes;
    public static readonly ImmutableArray<string> ToolboxTools = ["views", "filters", "review", "organize", "search", "intelligence", "trash"];
    public bool ToolboxVisible { get; init; } = true;
    // Null coordinates place the toolbox at the top-left of the main content.
    public double? ToolboxX { get; init; }
    public double? ToolboxY { get; init; }
    public string ToolboxTool { get; init; } = "";
}

public sealed record StorageOverview(
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
    [System.Text.Json.Serialization.JsonPropertyName("IndexPending")]
    public bool IsIndexPending { get; init; }
    public bool IsStale { get; init; }
    public long? CatalogRevision { get; init; }
}

public enum DriveQueueState { Idle, Running, Waiting, Paused, Faulted }

/// <summary>Progress of queued drive operations (file deletes, index scans and recovery) played in the background.</summary>
public sealed record DriveQueueStatus(DriveQueueState State, int Pending, int Failed, int Completed, string Activity)
{
    public static readonly DriveQueueStatus Empty = new(DriveQueueState.Idle, 0, 0, 0, "");
    public bool HasWork => Pending > 0 || Failed > 0 || State is DriveQueueState.Running or DriveQueueState.Waiting;
}

public sealed record BackupOptions(
    string DestinationType,
    string LocalDirectory,
    string AzureContainer,
    string AzurePrefix,
    bool HasAzureCredentials,
    string? LastBackupName,
    string? LastBackupType);

public sealed record BackupRunResult(
    string BackupType,
    string BackupName,
    int MediaFilesCopied,
    int TotalMediaFiles,
    long BytesWritten,
    DateTimeOffset CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("Skipped")] bool WasSkipped = false);

public sealed record VisionSettings
{
    public const int MinRequestTimeoutSeconds = 10;
    public const int MaxRequestTimeoutSeconds = 3600;
    public string Endpoint { get; init; } = "http://127.0.0.1:1234/v1/";
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public int RequestTimeoutSeconds { get; init; } = 180;
}
