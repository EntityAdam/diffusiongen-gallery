using Gallery.Core.Services.Validators;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ImportDimensionBoundaryTests
{
    [Theory]
    [InlineData(8000, 5000, true)]
    [InlineData(8000, 5001, false)]
    [InlineData(16384, 1, true)]
    [InlineData(1, 16384, true)]
    [InlineData(16385, 1, false)]
    [InlineData(1, 16385, false)]
    public void ImageDimensions_WhenAtOrBeyondEachLimit_ThenRetainsTheEstablishedAcceptanceBoundary(
        int width, int height, bool dimensionsAreSupported)
    {
        // Arrange
        var expectedErrorMessage = dimensionsAreSupported ? null
            : $"Image dimensions {width} x {height} exceed the 40 megapixel / 16384 pixel dimension limit.";

        // Act
        var actualException = Record.Exception(() => ImportMediaValidator.ValidateImageDimensions(width, height));

        // Assert
        Assert.Equal(expectedErrorMessage, actualException?.Message);
        if (!dimensionsAreSupported) Assert.IsType<InvalidOperationException>(actualException);
    }
}
