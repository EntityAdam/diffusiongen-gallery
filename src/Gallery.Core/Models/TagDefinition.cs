using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record TagDefinition(string Name, int MediaCount);

internal sealed record TagCatalog(ImmutableArray<string> Names)
{
    internal const string SettingId = "tag-catalog";
    internal const string SettingPurpose = "tag-catalog:v1";
}
