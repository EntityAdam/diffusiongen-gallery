using Gallery.Web.Exceptions;
using System.Security.Cryptography;
using Gallery.Web.Models;
using Gallery.Core.Processors;
using Gallery.Web.Services.Validators;

namespace Gallery.Web.Services.Processors;

internal static class GalleryRecordUpdater
{
    public static WebImageRecord Apply(WebImageRecord original, Func<WebImageRecordEdit, WebImageRecordEdit> update)
    {
        var edit = update(new WebImageRecordEdit(original));
        ImageRecordEditValidator.Validate(edit, original.Id);
        return LibraryRules.ApplyEdit(original.ToDomain(), edit.ToDomain()).ToWeb();
    }

    public static async Task<List<WebImageRecord>> UpdateBatchAsync(
        IReadOnlyList<string> ids, Func<string, Task<WebImageRecord>> update)
    {
        var records = new List<WebImageRecord>(ids.Count);
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try { records.Add(await update(id)); }
            catch (Exception exception) when (exception is GalleryException or InvalidOperationException or CryptographicException or IOException)
            {
                failures.Add(new InvalidOperationException($"Could not update gallery record '{id}'.", exception));
            }
        }
        ThrowFailures(failures);
        return records;
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 0) return;
        var summary = string.Join(", ", failures.Select(exception => exception.Message));
        throw new AggregateException($"Failed to update {failures.Count} gallery record(s): {summary}", failures);
    }
}
