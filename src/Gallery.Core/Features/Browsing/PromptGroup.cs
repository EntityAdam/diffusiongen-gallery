using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record PromptGroup(string Kind, string Prompt, ImmutableArray<ImageRecord> Images)
{
    public const string Shared = "shared", Unique = "unique", None = "none";
}
