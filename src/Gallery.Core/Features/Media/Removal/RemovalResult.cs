namespace Gallery.Core.Models;

public sealed record RemovalFailure(string ImageId, string Message, [property: System.Text.Json.Serialization.JsonPropertyName("Cause")] Exception Exception);
public sealed record RemovalResult(string Id, [property: System.Text.Json.Serialization.JsonPropertyName("Removed")] bool WasRemoved, string? RestoredPath, [property: System.Text.Json.Serialization.JsonPropertyName("Failure")] RemovalFailure? RemovalFailure)
{
    public string? Error => RemovalFailure?.Message;
}
