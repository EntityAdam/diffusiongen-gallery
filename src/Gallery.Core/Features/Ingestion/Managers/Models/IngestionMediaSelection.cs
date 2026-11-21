namespace Gallery.Core.Features.Ingestion.Managers.Models;

[Flags]
internal enum IngestionMediaSelection
{
    None = 0,
    Images = 1,
    Videos = 2,
    All = Images | Videos
}
