using Gallery.Core;
using System.Security.Cryptography;
using Gallery.Web.Models;

namespace Gallery.Web.Services.Processors;

internal static class GalleryRecordUpdater
{
    public static ImageRecord Apply(ImageRecord original, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        var updated = update(new ImageRecordEdit(original)).ToRecord(original);
        if ((updated.Rating > 0 && updated.Rating != original.Rating)
            || (updated.MarkedForDeletion && !original.MarkedForDeletion))
            updated = updated with { Reviewed = true };
        return updated;
    }

    public static async Task UpdateBatchAsync(IReadOnlyList<string> ids, Func<string, Task> update)
    {
        var failures = await AttemptEveryUpdateAsync(ids, update);
        ThrowFailures(failures);
    }

    private static async Task<List<Exception>> AttemptEveryUpdateAsync(IReadOnlyList<string> ids, Func<string, Task> update)
    {
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try { await update(id); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or CryptographicException or IOException)
            {
                failures.Add(new InvalidOperationException($"Could not update gallery record '{id}'.", exception));
            }
        }
        return failures;
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 0) return;
        var summary = string.Join(", ", failures.Select(exception => exception.Message));
        throw new AggregateException($"Failed to update {failures.Count} gallery record(s): {summary}", failures);
    }
}
