using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gallery.Core.Exceptions;
using Gallery.Core.Services.Gateways;

namespace Gallery.Tests;

public sealed class VisionLibraryMalformedResponseTests
{
    [Theory]
    [InlineData("minRating", null)]
    [InlineData("minRating", "null")]
    [InlineData("minRating", "\"2\"")]
    [InlineData("minRating", "2.5")]
    [InlineData("minRating", "2147483648")]
    [InlineData("minRating", "true")]
    [InlineData("favoritesOnly", null)]
    [InlineData("favoritesOnly", "null")]
    [InlineData("favoritesOnly", "\"true\"")]
    [InlineData("favoritesOnly", "1")]
    public async Task InterpretSearch_WhenTypedFieldIsMissingOrMalformed_ThenReportsDomainFormat(
        string fieldName, string? replacement)
    {
        var response = JsonNode.Parse("""
            {"search":"","tag":"","collection":"","minRating":2,"favoritesOnly":false,
             "orientation":"all","review":"all"}
            """)!.AsObject();
        if (replacement is null) response.Remove(fieldName);
        else response[fieldName] = JsonNode.Parse(replacement);
        string completion = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = response.ToJsonString() } } }
        });
        using var client = new HttpClient(new ResponseHandler(completion));
        var service = new VisionService(client);

        var exception = await Assert.ThrowsAsync<VisionResponseFormatException>(
            () => service.InterpretSearchAsync("model", 180, "show pictures", []));

        Assert.Contains(fieldName, exception.Message);
    }

    [Fact]
    public async Task InterpretSearch_WhenTypedFieldsAreValid_ThenKeepsTheirValues()
    {
        const string completion = """
            {"choices":[{"message":{"content":"{\"search\":\"\",\"tag\":\"\",\"collection\":\"\",\"minRating\":5,\"favoritesOnly\":true,\"orientation\":\"all\",\"review\":\"all\"}"}}]}
            """;
        using var client = new HttpClient(new ResponseHandler(completion));
        var service = new VisionService(client);

        var filter = await service.InterpretSearchAsync("model", 180, "favorites", []);

        Assert.Equal(5, filter.MinRating);
        Assert.True(filter.IncludesFavoritesOnly);
    }

    private sealed class ResponseHandler(string completion) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion) });
    }
}
