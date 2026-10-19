namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class PngFrameAssertionTests
{
    [Theory]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/png;base64,AQID")]
    [InlineData("data:image/png;base64,not base64")]
    public void Frame_WhenPayloadIsEmptyOrMalformed_ThenRejectsThePrefixOnlyResult(string dataUri)
    {
        var failure = Record.Exception(() => AsserterPngFrame.AssertMatches(new(384, 216), dataUri));

        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
    }

    [Fact]
    public void Frame_WhenBothDimensionsDiffer_ThenReportsBothObservedDimensions()
    {
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(GalleryTests.MakePng(10, 20));

        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AsserterPngFrame.AssertMatches(new(384, 216), dataUri));

        Assert.Contains("Width: Expected: 384; Actual: 10", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Height: Expected: 216; Actual: 20", failure.Message, StringComparison.Ordinal);
    }
}
