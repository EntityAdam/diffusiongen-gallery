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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageDetails_ShowsOrganizedFieldsWithoutPrivateStoragePathOrRawMetadata(bool isVideo)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var created = new DateTimeOffset(2020, 2, 3, 4, 5, 0, TimeSpan.Zero);
        var record = new ImageRecord
        {
            OriginalName = "original.png", SourcePath = @"C:\originals\original.png",
            StoredPath = @"C:\private\encrypted.dpng", SourceCreatedAt = created,
            MediaType = isVideo ? "video" : "image", DurationSeconds = 65,
            Collections = ["Cats"], Metadata = "RAW_JSON_SENTINEL",
            Generation = new GenerationInfo
            {
                Model = @"models\cat.safetensors", Steps = 1, Cfg = 3,
                Sampler = "euler", Scheduler = "normal", Prompt = "a cat",
                NegativePrompt = "watermark", HasWorkflow = true
            }
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ImageDetailFields>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ImageDetailFields.Image)] = record,
                    [nameof(ImageDetailFields.Description)] = "Editable vision notes",
                    [nameof(ImageDetailFields.UserDescription)] = "My user entered notes",
                }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("File information", html);
        Assert.Contains("aria-label=\"Image detail sections\"", html);
        Assert.Contains("role=\"tab\"", html);
        Assert.Contains(">Generation</button>", html);
        Assert.Contains("detail-section-active", html);
        Assert.Contains(record.SourcePath, html);
        Assert.Contains(created.ToLocalTime().ToString("g"), html);
        Assert.DoesNotContain("Cats", html);
        Assert.Contains("<dd>cat</dd>", html);
        Assert.Contains("<dd>1</dd>", html);
        Assert.Contains("<dd>3.0</dd>", html);
        Assert.Contains("euler / normal", html);
        Assert.Contains("Positive prompt", html);
        Assert.Contains("Negative prompt", html);
        Assert.Contains("watermark", html);
        Assert.Contains("Download workflow", html);
        Assert.Contains("<textarea", html);
        Assert.Contains("Editable vision notes", html);
        Assert.Contains("My user entered notes", html);
        Assert.DoesNotContain("Organize", html);
        Assert.Contains(">Notes</button>", html);
        Assert.Contains("for=\"description\"", html);
        Assert.Contains("for=\"vision-notes\"", html);
        var sections = Regex.Matches(html, @"<div class=""[^""]*detail-section[\s\S]*?(?=<div class=""[^""]*detail-section|$)")
            .Select(match => match.Value).ToArray();
        var fileSection = Assert.Single(sections, section => section.Contains("File information"));
        Assert.DoesNotContain("Cats", fileSection);
        var notesSection = Assert.Single(sections, section => section.Contains("<h3>Notes</h3>"));
        Assert.Contains("My user entered notes", notesSection);
        Assert.Contains("Editable vision notes", notesSection);
        Assert.DoesNotContain("Remove tag", notesSection);
        Assert.DoesNotContain("Image tags", html);
        Assert.DoesNotContain("Save tags", html);
        Assert.DoesNotContain(record.StoredPath, html);
        Assert.DoesNotContain("RAW_JSON_SENTINEL", html);
        Assert.DoesNotContain(".safetensors", html);
        Assert.DoesNotContain("Move", html);
        if (isVideo) Assert.Contains("1:05", html);
        else Assert.DoesNotContain("Video duration", html);
    }

    [Fact]
    public async Task ImageDetails_WhenLegacyRecordHasNoGenerationOrCreationDate_ThenShowsHonestMissingFields()
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ImageDetailFields>(ParameterView.Empty);
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("Unknown (not captured)", html);
        Assert.DoesNotContain("No collection", html);
        Assert.DoesNotContain("Download workflow", html);
        Assert.DoesNotContain("Negative prompt", html);
        Assert.DoesNotContain("Embedded generation metadata", html);
        Assert.DoesNotContain(">Generation</button>", html);
    }

    [Fact]
    public async Task ImageDetails_WhenComfyMetadataLacksNegativePrompt_ThenOmitsNegativePromptAndWorkflowWithoutDocuments()
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ImageDetailFields>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ImageDetailFields.Image)] = new ImageRecord
                    { Generation = new GenerationInfo { Prompt = "a cat" } }
                }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("Positive prompt", html);
        Assert.DoesNotContain("Negative prompt", html);
        Assert.DoesNotContain("Download workflow", html);
    }

    [Fact]
    public async Task MainLayout_RetainsBodyWithoutAppHeaderOrPrimaryFooter()
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Gallery.Web.Components.Layout.MainLayout>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Body"] = (RenderFragment)(builder => builder.AddContent(0, "Workflow content"))
                }));
            return root.ToHtmlString();
        });
        Assert.DoesNotContain("<header", html);
        Assert.DoesNotContain("LOCAL &amp; PRIVATE", html);
        Assert.DoesNotContain("DiffusionGen Gallery home", html);
        Assert.Contains("Workflow content", html);
        Assert.DoesNotContain("<footer", html);
    }

    [Fact]
    public async Task SidebarFooter_ShowsBrandAndActualSemanticBuildVersion()
    {
        await using var fixture = ComponentRenderFixture.Create();
        var html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.HtmlRenderer.RenderComponentAsync<WorkspaceSidebarFooter>();
            return root.ToHtmlString();
        });
        var expectedVersion = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(WorkspaceSidebarFooter).Assembly)!
            .InformationalVersion.Split('+')[0];
        Assert.Contains("<footer", html);
        Assert.Contains("DiffusionGen Gallery home", html);
        Assert.Contains($"V{expectedVersion}", html);
        Assert.Matches(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", WorkspaceSidebarFooter.AppVersion);
    }

    [Fact]
    public async Task Toolbox_ShowsIconToolsWithTooltipsAndExpandsOnlyTheActivePanel()
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        async Task<string> RenderAsync(string activeTool) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<GalleryToolbox>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GalleryToolbox.ActiveTool)] = activeTool,
                [nameof(GalleryToolbox.Badge)] = (Func<string, string>)(tool => tool == "trash" ? "4" : ""),
                [nameof(GalleryToolbox.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "panel body"))
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });

        var collapsed = await RenderAsync("");
        var expanded = await RenderAsync("organize");

        foreach (var tool in GalleryToolbox.Tools)
        {
            Assert.Contains($"data-tool=\"{tool.Id}\"", collapsed);
            Assert.Contains($"data-tooltip=\"{tool.Tooltip}\"", collapsed);
        }
        Assert.Contains("Change view", collapsed);
        Assert.Contains("Use AI to describe and tag your images", collapsed);
        Assert.Contains("double-click to reset", collapsed);
        Assert.Matches(@"class=""gallery-toolbox-panel""[^>]*hidden", collapsed);
        Assert.DoesNotContain("aria-pressed=\"true\"", collapsed);
        Assert.Contains("gallery-toolbox-badge", collapsed);
        Assert.Contains("is-expanded", expanded);
        Assert.Single(Regex.Matches(expanded, @"aria-pressed=""true"""));
        Assert.Matches(@"data-tool=""organize""[^>]*aria-pressed=""true""", expanded);
        Assert.Contains(">Organize</h3>", expanded);
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
    public async Task FilterPanel_RendersInlineAndExpandsActiveSectionsOnly()
    {
        // Arrange
        await using ComponentRenderFixture componentRenderFixture = ComponentRenderFixture.Create();
        HtmlRenderer htmlRenderer = componentRenderFixture.HtmlRenderer;
        GalleryFilter activeFilter = new() { Deletion = GalleryFilter.AnyValue, Orientation = "portrait" };

        // Act
        string html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await htmlRenderer.RenderComponentAsync<GalleryFilterPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GalleryFilterPanel.Filter)] = activeFilter,
                [nameof(GalleryFilterPanel.ResultCount)] = 7
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });

        // Assert
        Assert.DoesNotContain("role=\"dialog\"", html);
        Assert.DoesNotContain("aria-modal", html);
        Assert.Contains("7 of 0 images match", html);
        Assert.Equal(1, GalleryFilterPanel.CountActive(activeFilter));
        Assert.Equal(0, GalleryFilterPanel.CountChanged(new GalleryFilter()));
        Assert.Equal(2, GalleryFilterPanel.CountChanged(activeFilter));
        Assert.Matches(@"aria-expanded=""true"" aria-controls=""filter-section-media""", html);
        Assert.Matches(@"aria-expanded=""false"" aria-controls=""filter-section-organization""", html);
        Assert.Matches(@"id=""filter-section-organization"" class=""filter-section-body"" hidden", html);
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
