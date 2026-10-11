using System.Collections.Immutable;

namespace Gallery.Models;

public sealed class ImageRecordEdit
{
    internal ImageRecordEdit(ImageRecord image)
    {
        Id = image.Id;
        Name = image.Name;
        StoredPath = image.StoredPath;
        Description = image.Description;
        Tags = image.Tags;
        AnalysisModel = image.AnalysisModel;
        Favorite = image.Favorite;
        MarkedForDeletion = image.MarkedForDeletion;
        Collections = image.Collections.ToList();
        Rating = image.Rating;
        Reviewed = image.Reviewed;
        Generation = image.Generation;
        MetadataVersion = image.MetadataVersion;
    }

    public string Id { get; }
    public string Name { get; set; }
    public string StoredPath { get; set; }
    public string Description { get; set; }
    public string Tags { get; set; }
    public string AnalysisModel { get; set; }
    public bool Favorite { get; set; }
    public bool MarkedForDeletion { get; set; }
    public List<string> Collections { get; }
    public int Rating { get; set; }
    public bool Reviewed { get; set; }
    public GenerationInfo? Generation { get; set; }
    public int MetadataVersion { get; set; }

    internal ImageRecord ToRecord(ImageRecord source) => source with
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
