using Gallery.Core.Exceptions;
using System.Security.Cryptography;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services.Processors;

internal static class GalleryRecordUpdater
{
    public static async Task<List<ImageRecord>> UpdateBatchAsync(
        IReadOnlyList<string> ids, Func<string, Task<ImageRecord>> update)
    {
        List<ImageRecord> records;
        List<Exception> failures;
        (records, failures) = await UpdateRecordsAsync(ids, update);
        ThrowFailures(failures);
        return records;
    }

    private static async Task<(List<ImageRecord> Records, List<Exception> Failures)> UpdateRecordsAsync(
        IReadOnlyList<string> ids, Func<string, Task<ImageRecord>> update)
    {
        var records = new List<ImageRecord>(ids.Count);
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try { records.Add(await update(id)); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or CryptographicException or IOException)
            {
                failures.Add(new InvalidOperationException($"Could not update gallery record '{id}'.", exception));
            }
        }
        return (records, failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 0) return;
        var summary = string.Join(", ", failures.Select(exception => exception.Message));
        throw new AggregateException($"Failed to update {failures.Count} gallery record(s): {summary}", failures);
    }
}
