using Gallery.Web.Exceptions;
using System.Security.Cryptography;
using Gallery.Web.Models;
using Gallery.Core.Processors;

namespace Gallery.Web.Services.Processors;

internal static class GalleryRecordUpdater
{
    public static WebImageRecord Apply(WebImageRecord original, Func<WebImageRecordEdit, WebImageRecordEdit> update)
    {
        var edit = update(new WebImageRecordEdit(original));
        return LibraryRules.ApplyEdit(original.ToDomain(), edit.ToDomain()).ToWeb();
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
