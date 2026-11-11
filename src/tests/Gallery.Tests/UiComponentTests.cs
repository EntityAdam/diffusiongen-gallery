using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Gallery.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiComponentTests
{
    [Fact]
    public async Task TagManagement_ShowsUnusedDefinitionsAndCascadingActions()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Service.AddTagAsync("Unused vocabulary");
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        string html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.HtmlRenderer.RenderComponentAsync<TagManagement>();
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("Manage tags", html);
        Assert.Contains(">Unused vocabulary</strong>", html);
        Assert.Contains(">Unused</span>", html);
        Assert.Contains("aria-label=\"Rename tag Unused vocabulary\"", html);
        Assert.Contains("aria-label=\"Delete tag Unused vocabulary\"", html);
    }

    [Fact]
    public async Task LlmEndpointSettings_ShowsTheSavedVisionPromptAndRestoreAction()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        const string prompt = "Describe lighting.\nReturn JSON with description and tags.";
        string html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.HtmlRenderer.RenderComponentAsync<LlmEndpointSettings>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LlmEndpointSettings.SavedSettings)] = new VisionSettings
                {
                    Model = "vision", SystemPrompt = prompt, CollectionAnalysisInstructions = "Additional analysis directions.",
                    CollectionSuggestionSystemPrompt = "Collection directions.", SearchSystemPrompt = "Search directions."
                }
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("vision-existing-tags", html);
        Assert.Contains("vision-new-tags", html);
        Assert.Contains("vision-output-format", html);
        Assert.Contains("structured-analysis-max-tokens", html);
        Assert.Contains("Vision system prompt", html);
        Assert.Contains("id=\"vision-system-prompt\"", html);
        Assert.Contains(prompt, html);
        Assert.Contains("Restore default prompt", html);
        Assert.Contains("Toolbox collection suggestions and full organization analysis also append the collection instructions below", html);
        Assert.Contains("id=\"collection-analysis-instructions\"", html);
        Assert.Contains("id=\"collection-suggestion-prompt\"", html);
        Assert.Contains("id=\"search-system-prompt\"", html);
        Assert.Contains("Effective system prompt for collection-aware analysis", html);
        Assert.Contains(prompt + " Additional analysis directions.", html);
        Assert.Contains("Collection directions.", html);
        Assert.Contains("Search directions.", html);
    }

    [Fact]
    public async Task StructuredAnalysis_ShowsSeparateEntityDetailsWarningsAndOriginalJson()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"scene":{"description":"Overview only"},"entities":[
            {"id":"p","kind":"person","description":"A person in a blue jacket","attributes":[{"name":"pose","value":"standing"}]},
            {"id":"unresolved"}],"relationships":[{"subjectId":"p.hand","predicate":"holding","objectId":"unresolved"}],
            "visual":{"medium":"anime"},"custom_field":"preserved"}
            """);
        var result = Gallery.Core.Services.Processors.StructuredVision.Parse(json.RootElement);
        await using var fixture = ComponentRenderFixture.Create();
        var html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.HtmlRenderer.RenderComponentAsync<VisionAnalysisView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(VisionAnalysisView.Analysis)] = result.Analysis }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Equal("Overview only", result.Description);
        Assert.Contains("A person in a blue jacket", html);
        Assert.Contains("standing", html);
        Assert.Contains("excluded from search", html);
        Assert.Contains("anime", html);
        Assert.Contains("custom_field", html);
    }

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
        Assert.Contains("aria-label=\"Media detail sections\"", html);
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
        Assert.Contains("Use AI to describe and tag your media", collapsed);
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
    public async Task Home_WhileImportRuns_ToolboxCannotLeaveIngestionProgress()
    {
        var home = new Home();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Home).GetField(name, flags)!.SetValue(home, value);
        T Get<T>(string name) => (T)typeof(Home).GetField(name, flags)!.GetValue(home)!;
        Task Invoke(string name, params object[] args) =>
            (Task)typeof(Home).GetMethod(name, flags)!.Invoke(home, args)!;

        Set("workflow", "ingest");
        Set("toolboxVisible", false);
        Set("activeTool", "filters");
        Set("busy", true);
        Set("importRunning", true);

        await Invoke("ToggleToolboxAsync");
        await Invoke("SetActiveToolAsync", "trash");
        await Invoke("SwitchWorkflowAsync", "gallery");

        Assert.Equal("ingest", Get<string>("workflow"));
        Assert.False(Get<bool>("toolboxVisible"));
        Assert.Equal("filters", Get<string>("activeTool"));

        Set("importRunning", false);
        await Invoke("ToggleToolboxAsync");
        await Invoke("SwitchWorkflowAsync", "gallery");
        Assert.Equal("ingest", Get<string>("workflow"));
        Assert.False(Get<bool>("toolboxVisible"));
    }

    [Fact]
    public async Task Home_WhenAutoLockPreferenceSaveFails_KeepsExistingTimerAndReportsError()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        fixture.Service.LockVault();
        var home = new Home();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(Home).GetProperty("Gallery", flags)!.SetValue(home, fixture.Service);
        typeof(Home).GetProperty("Logger", flags)!.SetValue(home, NullLogger<Home>.Instance);
        typeof(Home).GetField("autoLockMinutes", flags)!.SetValue(home, 15);

        await (Task)typeof(Home).GetMethod("UpdateAutoLockMinutesAsync", flags)!
            .Invoke(home, ["30"])!;

        Assert.Equal(15, typeof(Home).GetField("autoLockMinutes", flags)!.GetValue(home));
        Assert.Contains("previous timer is still active",
            (string)typeof(Home).GetField("error", flags)!.GetValue(home)!);
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
        Assert.Contains("aria-label=\"Move to Trash\"", actualHtml);
        Assert.DoesNotContain("Mark deletion", actualHtml);
        Assert.DoesNotContain("Encrypted", actualHtml);
        Assert.Contains("aria-label=\"Not reviewed\"", actualHtml);
        Assert.DoesNotContain(">Pending<", actualHtml);
        Assert.DoesNotContain("collection-chips", actualHtml);
        Assert.DoesNotContain("Hidden collection", actualHtml);
        Assert.Contains("<svg", actualHtml);
        Assert.DoesNotContain("<Trash", actualHtml);
        var footer = actualHtml[actualHtml.IndexOf("class=\"card-actions", StringComparison.Ordinal)..];
        Assert.Contains("card-rating-row", footer);
        Assert.Contains("aria-label=\"Clear rating\"", footer);
        Assert.Contains("aria-label=\"Remove favorite\"", footer);
        Assert.Contains("aria-label=\"Move to Trash\"", footer);
        Assert.True(footer.IndexOf("card-rating-row", StringComparison.Ordinal) < footer.IndexOf("aria-label=\"Remove favorite\"", StringComparison.Ordinal));
        Assert.True(footer.IndexOf("aria-label=\"Remove favorite\"", StringComparison.Ordinal) < footer.IndexOf("aria-label=\"Move to Trash\"", StringComparison.Ordinal));
        Assert.DoesNotContain("action-label", footer);
        Assert.DoesNotContain(">Trash<", footer);
        Assert.DoesNotContain(">Favorite<", footer);
    }

    [Theory]
    [InlineData(640, 480, 0, false, "1MP")]
    [InlineData(1499, 1000, 0, false, "1MP")]
    [InlineData(1500, 1000, 0, false, "2MP")]
    [InlineData(2000, 2000, 0, false, "4MP")]
    [InlineData(100000, 100000, 0, false, "10000MP")]
    [InlineData(1920, 1080, 65, true, "00:01:05")]
    [InlineData(1920, 1080, 90061, true, "25:01:01")]
    public async Task DetailCard_FormatsMegapixelThresholdsAndTotalVideoHours(int width, int height,
        double seconds, bool video, string expected)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<GalleryImageCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GalleryImageCard.Image)] = new ImageRecord
                { Width = width, Height = height, DurationSeconds = seconds, MediaType = video ? "video" : "image" }
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains($"· {expected} · 0.0 MiB", html);
    }

    [Theory]
    [InlineData(false, false, false, false, false, "Not analyzed", "eye-off", "circle-dashed-check")]
    [InlineData(false, true, false, true, true, "Vision analysis accepted", "eye", "circle-check")]
    [InlineData(true, false, true, false, false, "Vision analysis awaiting acceptance", "eye-dashed", "circle-dashed-check")]
    [InlineData(true, true, true, true, true, "Vision analysis awaiting acceptance", "eye-dashed", "circle-check")]
    public async Task DetailCard_ShowsFilenameMediaSummaryAndIndependentIconStates(bool video, bool analyzed,
        bool pending, bool reviewed, bool generated, string visionLabel, string visionIcon, string reviewIcon)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var renderer = fixture.HtmlRenderer;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<GalleryImageCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GalleryImageCard.Image)] = new ImageRecord
                {
                    Name = "display name", OriginalName = video ? "original.mp4" : "original.png",
                    Width = 1536, Height = 1024, OriginalBytes = 2516582,
                    MediaType = video ? "video" : "image", DurationSeconds = 3665,
                    Description = analyzed ? "vision result" : "", IsReviewed = reviewed,
                    Generation = generated ? new GenerationInfo { Model = @"models\cat.safetensors", Prompt = "a cat in sunlight" } : null
                },
                [nameof(GalleryImageCard.HasSuggestion)] = pending
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains(video ? "original.mp4" : "original.png", html);
        Assert.Contains("1536 × 1024", html);
        Assert.Contains(video ? "01:01:05" : "2MP", html);
        Assert.Contains("2.4 MiB", html);
        Assert.Contains($"aria-label=\"{(video ? "Video" : "Image")}\"", html);
        Assert.Contains($"aria-label=\"{visionLabel}\"", html);
        Assert.Contains($"aria-label=\"{(reviewed ? "Reviewed" : "Not reviewed")}\"", html);
        foreach (var label in new[] { video ? "Video" : "Image", visionLabel, reviewed ? "Reviewed" : "Not reviewed" }
            .Concat(generated ? ["AI generated: ComfyUI metadata detected"] : Array.Empty<string>()))
        {
            var indicator = Regex.Matches(html, "<span[^>]*>").Select(match => match.Value)
                .Single(span => span.Contains($"aria-label=\"{label}\""));
            Assert.Contains($"data-tooltip=\"{label}\"", indicator);
            Assert.Contains("tabindex=\"0\"", indicator);
            Assert.Contains("card-status", indicator);
        }
        foreach (var icon in new[] { visionIcon, reviewIcon })
        {
            var expectedSvg = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LucideBlazor.LucideIcon>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["Name"] = icon, ["Size"] = 16, ["aria-hidden"] = "true" }));
                return WebUtility.HtmlDecode(root.ToHtmlString());
            });
            Assert.Contains("<path", expectedSvg);
            Assert.Contains(expectedSvg, html);
        }
        Assert.Equal(generated, html.Contains("AI generated: ComfyUI metadata detected"));
        if (generated)
        {
            Assert.Contains("Model: cat", html);
            Assert.DoesNotContain("cat.safetensors", Regex.Replace(html, "title=\"[^\"]*\"", ""));
            Assert.Contains("a cat in sunlight", html);
            Assert.Contains("line-clamp-2", html);
        }
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
            Assert.Single(buttons, button => button.Contains($"aria-label=\"{(isCurated ? "Remove favorite" : "Favorite")}\"")));
        Assert.Contains($"aria-label=\"{(isCurated ? "Restore from Trash" : "Move to Trash")}\"", actualHtml);
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

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    public async Task Viewer_RatingShortcut_SavesAndOnlyAdvancesInReview(bool reviewMode, int rating)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        FullscreenViewer? viewer = null;
        ImageRecord? changed = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            fixture.HtmlRenderer.RenderComponentAsync<FullscreenViewerTestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FullscreenViewerTestHost.Images)] = new[] { first, second },
                [nameof(FullscreenViewerTestHost.ReviewMode)] = reviewMode,
                [nameof(FullscreenViewerTestHost.Capture)] = (Action<FullscreenViewer>)(value => viewer = value),
                [nameof(FullscreenViewerTestHost.Changed)] = (Action<ImageRecord>)(value => changed = value)
            })));
        var cache = (Dictionary<string, (string Url, long Bytes)>)typeof(FullscreenViewer)
            .GetField("fitCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        cache[first.Id] = ("blob:first", 0);
        cache[second.Id] = ("blob:second", 0);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey(rating.ToString()));

        var saved = Assert.Single(await vault.Service.ListAsync(), image => image.Id == first.Id);
        Assert.Equal(rating, saved.Rating);
        Assert.True(saved.IsReviewed);
        Assert.Equal(saved, changed);
        string html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains(reviewMode ? "2 / 2" : "1 / 2", html);
        Assert.Contains(reviewMode ? "data-keys=\"1 2 3 4 5 t c\"" : "data-keys=\"0 1 2 3 4 5 t c\"", html);
        Assert.Contains("1-5: rate", html);
        Assert.Contains("Quick edit media", html);
        Assert.Equal(reviewMode ? 5 : 4, Regex.Matches(html, "aria-label=\"Rate [1-5] out of 5 stars\"").Count);
    }

    [Theory]
    [InlineData(false, "t")]
    [InlineData(false, "c")]
    [InlineData(true, "t")]
    [InlineData(true, "c")]
    public async Task Viewer_QuickOrganization_AddsWithoutAdvancingAndCancelDoesNotSave(bool reviewMode, string key)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => vault.Service.AddCollection(edit with { Tags = "existing" }, "Existing"));
        var collection = await vault.Service.CreateCollectionAsync("Destination");
        var destinationSet = await vault.Service.CreateSetAsync(collection.Id, "New set");
        image = (await vault.Service.ListAsync()).Single();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        FullscreenViewer? viewer = null;
        ImageRecord? changed = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            fixture.HtmlRenderer.RenderComponentAsync<FullscreenViewerTestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FullscreenViewerTestHost.Images)] = new[] { image },
                [nameof(FullscreenViewerTestHost.ReviewMode)] = reviewMode,
                [nameof(FullscreenViewerTestHost.Capture)] = (Action<FullscreenViewer>)(value => viewer = value),
                [nameof(FullscreenViewerTestHost.Changed)] = (Action<ImageRecord>)(value => changed = value)
            })));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Task<string> Html() => fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Task Save(string value) => fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(FullscreenViewer).GetMethod("SaveQuickEditAsync", flags)!.Invoke(viewer, [value])!;
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(viewer, null);
        });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey(key));
        Assert.Contains("<dialog", await Html());
        Assert.Contains(key == "c" ? "Move 1 media to a set" : "Quick tag", await Html());
        Assert.Contains(key == "c" ? "Existing / Imported media" : ">existing</span>", await Html());
        foreach (var shortcut in new[] { "arrowright", "1", "f", "d", "escape" })
            await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey(shortcut));
        Assert.Contains("<dialog", await Html());
        Assert.Null(changed);

        if (key == "c")
        {
            await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = await vault.Service.MoveMediaToSetAsync([image.Id], destinationSet.Id);
                await (Task)typeof(FullscreenViewer).GetMethod("CollectionMovedAsync", flags)!.Invoke(viewer, [result])!;
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(viewer, null);
            });
        }
        else await Save("EXISTING, new tag");

        var saved = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal(key == "c" ? "existing" : "existing, new tag", saved.Tags);
        Assert.Equal(key == "c" ? new[] { "Destination" } : new[] { "Existing" }, saved.Collections.ToArray());
        Assert.False(saved.IsReviewed);
        Assert.NotNull(changed);
        Assert.Equal(saved.Tags, changed.Tags);
        Assert.Equal(saved.Collections.ToArray(), changed.Collections.ToArray());
        Assert.DoesNotContain("<dialog", await Html());
        Assert.Contains("1 / 1", await Html());
        if (reviewMode) Assert.Contains("0 Reviewed + 0 Skipped / 1", await Html());

        changed = null;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey(key));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => typeof(FullscreenViewer).GetMethod("CancelQuickEdit", flags)!.Invoke(viewer, null));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey("ignored"));
        Assert.DoesNotContain("<dialog", await Html());
        Assert.Null(changed);
        var afterCancel = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal(saved.Tags, afterCancel.Tags);
        Assert.Equal(saved.Collections.ToArray(), afterCancel.Collections.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Viewer_QuickTag_SavesRemovalOfAllTagsWithoutAdvancing(bool reviewMode)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => edit with { Tags = "cat, dog" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        FullscreenViewer? viewer = null;
        ImageRecord? changed = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            fixture.HtmlRenderer.RenderComponentAsync<FullscreenViewerTestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FullscreenViewerTestHost.Images)] = new[] { image },
                [nameof(FullscreenViewerTestHost.ReviewMode)] = reviewMode,
                [nameof(FullscreenViewerTestHost.Capture)] = (Action<FullscreenViewer>)(value => viewer = value),
                [nameof(FullscreenViewerTestHost.Changed)] = (Action<ImageRecord>)(value => changed = value)
            })));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Task<string> Html() => fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer!.HandleKey("t"));
        string dialog = Regex.Match(await Html(), @"<dialog\b[\s\S]*?</dialog>").Value;
        Assert.Contains("aria-label=\"Remove tag cat\"", dialog);
        Assert.Contains("aria-label=\"Remove tag dog\"", dialog);
        Assert.DoesNotContain(image.Name, dialog);
        Assert.Matches(@"<button[^>]*type=""button""[^>]*aria-label=""Remove tag cat""", dialog);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(FullscreenViewer).GetMethod("SaveQuickEditAsync", flags)!.Invoke(viewer, [""])!;
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(viewer, null);
        });
        Assert.Equal("", Assert.Single(await vault.Service.ListAsync()).Tags);
        Assert.NotNull(changed);
        Assert.False(changed.IsReviewed);
        Assert.DoesNotContain("<dialog", await Html());
        Assert.Contains("1 / 1", await Html());
        if (reviewMode) Assert.Contains("0 Reviewed + 0 Skipped / 1", await Html());
    }

    [Fact]
    public async Task DuplicateReviewer_EscapeWhileBusy_ThenClosesWithoutWaitingForLoading()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var first = new ImageRecord { Id = "first", Name = "First", Width = 10, Height = 10, OriginalBytes = 10 };
        var second = new ImageRecord { Id = "second", Name = "Second", Width = 10, Height = 10, OriginalBytes = 10 };
        var records = new Dictionary<string, ImageRecord> { [first.Id] = first, [second.Id] = second };
        var groups = new[] { new DuplicateGroup(first.Id, ImmutableArray.Create(first.Id, second.Id), 0, true, 10) };
        DuplicateReviewer? component = null;
        var closeCalled = false;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<DuplicateReviewerTestHost>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DuplicateReviewerTestHost.Groups)] = groups,
                [nameof(DuplicateReviewerTestHost.Records)] = records,
                [nameof(DuplicateReviewerTestHost.Capture)] = (Action<DuplicateReviewer>)(value => component = value),
                [nameof(DuplicateReviewerTestHost.Closed)] = (Action<string>)(_ => closeCalled = true)
            })));

        Assert.NotNull(component);
        var fields = typeof(DuplicateReviewer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        fields.Single(field => field.Name == "busy").SetValue(component, true);
        fields.Single(field => field.Name == "loading").SetValue(component, true);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => component!.HandleKey("escape"));

        Assert.True(closeCalled);
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
        Assert.Contains("7 of 0 media items match", html);
        Assert.Contains(">All media<", html);
        Assert.Contains(">Images only<", html);
        Assert.Contains(">Videos only<", html);
        Assert.Contains($"value=\"{GalleryFilter.AwaitingApproval}\"", html);
        Assert.Contains(">Awaiting approval<", html);
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

    private sealed class FullscreenViewerTestHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<ImageRecord> Images { get; set; } = [];
        [Parameter] public bool ReviewMode { get; set; }
        [Parameter] public Action<FullscreenViewer>? Capture { get; set; }
        [Parameter] public Action<ImageRecord>? Changed { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FullscreenViewer>(0);
            builder.AddAttribute(1, nameof(FullscreenViewer.Images), Images);
            builder.AddAttribute(2, nameof(FullscreenViewer.ReviewMode), ReviewMode);
            builder.AddAttribute(3, nameof(FullscreenViewer.OnImageChanged), EventCallback.Factory.Create<ImageRecord>(this,
                value => Changed?.Invoke(value)));
            builder.AddComponentReferenceCapture(4, instance => Capture?.Invoke((FullscreenViewer)instance));
            builder.CloseComponent();
        }
    }

    private sealed class DuplicateReviewerTestHost : ComponentBase
    {
        [Parameter] public IReadOnlyList<DuplicateGroup> Groups { get; set; } = [];
        [Parameter] public IReadOnlyDictionary<string, ImageRecord> Records { get; set; } = new Dictionary<string, ImageRecord>();
        [Parameter] public Action<DuplicateReviewer>? Capture { get; set; }
        [Parameter] public Action<string>? Closed { get; set; }

        public DuplicateReviewerTestHost() { }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<DuplicateReviewer>(0);
            builder.AddAttribute(1, nameof(DuplicateReviewer.Groups), Groups);
            builder.AddAttribute(2, nameof(DuplicateReviewer.Lookup), (Func<string, ImageRecord?>)(id =>
                Records.TryGetValue(id, out var record) ? record : null));
            builder.AddAttribute(3, nameof(DuplicateReviewer.OnClose), EventCallback.Factory.Create<string>(this,
                value => Closed?.Invoke(value)));
            builder.AddComponentReferenceCapture(4, instance => Capture?.Invoke((DuplicateReviewer)instance));
            builder.CloseComponent();
        }
    }

}
