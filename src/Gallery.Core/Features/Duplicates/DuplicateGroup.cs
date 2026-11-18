using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record DuplicateGroup(
    string KeepId,
    [property: System.Text.Json.Serialization.JsonPropertyName("Ids")] ImmutableArray<string> ImageIds,
    int MaxDistance,
    [property: System.Text.Json.Serialization.JsonPropertyName("Exact")] bool IsExact,
    long ReclaimableBytes);
