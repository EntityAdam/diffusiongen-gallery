using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Ports.Models;

internal sealed record ImportCatalogEntry(ImageRecord Record, ImmutableArray<byte> EncryptedThumbnail)
{
    public ImportCatalogEntry(ImageRecord imageRecord, byte[] encryptedThumbnail)
        : this(imageRecord,  ImmutableArray.CreateRange(encryptedThumbnail)) { }
}

internal sealed record ImportCommitResult(ImmutableArray<string> ImportedIds, ImmutableArray<string> DuplicateIds);

internal sealed record CatalogImportSource(string Id, string SourcePath, string Sha256, long OriginalBytes);
