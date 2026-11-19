using Gallery.Core.Infrastructure.Persistence;

namespace Gallery.Core.Features.Ingestion;

/// <summary>Owns the ciphertext file until the catalog accepts its entry.</summary>
internal sealed class StagedImport
{
    private bool ownsFile = true;

    private StagedImport(ImportCatalogEntry entry) => Entry = entry;

    internal ImportCatalogEntry Entry { get; }

    internal static async Task<StagedImport> WriteAsync(PreparedImport prepared)
    {
        var staged = new StagedImport(new ImportCatalogEntry(prepared.Record, prepared.EncryptedThumbnail));
        var path = staged.Entry.Record.StoredPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (output)
            {
                await output.WriteAsync(prepared.EncryptedMedia);
                output.Flush(flushToDisk: true);
            }
        }
        catch (Exception writeError)
        {
            if (staged.RemoveUncommitted() is { } cleanupError)
                throw new IOException($"Writing the import failed and incomplete ciphertext remains at '{path}'.",
                    new AggregateException(writeError, cleanupError));
            throw;
        }
        return staged;
    }

    internal void MarkCommitted() => ownsFile = false;

    internal Exception? RemoveUncommitted()
    {
        if (!ownsFile) return null;

        // The caller reports deletion failures. Fallback cleanup must not try the same path again.
        ownsFile = false;
        try { File.Delete(Entry.Record.StoredPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
        return null;
    }
}
