using System.Collections.Immutable;

namespace Gallery.Core.Models;

public sealed record ImageRecordEdit
{
    public ImageRecordEdit(ImageRecord imageRecord)
    {
        Id = imageRecord.Id;
        Description = imageRecord.Description;
        Tags = imageRecord.Tags;
        AnalysisModel = imageRecord.AnalysisModel;
        Favorite = imageRecord.Favorite;
        MarkedForDeletion = imageRecord.MarkedForDeletion;
        Collections = imageRecord.Collections;
        Rating = imageRecord.Rating;
        Reviewed = imageRecord.Reviewed;
    }

    public string Id { get; }
    public string Description { get; init; }
    public string Tags { get; init; }
    public string AnalysisModel { get; init; }
    public bool Favorite { get; init; }
    public bool MarkedForDeletion { get; init; }
    public ImmutableArray<string> Collections { get; init; }
    public int Rating { get; init; }
    public bool Reviewed { get; init; }

    public ImageRecord ToRecord(ImageRecord imageRecord) => imageRecord with
    {
        Description = Description,
        Tags = Tags,
        AnalysisModel = AnalysisModel,
        Favorite = Favorite,
        MarkedForDeletion = MarkedForDeletion,
        Collections = Collections.ToImmutableArray(),
        Rating = Rating,
        Reviewed = Reviewed
    };
}
