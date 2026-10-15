using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Web.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainMappingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageMappings_PreserveEveryPropertyAndUseSeparateNestedModels(bool hasGeneration)
    {
        var image = new WebImageRecord
        {
            Id = "mapped-id",
            Name = "mapped name",
            OriginalName = "source.mp4",
            StoredPath = @"C:\gallery\saved.dmp4",
            SourceFolder = @"C:\source",
            SourcePath = @"C:\source\source.mp4",
            ImportedAt = DateTimeOffset.Parse("2026-01-02T03:04:05+02:00"),
            Width = 1024,
            Height = 768,
            OriginalBytes = 12345,
            Sha256 = "source hash",
            PngSha256 = "stored hash",
            Metadata = "{\"video\":[]}",
            Description = "description",
            Tags = "one, two",
            AnalysisModel = "vision model",
            Favorite = true,
            MarkedForDeletion = true,
            Collections = ["first", "second"],
            Rating = 4,
            Reviewed = true,
            MediaType = "video",
            ContentType = "video/mp4",
            DurationSeconds = 12.5,
            Generation = hasGeneration ? new WebGenerationInfo
            {
                Prompt = "positive",
                NegativePrompt = "negative",
                Cfg = 7.5,
                Steps = 23,
                Model = "checkpoint",
                ModelKind = "Checkpoint",
                HasWorkflow = true,
                HasApiPrompt = true
            } : null,
            MetadataVersion = 19
        };

        var domain = image.ToDomain();
        var mapped = domain.ToWeb();
        Assert.Equal(JsonSerializer.Serialize(image), JsonSerializer.Serialize(mapped));
        AssertMatchingProperties(image, domain);
        AssertMatchingProperties(new WebImageRecordEdit(image), new WebImageRecordEdit(image).ToDomain());
        Assert.Equal(new WebImageRecordEdit(image), new WebImageRecordEdit(image).ToDomain().ToWeb());
        Assert.Equal(image.Id, domain.Id);
        Assert.Equal(image.ImportedAt, domain.ImportedAt);
        Assert.Equal(image.IsVideo, domain.IsVideo);
        Assert.Equal(image.Prompt, domain.Prompt);
        if (hasGeneration)
        {
            Assert.IsType<GenerationInfo>(domain.Generation);
            AssertMatchingProperties(image.Generation!, domain.Generation!);
        }
    }

    [Fact]
    public void FilterMapping_PreservesEveryCriterion()
    {
        var filter = new WebGalleryFilter
        {
            Search = "search",
            Folder = "folder",
            Tag = "tag",
            Model = "model",
            FavoritesOnly = true,
            Deletion = "marked",
            Analysis = "analyzed",
            Orientation = "landscape",
            MinWidth = 123,
            MinHeight = 456,
            From = new DateTime(2026, 1, 2),
            To = new DateTime(2026, 3, 4),
            Sort = "rating",
            Collection = "collection",
            MinRating = 3,
            Review = "reviewed",
            UnorganizedOnly = true,
            Media = "video",
            Prompt = "exact prompt"
        };

        AssertMatchingProperties(filter, filter.ToDomain());
    }

    [Fact]
    public void ResultMappings_PreserveGroupOrderingAndReviewState()
    {
        var first = new ImageRecord { Id = "first" };
        var second = new ImageRecord { Id = "second" };
        var promptGroup = new PromptGroup(PromptGroup.Shared, "prompt", [second, first]).ToWeb();
        var duplicateGroup = new DuplicateGroup("second", ["second", "first"], 6, true, 123).ToWeb();
        var review = new WebReviewState(true, true, true, 5);

        Assert.Equal(["second", "first"], promptGroup.Images.Select(image => image.Id));
        Assert.Equal("prompt", promptGroup.Prompt);
        Assert.Equal(WebPromptGroup.Shared, promptGroup.Kind);
        Assert.Equal("second", duplicateGroup.KeepId);
        Assert.Equal(["second", "first"], duplicateGroup.ImageIds.ToArray());
        Assert.Equal(6, duplicateGroup.MaxDistance);
        Assert.True(duplicateGroup.Exact);
        Assert.Equal(123, duplicateGroup.ReclaimableBytes);
        Assert.Equal(review, review.ToDomain().ToWeb());
    }

    [Fact]
    public void WebSerialization_PreservesExistingCatalogAndCachedSettingContracts()
    {
        const string legacyRecord = """
            {"Id":"legacy","Name":"old","Collections":["saved"],"Generation":{"Prompt":"existing"}}
            """;
        const string legacyScan = """
            {"Groups":[{"KeepId":"first","Ids":["first","second"],"MaxDistance":0,"Exact":true,"ReclaimableBytes":42}],
             "ScannedCount":2,"ScannedAt":"2026-01-01T00:00:00+00:00"}
            """;
        var image = JsonSerializer.Deserialize<WebImageRecord>(legacyRecord)!;
        var scan = JsonSerializer.Deserialize<WebDuplicateScan>(legacyScan)!;

        Assert.Equal("legacy", image.ToDomain().ToWeb().Id);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("existing", image.Prompt);
        Assert.Equal(["saved"], image.Collections.ToArray());
        Assert.Equal(["first", "second"], Assert.Single(scan.Groups).ImageIds.ToArray());
        var serializedGroup = JsonSerializer.SerializeToElement(scan.Groups[0]);
        Assert.True(serializedGroup.TryGetProperty("Ids", out _));
        Assert.False(serializedGroup.TryGetProperty("ImageIds", out _));
        var serializedImage = JsonSerializer.SerializeToElement(image);
        Assert.False(serializedImage.TryGetProperty("Prompt", out _));
        Assert.False(serializedImage.TryGetProperty("IsVideo", out _));
    }

    private static void AssertMatchingProperties(object web, object domain)
    {
        var webProperties = web.GetType().GetProperties();
        var domainProperties = domain.GetType().GetProperties();
        Assert.Equal(webProperties.Select(property => property.Name).Order(),
            domainProperties.Select(property => property.Name).Order());
        foreach (var property in webProperties)
        {
            var webValue = property.GetValue(web);
            var domainValue = domain.GetType().GetProperty(property.Name)!.GetValue(domain);
            if (webValue is WebGenerationInfo generation)
                AssertMatchingProperties(generation, domainValue!);
            else
                Assert.Equal(webValue, domainValue);
        }
    }
}
