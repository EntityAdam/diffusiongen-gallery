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
    [Fact]
    public async Task ImageCard_WhenSelectedAndRated_ThenRetainsKeyboardHooksAndAccessibleLibraryControls()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        ParameterView parameterView = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GalleryImageCard.Image)] = new ImageRecord { Id = "sample", Name = "Sample", Rating = 3, IsFavorite = true },
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
        Assert.Contains("<svg", actualHtml);
        Assert.DoesNotContain("<Trash", actualHtml);
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

        // Assert
        Assert.NotEmpty(controls);
        foreach (Match control in controls) Assert.Contains("disabled", control.Value);
        Assert.Contains("type=\"date\"", actualHtml);
        Assert.Contains("type=\"number\"", actualHtml);
        Assert.Contains("role=\"checkbox\"", actualHtml);
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
