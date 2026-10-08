using System.Net;
using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

public sealed class VisionTests
{
    [Fact]
    public async Task BulkAnalysisUsesOneVisionRequestIncludingCollectionContext()
    {
        var calls = 0;
        var service = new VisionService(new HttpClient(new FakeHandler(async request =>
        {
            calls++;
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("image_url", body);
            Assert.Contains("Worlds", body);
            Assert.Contains("collection", body);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":" \n```json\n{\"description\":\"A landscape.\",\"tags\":[\"green\"],\"collection\":\"Worlds\"}\n``` "}}]}""")
            };
        })));
        var result = await service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "data:image/png;base64,example", ["Worlds"]);
        Assert.Equal(("A landscape.", "green", "Worlds", ""), result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("", "empty response")]
    [InlineData("""{"choices":[]}""", "no completion choices")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""", "empty completion content")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""", "empty completion content")]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":""}}]}""", "token limit")]
    [InlineData("""{"choices":[{"message":{"content":"```json\n\n```"}}]}""", "empty JSON code block")]
    [InlineData("""{"choices":[{"message":{"content":"not JSON"}}]}""", "valid JSON")]
    public async Task EmptyOrInvalidBulkResponsesExplainFailure(string body, string expected)
    {
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task MissingCollectionPreservesAnalysisWithExplicitWarning()
    {
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            }))));
        Assert.Equal("A landscape.", (await service.AnalyzeAsync(new() { Model = "vision" }, "image")).Description);
        var result = await service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []);
        Assert.Equal("A landscape.", result.Description);
        Assert.Equal("green", result.Tags);
        Assert.Equal("", result.Collection);
        Assert.Contains("missing collection", result.Warning);
    }

    [Theory]
    [InlineData("""{}""", "description")]
    [InlineData("""{"description":123,"tags":[]}""", "description")]
    [InlineData("""{"description":"Valid","tags":"green"}""", "tags")]
    [InlineData("""{"description":"Valid","tags":[null]}""", "not a string")]
    [InlineData("""{"description":"Valid","tags":[123]}""", "not a string")]
    [InlineData("""[]""", "description")]
    public async Task InvalidAnalysisFieldsHaveActionableErrors(string content, string expected)
    {
        var envelope = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) }))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeImageWithCollectionAsync(new() { Model = "vision" }, "image", []));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
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
    public void PublicOrUnsafeEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => VisionService.Validate(new() { Endpoint = endpoint, Model = "vision" }));
    }

    [Fact]
    public void LoopbackEndpointsAreAcceptedAndModelIsRequired()
    {
        Assert.Equal("http://127.0.0.1:1234/v1/", VisionService.Validate(new() { Endpoint = "http://127.0.0.1:1234/v1", Model = "vision" }).AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => VisionService.Validate(new()));
    }

    [Theory]
    [InlineData("http://10.0.0.0/v1/")]
    [InlineData("http://10.255.255.255/v1/")]
    [InlineData("http://172.16.0.0/v1/")]
    [InlineData("https://172.31.255.255:1234/v1/")]
    [InlineData("http://192.168.0.0/v1/")]
    [InlineData("http://192.168.255.255/v1/")]
    [InlineData("http://[::1]:1234/v1/")]
    public void PrivateLanAndIpv6LoopbackEndpointsAreAccepted(string endpoint)
    {
        Assert.Equal(endpoint, VisionService.Validate(new() { Endpoint = endpoint, Model = "vision" }).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://10.1.2.3:1234/v1/")]
    [InlineData("http://172.20.1.2:1234/v1/")]
    [InlineData("http://192.168.1.2:1234/v1/")]
    public async Task PrivateLanModelDiscoveryAndAnalysisUseValidatedEndpoint(string endpoint)
    {
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal(new Uri(new Uri(endpoint), request.Method == HttpMethod.Get ? "models" : "chat/completions"), request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? """{"data":[{"id":"vision"}]}"""
                    : """{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"]}"}}]}""")
            });
        })));
        var settings = new VisionSettings { Endpoint = endpoint, Model = "vision" };
        Assert.Equal(new[] { "vision" }, await service.GetModelsAsync(settings));
        Assert.Equal("A landscape.", (await service.AnalyzeAsync(settings, "image")).Description);
    }

    [Fact]
    public async Task LocalOpenAiRequestSavesStructuredAnalysis()
    {
        string? sent = null;
        var handler = new FakeHandler(async request =>
        {
            Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            sent = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A green landscape.\",\"tags\":[\"green\",\"landscape\",\"green\"]}"}}]}""")
            };
        });
        var service = new VisionService(new HttpClient(handler));
        var result = await service.AnalyzeAsync(new() { Model = "local-vision" }, "data:image/png;base64,example");
        Assert.Equal("A green landscape.", result.Description);
        Assert.Equal("green, landscape", result.Tags);
        Assert.Contains("local-vision", sent!);
        Assert.Contains("data:image/png;base64,example", sent!);
    }

    [Fact]
    public async Task EndpointFailuresAreNotSuccessShaped()
    {
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(new() { Model = "local" }, "image"));
    }

    [Fact]
    public async Task DiscoveryWorksWithoutModelAndUsesPerRequestAuthentication()
    {
        var calls = 0;
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:1234/v1/models", request.RequestUri!.AbsoluteUri);
            if (calls++ == 0) Assert.Equal("Bearer private-token", request.Headers.Authorization!.ToString());
            else Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"z-model"},{"id":"a-model"},{"id":"z-model"}]}""")
            });
        })));
        Assert.Equal(new[] { "a-model", "z-model" }, await service.GetModelsAsync(new() { ApiKey = "private-token" }));
        await service.GetModelsAsync(new());
    }

    [Theory]
    [InlineData("""{"data":[]}""", false)]
    [InlineData("""{"models":[]}""", true)]
    [InlineData("""{"data":[{"id":""}]}""", true)]
    [InlineData("""{"data":[{}]}""", true)]
    public async Task DiscoveryReportsEmptyOrMalformedModelLists(string json, bool invalid)
    {
        var service = new VisionService(new HttpClient(new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }))));
        if (invalid) await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(new()));
        else Assert.Empty(await service.GetModelsAsync(new()));
    }

    [Fact]
    public async Task DiscoveryRejectsRemoteEndpointsAndInvalidTimeoutBeforeSending()
    {
        var service = new VisionService(new HttpClient(new FakeHandler(_ => throw new Exception("Must not send"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(new() { Endpoint = "https://example.com/v1/" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(new() { RequestTimeoutSeconds = 0 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(new() { RequestTimeoutSeconds = 3601 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetModelsAsync(new() { ApiKey = "bad\r\nkey" }));
    }

    [Fact]
    public async Task BothVisionAndTextRequestsUseSavedAuthentication()
    {
        var service = new VisionService(new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("Bearer saved-token", request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A landscape.\",\"tags\":[\"green\"],\"collection\":\"Landscapes\"}"}}]}""")
            });
        })));
        var settings = new VisionSettings { Model = "vision", ApiKey = "saved-token" };
        await service.AnalyzeAsync(settings, "image");
        Assert.Equal("Landscapes", await service.SuggestCollectionAsync(settings, "Landscape", "green", []));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
