using Gallery.Core.Models;

namespace Gallery.Core.Features.Ingestion;

internal sealed record PreparedImport(ImageRecord Record, byte[] EncryptedMedia, byte[] EncryptedThumbnail);
