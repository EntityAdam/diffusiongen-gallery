namespace Gallery.Core.Models;

public enum ReviewAction { Keep, Favorite, MarkDeletion, Rate }

public sealed record ReviewState([property: System.Text.Json.Serialization.JsonPropertyName("Reviewed")] bool IsReviewed, [property: System.Text.Json.Serialization.JsonPropertyName("Favorite")] bool IsFavorite, [property: System.Text.Json.Serialization.JsonPropertyName("MarkedForDeletion")] bool IsMarkedForDeletion, int Rating);
