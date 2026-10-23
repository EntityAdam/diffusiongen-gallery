using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record PromptGroup(string Kind, string Prompt, ImmutableArray<ImageRecord> Images)
{
    public const string Shared = "shared", Unique = "unique", None = "none";
}

public sealed record DiffusionModelGroup(string Model, ImmutableArray<ImageRecord> Images);

public sealed record DuplicateGroup(
    string KeepId,
    [property: System.Text.Json.Serialization.JsonPropertyName("Ids")] ImmutableArray<string> ImageIds,
    int MaxDistance,
    [property: System.Text.Json.Serialization.JsonPropertyName("Exact")] bool IsExact,
    long ReclaimableBytes);

public sealed record DuplicateScan(ImmutableArray<DuplicateGroup> Groups, int ScannedCount, DateTimeOffset ScannedAt)
{
    public int DuplicateCount => Groups.Sum(duplicateGroup => duplicateGroup.ImageIds.Length - 1);
    public long ReclaimableBytes => Groups.Sum(duplicateGroup => duplicateGroup.ReclaimableBytes);
}
