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
        using var fixture = new VisionGalleryFixture(httpClient);
        await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        var galleryService = fixture.Service;
        var firstSettings = new VisionSettings { ApiKey = "first-token", Model = "first-model", RequestTimeoutSeconds = 10 };
        var secondSettings = new VisionSettings { Endpoint = "http://192.168.1.2:8080/api/", ApiKey = "second-token", Model = "second-model", RequestTimeoutSeconds = 20 };
        var expectedRequests = new[]
        {
            "http://127.0.0.1:1234/v1/chat/completions|first-token|first-model|800",
            "http://192.168.1.2:8080/api/chat/completions|second-token|second-model|800"
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

    private sealed class VisionGalleryFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vision-connection-" + Guid.NewGuid().ToString("N"));
        public GalleryService Service { get; }
        public VisionGalleryFixture(HttpClient client) => Service = GalleryService.Create(Path.Combine(root, "vault"),
            new GalleryRuntimeOptions { StateDirectory = Path.Combine(root, "state") }, client);
        public void Dispose()
        {
            Service.Dispose();
            Service.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
            capturedRequests.Enqueue($"{httpRequestMessage.RequestUri}|{httpRequestMessage.Headers.Authorization?.Parameter}|" +
                $"{jsonElement.GetProperty("model").GetString()}|{jsonElement.GetProperty("max_tokens").GetInt32()}");
            await Task.Yield();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            };
        }
    }
}
