using System.Net;
using System.Text.RegularExpressions;
using Gallery.Web.Components;
using Gallery.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiComponentTests
{
    [Theory]
    [InlineData(typeof(LibraryQuickEdit), "Favorite", "Not Reviewed", 11)]
    [InlineData(typeof(ReviewQuickEdit), "Not Reviewed", "Favorite", 9)]
    [InlineData(typeof(RemovalQuickEdit), "Delete Forever", "Clear Rating", 3)]
    public async Task QuickEdit_ShowsOnlyItsWorkflowActions(Type componentType, string expected, string excluded, int buttonCount)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync(componentType, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["SelectedCount"] = 3,
                ["Busy"] = true
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });

        Assert.Contains("3 Selected", html);
        Assert.Contains("Clear Selection", html);
        Assert.Contains("ESC", html);
        Assert.Contains(expected, html);
        Assert.DoesNotContain(excluded, html);
        var buttons = Regex.Matches(html, @"<button\b[^>]*>");
        Assert.Equal(buttonCount, buttons.Count);
        foreach (Match button in buttons) Assert.Matches(@"\sdisabled(?:=|\s|/?>)", button.Value);
    }

    [Fact]
    public async Task ImageCard_WhenSelectedAndRated_ThenRetainsKeyboardHooksAndAccessibleLibraryControls()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GalleryImageCard.Image)] = new ImageRecord { Id = "sample", Name = "Sample", Rating = 3, IsFavorite = true, Collections = ["Hidden collection"] },
            [nameof(GalleryImageCard.ShowSelection)] = true,
            [nameof(GalleryImageCard.Selected)] = true
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<GalleryImageCard>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains("image-card", actualHtml);
        Assert.Contains("data-image-id=\"sample\"", actualHtml);
        Assert.Contains("image-open", actualHtml);
        Assert.Contains("aria-label=\"Select Sample\"", actualHtml);
        Assert.Contains("aria-label=\"Rating for Sample: 3 out of 5 stars\"", actualHtml);
        Assert.Contains("aria-label=\"Clear rating\"", actualHtml);
        Assert.Contains("h-7!", actualHtml);
        Assert.Contains("focus-visible:ring-ring", actualHtml);
        Assert.Contains("aria-label=\"Trash\"", actualHtml);
        Assert.DoesNotContain("Mark deletion", actualHtml);
        Assert.DoesNotContain("Encrypted", actualHtml);
        Assert.DoesNotContain("Reviewed", actualHtml);
        Assert.DoesNotContain(">Pending<", actualHtml);
        Assert.DoesNotContain("collection-chips", actualHtml);
        Assert.DoesNotContain("Hidden collection", actualHtml);
        Assert.Contains("<svg", actualHtml);
        Assert.DoesNotContain("<Trash", actualHtml);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImageDetailPreview_WhenMediaAndCurationChange_ThenShowsOverlayAndBelowImageControls(
        bool isVideo, bool isCurated)
    {
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(ImageDetailPreview.Image)] = new ImageRecord
            {
                Id = "sample",
                Name = "Sample",
                MediaType = isVideo ? "video" : "image",
                Rating = isCurated ? 3 : 0,
                IsFavorite = isCurated,
                IsMarkedForDeletion = isCurated
            },
            [nameof(ImageDetailPreview.Preview)] = "preview.png",
            [nameof(ImageDetailPreview.VideoUrl)] = isVideo ? "blob:video" : ""
        });

        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<ImageDetailPreview>(parameterView));
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        Assert.Contains(isVideo ? "<video" : "<img", actualHtml);
        Assert.Contains(isVideo ? "poster=\"preview.png\"" : "src=\"preview.png\"", actualHtml);
        string[] buttons = Regex.Matches(actualHtml, @"<button\b[^>]*>").Select(match => match.Value).ToArray();
        Assert.Contains("absolute top-2 right-2", Assert.Single(buttons, button => button.Contains("aria-label=\"Download a copy\"")));
        Assert.Contains($"absolute right-2 {(isVideo ? "bottom-14" : "bottom-2")}",
            Assert.Single(buttons, button => button.Contains("aria-label=\"Full screen view\"")));
        Assert.Contains($"aria-label=\"Rating for Sample: {(isCurated ? 3 : 0)} out of 5 stars\"", actualHtml);
        Assert.Equal(5, Regex.Matches(actualHtml, "<button[^>]*aria-label=\"(?:Clear rating|Rate [1-5] out of 5 stars)\"").Count);
        Assert.Contains(isCurated ? "aria-label=\"Clear rating\"" : "aria-label=\"Rate 3 out of 5 stars\"", actualHtml);
        Assert.Contains($"aria-pressed=\"{isCurated.ToString().ToLowerInvariant()}\"",
            Assert.Single(buttons, button => button.Contains("aria-label=\"Toggle favorite\"")));
        Assert.Contains($"aria-label=\"{(isCurated ? "Unmark deletion" : "Trash")}\"", actualHtml);
        Assert.True(actualHtml.IndexOf("aria-label=\"Full screen view\"", StringComparison.Ordinal)
            < actualHtml.IndexOf("role=\"group\"", StringComparison.Ordinal));
        Assert.DoesNotContain(">Full screen view<", actualHtml);
        Assert.DoesNotContain("Save a copy", actualHtml);
        Assert.DoesNotContain("Export workflow", actualHtml);
        Assert.DoesNotContain("Mark for deletion", actualHtml);
    }

    [Fact]
    public async Task ImageDetailPreview_WhenBusy_ThenAllActionsAreDisabled()
    {
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(ImageDetailPreview.Image)] = new ImageRecord { Id = "sample", Name = "Sample" },
            [nameof(ImageDetailPreview.Busy)] = true
        });

        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<ImageDetailPreview>(parameterView));
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        MatchCollection buttons = Regex.Matches(actualHtml, @"<button\b[^>]*>");
        Assert.Equal(9, buttons.Count);
        foreach (Match button in buttons) Assert.Contains("disabled", button.Value);
    }

    // Every unavailable thumbnail state selects its matching placeholder and hides the image.
    [Theory]
    [InlineData(false, "Loading preview...")]
    [InlineData(true, "Preview unavailable")]
    public async Task ImageCard_WhenThumbnailIsNotYetDecrypted_ThenShowsPlaceholder(bool thumbnailHasFailed, string expectedPlaceholder)
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GalleryImageCard.Image)] = new ImageRecord { Id = "pending", Name = "Pending" },
            [nameof(GalleryImageCard.ThumbnailFailed)] = thumbnailHasFailed
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<GalleryImageCard>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains(expectedPlaceholder, actualHtml);
        Assert.DoesNotContain("<img", actualHtml);
    }

    // Both credential modes preserve secret-field behavior and the same accessible tabs.
    [Theory]
    [InlineData(false, "input", "password")]
    [InlineData(true, "textarea", null)]
    public async Task Credentials_WhenModeChanges_ThenPreservesSecretFieldAndTabSemantics(
        bool useRecoveryPhrase, string expectedElementName, string? expectedInputType)
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GalleryCredentialInput.Value)] = "secret value",
            [nameof(GalleryCredentialInput.UseRecovery)] = useRecoveryPhrase,
            [nameof(GalleryCredentialInput.Disabled)] = true
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<GalleryCredentialInput>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains("role=\"tablist\"", actualHtml);
        Assert.Equal(2, Regex.Matches(actualHtml, "role=\"tab\"").Count);
        Assert.Contains($"<{expectedElementName}", actualHtml);
        if (expectedInputType is not null) Assert.Contains($"type=\"{expectedInputType}\"", actualHtml);
        Assert.Contains("autocomplete=\"off\"", actualHtml);
        Assert.Contains("secret value", actualHtml);
        Assert.Equal(3, Regex.Matches(actualHtml, @"\bdisabled(?:=|\s|>)").Count);
    }

    [Fact]
    public async Task Filters_WhenBusy_ThenEveryFieldIsDisabledIncludingTypedNativeFields()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GalleryFilterPanel.Filter)] = new GalleryFilter(),
            [nameof(GalleryFilterPanel.Disabled)] = true
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<GalleryFilterPanel>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        MatchCollection controls = Regex.Matches(actualHtml, @"<(?:input|select|button)\b[^>]*>");
        Match[] fields = controls.Where(control => !control.Value.Contains("data-filter-navigation")).ToArray();
        Match[] navigation = controls.Where(control => control.Value.Contains("data-filter-navigation")).ToArray();

        // Assert
        Assert.NotEmpty(fields);
        foreach (Match field in fields) Assert.Matches(@"\sdisabled(?:=|\s|/?>)", field.Value);
        Assert.NotEmpty(navigation);
        foreach (Match control in navigation) Assert.DoesNotMatch(@"\sdisabled(?:=|\s|/?>)", control.Value);
        Assert.Contains("type=\"date\"", actualHtml);
        Assert.Contains("type=\"number\"", actualHtml);
        Assert.Contains("role=\"checkbox\"", actualHtml);
    }

    [Fact]
    public async Task FilterDrawer_WhenClosed_ThenIsNotModalAndWhenOpen_ThenExpandsActiveSectionsOnly()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        GalleryFilter activeFilter = new() { Deletion = GalleryFilter.AnyValue, Orientation = "portrait" };

        async Task<string> RenderAsync(bool open) => await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await htmlRenderer.RenderComponentAsync<GalleryFilterPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GalleryFilterPanel.Filter)] = activeFilter,
                [nameof(GalleryFilterPanel.Open)] = open,
                [nameof(GalleryFilterPanel.ResultCount)] = 7
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });

        // Act
        string closedHtml = await RenderAsync(false);
        string openHtml = await RenderAsync(true);

        // Assert
        Assert.DoesNotContain("aria-modal", closedHtml);
        Assert.DoesNotContain("role=\"dialog\"", closedHtml);
        Assert.Contains("inert", closedHtml);
        Assert.Contains("role=\"dialog\"", openHtml);
        Assert.Contains("aria-modal=\"true\"", openHtml);
        Assert.Contains("Show 7 images", openHtml);
        Assert.Matches(@"aria-expanded=""true"" aria-controls=""filter-section-media""", openHtml);
        Assert.Matches(@"aria-expanded=""false"" aria-controls=""filter-section-organization""", openHtml);
        Assert.Matches(@"id=""filter-section-organization"" class=""filter-section-body"" hidden", openHtml);
    }

    [Fact]
    public async Task BackupDestination_WhenAzureSelected_ThenPreservesPasswordAndSavedCredentialHint()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(BackupLocation.Type)] = "azure",
            [nameof(BackupLocation.Container)] = "backups",
            [nameof(BackupLocation.HasSavedCredentials)] = true
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<BackupLocation>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains("value=\"backups\"", actualHtml);
        Assert.Contains("type=\"password\"", actualHtml);
        Assert.Contains("Saved securely; leave blank to keep it", actualHtml);
        Assert.Contains("autocomplete=\"off\"", actualHtml);
    }

    // Directory guidance follows the configured local-only or network-capable storage policy.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectoryInput_WhenStoragePolicyVaries_ThenExplainsWhetherNetworkStorageIsAllowed(bool storageIsLocalOnly)
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(DirectoryPathInput.Label)] = "Catalog location",
            [nameof(DirectoryPathInput.LocalOnly)] = storageIsLocalOnly
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<DirectoryPathInput>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains("Catalog location", actualHtml);
        Assert.Contains("aria-describedby", actualHtml);
        Assert.Contains(storageIsLocalOnly ? "not a NAS/SMB share" : "local directory or UNC path", actualHtml);
        Assert.Contains(storageIsLocalOnly ? @"C:\Gallery\catalog" : @"\\server\share\gallery", actualHtml);
    }

    // Every freshness state shows its timestamp; only stale data asks the user to refresh.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageOverviewStatus_WhenFreshnessChanges_ThenShowsLastUpdatedAndIndicatesStaleData(bool overviewIsStale)
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        var storageOverview = new StorageOverview(1, 1, 1, 1, 1, 0, DateTimeOffset.UtcNow) { IsStale = overviewIsStale };

        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(StorageOverviewStatus.Overview)] = storageOverview
        });

        // Act
        HtmlRootComponent htmlRootComponent = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            htmlRenderer.RenderComponentAsync<StorageOverviewStatus>(parameterView));

        // Assert - gather the completed component HTML on its dispatcher
        string actualHtml = await htmlRenderer.Dispatcher.InvokeAsync(() =>
            WebUtility.HtmlDecode(htmlRootComponent.ToHtmlString()));

        // Assert
        Assert.Contains("Last updated", actualHtml);
        Assert.Contains($"datetime=\"{storageOverview.UpdatedAt:O}\"", actualHtml);
        if (overviewIsStale)
        {
            Assert.Contains("data-testid=\"overview-stale\"", actualHtml);
            Assert.Contains("role=\"status\"", actualHtml);
            Assert.Contains("Select Refresh", actualHtml);
        }
        else
            Assert.DoesNotContain("overview-stale", actualHtml);
    }

}
