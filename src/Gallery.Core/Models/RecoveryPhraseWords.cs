using System.Collections.Immutable;

namespace Gallery.Core.Models;

internal static class RecoveryPhraseWords
{
    public const int Count = 32;
    public static readonly ImmutableArray<string> Prefixes =
        ["amber", "aqua", "azure", "birch", "coral", "dusk", "ember", "fern", "frost", "gold", "jade", "lunar", "mist", "moss", "oak", "pearl"];
    public static readonly ImmutableArray<string> Suffixes =
        ["bay", "bird", "bloom", "brook", "cloud", "dune", "field", "flame", "glen", "hill", "lake", "leaf", "moon", "reed", "star", "wave"];
}
