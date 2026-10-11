using System.Net;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

// Direct gateway tests supply malformed response envelopes through HttpMessageHandler and observe
// exact outgoing requests. The UI boundary cannot deterministically produce these protocol failures
// or distinguish per-request authentication from shared HttpClient state without this transport oracle.
public sealed class VisionTests
{
    [Fact]
    public async Task BulkAnalysis_WhenCollectionsAreAvailable_UsesOneVisionRequestWithCollectionContext()
    {
        // Arrange
        var calls = 0;
        string? sent = null;
        var service = new VisionService(new HttpClient(new FakeHandler(async request =>
        {
            calls++;
            sent = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":" \n```json\n{\"description\":\"A landscape.\",\"tags\":[\"green\"],\"collection\":\"Worlds\"}\n``` "}}]}""")
            };
        })));
        // Act
        var result = await service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "data:image/png;base64,example", ["Worlds"]);

        // Assert
        TestAssertions.Contains("image_url", sent!, "the vision request includes image data");
        TestAssertions.Contains("Worlds", sent!, "the request includes available collection context");
        TestAssertions.Contains("collection", sent!, "the request asks for a collection");
        Assert.Equal(("A landscape.", "green", "Worlds", ""), result);
        Assert.Equal(1, calls);
    }

    [Theory]
    // Each invalid envelope must explain its specific failure rather than return success-shaped analysis.
    [InlineData("", "empty response")]
    [InlineData("""{"choices":[]}""", "no completion choices")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""", "empty completion content")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""", "empty completion content")]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":""}}]}""", "token limit")]
    [InlineData("""{"choices":[{"message":{"content":"```json\n\n```"}}]}""", "empty JSON code block")]
    [InlineData("""{"choices":[{"message":{"content":"not JSON"}}]}""", "valid JSON")]
    public async Task BulkAnalysis_WhenResponseIsEmptyOrInvalid_ExplainsTheFailure(string body, string expected)
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }))));
        // Act
        var error = await Record.ExceptionAsync(() =>
            service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(error,
            "the failure message describes the malformed vision response", expected);
    }

    [Fact]
    public async Task BulkAnalysis_WhenCollectionIsMissing_PreservesAnalysisWithWarning()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            }))));
        // Act
        var analysis = await service.AnalyzeAsync(new() { Model = "vision" }, "image");
        var result = await service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []);

        // Assert
        Assert.Equal("A landscape.", analysis.Description);
        Assert.Equal("A landscape.", result.Description);
        Assert.Equal("green", result.Tags);
        Assert.Equal("", result.Collection);
        Assert.Contains("missing collection", result.Warning);
    }

    [Theory]
    // Description must be a string and every tag must be a string in an array, for all response rows.
    [InlineData("""{}""", "description")]
    [InlineData("""{"description":123,"tags":[]}""", "description")]
    [InlineData("""{"description":"Valid","tags":"green"}""", "tags")]
    [InlineData("""{"description":"Valid","tags":[null]}""", "not a string")]
    [InlineData("""{"description":"Valid","tags":[123]}""", "not a string")]
    [InlineData("""[]""", "description")]
    public async Task Analysis_WhenResponseFieldsAreInvalid_ReportsActionableErrors(string content, string expected)
    {
        // Arrange
        var envelope = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) }))));
        // Act
        var error = await Record.ExceptionAsync(() =>
            service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(error,
            "the error identifies the invalid analysis field", expected);
    }

    [Theory]
    // Rows cover public, malformed, and special-use network addresses; secrets are never echoed in expected text.
    [InlineData("https://example.com/v1/")]
    [InlineData("http://localhost:1234/v1/")]
    [InlineData("http://9.255.255.255/v1/")]
    [InlineData("http://11.0.0.0/v1/")]
    [InlineData("http://172.15.255.255/v1/")]
    [InlineData("http://172.32.0.0/v1/")]
    [InlineData("http://192.167.255.255/v1/")]
    [InlineData("http://192.169.0.0/v1/")]
    [InlineData("http://169.254.1.2/v1/")]
    [InlineData("http://100.64.0.1/v1/")]
    [InlineData("http://0.0.0.0/v1/")]
    [InlineData("http://[fc00::1]/v1/")]
    [InlineData("file:///C:/data")]
    [InlineData("http://user:password@127.0.0.1/v1/")]
    [InlineData("http://127.0.0.1/v1/?secret=123")]
    public void VisionSettings_WhenEndpointIsPublicOrUnsafe_RejectsItWithPolicyMessage(string endpoint)
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };

        // Act
        var error = Record.Exception(() => VisionService.Validate(settings));

        // Assert
        AssertEndpointPolicy(error);
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var parsedEndpoint)
            && parsedEndpoint.Host.Length > 0 && parsedEndpoint.Query.Length == 0 && parsedEndpoint.Fragment.Length == 0)
            Assert.Contains(parsedEndpoint.GetLeftPart(UriPartial.Authority), error!.Message, StringComparison.OrdinalIgnoreCase);
        if (endpoint.Contains("secret=", StringComparison.Ordinal))
            TestAssertions.DoesNotContain("secret=123", error!.Message, "endpoint validation does not echo query secrets");
    }

    [Fact]
    public void VisionSettings_WhenEndpointIsLoopback_AcceptsItAndRequiresAModel()
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = "http://127.0.0.1:1234/v1", Model = "vision" };

        // Act
        var validated = VisionService.Validate(settings);
        var missingModel = Record.Exception(() => VisionService.Validate(new()));

        // Assert
        Assert.Equal("http://127.0.0.1:1234/v1/", validated.AbsoluteUri);
        TestAssertions.Exception<InvalidOperationException>(missingModel,
            "settings validation reports the empty model and its requirement", "Model ''", "empty", "loaded", "vision-capable model");
    }

    [Theory]
    // These rows cover the inclusive private IPv4 ranges and IPv6 loopback accepted by local-only routing.
    [InlineData("http://10.0.0.0/v1/")]
    [InlineData("http://10.255.255.255/v1/")]
    [InlineData("http://172.16.0.0/v1/")]
    [InlineData("https://172.31.255.255:1234/v1/")]
    [InlineData("http://192.168.0.0/v1/")]
    [InlineData("http://192.168.255.255/v1/")]
    [InlineData("http://[::1]:1234/v1/")]
    public void VisionSettings_WhenEndpointIsPrivateOrIpv6Loopback_AcceptsIt(string endpoint)
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };

        // Act
        var validated = VisionService.Validate(settings);

        // Assert
        Assert.Equal(endpoint, validated.AbsoluteUri);
    }

    [Theory]
    // These rows ensure both model discovery and analysis stay on a validated private-LAN origin.
    [InlineData("http://10.1.2.3:1234/v1/")]
    [InlineData("http://172.20.1.2:1234/v1/")]
    [InlineData("http://192.168.1.2:1234/v1/")]
    public async Task VisionService_WhenEndpointIsPrivateLan_UsesValidatedOriginForDiscoveryAndAnalysis(string endpoint)
    {
        // Arrange
        var requests = new List<Uri?>();
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? """{"data":[{"id":"vision"}]}"""
                    : """{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            });
        })));
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };

        // Act
        var models = await service.GetModelsAsync(settings);
        var analysis = await service.AnalyzeAsync(settings, "image");

        // Assert
        Assert.Equal(new[] { "vision" }, models);
        Assert.Equal("A landscape.", analysis.Description);
        Assert.Equal(new[] { new Uri(new Uri(endpoint), "models"), new Uri(new Uri(endpoint), "chat/completions") }, requests);
    }

    [Fact]
    public async Task VisionService_WhenAnalyzingLocally_SendsAndParsesStructuredOpenAiRequest()
    {
        // Arrange
        string? sent = null;
        string? requestUri = null;
        var handler = new FakeHandler(async request =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            sent = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A green landscape.\",\"tags\":[\"green\",\"landscape\",\"green\"]}"}}]}""")
            };
        });
        var service = new VisionService(new HttpClient(handler));
        // Act
        var result = await service.AnalyzeAsync(new() { Model = "local-vision" }, "data:image/png;base64,example");

        // Assert
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", requestUri);
        Assert.Equal("A green landscape.", result.Description);
        Assert.Equal("green, landscape", result.Tags);
        TestAssertions.Contains("local-vision", sent!, "the request specifies the configured model");
        TestAssertions.Contains("data:image/png;base64,example", sent!, "the request includes the image payload");
    }

    [Fact]
    public async Task VisionService_WhenEndpointReturnsError_ReportsTheHttpFailure()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)))));
        // Act
        var error = await Record.ExceptionAsync(() => service.AnalyzeAsync(new() { Model = "local" }, "image"));

        // Assert
        TestAssertions.Exception<InvalidOperationException>(error,
            "the failure identifies the HTTP status", "HTTP 400");
    }

    [Fact]
    public async Task ModelDiscovery_WhenModelIsNotConfigured_UsesPerRequestAuthentication()
    {
        // Arrange
        var requests = new List<(HttpMethod Method, Uri Uri, string? Scheme, string? Token)>();
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"z-model"},{"id":"a-model"},{"id":"z-model"}]}""")
            });
        })));
        var authenticatedSettings = new VisionSettings { ApiKey = "private-token" };

        // Act
        var models = await service.GetModelsAsync(authenticatedSettings);
        await service.GetModelsAsync(new());

        // Assert
        Assert.Equal(new[] { "a-model", "z-model" }, models);
        Assert.Equal(new[]
        {
            (HttpMethod.Get, new Uri("http://127.0.0.1:1234/v1/models"), "Bearer", authenticatedSettings.ApiKey),
            (HttpMethod.Get, new Uri("http://127.0.0.1:1234/v1/models"), (string?)null, (string?)null)
        }, requests);
    }
    [Theory]
    [InlineData("""{"data":[]}""", false)]
    [InlineData("""{"models":[]}""", true)]
    [InlineData("""{"data":[{"id":""}]}""", true)]
    [InlineData("""{"data":[{}]}""", true)]
    // Empty arrays are valid; malformed response shapes and identifiers must explain model-list failure.
    public async Task ModelDiscovery_WhenResponseIsEmptyOrMalformed_ReportsTheCorrectOutcome(string json, bool invalid)
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }))));
        string[]? models = null;

        // Act
        var error = await Record.ExceptionAsync(async () => models = (await service.GetModelsAsync(new())).ToArray());

        // Assert
        if (invalid)
        {
            var expectedParts = json.Contains("\"models\"", StringComparison.Ordinal)
                ? new[] { "{\"models\":[]}", "OpenAI-compatible object", "data array" }
                : new[] { "model element", "nonempty string id", json.Contains("\"id\":\"\"") ? "\"\"" : "{}" };
            TestAssertions.Exception<InvalidOperationException>(error,
                "the error identifies the malformed response element and expected shape", expectedParts);
        }
        else
        {
            Assert.Null(error);
            TestAssertions.Empty(models!, "a valid empty model list produces no models");
        }
    }

    [Fact]
    public async Task ModelDiscovery_WhenEndpointTimeoutOrKeyIsInvalid_RejectsBeforeSending()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => throw new Exception("Must not send"))));
        // Act
        var endpointError = await Record.ExceptionAsync(() => service.GetModelsAsync(new() { Endpoint = "https://example.com/v1/" }));
        var lowTimeout = await Record.ExceptionAsync(() => service.GetModelsAsync(new() { RequestTimeoutSeconds = 0 }));
        var highTimeout = await Record.ExceptionAsync(() => service.GetModelsAsync(new() { RequestTimeoutSeconds = 3601 }));
        var keyError = await Record.ExceptionAsync(() => service.GetModelsAsync(new() { ApiKey = "bad\r\nkey" }));

        // Assert
        AssertEndpointPolicy(endpointError);
        Assert.Contains("https://example.com", endpointError!.Message, StringComparison.OrdinalIgnoreCase);
        var timeoutBounds = new[] { "between", VisionSettings.MinRequestTimeoutSeconds.ToString(), VisionSettings.MaxRequestTimeoutSeconds.ToString() };
        TestAssertions.Exception<InvalidOperationException>(lowTimeout, "the timeout message provides both allowed bounds", timeoutBounds);
        TestAssertions.Exception<InvalidOperationException>(highTimeout, "the timeout message provides both allowed bounds", timeoutBounds);
        Assert.Contains("0 seconds", lowTimeout!.Message, StringComparison.Ordinal);
        Assert.Contains("3601 seconds", highTimeout!.Message, StringComparison.Ordinal);
        TestAssertions.Exception<InvalidOperationException>(keyError, "the API-key message explains the rejected content", "control characters");
    }

    [Fact]
    public async Task VisionAndTextRequests_WhenAuthenticationIsSaved_UseItForBothOperations()
    {
        // Arrange
        var authorization = new List<(string? Scheme, string? Token)>();
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            authorization.Add((request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"],\"collection\":\"Landscapes\"}"}}]}""")
            });
        })));
        var settings = new VisionSettings { Model = "vision", ApiKey = "saved-token" };

        // Act
        await service.AnalyzeAsync(settings, "image");
        var collection = await service.SuggestCollectionAsync(settings, "Landscape", "green", []);

        // Assert
        Assert.Equal("Landscapes", collection);
        Assert.Equal(new (string? Scheme, string? Token)[]
        {
            ("Bearer", settings.ApiKey), ("Bearer", settings.ApiKey)
        }, authorization);
    }

    private static void AssertEndpointPolicy(Exception? error) =>
        TestAssertions.Exception<InvalidOperationException>(error,
            "the endpoint policy identifies all permitted address classes",
            "127.0.0.1", "::1", "10.x.x.x", "172.16.x.x", "172.31.x.x", "192.168.x.x",
            "Public addresses and hostnames are not allowed");

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        // Handler callbacks are serialized; captured test recorders are single-threaded for each request.
        private readonly SemaphoreSlim callbackGate = new(1, 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await callbackGate.WaitAsync(cancellationToken);
            try { return await send(request); }
            finally { callbackGate.Release(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) callbackGate.Dispose();
            base.Dispose(disposing);
        }
    }
}