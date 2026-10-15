using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record PromptGroup(string Kind, string Prompt, ImmutableArray<ImageRecord> Images)
{
    public const string Shared = "shared", Unique = "unique", None = "none";
}

public sealed record DuplicateGroup(
    string KeepId,
    ImmutableArray<string> ImageIds,
    int MaxDistance,
    bool Exact,
    long ReclaimableBytes);

public sealed record DuplicateScan(ImmutableArray<DuplicateGroup> Groups, int ScannedCount, DateTimeOffset ScannedAt)
{
    public int DuplicateCount => Groups.Sum(group => group.ImageIds.Length - 1);
    public long ReclaimableBytes => Groups.Sum(group => group.ReclaimableBytes);
}
