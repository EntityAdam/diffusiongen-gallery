using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Validators;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public const long MaxTagCsvBytes = 10 * 1024 * 1024;

    public async Task<(int Added, int Skipped)> ImportTagsCsvAsync(Stream input)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var content = await ImportContentValidator.ReadAsync(input, MaxTagCsvBytes, "Tag CSV files must be 10 MiB or smaller.");
        string[] imported;
        try
        {
            content.Position = 0;
            imported = TagCsvProcessor.Read(content).Select((name, index) =>
            {
                try { return ValidateTagName(name); }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException($"CSV tag {index + 1}: {exception.Message}", exception);
                }
            }).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(content.GetBuffer()); }
        generation.RevocationToken.ThrowIfCancellationRequested();
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await ReadTagCatalogWithKeyAsync(key.Bytes, records, store);
            var names = NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagsOf)));
            var next = NormalizeTagNames(names.Concat(imported));
            var added = next.Length - names.Length;
            generation.RevocationToken.ThrowIfCancellationRequested();
            if (!next.SequenceEqual(catalog.Names)) await store.CommitTagCatalogAsync([], new(next), null, key.Bytes);
            return (added, imported.Length - added);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ExportFile> ExportTagsCsvAsync()
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var records = await store.ReadRecordsAsync(key.Bytes);
            var catalog = await ReadTagCatalogWithKeyAsync(key.Bytes, records, store);
            var names = NormalizeTagNames(catalog.Names.Concat(records.SelectMany(TagsOf)));
            generation.RevocationToken.ThrowIfCancellationRequested();
            var csv = TagCsvProcessor.Write(names);
            try { return new("tags.csv", "text/csv;charset=utf-8", csv); }
            finally { CryptographicOperations.ZeroMemory(csv); }
        }
        finally { store.Gate.Release(); }
    }
}
