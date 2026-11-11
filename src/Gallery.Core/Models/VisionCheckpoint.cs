using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record VisionDraft(string Id, string Name, string Description, string Tags, string Collection,
    string Model, string Warning = "", bool TagsOnly = false, VisionAnalysis? Analysis = null, bool UseExistingTags = true, bool SuggestNewTags = true);

// A completed item is removed from the queue in the same encrypted write that stages its draft.
// Failed items remain available for an explicit retry. In-flight items stay pending until checkpointed.
public sealed record VisionQueueItem(string Id, bool TagsOnly = false, bool IncludeCollection = false, bool Failed = false);

public sealed record VisionCheckpoint
{
    public ImmutableArray<VisionDraft> Drafts { get; init; } = [];
    public ImmutableArray<VisionQueueItem> Queue { get; init; } = [];
}
