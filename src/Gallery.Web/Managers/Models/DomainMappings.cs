using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Web.Models;

internal static class DomainMappings
{
    public static ImageRecord ToDomain(this WebImageRecord image) => new()
    {
        Id = image.Id,
        Name = image.Name,
        OriginalName = image.OriginalName,
        StoredPath = image.StoredPath,
        SourceFolder = image.SourceFolder,
        SourcePath = image.SourcePath,
        ImportedAt = image.ImportedAt,
        Width = image.Width,
        Height = image.Height,
        OriginalBytes = image.OriginalBytes,
        Sha256 = image.Sha256,
        PngSha256 = image.PngSha256,
        Metadata = image.Metadata,
        Description = image.Description,
        Tags = image.Tags,
        AnalysisModel = image.AnalysisModel,
        Favorite = image.Favorite,
        MarkedForDeletion = image.MarkedForDeletion,
        Collections = image.Collections,
        Rating = image.Rating,
        Reviewed = image.Reviewed,
        MediaType = image.MediaType,
        ContentType = image.ContentType,
        DurationSeconds = image.DurationSeconds,
        Generation = image.Generation?.ToDomain(),
        MetadataVersion = image.MetadataVersion
    };

    public static WebImageRecord ToWeb(this ImageRecord image) => new()
    {
        Id = image.Id,
        Name = image.Name,
        OriginalName = image.OriginalName,
        StoredPath = image.StoredPath,
        SourceFolder = image.SourceFolder,
        SourcePath = image.SourcePath,
        ImportedAt = image.ImportedAt,
        Width = image.Width,
        Height = image.Height,
        OriginalBytes = image.OriginalBytes,
        Sha256 = image.Sha256,
        PngSha256 = image.PngSha256,
        Metadata = image.Metadata,
        Description = image.Description,
        Tags = image.Tags,
        AnalysisModel = image.AnalysisModel,
        Favorite = image.Favorite,
        MarkedForDeletion = image.MarkedForDeletion,
        Collections = image.Collections,
        Rating = image.Rating,
        Reviewed = image.Reviewed,
        MediaType = image.MediaType,
        ContentType = image.ContentType,
        DurationSeconds = image.DurationSeconds,
        Generation = image.Generation?.ToWeb(),
        MetadataVersion = image.MetadataVersion
    };

    public static GenerationInfo ToDomain(this WebGenerationInfo generation) => new()
    {
        Prompt = generation.Prompt,
        NegativePrompt = generation.NegativePrompt,
        Cfg = generation.Cfg,
        Steps = generation.Steps,
        Model = generation.Model,
        ModelKind = generation.ModelKind,
        HasWorkflow = generation.HasWorkflow,
        HasApiPrompt = generation.HasApiPrompt
    };

    public static WebGenerationInfo ToWeb(this GenerationInfo generation) => new()
    {
        Prompt = generation.Prompt,
        NegativePrompt = generation.NegativePrompt,
        Cfg = generation.Cfg,
        Steps = generation.Steps,
        Model = generation.Model,
        ModelKind = generation.ModelKind,
        HasWorkflow = generation.HasWorkflow,
        HasApiPrompt = generation.HasApiPrompt
    };

    public static ImageRecordEdit ToDomain(this WebImageRecordEdit edit) =>
        new(new ImageRecord
        {
            Id = edit.Id,
            Name = edit.Name,
            StoredPath = edit.StoredPath,
            Description = edit.Description,
            Tags = edit.Tags,
            AnalysisModel = edit.AnalysisModel,
            Favorite = edit.Favorite,
            MarkedForDeletion = edit.MarkedForDeletion,
            Collections = edit.Collections,
            Rating = edit.Rating,
            Reviewed = edit.Reviewed,
            Generation = edit.Generation?.ToDomain(),
            MetadataVersion = edit.MetadataVersion
        });

    public static WebImageRecordEdit ToWeb(this ImageRecordEdit edit) =>
        new(new WebImageRecord
        {
            Id = edit.Id,
            Name = edit.Name,
            StoredPath = edit.StoredPath,
            Description = edit.Description,
            Tags = edit.Tags,
            AnalysisModel = edit.AnalysisModel,
            Favorite = edit.Favorite,
            MarkedForDeletion = edit.MarkedForDeletion,
            Collections = edit.Collections,
            Rating = edit.Rating,
            Reviewed = edit.Reviewed,
            Generation = edit.Generation?.ToWeb(),
            MetadataVersion = edit.MetadataVersion
        });

    public static GalleryFilter ToDomain(this WebGalleryFilter filter) => new()
    {
        Search = filter.Search,
        Folder = filter.Folder,
        Tag = filter.Tag,
        Model = filter.Model,
        FavoritesOnly = filter.FavoritesOnly,
        Deletion = filter.Deletion,
        Analysis = filter.Analysis,
        Orientation = filter.Orientation,
        MinWidth = filter.MinWidth,
        MinHeight = filter.MinHeight,
        From = filter.From,
        To = filter.To,
        Sort = filter.Sort,
        Collection = filter.Collection,
        MinRating = filter.MinRating,
        Review = filter.Review,
        UnorganizedOnly = filter.UnorganizedOnly,
        Media = filter.Media,
        Prompt = filter.Prompt
    };

    public static WebPromptGroup ToWeb(this PromptGroup group) =>
        new(group.Kind, group.Prompt, group.Images.Select(image => image.ToWeb()).ToImmutableArray());

    public static WebDuplicateGroup ToWeb(this DuplicateGroup group) =>
        new(group.KeepId, group.ImageIds, group.MaxDistance, group.Exact, group.ReclaimableBytes);

    public static ReviewState ToDomain(this WebReviewState state) =>
        new(state.Reviewed, state.Favorite, state.MarkedForDeletion, state.Rating);

    public static WebReviewState ToWeb(this ReviewState state) =>
        new(state.Reviewed, state.Favorite, state.MarkedForDeletion, state.Rating);

    public static ReviewAction ToDomain(this WebReviewAction action) => action switch
    {
        WebReviewAction.Keep => ReviewAction.Keep,
        WebReviewAction.Favorite => ReviewAction.Favorite,
        WebReviewAction.MarkDeletion => ReviewAction.MarkDeletion,
        WebReviewAction.Rate => ReviewAction.Rate,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown review action.")
    };

    public static RemovalItem ToDomain(this WebRemovalItem item) =>
        new(item.Id, item.Name, item.OriginalPath, item.OriginalExists);

    public static ExistingOriginalAction ToDomain(this WebExistingOriginalAction action) => action switch
    {
        WebExistingOriginalAction.Retain => ExistingOriginalAction.Retain,
        WebExistingOriginalAction.Delete => ExistingOriginalAction.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown original retention action.")
    };

    public static MissingOriginalAction ToDomain(this WebMissingOriginalAction action) => action switch
    {
        WebMissingOriginalAction.RestorePng => MissingOriginalAction.RestorePng,
        WebMissingOriginalAction.Discard => MissingOriginalAction.Discard,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown original restoration action.")
    };
}
