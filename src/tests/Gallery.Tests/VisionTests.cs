using Gallery.Core.Services.Gateways;
using System.Net;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

// Direct gateway tests supply malformed response envelopes through HttpMessageHandler and observe
// exact outgoing requests. The UI boundary cannot deterministically produce these protocol failures
// or distinguish per-request authentication from shared HttpClient state without this transport oracle.
[Trait("Category", "class_test")]
public static class VisionTests
{
    [Fact]
    public static async Task BulkAnalysis_WhenCollectionsAreAvailable_ThenUsesOneVisionRequestWithCollectionContext()
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
        var expected = ExpectedVisionScenario.Create("one collection-aware request returns the complete analysis",
            new ExpectedVisionObligation("image payload", "image_url", VisionComparison.Contains), new ExpectedVisionObligation("available collections", "Worlds", VisionComparison.Contains),
            new ExpectedVisionObligation("collection instruction", "collection", VisionComparison.Contains), new ExpectedVisionObligation("analysis", new[] { "A landscape.", "green", "Worlds", "" }), new ExpectedVisionObligation("requests", 1));
        // Act
        var result = await service.AnalyzeImageWithCollectionAsync("vision", 180, "data:image/png;base64,example", ["Worlds"]);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        {
            ["image payload"] = sent, ["available collections"] = sent, ["collection instruction"] = sent,
            ["analysis"] = new[] { result.Description, result.Tags, result.Collection, result.Warning }, ["requests"] = calls
        });
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
    public static async Task BulkAnalysis_WhenResponseIsEmptyOrInvalid_ThenExplainsTheFailure(string body, string expected)
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }))));
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeImageWithCollectionAsync("vision", 180, "image", []));

        // Assert
        var malformedResponse = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(expected, malformedResponse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task BulkAnalysis_WhenCollectionIsMissing_ThenPreservesAnalysisWithWarning()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            }))));
        var expected = ExpectedVisionScenario.Create("missing collection preserves description and tags and explains the omission",
            new ExpectedVisionObligation("analysis", new[] { "A landscape.", "green", "" }), new ExpectedVisionObligation("warning", "missing collection", VisionComparison.Contains));
        // Act
        var result = await service.AnalyzeImageWithCollectionAsync("vision", 180, "image", []);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        { ["analysis"] = new[] { result.Description, result.Tags, result.Collection }, ["warning"] = result.Warning });
    }

    [Fact]
    public static async Task Analysis_WhenCollectionIsMissing_ThenPreservesDescriptionAndTags()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""") }))));
        var expected = ExpectedVisionScenario.Create("basic analysis does not require a collection", new ExpectedVisionObligation("analysis", new[] { "A landscape.", "green" }));
        // Act
        var analysis = await service.AnalyzeAsync("vision", 180, "image");
        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?> { ["analysis"] = new[] { analysis.Description, analysis.Tags } });
    }

    [Theory]
    // Description must be a string and every tag must be a string in an array, for all response rows.
    [InlineData("""{}""", "description")]
    [InlineData("""{"description":123,"tags":[]}""", "description")]
    [InlineData("""{"description":"Valid","tags":"green"}""", "tags")]
    [InlineData("""{"description":"Valid","tags":[null]}""", "not a string")]
    [InlineData("""{"description":"Valid","tags":[123]}""", "not a string")]
    [InlineData("""[]""", "description")]
    public static async Task Analysis_WhenResponseFieldsAreInvalid_ThenReportsActionableErrors(string content, string expected)
    {
        // Arrange
        var envelope = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) }))));
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeImageWithCollectionAsync("vision", 180, "image", []));

        // Assert
        var invalidAnalysis = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(expected, invalidAnalysis.Message, StringComparison.OrdinalIgnoreCase);
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
    public static void VisionSettings_WhenEndpointIsPublicOrUnsafe_ThenRejectsItWithPolicyMessage(string endpoint)
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };

        var expected = EndpointExpectation(endpoint);
        // Act
        var error = Assert.Throws<InvalidOperationException>(() => VisionService.Validate(settings));

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, EndpointEvidence(error));
    }

    [Fact]
    public static void VisionSettings_WhenEndpointIsLoopback_ThenAcceptsIt()
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = "http://127.0.0.1:1234/v1", Model = "vision" };

        // Act
        var validated = VisionService.Validate(settings);

        // Assert - loopback validation normalizes the accepted endpoint
        Assert.Equal("http://127.0.0.1:1234/v1/", validated.AbsoluteUri);

    }

    [Fact]
    public static void VisionSettings_WhenModelIsMissing_ThenExplainsTheRequirement()
    {
        // Arrange
        var settings = new VisionSettings();
        string[] expectedMessagePhrases = ["Model ''", "empty", "loaded", "vision-capable model"];

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => VisionService.Validate(settings));
        // Assert - missing models explain how to satisfy the requirement
        var missingModel = Assert.IsType<InvalidOperationException>(error);
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, missingModel.Message);
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
    public static void VisionSettings_WhenEndpointIsPrivateOrIpv6Loopback_ThenAcceptsIt(string endpoint)
    {
        // Arrange
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };

        // Act
        var validated = VisionService.Validate(settings);

        // Assert - validation preserves the accepted private or loopback endpoint
        Assert.Equal(endpoint, validated.AbsoluteUri);
    }

    [Theory]
    // These rows ensure both model discovery and analysis stay on a validated private-LAN origin.
    [InlineData("http://10.1.2.3:1234/v1/")]
    [InlineData("http://172.20.1.2:1234/v1/")]
    [InlineData("http://192.168.1.2:1234/v1/")]
    public static async Task VisionService_WhenEndpointIsPrivateLan_ThenUsesValidatedOriginForDiscovery(string endpoint)
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

        var expected = ExpectedVisionScenario.Create("private-LAN discovery uses the configured origin",
            new ExpectedVisionObligation("models", new[] { "vision" }), new ExpectedVisionObligation("request origins", new[] { new Uri(new Uri(endpoint), "models").AbsoluteUri }));
        // Act
        var models = await service.Connect(settings.Endpoint, settings.ApiKey).GetModelsAsync(settings.RequestTimeoutSeconds);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        { ["models"] = models, ["request origins"] = requests.Select(uri => uri!.AbsoluteUri) });
    }

    // Every private-LAN origin keeps analysis on its configured validated address.
    [Theory]
    [InlineData("http://10.1.2.3:1234/v1/")]
    [InlineData("http://172.20.1.2:1234/v1/")]
    [InlineData("http://192.168.1.2:1234/v1/")]
    public static async Task VisionService_WhenEndpointIsPrivateLan_ThenUsesValidatedOriginForAnalysis(string endpoint)
    {
        // Arrange
        string? requestUri = null;
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""") });
        })));
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };
        var expected = ExpectedVisionScenario.Create("private-LAN analysis uses the configured origin and parses the response",
            new ExpectedVisionObligation("request origin", new Uri(new Uri(endpoint), "chat/completions").AbsoluteUri), new ExpectedVisionObligation("analysis", new[] { "A landscape.", "green" }));
        // Act
        var analysis = await service.Connect(settings.Endpoint, settings.ApiKey).AnalyzeAsync(settings.Model, settings.RequestTimeoutSeconds, "image");
        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?> { ["request origin"] = requestUri, ["analysis"] = new[] { analysis.Description, analysis.Tags } });
    }

    [Fact]
    public static async Task VisionService_WhenAnalyzingLocally_ThenSendsAndParsesStructuredOpenAiRequest()
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
        var expected = ExpectedVisionScenario.Create("structured vision requests preserve origin, model, payload and parsed analysis",
            new ExpectedVisionObligation("origin", "http://127.0.0.1:1234/v1/chat/completions"), new ExpectedVisionObligation("analysis", new[] { "A green landscape.", "green, landscape" }),
            new ExpectedVisionObligation("model", "local-vision", VisionComparison.Contains), new ExpectedVisionObligation("payload", "data:image/png;base64,example", VisionComparison.Contains));
        // Act
        var result = await service.AnalyzeAsync("local-vision", 180, "data:image/png;base64,example");

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        { ["origin"] = requestUri, ["analysis"] = new[] { result.Description, result.Tags }, ["model"] = sent, ["payload"] = sent });
    }

    [Fact]
    public static async Task VisionService_WhenEndpointReturnsError_ThenReportsTheHttpFailure()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)))));
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync("local", 180, "image"));

        // Assert
        var httpFailure = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("HTTP 400", httpFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public static async Task ModelDiscovery_WhenModelIsNotConfigured_ThenUsesPerRequestAuthentication()
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

        var expected = ExpectedVisionScenario.Create("model discovery orders unique identifiers and authentication never leaks to the next request",
            new ExpectedVisionObligation("models", new[] { "a-model", "z-model" }), new ExpectedVisionObligation("requests", new[]
            { new string?[] { "GET", "http://127.0.0.1:1234/v1/models", "Bearer", authenticatedSettings.ApiKey },
              new string?[] { "GET", "http://127.0.0.1:1234/v1/models", null, null } }));
        await service.Connect(authenticatedSettings.Endpoint, authenticatedSettings.ApiKey).GetModelsAsync(authenticatedSettings.RequestTimeoutSeconds);

        // Act - authentication from the preceding request must not persist
        var models = await service.GetModelsAsync(180);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        { ["models"] = models, ["requests"] = requests.Select(request => new[] { request.Method.Method, request.Uri.AbsoluteUri, request.Scheme, request.Token }) });
    }

    [Fact]
    public static async Task ModelDiscovery_WhenResponseContainsAnEmptyModelList_ThenReturnsNoModels()
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") }))));
        var expectedVisionScenario = ExpectedVisionScenario.Create("valid empty model lists succeed without an error",
            new ExpectedVisionObligation("models", Array.Empty<string>()));

        // Act
        IReadOnlyList<string> models = await service.GetModelsAsync(180);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario,
            new Dictionary<string, object?> { ["models"] = models });
    }

    // Every malformed model-list envelope is rejected with the invalid shape or identifier.
    [Theory]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"data":[{"id":""}]}""")]
    [InlineData("""{"data":[{}]}""")]
    public static async Task ModelDiscovery_WhenResponseIsMalformed_ThenReportsTheInvalidEnvelope(string json)
    {
        // Arrange
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }))));
        string[] expectedMessagePhrases = json.Contains("\"models\"", StringComparison.Ordinal)
            ? ["{\"models\":[]}", "OpenAI-compatible object", "data array"]
            : ["model element", "nonempty string id", json.Contains("\"id\":\"\"", StringComparison.Ordinal) ? "\"\"" : "{}"];

        // Act
        var malformedResponse = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(180));

        // Assert
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, malformedResponse.Message);
    }

    [Fact]
    public static async Task ModelDiscovery_WhenEndpointIsPublic_ThenRejectsItsRoutingPolicyBeforeSending()
    {
        // Arrange
        var calls = 0;
        var service = new VisionService(new HttpClient(new FakeHandler(_ => { calls++; throw new Exception("Must not send"); })));
        var settings = new VisionSettings { Endpoint = "https://example.com/v1/" };
        ExpectedVisionScenario expectedVisionScenario = EndpointExpectation(settings.Endpoint);
        expectedVisionScenario = expectedVisionScenario with
        {
            Obligations = expectedVisionScenario.Obligations.Add(new ExpectedVisionObligation("requests", 0))
        };

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.Connect(settings.Endpoint, settings.ApiKey).GetModelsAsync(settings.RequestTimeoutSeconds));

        // Assert - gather the rejection and the number of attempted transport requests
        Dictionary<string, object?> actualVisionValues = EndpointEvidence(error);
        actualVisionValues["requests"] = calls;

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario, actualVisionValues);
    }

    // Both out-of-range timeout bounds must be explained before any request reaches transport.
    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public static async Task ModelDiscovery_WhenTimeoutIsOutsideTheSupportedRange_ThenRejectsBeforeSending(int timeoutSeconds)
    {
        // Arrange
        var calls = 0;
        var service = new VisionService(new HttpClient(new FakeHandler(_ => { calls++; throw new Exception("Must not send"); })));
        var settings = new VisionSettings { RequestTimeoutSeconds = timeoutSeconds };
        var expectedVisionScenario = ExpectedVisionScenario.Create("invalid timeout settings explain both bounds before sending",
            new ExpectedVisionObligation("exception type", typeof(InvalidOperationException).FullName),
            new ExpectedVisionObligation("requests", 0));
        string[] expectedMessagePhrases = [timeoutSeconds + " seconds", "between 10 and 3600 seconds", "between"];

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.Connect(settings.Endpoint, settings.ApiKey).GetModelsAsync(settings.RequestTimeoutSeconds));

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario,
            new Dictionary<string, object?> { ["exception type"] = error.GetType().FullName, ["requests"] = calls });
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, error.Message);
    }

    [Fact]
    public static async Task ModelDiscovery_WhenApiKeyContainsControlCharacters_ThenRejectsBeforeSending()
    {
        // Arrange
        var calls = 0;
        var service = new VisionService(new HttpClient(new FakeHandler(_ => { calls++; throw new Exception("Must not send"); })));
        var settings = new VisionSettings { ApiKey = "bad\r\nkey" };
        var expectedVisionScenario = ExpectedVisionScenario.Create("API-key control characters are rejected before sending",
            new ExpectedVisionObligation("exception type", typeof(InvalidOperationException).FullName),
            new ExpectedVisionObligation("requests", 0),
            new ExpectedVisionObligation("requirement", "control characters", VisionComparison.Contains));

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.Connect(settings.Endpoint, settings.ApiKey).GetModelsAsync(settings.RequestTimeoutSeconds));

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario,
            new Dictionary<string, object?> { ["exception type"] = error.GetType().FullName, ["requests"] = calls, ["requirement"] = error.Message });
    }

    [Fact]
    public static async Task VisionAndTextRequests_WhenAuthenticationIsSaved_ThenUseItForBothOperations()
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

        var expected = ExpectedVisionScenario.Create("saved authentication is applied to vision and text requests", new ExpectedVisionObligation("collection", "Landscapes"),
            new ExpectedVisionObligation("authorization", new[] { new[] { "Bearer", settings.ApiKey }, new[] { "Bearer", settings.ApiKey } }));
        await service.Connect(settings.Endpoint, settings.ApiKey).AnalyzeAsync(settings.Model, settings.RequestTimeoutSeconds, "image");

        // Act - text request follows an authenticated vision request
        var collection = await service.Connect(settings.Endpoint, settings.ApiKey).SuggestCollectionAsync(settings.Model, settings.RequestTimeoutSeconds, "Landscape", "green", []);

        // Assert
        AsserterVisionScenario.AssertAllObligationsAreMet(expected, new Dictionary<string, object?>
        { ["collection"] = collection, ["authorization"] = authorization.Select(value => new[] { value.Scheme, value.Token }) });
    }

    private static ExpectedVisionScenario EndpointExpectation(string endpoint)
    {
        var expected = ExpectedVisionScenario.Create("unsafe endpoints explain local-only routing without disclosing secrets",
            new ExpectedVisionObligation("exception type", typeof(InvalidOperationException).FullName));
        foreach (var part in new[] { "127.0.0.1", "::1", "10.x.x.x", "172.16.x.x", "172.31.x.x", "192.168.x.x", "Public addresses and hostnames are not allowed" })
            expected = expected with { Obligations = expected.Obligations.Add(new ExpectedVisionObligation(part, part, VisionComparison.Contains)) };
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            expected = expected with { Obligations = expected.Obligations.Add(new ExpectedVisionObligation("authority", uri.GetLeftPart(UriPartial.Authority), VisionComparison.Contains)) };
        if (endpoint.Contains("secret=", StringComparison.Ordinal))
            expected = expected with { Obligations = expected.Obligations.Add(new ExpectedVisionObligation("redaction", "secret=123", VisionComparison.Excludes)) };
        return expected;
    }

    private static Dictionary<string, object?> EndpointEvidence(Exception? error)
    {
        var evidence = new Dictionary<string, object?> { ["exception type"] = error?.GetType().FullName, ["authority"] = error?.Message, ["redaction"] = error?.Message };
        foreach (var part in new[] { "127.0.0.1", "::1", "10.x.x.x", "172.16.x.x", "172.31.x.x", "192.168.x.x", "Public addresses and hostnames are not allowed" }) evidence[part] = error?.Message;
        return evidence;
    }

    // This handler owns the callback for one test lifetime. Invocations are serialized by callbackGate;
    // callback observations belong to that test and must not be accessed through this handler after disposal.
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