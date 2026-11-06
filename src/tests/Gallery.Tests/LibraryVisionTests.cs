using System.Net;
using System.Reflection;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class LibraryVisionTests
{
    [Theory]
    [InlineData("SuggestTagsAsync", "tagSuggestions", false)]
    [InlineData("AnalyzeUnanalyzedAsync", "suggestions", false)]
    [InlineData("SuggestAsync", "suggestions", false)]
    public async Task WorkbenchTagAndDescriptionSuggestions_DoNotUseCollectionNamesAsTagVocabulary(
        string action, string suggestionField, bool expectsCollection)
    {
        using var handler = new CapturingVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => vault.Service.AddCollection(edit, "Existing collection sentinel"));
        var settings = new VisionSettings
        {
            Model = "vision", SystemPrompt = "Image description and tags only. Return JSON with description and tags.",
            CollectionAnalysisInstructions = "Collection context marker. Also return collection."
        };
        await vault.Service.SaveSettingsAsync(settings);
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        LibraryWorkbench? workbench = null;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            fixture.HtmlRenderer.RenderComponentAsync<WorkbenchTestHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkbenchTestHost.Image)] = image,
                [nameof(WorkbenchTestHost.Capture)] = (Action<LibraryWorkbench>)(component => workbench = component)
            })));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
            await (Task)typeof(LibraryWorkbench).GetMethod(action, flags)!.Invoke(workbench, null)!);

        Assert.NotNull(handler.RequestBody);
        using var request = JsonDocument.Parse(handler.RequestBody);
        var messages = request.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString();
        var task = messages[1].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal(expectsCollection ? settings.SystemPrompt + " " + settings.CollectionAnalysisInstructions : settings.SystemPrompt, system);
        if (expectsCollection) Assert.Contains("Existing collection sentinel", task);
        else
        {
            Assert.Equal("Describe and tag this image.", task);
            Assert.DoesNotContain("Existing collection sentinel", handler.RequestBody);
            Assert.DoesNotContain("Collection context marker", handler.RequestBody);
        }
        var results = (List<OrganizationSuggestion>)typeof(LibraryWorkbench).GetField(suggestionField, flags)!.GetValue(workbench)!;
        var suggestion = Assert.Single(results);
        Assert.Equal("sunlit, landscape", suggestion.Tags);
        Assert.Equal(expectsCollection ? "Existing collection sentinel" : "", suggestion.Collection);
        Assert.Equal("", suggestion.Warning);
        Assert.Equal("", Assert.Single(await vault.Service.ListAsync()).Tags);
    }

    private sealed class WorkbenchTestHost : ComponentBase
    {
        [Parameter] public ImageRecord Image { get; set; } = new();
        [Parameter] public Action<LibraryWorkbench>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<LibraryWorkbench>(0);
            builder.AddAttribute(1, nameof(LibraryWorkbench.Visible), true);
            builder.AddAttribute(2, nameof(LibraryWorkbench.Workflow), "organize");
            builder.AddAttribute(3, nameof(LibraryWorkbench.Images), new[] { Image });
            builder.AddAttribute(4, nameof(LibraryWorkbench.SelectedIds), new[] { Image.Id });
            builder.AddComponentReferenceCapture(5, instance => Capture?.Invoke((LibraryWorkbench)instance));
            builder.CloseComponent();
        }
    }

    private sealed class CapturingVisionHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var content = JsonSerializer.Serialize(new
            {
                description = "A sunlit landscape.", tags = new[] { "sunlit", "landscape" }, collection = "Existing collection sentinel"
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }))
            };
        }
    }
}
