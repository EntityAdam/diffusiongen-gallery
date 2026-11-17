using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

public sealed class MediaCurationCommands(GalleryService gallery, Func<Task> reload)
{
    public Task<ImageRecord> RateAsync(ImageRecord image, int value) =>
        gallery.UpdateAsync(image.Id, current => gallery.SetCuration(current, current.Rating == value ? 0 : value, null));
    public Task<ImageRecord> FavoriteAsync(ImageRecord image) =>
        gallery.UpdateAsync(image.Id, current => current with { IsFavorite = !current.IsFavorite });
    public Task<ImageRecord> MarkAsync(ImageRecord image) =>
        gallery.UpdateAsync(image.Id, current => gallery.SetDeletionMark(current, !current.IsMarkedForDeletion));
    public Task<IReadOnlyList<ImageRecord>> MarkManyAsync(IReadOnlyCollection<string> ids, bool value) =>
        UpdateManyAsync(ids, current => gallery.SetDeletionMark(current, value));

    public async Task<IReadOnlyList<ImageRecord>> UpdateManyAsync(
        IReadOnlyCollection<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update)
    {
        if (ids.Count == 0) return [];
        try { return await gallery.UpdateManyAsync(ids, update); }
        catch (AggregateException)
        {
            // Some edits may already be committed. Recover the displayed snapshot
            // without replaying the edit against those records.
            await reload();
            throw;
        }
    }
}
