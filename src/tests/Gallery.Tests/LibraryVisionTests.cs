using System.Net;
using System.Text.Json;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Models;
using Gallery.Web.Components.Features.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class LibraryVisionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task TagAndDescriptionSuggestions_DoNotUseCollectionNamesAsTagVocabulary(bool tagsOnly, bool wholeLibrary)
    {
        using var handler = new CapturingVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Scope.Records.UpdateAsync(image.Id, edit => edit with { Collections = ["Existing collection sentinel"] });
        var settings = new VisionSettings
        {
            Model = "vision", SystemPrompt = "Image description and tags only. Return JSON with description and tags."
        };
        await vault.Scope.Vision.SaveSettingsAsync(settings);
        using var analysis = new LibraryAnalysis(vault.Scope.Catalog, vault.Scope.Vision, NullLogger.Instance);
        if (wholeLibrary) await analysis.AnalyzeUnanalyzedAsync();
        else await analysis.AnalyzeSelectionAsync([image], tagsOnly);

        Assert.NotNull(handler.RequestBody);
        using var request = JsonDocument.Parse(handler.RequestBody);
        var messages = request.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString();
        var task = messages[1].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal(settings.SystemPrompt, system);
        Assert.Equal("Describe and tag this image.", task);
        Assert.DoesNotContain("Existing collection sentinel", handler.RequestBody);
        var suggestion = Assert.Single(tagsOnly ? analysis.TagSuggestions : analysis.Suggestions);
        Assert.Equal("sunlit, landscape", suggestion.Tags);
        Assert.Equal("", suggestion.Warning);
        Assert.Equal("", Assert.Single(await vault.Scope.Catalog.ListAsync()).Tags);
    }

    private sealed class CapturingVisionHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var content = JsonSerializer.Serialize(new
            {
                description = "A sunlit landscape.", tags = new[] { "sunlit", "landscape" }
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }))
            };
        }
    }
}
