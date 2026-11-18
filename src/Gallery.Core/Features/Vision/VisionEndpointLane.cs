namespace Gallery.Core.Models;

public sealed record VisionEndpointLane
{
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = VisionSettings.DefaultEndpoint;
    public string Model { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public int RequestTimeoutSeconds { get; init; } = VisionSettings.DefaultRequestTimeoutSeconds;
    public int MaxConcurrentRequests { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool AutoAccept { get; init; }
}
