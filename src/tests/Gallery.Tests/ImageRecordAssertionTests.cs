using Gallery.Core.Models;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ImageRecordAssertionTests
{
    [Fact]
    public void Values_WhenCollectionsAreSeparatelyMaterialized_ThenAcceptsEqualOrderedContents()
    {
        var original = new ImageRecord { Id = "image", Collections = ["first", "second"] };
        var expected = ExpectedImageRecord.CreateFrom(original);
        var actual = original with { Collections = [.. original.Collections] };

        AsserterImageRecord.AssertValuesMatch(expected, actual);
    }

    [Fact]
    public void Values_WhenSeveralPropertiesAndCollectionOrderDiffer_ThenReportsEveryMismatch()
    {
        var original = new ImageRecord
        {
            Id = "expected id", Name = "expected name", Collections = ["first", "second"],
            Generation = new GenerationInfo { Prompt = "expected prompt" }
        };
        var expected = ExpectedImageRecord.CreateFrom(original);
        var actual = original with
        {
            Id = "wrong id", Name = "wrong name", Collections = ["second", "first"],
            Generation = new GenerationInfo { Prompt = "wrong prompt" }
        };

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterImageRecord.AssertValuesMatch(expected, actual));

        Assert.Contains("Id: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Name: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Collections: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Generation: Expected:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:", failure.Message, StringComparison.Ordinal);
    }
}
