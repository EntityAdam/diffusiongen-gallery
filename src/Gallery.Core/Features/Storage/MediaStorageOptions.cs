namespace Gallery.Core.Models;

public sealed record MediaStorageOptions
{
    internal const string SettingId = "media-storage";
    internal const string SettingPurpose = "media-storage:v1";

    /// <summary>Empty uses the local gallery directory; existing files are never moved by this setting.</summary>
    public string MediaDirectory { get; init; } = "";
}
