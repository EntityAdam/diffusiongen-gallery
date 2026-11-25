using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Ingestion.Processors;

internal sealed record PreparedImport(
    ImageRecord Record, ImmutableArray<byte> EncryptedMedia, ImmutableArray<byte> EncryptedThumbnail);
