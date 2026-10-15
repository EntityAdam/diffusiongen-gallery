using System.Collections.Immutable;

namespace Gallery.Web.Models;

public sealed record WebImageRecordEdit
{
    internal WebImageRecordEdit(WebImageRecord imageRecord)
    {
        Id = imageRecord.Id;
        Name = imageRecord.Name;
        StoredPath = imageRecord.StoredPath;
        Description = imageRecord.Description;
        Tags = imageRecord.Tags;
        AnalysisModel = imageRecord.AnalysisModel;
        Favorite = imageRecord.Favorite;
        MarkedForDeletion = imageRecord.MarkedForDeletion;
        Collections = imageRecord.Collections;
        Rating = imageRecord.Rating;
        Reviewed = imageRecord.Reviewed;
        Generation = imageRecord.Generation;
        MetadataVersion = imageRecord.MetadataVersion;
    }

    public string Id { get; }
    public string Name { get; init; }
    public string StoredPath { get; init; }
    public string Description { get; init; }
    public string Tags { get; init; }
    public string AnalysisModel { get; init; }
    public bool Favorite { get; init; }
    public bool MarkedForDeletion { get; init; }
    public ImmutableArray<string> Collections { get; init; }
    public int Rating { get; init; }
    public bool Reviewed { get; init; }
    public WebGenerationInfo? Generation { get; init; }
    public int MetadataVersion { get; init; }

    internal WebImageRecord ToRecord(WebImageRecord imageRecord) => imageRecord with
    {
        Name = Name,
        StoredPath = StoredPath,
        Description = Description,
        Tags = Tags,
        AnalysisModel = AnalysisModel,
        Favorite = Favorite,
        MarkedForDeletion = MarkedForDeletion,
        Collections = Collections.ToImmutableArray(),
        Rating = Rating,
        Reviewed = Reviewed,
        Generation = Generation,
        MetadataVersion = MetadataVersion
    };
}
