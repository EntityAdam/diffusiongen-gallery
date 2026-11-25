using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Features.Ingestion;

namespace Gallery.Core.Features.Ingestion.Processors;

/// <summary>Records the catalog entry associated with a staged ciphertext file.</summary>
internal sealed record StagedImport(ImportCatalogEntry Entry);

internal static class StagedImportWriter
{
    internal static async Task<StagedImport> WriteAsync(PreparedImport prepared)
    {
        var staged = new StagedImport(new ImportCatalogEntry(prepared.Record, prepared.EncryptedThumbnail));
        var path = staged.Entry.Record.StoredPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (fileStream)
            {
                await fileStream.WriteAsync(prepared.EncryptedMedia.AsMemory());
                fileStream.Flush(flushToDisk: true);
            }
        }
        catch (Exception writeError)
        {
            try { RemoveUncommitted(staged); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Writing the import failed and incomplete ciphertext remains at '{path}'.",
                    new AggregateException(writeError, cleanupError));
            }
            throw;
        }
        return staged;
    }

    internal static void RemoveUncommitted(StagedImport stagedImport)
    {
        File.Delete(stagedImport.Entry.Record.StoredPath);
    }
}
