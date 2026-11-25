using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

internal interface IGalleryRecordTagCommitter
{
    Task CommitEditedTagsAsync(IGalleryStore galleryStore, ImageRecord originalRecord, ImageRecord editedRecord, byte[] key);
}
