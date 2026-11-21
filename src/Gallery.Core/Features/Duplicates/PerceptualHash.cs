using System.Text.Json.Serialization;

namespace Gallery.Core.Features.Duplicates;

internal sealed record PerceptualHash([property: JsonPropertyName("PngSha256")] string ContentSha256, ulong Hash);
