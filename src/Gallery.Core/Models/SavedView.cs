using Gallery.Core.Exceptions;

namespace Gallery.Core.Models;

public sealed record SavedView(string Name, [property: System.Text.Json.Serialization.JsonPropertyName("Filter")] GalleryFilter GalleryFilter);
