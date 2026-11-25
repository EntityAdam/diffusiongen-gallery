using Gallery.Core.Exceptions;

namespace Gallery.Core.Features.Diagnostics.Models;

internal sealed class LogEventGallery(string value) : LogEventBase(value)
{
    internal static readonly LogEventGallery AcquireVaultKey = new("Vault.AcquireKey");
    internal static readonly LogEventGallery ParseMetadata = new("Metadata.ParseStoredDocument");
    internal static readonly LogEventGallery PrepareVideo = new("Video.PrepareImport");
    internal static readonly LogEventGallery ReadThumbnail = new("Gallery.ReadThumbnail");
    internal static readonly LogEventGallery PrepareRemoval = new("Gallery.PrepareRemoval");
    internal static readonly LogEventGallery RemoveImage = new("Gallery.RemoveImage");
    internal static readonly LogEventGallery ValidateSavedViewName = new("Gallery.ValidateSavedViewName");
    internal static readonly LogEventGallery DriveOperationConflict = new("Gallery.DriveOperationConflict");
}
