using System.Net;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services.Gateways;

namespace Gallery.Tests;

public sealed class SetSuggestionTests
{
    [Theory]
    [InlineData("set-1", true)]
    [InlineData("outside-set", false)]
    [InlineData("", true)]
    public async Task DestinationSuggestionsUseConfiguredPromptAndOnlyAcceptSuppliedSetIds(string resultId, bool valid)
    {
        using var handler = new SuggestionHandler(resultId);
        using var client = new HttpClient(handler);
        var settings = new VisionSettings { Model = "vision", SetSuggestionSystemPrompt = "Custom set selection instructions." };
        var service = new VisionService(client, settings);
        var task = service.SuggestSetAsync("Series", [new MediaSet("set-1", "collection-1", "Season 1", 0)],
            [new ImageRecord { Description = "Pilot episode", Tags = "drama" }]);
        if (valid) Assert.Equal(resultId, await task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        using var request = JsonDocument.Parse(handler.Body!);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal(settings.SetSuggestionSystemPrompt, messages[0].GetProperty("content").GetString());
        var payload = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("Season 1", payload);Assert.Contains("set-1", payload);Assert.Contains("Pilot episode", payload);
    }
    private sealed class SuggestionHandler(string resultId) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { setId = resultId }) } } } })) };
        }
    }
}
