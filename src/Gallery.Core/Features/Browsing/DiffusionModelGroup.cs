using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record DiffusionModelGroup(string Model, ImmutableArray<ImageRecord> Images);
