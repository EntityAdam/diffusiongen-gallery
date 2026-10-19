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
        IsFavorite = imageRecord.IsFavorite;
        IsMarkedForDeletion = imageRecord.IsMarkedForDeletion;
        Collections = imageRecord.Collections;
        Rating = imageRecord.Rating;
        IsReviewed = imageRecord.IsReviewed;
    }

    public string Id { get; }
    public string Description { get; init; }
    public string Tags { get; init; }
    public string AnalysisModel { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("Favorite")]
    public bool IsFavorite { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("MarkedForDeletion")]
    public bool IsMarkedForDeletion { get; init; }
    public ImmutableArray<string> Collections { get; init; }
    public int Rating { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("Reviewed")]
    public bool IsReviewed { get; init; }

}
