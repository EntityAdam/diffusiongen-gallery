using System.Collections.Immutable;

namespace Gallery.Core.Models;

// The model document stays JSON-backed. These are application-owned, safely extracted facts.
public sealed record VisionEntity(string Id, string Kind, string Description, ImmutableArray<VisionAttribute> Attributes);
public sealed record VisionAttribute(string Name, string Value);
public sealed record VisionRelation(string SubjectId, string Predicate, string ObjectId);
public sealed record VisionFacts
{
    public string RunId { get; init; } = "";
    public string SourceContentHash { get; init; } = "";
    public ImmutableArray<VisionEntity> Entities { get; init; } = [];
    public ImmutableArray<VisionRelation> Relationships { get; init; } = [];
}

public sealed record VisionAnalysis
{
    public int Version { get; init; } = 1;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string Json { get; init; } = "";
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string ImageInput { get; init; } = "fit-preview";
    public string EffectiveSystemPrompt { get; init; } = "";
    public int MaxTokens { get; init; }
    public ImmutableArray<string> ExistingTagCandidates { get; init; } = [];
    public int RequestTimeoutSeconds { get; init; }
    public double Temperature { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string SourceContentHash { get; init; } = "";
    public string SourceDescription { get; init; } = "";
    public string PreviousRunId { get; init; } = "";
    public bool UseExistingTags { get; init; } = true;
    public bool SuggestNewTags { get; init; } = true;
    public ImmutableArray<string> Warnings { get; init; } = [];
    public VisionFacts Facts { get; init; } = new();
}

public sealed record VisionAnalysisResult(string Description, string Tags, string Collection = "", string Warning = "", VisionAnalysis? Analysis = null);

// Positive, entity-bound search. All object conditions apply to the SAME object.
public sealed record VisionQuery
{
    public string Subject { get; init; } = "";
    public string Relationship { get; init; } = "";
    public string Object { get; init; } = "";
    public string Attribute { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsEmpty => Subject.Length + Relationship.Length + Object.Length + Attribute.Length + Value.Length == 0;
}
