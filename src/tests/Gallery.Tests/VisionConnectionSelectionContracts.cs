using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Gallery.Core.Services;
using Gallery.Core.Models;

namespace Gallery.Tests;

public sealed class VisionConnectionSelectionContracts
{
    [Fact]
    public async Task ProviderSelection_WhenCallsOverlap_ThenKeepsIdentityAndModelIndependent()
    {
        // Arrange
        var visionRequestRecorder = new VisionRequestRecorder();
        using var httpClient = new HttpClient(visionRequestRecorder);
        using var galleryService = CreateGalleryService(httpClient);
        var firstSettings = new VisionSettings { ApiKey = "first-token", Model = "first-model", RequestTimeoutSeconds = 10 };
        var secondSettings = new VisionSettings { Endpoint = "http://192.168.1.2:8080/api/", ApiKey = "second-token", Model = "second-model", RequestTimeoutSeconds = 20 };
        var expectedRequests = new[]
        {
            "http://127.0.0.1:1234/v1/chat/completions|first-token|first-model|800|False",
            "http://192.168.1.2:8080/api/chat/completions|second-token|second-model|800|False"
        };

        // Act
        var analyses = await Task.WhenAll(galleryService.AnalyzeImageAsync(firstSettings, "image"),
            galleryService.AnalyzeImageAsync(secondSettings, "image"));

        // Assert
        Assert.Equal(expectedRequests.Order(StringComparer.Ordinal), visionRequestRecorder.ReadRequests());
        Assert.All(analyses, analysis => Assert.Equal(("A landscape.", "green"), analysis));
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        Assert.Equal(new[] { "available" }, await galleryService.GetVisionModelsAsync(new VisionSettings { RequestTimeoutSeconds = 10 }));
    }

    [Fact]
    public async Task ImageAnalysis_WhenCollectionSuggestionIsSelected_ThenChangesPromptBudgetAndParserTogether()
    {
        // Arrange
        var visionRequestRecorder = new VisionRequestRecorder();
        using var httpClient = new HttpClient(visionRequestRecorder);
        using var galleryService = CreateGalleryService(httpClient);
        var visionSettings = new VisionSettings { Model = "vision", RequestTimeoutSeconds = 10 };
        var expectedRequests = new[]
        {
            "http://127.0.0.1:1234/v1/chat/completions||vision|800|False",
            "http://127.0.0.1:1234/v1/chat/completions||vision|1200|True"
        };

        // Act
        var analysis = await galleryService.AnalyzeImageAsync(visionSettings, "image");
        var collectionAnalysis = await galleryService.AnalyzeImageWithCollectionAsync(visionSettings, "image", ["Worlds"]);

        // Assert
        Assert.Equal(expectedRequests.Order(StringComparer.Ordinal), visionRequestRecorder.ReadRequests());
        Assert.Equal(("A landscape.", "green"), analysis);
        Assert.Equal(("A landscape.", "green", "Worlds", ""), collectionAnalysis);
    }

    private static GalleryService CreateGalleryService(HttpClient httpClient)
    {
        string root = Path.Combine(Path.GetTempPath(), "vision-connection-" + Guid.NewGuid().ToString("N"));
        return GalleryService.Create(root, new GalleryRuntimeOptions { StateDirectory = root }, httpClient);
    }

    private sealed class VisionRequestRecorder : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> capturedRequests = new();

        public string[] ReadRequests() => capturedRequests.Order(StringComparer.Ordinal).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage httpRequestMessage,
            CancellationToken cancellationToken)
        {
            if (httpRequestMessage.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"available"}]}""") };
            using var jsonDocument = JsonDocument.Parse(await httpRequestMessage.Content!.ReadAsStringAsync(cancellationToken));
            var jsonElement = jsonDocument.RootElement;
            string prompt = jsonElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
            capturedRequests.Enqueue($"{httpRequestMessage.RequestUri}|{httpRequestMessage.Headers.Authorization?.Parameter}|" +
                $"{jsonElement.GetProperty("model").GetString()}|{jsonElement.GetProperty("max_tokens").GetInt32()}|{prompt.Contains("Worlds", StringComparison.Ordinal)}");
            await Task.Yield();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"],\"collection\":\"Worlds\"}"}}]}""")
            };
        }
    }
}
