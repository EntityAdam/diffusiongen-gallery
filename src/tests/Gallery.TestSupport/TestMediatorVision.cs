using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

internal abstract record VisionAnswer;

internal sealed record VisionSearchAnswer(
    string Search, string Tag, string Collection, int? MinRating,
    bool FavoritesOnly, string Orientation, string Review) : VisionAnswer;

internal sealed record VisionCollectionAnswer(string Collection) : VisionAnswer;

internal sealed record CapturedVisionRequest(string Model, string Path);

internal sealed class TestMediatorVision
{
    private readonly ImmutableArray<VisionAnswer> answers;
    private readonly Lock captureLock = new();
    private readonly List<CapturedVisionRequest> capturedVisionRequests = [];

    public TestMediatorVision(params VisionAnswer[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Length == 0)
            throw new ArgumentException("Vision script requires at least one answer.", nameof(answers));
        if (answers.Any(answer => answer is null))
            throw new ArgumentException("Vision script answers must not contain null.", nameof(answers));
        this.answers = [.. answers];
    }

    public ImmutableArray<CapturedVisionRequest> CapturedVisionRequests
    {
        get
        {
            lock (captureLock) return [.. capturedVisionRequests];
        }
    }

    public HttpMessageHandler CreateHttpMessageHandler() => new SpyingHttpMessageHandler(this);

    private sealed class SpyingHttpMessageHandler(TestMediatorVision mediator) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var jsonDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var capturedRequest = new CapturedVisionRequest(
                jsonDocument.RootElement.GetProperty("model").GetString()!, request.RequestUri!.AbsolutePath);
            VisionAnswer answer;
            lock (mediator.captureLock)
            {
                answer = mediator.answers[Math.Min(mediator.capturedVisionRequests.Count, mediator.answers.Length - 1)];
                mediator.capturedVisionRequests.Add(capturedRequest);
            }
            // The final answer repeats, so one script also models repeated identical replies.
            var content = answer switch
            {
                VisionSearchAnswer search => JsonSerializer.Serialize(new
                {
                    search = search.Search, tag = search.Tag, collection = search.Collection,
                    minRating = search.MinRating, favoritesOnly = search.FavoritesOnly,
                    orientation = search.Orientation, review = search.Review
                }),
                VisionCollectionAnswer collection => JsonSerializer.Serialize(new { collection = collection.Collection }),
                _ => throw new InvalidOperationException($"Unsupported Vision answer type '{answer.GetType().Name}'.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}

internal static class GalleryVisionFactory
{
    public static GalleryService Create(string vaultRoot, TestMediatorVision mediator) =>
        GalleryService.Create(vaultRoot, new GalleryRuntimeOptions { StateDirectory = vaultRoot + "-state" },
            new HttpClient(mediator.CreateHttpMessageHandler()));
}
