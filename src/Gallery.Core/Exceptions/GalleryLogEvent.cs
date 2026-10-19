namespace Gallery.Core.Exceptions;

internal sealed class GalleryLogEvent(string value) : LogEventBase(value)
{
    internal static readonly GalleryLogEvent AcquireVaultKey = new("Vault.AcquireKey");
    internal static readonly GalleryLogEvent ParseMetadata = new("Metadata.ParseStoredDocument");
    internal static readonly GalleryLogEvent PrepareVideo = new("Video.PrepareImport");
    internal static readonly GalleryLogEvent ReadThumbnail = new("Gallery.ReadThumbnail");
    internal static readonly GalleryLogEvent PrepareRemoval = new("Gallery.PrepareRemoval");
    internal static readonly GalleryLogEvent RemoveImage = new("Gallery.RemoveImage");
}
