using Gallery.Core.Models;

namespace Gallery.Tests;

public sealed class BrowsingBoundaryValidationTests
{
    [Fact]
    public async Task InitialPage_RejectsInvalidPageSizeBeforeAcquiringTheGallerySession()
    {
        using var fixture = new TestVault();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Scope.Catalog.LoadInitialPageAsync(0));

        Assert.Equal("pageSize", error.ParamName);
    }

    [Fact]
    public void Filter_RejectsMissingInputsAtTheCatalogBoundary()
    {
        using var fixture = new TestVault();

        var missingRecords = Assert.Throws<ArgumentNullException>(() =>
            fixture.Scope.Catalog.Filter(null!, new GalleryFilter()));
        var missingFilter = Assert.Throws<ArgumentNullException>(() =>
            fixture.Scope.Catalog.Filter([], null!));

        Assert.Equal("records", missingRecords.ParamName);
        Assert.Equal("filter", missingFilter.ParamName);
    }

    [Fact]
    public void Grouping_RejectsMissingRecordsAtTheCatalogBoundary()
    {
        using var fixture = new TestVault();

        var missingPromptRecords = Assert.Throws<ArgumentNullException>(() =>
            fixture.Scope.Catalog.GroupByPrompt(null!));
        var missingModelRecords = Assert.Throws<ArgumentNullException>(() =>
            fixture.Scope.Catalog.GroupByDiffusionModel(null!));

        Assert.Equal("records", missingPromptRecords.ParamName);
        Assert.Equal("records", missingModelRecords.ParamName);
    }
}
