using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DomainMappingTests
{
    [Fact]
    public void CanonicalContracts_PreserveExistingCatalogAndCachedSettingSerialization()
    {
        const string legacyRecord = """
            {"Id":"legacy","Name":"old","Collections":["saved"],"Generation":{"Prompt":"existing"}}
            """;
        const string legacyScan = """
            {"Groups":[{"KeepId":"first","Ids":["first","second"],"MaxDistance":0,"Exact":true,"ReclaimableBytes":42}],
             "ScannedCount":2,"ScannedAt":"2026-01-01T00:00:00+00:00"}
            """;
        var image = JsonSerializer.Deserialize<ImageRecord>(legacyRecord)!;
        var scan = JsonSerializer.Deserialize<DuplicateScan>(legacyScan)!;

        Assert.Equal("legacy", image.Id);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("existing", image.Prompt);
        Assert.Equal(["saved"], image.Collections.ToArray());
        Assert.True(scan.Groups.Length == 1,
            $"Cached scan group count: Expected: 1; Actual: {scan.Groups.Length}.");
        Assert.Equal(["first", "second"], scan.Groups[0].ImageIds.ToArray());
        var serializedGroup = JsonSerializer.SerializeToElement(scan.Groups[0]);
        Assert.True(serializedGroup.TryGetProperty("Ids", out _));
        Assert.False(serializedGroup.TryGetProperty("ImageIds", out _));
        var serializedImage = JsonSerializer.SerializeToElement(image);
        Assert.False(serializedImage.TryGetProperty("Prompt", out _));
        Assert.False(serializedImage.TryGetProperty("IsVideo", out _));
    }

    [Fact]
    public void PublicEntry_UsesTheSameCanonicalContractsForEditingAndReview()
    {
        var service = typeof(GalleryService);
        var applyEdit = service.GetMethod(nameof(GalleryService.ApplyEdit))!;
        var captureReview = service.GetMethod(nameof(GalleryService.CaptureReviewState), [typeof(ImageRecord)])!;

        Assert.Equal(typeof(ImageRecord), applyEdit.ReturnType);
        Assert.Equal(typeof(Func<ImageRecordEdit, ImageRecordEdit>), applyEdit.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(ReviewState), captureReview.ReturnType);
    }
}
