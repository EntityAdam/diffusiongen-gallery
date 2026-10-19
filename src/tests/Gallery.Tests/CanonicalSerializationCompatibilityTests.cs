using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class CanonicalSerializationCompatibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyCatalogFlags_RetainTheirValuesAndWireNames(bool flag)
    {
        var literal = flag ? "true" : "false";
        var image = JsonSerializer.Deserialize<ImageRecord>(
            $$$"""{"Id":"legacy","Favorite":{{{literal}}},"Reviewed":{{{literal}}},"MarkedForDeletion":{{{literal}}}} """)!;

        Assert.Equal(flag, image.IsFavorite);
        Assert.Equal(flag, image.IsReviewed);
        Assert.Equal(flag, image.IsMarkedForDeletion);
        AssertFlags(JsonSerializer.SerializeToElement(image), flag, "Favorite", "Reviewed", "MarkedForDeletion");
        var edit = new ImageRecordEdit(image);
        AssertFlags(JsonSerializer.SerializeToElement(edit), flag, "Favorite", "Reviewed", "MarkedForDeletion");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacySavedViewAndReviewFlags_RetainTheirValuesAndWireNames(bool flag)
    {
        var literal = flag ? "true" : "false";
        var view = JsonSerializer.Deserialize<SavedView>(
            $$$"""{"Name":"legacy","Filter":{"FavoritesOnly":{{{literal}}},"UnorganizedOnly":{{{literal}}}}}""")!;
        var review = JsonSerializer.Deserialize<ReviewState>(
            $$$"""{"Favorite":{{{literal}}},"Reviewed":{{{literal}}},"MarkedForDeletion":{{{literal}}},"Rating":4}""")!;

        Assert.Equal("legacy", view.Name);
        Assert.Equal(flag, view.GalleryFilter.IncludesFavoritesOnly);
        Assert.Equal(flag, view.GalleryFilter.IncludesUnorganizedOnly);
        var viewJson = JsonSerializer.SerializeToElement(view);
        Assert.False(viewJson.TryGetProperty("GalleryFilter", out _));
        AssertFlags(viewJson.GetProperty("Filter"), flag, "FavoritesOnly", "UnorganizedOnly");
        Assert.Equal(flag, review.IsFavorite);
        Assert.Equal(flag, review.IsReviewed);
        Assert.Equal(flag, review.IsMarkedForDeletion);
        AssertFlags(JsonSerializer.SerializeToElement(review), flag, "Favorite", "Reviewed", "MarkedForDeletion");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyCachedResults_RetainTheirValuesAndWireNames(bool flag)
    {
        var literal = flag ? "true" : "false";
        var scan = JsonSerializer.Deserialize<DuplicateScan>(
            $$$"""{"Groups":[{"KeepId":"keep","Ids":["keep","duplicate"],"MaxDistance":0,"Exact":{{{literal}}},"ReclaimableBytes":42}],"ScannedCount":2,"ScannedAt":"2026-01-01T00:00:00Z"}""")!;
        var overview = JsonSerializer.Deserialize<StorageOverview>($$$"""{"IndexPending":{{{literal}}}}""")!;
        var backup = JsonSerializer.Deserialize<BackupRunResult>($$$"""{"BackupType":"full","BackupName":"legacy","Skipped":{{{literal}}}}""")!;
        var removal = JsonSerializer.Deserialize<RemovalResult>($$$"""{"Id":"legacy","Removed":{{{literal}}},"RestoredPath":null,"Failure":null}""")!;

        Assert.Equal(flag, scan.Groups[0].IsExact);
        Assert.Equal(["keep", "duplicate"], scan.Groups[0].ImageIds.ToArray());
        AssertFlags(JsonSerializer.SerializeToElement(scan).GetProperty("Groups")[0], flag, "Exact");
        Assert.Equal(flag, overview.IsIndexPending);
        AssertFlags(JsonSerializer.SerializeToElement(overview), flag, "IndexPending");
        Assert.Equal(flag, backup.WasSkipped);
        AssertFlags(JsonSerializer.SerializeToElement(backup), flag, "Skipped");
        Assert.Equal(flag, removal.WasRemoved);
        Assert.Null(removal.RemovalFailure);
        var removalJson = JsonSerializer.SerializeToElement(removal);
        AssertFlags(removalJson, flag, "Removed");
        Assert.Equal(JsonValueKind.Null, removalJson.GetProperty("Failure").ValueKind);
        Assert.Equal(JsonValueKind.Null, removalJson.GetProperty("RestoredPath").ValueKind);
        Assert.False(removalJson.TryGetProperty("RemovalFailure", out _));
    }

    [Fact]
    public void LegacyNullableReferences_RetainTheirWireNames()
    {
        var view = JsonSerializer.Deserialize<SavedView>("""{"Name":"legacy","Filter":null}""")!;
        var failure = JsonSerializer.Deserialize<RemovalFailure>("""{"ImageId":"legacy","Message":"failed","Cause":null}""")!;
        Assert.Null(view.GalleryFilter);
        Assert.Null(failure.Exception);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(view).GetProperty("Filter").ValueKind);
        var failureJson = JsonSerializer.SerializeToElement(failure);
        Assert.Equal("failed", failureJson.GetProperty("Message").GetString());
        Assert.Equal(JsonValueKind.Null, failureJson.GetProperty("Cause").ValueKind);
        Assert.False(failureJson.TryGetProperty("Exception", out _));
    }

    private static void AssertFlags(JsonElement serialized, bool flag, params string[] wireNames)
    {
        foreach (var wireName in wireNames)
        {
            Assert.Equal(flag, serialized.GetProperty(wireName).GetBoolean());
            var renamedMember = wireName switch
            {
                "FavoritesOnly" => "IncludesFavoritesOnly",
                "UnorganizedOnly" => "IncludesUnorganizedOnly",
                "Removed" => "WasRemoved",
                "Skipped" => "WasSkipped",
                _ => "Is" + wireName
            };
            Assert.False(serialized.TryGetProperty(renamedMember, out _),
                $"Unexpected CLR member '{renamedMember}' in legacy wire payload: {serialized}.");
        }
    }
}
