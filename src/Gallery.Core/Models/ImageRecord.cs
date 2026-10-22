using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record ImageRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string StoredPath { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public DateTimeOffset? SourceCreatedAt { get; init; }
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Width { get; init; }
    public int Height { get; init; }
    public long OriginalBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string PngSha256 { get; init; } = "";
    public string Metadata { get; init; } = "";
    public string Description { get; init; } = "";
    public string UserDescription { get; init; } = "";
    public string Tags { get; init; } = "";
    public string AnalysisModel { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("Favorite")]
    public bool IsFavorite { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("MarkedForDeletion")]
    public bool IsMarkedForDeletion { get; init; }
    public ImmutableArray<string> Collections { get; init; } = [];
    public int Rating { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("Reviewed")]
    public bool IsReviewed { get; init; }
    public string MediaType { get; init; } = "image";
    public string ContentType { get; init; } = "image/png";
    public double DurationSeconds { get; init; }
    public GenerationInfo? Generation { get; init; }
    public int MetadataVersion { get; init; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsVideo => MediaType == "video";
    [System.Text.Json.Serialization.JsonIgnore] public string Prompt => Generation?.Prompt ?? "";
}

public sealed record GenerationInfo
{
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public double? Cfg { get; init; }
    public int? Steps { get; init; }
    public string Model { get; init; } = "";
    public string ModelKind { get; init; } = "";
    public string Sampler { get; init; } = "";
    public string Scheduler { get; init; } = "";
    public bool HasWorkflow { get; init; }
    public bool HasApiPrompt { get; init; }
}
