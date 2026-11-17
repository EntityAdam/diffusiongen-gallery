using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;

namespace Gallery.Web.Components.Media;

public sealed class MediaCurationCommands(GalleryService gallery, WorkspaceCatalog catalog, WorkspaceCommands commands)
{
    internal Task ToggleRatingAsync(ImageRecord image, int value) =>
        RunSingleAsync("Saving rating...", image.Id,
            current => gallery.SetCuration(current, current.Rating == value ? 0 : value, null));

    internal Task ToggleFavoriteAsync(ImageRecord image) =>
        RunSingleAsync("Saving favorite...", image.Id, current => current with { IsFavorite = !current.IsFavorite });

    internal Task ToggleMarkAsync(ImageRecord image) =>
        RunSingleAsync("Saving deletion mark...", image.Id,
            current => gallery.SetDeletionMark(current, !current.IsMarkedForDeletion));

    internal Task CurateAsync(IReadOnlyCollection<string> ids, Func<ImageRecordEdit, ImageRecordEdit> update, string message) =>
        RunBulkAsync("Saving...", ids, update, message);

    internal Task RestoreAsync(IReadOnlyCollection<string> ids) =>
        RunBulkAsync("Updating deletion marks...", ids, current => gallery.SetDeletionMark(current, false), "Deletion marks cleared");

    private Task RunSingleAsync(string status, string id, Func<ImageRecordEdit, ImageRecordEdit> update) =>
        commands.RunAsync(status, async () =>
        {
            var committed = await gallery.UpdateAsync(id, update);
            await catalog.UpdateAsync([committed], reloadThumbnails: true);
        });

    private Task RunBulkAsync(string status, IReadOnlyCollection<string> ids,
        Func<ImageRecordEdit, ImageRecordEdit> update, string message) =>
        commands.RunAsync(status, async () =>
        {
            if (ids.Count == 0) return;
            IReadOnlyList<ImageRecord> committed;
            try { committed = await gallery.UpdateManyAsync(ids, update); }
            catch (AggregateException)
            {
                // Recover any committed changes without replaying their edits.
                await catalog.ReloadAsync();
                throw;
            }
            if (committed.Count == 0 || !catalog.CanRead) return;
            await catalog.UpdateAsync(committed, reloadThumbnails: false);
            if (catalog.CanRead) commands.Activity.Message = message;
        });
}
