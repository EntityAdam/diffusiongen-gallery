using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

// Dependencies used by the workspace to compose its feature state.
internal sealed record WorkspaceGalleryServices(
    IGalleryVault Vault,
    IGallerySettings Settings,
    IGalleryCatalog Catalog,
    IGalleryCollections Collections,
    IGalleryTags Tags,
    IGalleryMedia Media,
    IGalleryMediaMaintenance MediaMaintenance,
    IGalleryRecordEditor Records,
    IGalleryDuplicates Duplicates,
    IGalleryVision Vision);
